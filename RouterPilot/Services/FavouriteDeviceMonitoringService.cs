using System;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Observes the existing client refresh only; it performs no I/O against the router.</summary>
public sealed class FavouriteDeviceMonitoringService
{
    public static readonly TimeSpan OfflineGracePeriod = TimeSpan.FromMinutes(5);
    private readonly ClientProfileService _profiles = new();
    private readonly INetworkHealthService _networkHealth;
    private readonly IClientPresenceHistoryService _presenceHistory;

    public FavouriteDeviceMonitoringService(INetworkHealthService networkHealth, IClientPresenceHistoryService presenceHistory) { _networkHealth = networkHealth; _presenceHistory = presenceHistory; }

    public void Observe(IEnumerable<ClientInfo> currentClients)
    {
        Dictionary<string, ClientProfile> profiles = _profiles.Load();
        if (!_profiles.LastLoadSucceeded) { _networkHealth.SetMonitoredDeviceIssues([]); return; }

        HashSet<string> online = currentClients.Select(client => ClientIdentity.NormalizeMac(client.MacAddress)).Where(key => key.Length == 12).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<NetworkHealthIssue> issues = [];
        foreach (ClientProfile profile in profiles.Values.Where(profile => profile.MonitorAvailability && ClientIdentity.NormalizeMac(profile.Key).Length == 12))
        {
            string key = ClientIdentity.NormalizeMac(profile.Key);
            if (online.Contains(key)) continue;
            ClientPresencePeriod? period = _presenceHistory.GetCurrentPeriod(key);
            if (period?.State != ClientPresenceState.Offline) continue;
            DateTimeOffset since = period.StartedAt;
            string name = !string.IsNullOrWhiteSpace(profile.Nickname) ? profile.Nickname : !string.IsNullOrWhiteSpace(profile.LastKnownName) ? profile.LastKnownName : profile.LastKnownIpAddress;
            string observed = FormatDuration(now - since);
            issues.Add(new NetworkHealthIssue($"client.monitor.{key}", NetworkHealthSeverity.Warning, "Network", name + " offline", $"{name} has not been observed for {observed}.", "clients", since, now, since.UtcTicks.ToString()));
        }
        _networkHealth.SetMonitoredDeviceIssues(issues);
    }

    private static string FormatDuration(TimeSpan duration) => duration < TimeSpan.FromMinutes(1) ? "less than a minute" : duration < TimeSpan.FromHours(1) ? $"{(int)duration.TotalMinutes} minutes" : $"{(int)duration.TotalHours}h {duration.Minutes}m";
}
