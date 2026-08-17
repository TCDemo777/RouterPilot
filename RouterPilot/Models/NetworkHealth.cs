using System;
using System.Collections.Generic;
using System.Linq;

namespace RouterPilot.Models;

public enum NetworkHealthState { Healthy, Attention, Critical, Unavailable }
public enum NetworkHealthSeverity { Info, Warning, Critical }

public sealed record NetworkHealthIssue(string Id, NetworkHealthSeverity Severity, string Subsystem, string Title, string Description, string NavigationTarget, DateTimeOffset FirstDetectedAt, DateTimeOffset LastObservedAt)
{
    public string Domain => Subsystem.Equals("Router", StringComparison.OrdinalIgnoreCase) ? "Router" : Subsystem.Equals("System", StringComparison.OrdinalIgnoreCase) ? "System" : "Network";
}

public sealed record NetworkHealthSnapshot(NetworkHealthState OverallState, IReadOnlyList<NetworkHealthIssue> Issues, DateTimeOffset UpdatedAt)
{
    public static NetworkHealthSnapshot Loading { get; } = new(NetworkHealthState.Unavailable, [], DateTimeOffset.MinValue);
    public int ActiveIssueCount => Issues.Count;
    public IReadOnlyList<NetworkHealthIssue> PrimaryIssues => Issues.Take(2).ToList();
    public IReadOnlyList<NetworkHealthIssue> RouterIssues => Issues.Where(issue => issue.Domain == "Router").Take(2).ToList();
    public IReadOnlyList<NetworkHealthIssue> NetworkIssues => Issues.Where(issue => issue.Domain == "Network").Take(2).ToList();
    public NetworkHealthIssue? PrimaryNetworkIssue => NetworkIssues.FirstOrDefault();
    public bool HasPrimaryNetworkIssue => PrimaryNetworkIssue is not null && !string.IsNullOrWhiteSpace(PrimaryNetworkIssue.NavigationTarget);
    public string NetworkIssueCountSummary => NetworkIssues.Count > 1 ? $"{NetworkIssues.Count} issues detected" : string.Empty;
    public IReadOnlyList<NetworkHealthIssue> SystemIssues => Issues.Where(issue => issue.Domain == "System").Take(2).ToList();
    public string Summary => OverallState switch
    {
        NetworkHealthState.Healthy => "No issues detected",
        NetworkHealthState.Unavailable => "Waiting for current network status…",
        _ => $"{ActiveIssueCount} issue{(ActiveIssueCount == 1 ? string.Empty : "s")} detected"
    };
}

public sealed record NetworkHealthInput(bool SourcesReady, bool RouterConnected, bool InternetConnected, AdGuardMaintenanceState AdGuardMaintenanceState, IReadOnlyList<double> CpuHistory, IReadOnlyList<double> MemoryHistory);
