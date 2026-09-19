using System.Collections.Generic;
using RouterPilot.Services;

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

/// <summary>
/// Deliberately narrow, manual PIA lifecycle observation.  This projection is
/// safe to copy: it contains only the explicitly allowlisted identifiers,
/// display metadata, and tunnel structure counts.
/// </summary>
public sealed class PiaManualStateSnapshot
{
    public bool ConfigReadSucceeded { get; init; }
    public IReadOnlyList<PiaManualConfigSnapshot> Configs { get; init; } = [];
    public bool TunnelReadSucceeded { get; init; }
    public RouterManager.VpnTunnelStructuralSnapshot? Tunnel { get; init; }
}

public sealed class PiaManualConfigSnapshot
{
    public int ConfigId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
}
#endif
