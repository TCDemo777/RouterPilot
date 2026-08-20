using System.Collections.Generic;

namespace RouterPilot.Models;

#if DEBUG
/// <summary>
/// DEBUG-only diagnostic projection of safe identifiers returned by existing
/// VPN reads. It deliberately contains no raw configuration or credentials.
/// </summary>
public sealed class VpnStateCaptureSnapshot
{
    public IReadOnlyList<VpnProfileGroupCapture> ProfileGroups { get; init; } = [];
    public IReadOnlyList<VpnTunnelInfo> Tunnels { get; init; } = [];
}

public sealed class VpnProfileGroupCapture
{
    public string Protocol { get; init; } = "Unknown";
    public int GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public bool IsProvider { get; init; }
    public IReadOnlyList<VpnPeerCapture> Peers { get; init; } = [];
}

public sealed class VpnPeerCapture
{
    public int PeerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public bool IsProvider { get; init; }
}
#endif
