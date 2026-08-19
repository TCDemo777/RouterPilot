using System;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Correlates supplied application state only; it performs no router or network I/O.</summary>
public sealed class NetworkHealthService : INetworkHealthService
{
    private const int InternetInstabilityThreshold = 3;
    private readonly TimelineService _timeline;
    private readonly Dictionary<string, NetworkHealthIssue> _active = new(StringComparer.Ordinal);
    private IReadOnlyList<NetworkHealthIssue> _monitoredDeviceIssues = [];
    private readonly object _sync = new();
    private NetworkHealthSnapshot _current = NetworkHealthSnapshot.Loading;
    public NetworkHealthService(TimelineService timeline) => _timeline = timeline;
    public NetworkHealthSnapshot Current { get { lock (_sync) return _current; } }
    public event Action<NetworkHealthSnapshot>? SnapshotChanged;

    public void SetMonitoredDeviceIssues(IReadOnlyList<NetworkHealthIssue> issues)
    {
        lock (_sync) _monitoredDeviceIssues = issues;
    }

    public NetworkHealthSnapshot Evaluate(NetworkHealthInput input)
    {
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!input.SourcesReady) return Publish(new(NetworkHealthState.Unavailable, [], now));
            List<Definition> rules = [];
            if (!input.RouterConnected)
                rules.Add(new("router.unreachable", NetworkHealthSeverity.Critical, "Router", "Router unavailable", "RouterPilot cannot currently reach the configured router.", "network"));
            else if (!input.InternetConnected)
                rules.Add(new("internet.unavailable", NetworkHealthSeverity.Critical, "Internet", "Internet unavailable", "WAN is available but Internet connectivity could not be confirmed.", "network"));
            else
            {
                if (input.AdGuardMaintenanceState == AdGuardMaintenanceState.Failed)
                    rules.Add(new("adguard.service_failed", NetworkHealthSeverity.Warning, "AdGuard", "AdGuard service unavailable", "AdGuard Home did not recover from its requested restart.", "protection"));
                if (input.RecentInternetOutageCount >= InternetInstabilityThreshold)
                {
                    string period = FormatObservedPeriod(input.RecentInternetObservedDuration);
                    rules.Add(new("internet.unstable", NetworkHealthSeverity.Warning, "Internet", "Connection unstable", $"Internet connectivity has dropped {input.RecentInternetOutageCount} times {period}.", "analytics", input.InternetInstabilityThresholdReachedAt?.UtcTicks.ToString()));
                }
                if (SustainedHigh(input.CpuHistory)) rules.Add(new("router.cpu_high", NetworkHealthSeverity.Warning, "Router", "High router CPU usage", "Router CPU usage has remained at or above 90% across recent samples.", "analytics"));
                if (SustainedHigh(input.MemoryHistory)) rules.Add(new("router.memory_high", NetworkHealthSeverity.Warning, "Router", "High router memory usage", "Router memory usage has remained at or above 90% across recent samples.", "analytics"));
                rules.AddRange(_monitoredDeviceIssues.Select(issue => new Definition(issue.Id, issue.Severity, issue.Subsystem, issue.Title, issue.Description, issue.NavigationTarget, issue.TimelineEpisodeKey)));
            }
            var next = new Dictionary<string, NetworkHealthIssue>(StringComparer.Ordinal);
            foreach (Definition rule in rules)
            {
                DateTimeOffset first = _active.TryGetValue(rule.Id, out NetworkHealthIssue? old) ? old.FirstDetectedAt : now;
                var issue = new NetworkHealthIssue(rule.Id, rule.Severity, rule.Subsystem, rule.Title, rule.Description, rule.NavigationTarget, first, now, rule.TimelineEpisodeKey);
                next.Add(issue.Id, issue);
                if (old is null) Record(issue, true);
            }
            foreach (NetworkHealthIssue old in _active.Values.Where(issue => !next.ContainsKey(issue.Id))) Record(old, false);
            _active.Clear(); foreach ((string id, NetworkHealthIssue issue) in next) _active.Add(id, issue);
            NetworkHealthState state = next.Values.Any(issue => issue.Severity == NetworkHealthSeverity.Critical) ? NetworkHealthState.Critical : next.Count > 0 ? NetworkHealthState.Attention : NetworkHealthState.Healthy;
            return Publish(new(state, next.Values.OrderByDescending(issue => issue.Severity).ThenBy(issue => issue.Id).ToList(), now));
        }
    }
    private NetworkHealthSnapshot Publish(NetworkHealthSnapshot snapshot) { _current = snapshot; SnapshotChanged?.Invoke(snapshot); return snapshot; }
    private void Record(NetworkHealthIssue issue, bool detected)
    {
        bool instability = issue.Id == "internet.unstable";
        bool monitoredDevice = issue.Id.StartsWith("client.monitor.", StringComparison.Ordinal);
        string episode = issue.TimelineEpisodeKey ?? issue.FirstDetectedAt.UtcTicks.ToString();
        _ = _timeline.AddAsync(new TimelineEvent
        {
            Category = TimelineCategory.Router,
            EventType = detected ? TimelineEventType.NetworkIssueDetected : TimelineEventType.NetworkIssueResolved,
            Title = monitoredDevice
                ? detected ? "Monitored device offline" : "Monitored device restored"
                : instability
                ? detected ? "Internet connection unstable" : "Internet connection stability restored"
                : detected ? "Network issue detected" : "Network issue resolved",
            Message = monitoredDevice
                ? detected ? issue.Description : $"{issue.Title} restored after {FormatDuration(DateTimeOffset.UtcNow - issue.FirstDetectedAt)} of observed offline monitoring."
                : instability
                ? detected ? issue.Description : "Recent Internet outage frequency has returned to normal."
                : detected ? issue.Title : issue.Title + " restored",
            Severity = detected ? issue.Severity == NetworkHealthSeverity.Critical ? TimelineSeverity.Error : TimelineSeverity.Warning : TimelineSeverity.Success,
            Source = "Network Health",
            DeduplicationKey = instability
                ? $"network-health:{issue.Id}:{(detected ? "detected" : "resolved")}:{episode}"
                : $"network-health:{issue.Id}:{(detected ? "detected" : "resolved")}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        });
    }
    private static bool SustainedHigh(IReadOnlyList<double> values) => values.Count >= 3 && values.TakeLast(3).All(value => value >= 90);
    private static string FormatDuration(TimeSpan duration) => duration < TimeSpan.FromMinutes(1) ? "less than a minute" : duration < TimeSpan.FromHours(1) ? $"{(int)duration.TotalMinutes} minutes" : $"{(int)duration.TotalHours}h {duration.Minutes}m";
    private static string FormatObservedPeriod(TimeSpan duration)
    {
        if (duration >= TimeSpan.FromMinutes(55)) return "in the last hour";
        if (duration >= TimeSpan.FromMinutes(1)) return $"in the last {(int)Math.Floor(duration.TotalMinutes)} minutes";
        return "recently";
    }
    private sealed record Definition(string Id, NetworkHealthSeverity Severity, string Subsystem, string Title, string Description, string NavigationTarget, string? TimelineEpisodeKey = null);
}
