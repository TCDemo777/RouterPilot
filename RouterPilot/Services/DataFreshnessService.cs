using System;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Tracks only the outcome of existing refresh work; it performs no I/O or scheduling.</summary>
public sealed class DataFreshnessService : IDataFreshnessService
{
    private sealed class Entry
    {
        public required string Source { get; init; }
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
        public DateTimeOffset? LastSuccessUtc { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public DataFreshnessState State { get; set; } = DataFreshnessState.Loading;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _suppressStaleUntilUtc;
    public event Action? Changed;

    public void Configure(string source, TimeSpan expectedRefreshInterval)
    {
        if (expectedRefreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expectedRefreshInterval));
        lock (_sync) { Entry entry = GetOrCreate(source); entry.Interval = expectedRefreshInterval; RefreshEntry(entry, DateTimeOffset.UtcNow); }
        Changed?.Invoke();
    }

    public void MarkAttempt(string source)
    {
        lock (_sync) { Entry entry = GetOrCreate(source); entry.LastAttemptUtc = DateTimeOffset.UtcNow; RefreshEntry(entry, entry.LastAttemptUtc.Value); }
        Changed?.Invoke();
    }

    public void MarkSuccess(string source)
    {
        lock (_sync)
        {
            Entry entry = GetOrCreate(source); DateTimeOffset now = DateTimeOffset.UtcNow;
            entry.LastAttemptUtc = now; entry.LastSuccessUtc = now; entry.State = DataFreshnessState.Fresh;
        }
        Changed?.Invoke();
    }

    public void MarkUnavailable(string source)
    {
        lock (_sync)
        {
            Entry entry = GetOrCreate(source); entry.LastAttemptUtc = DateTimeOffset.UtcNow;
            if (entry.LastSuccessUtc is null) entry.State = DataFreshnessState.Unavailable;
            else RefreshEntry(entry, entry.LastAttemptUtc.Value);
        }
        Changed?.Invoke();
    }

    public void Refresh()
    {
        lock (_sync) { DateTimeOffset now = DateTimeOffset.UtcNow; foreach (Entry entry in _entries.Values) RefreshEntry(entry, now); }
        Changed?.Invoke();
    }

    public DataFreshnessInfo Get(string source)
    {
        lock (_sync) { Entry entry = GetOrCreate(source); RefreshEntry(entry, DateTimeOffset.UtcNow); return Snapshot(entry); }
    }

    public IReadOnlyList<DataFreshnessInfo> GetAll()
    {
        lock (_sync) { DateTimeOffset now = DateTimeOffset.UtcNow; foreach (Entry entry in _entries.Values) RefreshEntry(entry, now); return _entries.Values.Select(Snapshot).OrderBy(info => info.Source).ToList(); }
    }

    public void BeginReestablishmentWindow(TimeSpan duration)
    {
        lock (_sync) _suppressStaleUntilUtc = DateTimeOffset.UtcNow.Add(duration);
        Changed?.Invoke();
    }

    private Entry GetOrCreate(string source)
    {
        if (_entries.TryGetValue(source, out Entry? entry)) return entry;
        entry = new Entry { Source = source }; _entries.Add(source, entry); return entry;
    }

    private void RefreshEntry(Entry entry, DateTimeOffset now)
    {
        if (entry.LastSuccessUtc is null) return;
        entry.State = now < _suppressStaleUntilUtc || now - entry.LastSuccessUtc.Value < entry.Interval * 3
            ? DataFreshnessState.Fresh : DataFreshnessState.Stale;
    }

    private static DataFreshnessInfo Snapshot(Entry entry) => new(entry.Source, entry.LastSuccessUtc, entry.LastAttemptUtc, entry.State, entry.Interval);
}
