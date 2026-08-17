using System;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Correlates supplied application state only; it performs no router or network I/O.</summary>
public sealed class NetworkHealthService : INetworkHealthService
{
    private readonly TimelineService _timeline;
    private readonly Dictionary<string, NetworkHealthIssue> _active = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private NetworkHealthSnapshot _current = NetworkHealthSnapshot.Loading;
    public NetworkHealthService(TimelineService timeline) => _timeline = timeline;
    public NetworkHealthSnapshot Current { get { lock (_sync) return _current; } }
    public event Action<NetworkHealthSnapshot>? SnapshotChanged;

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
                if (SustainedHigh(input.CpuHistory)) rules.Add(new("router.cpu_high", NetworkHealthSeverity.Warning, "Router", "High router CPU usage", "Router CPU usage has remained at or above 90% across recent samples.", "analytics"));
                if (SustainedHigh(input.MemoryHistory)) rules.Add(new("router.memory_high", NetworkHealthSeverity.Warning, "Router", "High router memory usage", "Router memory usage has remained at or above 90% across recent samples.", "analytics"));
            }
            var next = new Dictionary<string, NetworkHealthIssue>(StringComparer.Ordinal);
            foreach (Definition rule in rules)
            {
                DateTimeOffset first = _active.TryGetValue(rule.Id, out NetworkHealthIssue? old) ? old.FirstDetectedAt : now;
                var issue = new NetworkHealthIssue(rule.Id, rule.Severity, rule.Subsystem, rule.Title, rule.Description, rule.NavigationTarget, first, now);
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
    private void Record(NetworkHealthIssue issue, bool detected) => _ = _timeline.AddAsync(new TimelineEvent { Category = TimelineCategory.Router, EventType = detected ? TimelineEventType.NetworkIssueDetected : TimelineEventType.NetworkIssueResolved, Title = detected ? "Network issue detected" : "Network issue resolved", Message = detected ? issue.Title : issue.Title + " restored", Severity = detected ? issue.Severity == NetworkHealthSeverity.Critical ? TimelineSeverity.Error : TimelineSeverity.Warning : TimelineSeverity.Success, Source = "Network Health", DeduplicationKey = $"network-health:{issue.Id}:{(detected ? "detected" : "resolved")}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" });
    private static bool SustainedHigh(IReadOnlyList<double> values) => values.Count >= 3 && values.TakeLast(3).All(value => value >= 90);
    private sealed record Definition(string Id, NetworkHealthSeverity Severity, string Subsystem, string Title, string Description, string NavigationTarget);
}
