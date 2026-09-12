namespace RouterPilot.Models;

/// <summary>Authoritative, optional router policy data read as one VPN refresh input.</summary>
public sealed class VpnRoutingPolicySnapshot
{
    public VpnRoutingPolicyState State { get; init; } = VpnRoutingPolicyState.Unknown;
    public IReadOnlyList<VpnTunnelRoutingPolicy> Tunnels { get; init; } = [];
}

public sealed class VpnTunnelRoutingPolicy
{
    public int TunnelId { get; init; }
    public int? GroupId { get; init; }
    public int? PeerId { get; init; }
    public bool? Enabled { get; init; }
    public VpnInternetRoutingScope Scope { get; init; } = VpnInternetRoutingScope.Unknown;
    public IReadOnlyList<string> DeviceIdentities { get; init; } = [];
}
