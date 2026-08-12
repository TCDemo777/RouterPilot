namespace RouterPilot.Models;

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
}
