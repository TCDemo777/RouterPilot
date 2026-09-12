namespace RouterPilot.Models;

/// <summary>
/// The scope of traffic routed through an active VPN tunnel. Unknown is the
/// safe default when the router has not supplied an authoritative policy read.
/// </summary>
public enum VpnInternetRoutingScope
{
    Unknown,
    DefaultInternet,
    SelectedDevices,
    BypassOrExclusion,
    // Retained for existing callers compiled against the first conservative
    // routing presentation. New code must state the policy explicitly.
    ClientOrPolicy = SelectedDevices
}

/// <summary>Safe, read-only application summary of the configured client VPN state.</summary>
public sealed class VpnSummaryState
{
    public bool IsAvailable { get; init; }
    public bool IsConfigured { get; init; }
    public string State { get; init; } = "Unavailable";
    public string Protocol { get; init; } = string.Empty;
    public string TunnelName { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string VirtualIp { get; init; } = string.Empty;
    public VpnInternetRoutingScope InternetRoutingScope { get; init; } = VpnInternetRoutingScope.Unknown;
}

/// <summary>Shared conservative decision for Internet-route presentation.</summary>
public static class VpnInternetRoutePresentation
{
    public static bool UsesDefaultVpnRoute(VpnSummaryState summary) =>
        string.Equals(summary.State, "Connected", StringComparison.Ordinal) &&
        summary.InternetRoutingScope == VpnInternetRoutingScope.DefaultInternet;
}
