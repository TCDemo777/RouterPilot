using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Bounded, local transition history from existing authoritative client snapshots.</summary>
public sealed class ClientPresenceHistoryService : IClientPresenceHistoryService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly string _path;
    private readonly AtomicJsonFileStore _jsonStore;
    private readonly Dictionary<string, DateTimeOffset> _absentSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClientPresencePeriod> _periods;
    private bool _snapshotObserved;
    private DateTimeOffset _lastSave;

    public ClientPresenceHistoryService(
        ApplicationDataPathProvider paths,
        AtomicJsonFileStore? jsonStore = null)
    {
        _path = Path.Combine(paths.CurrentPath, "client-presence-history.json");
        _jsonStore = jsonStore ?? new AtomicJsonFileStore();
        _periods = Load();
        // A period left open by a previous process is capped at its last recorded
        // observation; the interval until this process observes a snapshot is Unknown.
        foreach (ClientPresencePeriod period in _periods.Where(period => period.EndedAt is null))
            period.EndedAt = period.LastObservedAt > period.StartedAt ? period.LastObservedAt : period.StartedAt;
        Save();
    }

    public void Observe(IEnumerable<ClientInfo> clients)
    {
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<string> online = clients.Select(client => ClientIdentity.NormalizeMac(client.MacAddress)).Where(key => key.Length == 12).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string key in online)
            {
                _absentSince.Remove(key);
                ClientPresencePeriod? active = Active(key);
                if (active is null || active.State != ClientPresenceState.Online)
                {
                    if (active is not null) active.EndedAt = now;
                    _periods.Add(new ClientPresencePeriod { NormalizedMac = key, StartedAt = now, LastObservedAt = now, State = ClientPresenceState.Online });
                }
                else active.LastObservedAt = now;
            }
            if (_snapshotObserved)
            {
                foreach (string key in _periods.Select(period => period.NormalizedMac).Distinct(StringComparer.OrdinalIgnoreCase).Where(key => !online.Contains(key)).ToList())
                {
                    if (!_absentSince.TryGetValue(key, out DateTimeOffset absent)) { _absentSince[key] = now; continue; }
                    if (now - absent < Grace) continue;
                    ClientPresencePeriod? active = Active(key);
                    if (active is not null && active.State == ClientPresenceState.Online)
                    {
                        active.EndedAt = active.LastObservedAt;
                        _periods.Add(new ClientPresencePeriod { NormalizedMac = key, StartedAt = now, LastObservedAt = now, State = ClientPresenceState.Offline });
                    }
                }
            }
            _snapshotObserved = true;
            Trim(now);
            if (now - _lastSave >= TimeSpan.FromMinutes(1)) Save();
        }
    }

    public IReadOnlyList<ClientPresencePeriod> GetRecent(string normalizedMac, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_sync) return _periods.Where(period => period.NormalizedMac.Equals(ClientIdentity.NormalizeMac(normalizedMac), StringComparison.OrdinalIgnoreCase) && period.StartedAt < to && (period.EndedAt ?? period.LastObservedAt) > from).Select(Clone).ToList();
    }
    public TimeSpan GetObservedOnlineToday(string normalizedMac, DateTimeOffset now)
    {
        DateTimeOffset start = StartOfLocalDay(now);
        return GetRecent(normalizedMac, start, now).Where(period => period.State == ClientPresenceState.Online).Aggregate(TimeSpan.Zero, (total, period) => total + (Min(period.EndedAt ?? period.LastObservedAt, now) - Max(period.StartedAt, start)));
    }
    public IReadOnlyList<ClientDailyAvailability> GetDailyAvailability(string normalizedMac, int days, DateTimeOffset now)
    {
        days = Math.Clamp(days, 1, 30);
        DateTimeOffset today = StartOfLocalDay(now);
        List<DateTimeOffset> starts = Enumerable.Range(0, days).Select(index => StartOfLocalDay(today.AddDays(-index))).OrderByDescending(value => value).ToList();
        DateTimeOffset from = starts.Last();
        List<ClientPresencePeriod> periods = GetRecent(normalizedMac, from, now).ToList();
        var results = new List<ClientDailyAvailability>();
        foreach (DateTimeOffset start in starts)
        {
            DateTimeOffset end = start == today ? now : StartOfLocalDay(start.AddDays(1));
            TimeSpan online = TimeSpan.Zero, offline = TimeSpan.Zero;
            foreach (ClientPresencePeriod period in periods)
            {
                DateTimeOffset periodStart = Max(period.StartedAt, start);
                DateTimeOffset periodEnd = Min(period.EndedAt ?? now, end);
                if (periodEnd <= periodStart) continue;
                if (period.State == ClientPresenceState.Online) online += periodEnd - periodStart;
                else offline += periodEnd - periodStart;
            }
            TimeSpan span = end - start;
            results.Add(new ClientDailyAvailability(start, online, offline, span > online + offline ? span - online - offline : TimeSpan.Zero));
        }
        return results;
    }
    public ClientPresencePeriod? GetCurrentPeriod(string normalizedMac)
    {
        lock (_sync)
        {
            ClientPresencePeriod? period = Active(ClientIdentity.NormalizeMac(normalizedMac));
            return period is null ? null : Clone(period);
        }
    }
    public bool Clear(string normalizedMac)
    {
        lock (_sync)
        {
            string key = ClientIdentity.NormalizeMac(normalizedMac);
            List<ClientPresencePeriod> removed = _periods.Where(period => period.NormalizedMac.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(Clone).ToList();
            bool hadAbsentSince = _absentSince.TryGetValue(key, out DateTimeOffset absentSince);
            _periods.RemoveAll(period => period.NormalizedMac.Equals(key, StringComparison.OrdinalIgnoreCase));
            _absentSince.Remove(key);
            if (Save()) return true;

            _periods.AddRange(removed);
            if (hadAbsentSince) _absentSince[key] = absentSince;
            return false;
        }
    }
    public void CloseSession() { lock (_sync) { foreach (ClientPresencePeriod period in _periods.Where(period => period.EndedAt is null)) period.EndedAt = period.LastObservedAt; Save(); } }
    private ClientPresencePeriod? Active(string key) => _periods.LastOrDefault(period => period.NormalizedMac.Equals(key, StringComparison.OrdinalIgnoreCase) && period.EndedAt is null);
    private List<ClientPresencePeriod> Load()
    {
        if (!File.Exists(_path)) return [];
        return _jsonStore.TryRead<List<ClientPresencePeriod>>(_path, options: null, out List<ClientPresencePeriod>? periods)
            ? periods ?? []
            : [];
    }
    private bool Save()
    {
        try
        {
            _jsonStore.Write(_path, _periods);
            _lastSave = DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to save client presence history ({ex.GetType().Name}).");
            return false;
        }
    }
    private void Trim(DateTimeOffset now) => _periods.RemoveAll(period => (period.EndedAt ?? period.LastObservedAt) < now - Retention);
    private static DateTimeOffset StartOfLocalDay(DateTimeOffset now)
    {
        TimeZoneInfo zone = TimeZoneInfo.Local;
        DateTime local = DateTime.SpecifyKind(now.LocalDateTime.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static ClientPresencePeriod Clone(ClientPresencePeriod period) => new() { NormalizedMac = period.NormalizedMac, StartedAt = period.StartedAt, EndedAt = period.EndedAt, LastObservedAt = period.LastObservedAt, State = period.State };
}
