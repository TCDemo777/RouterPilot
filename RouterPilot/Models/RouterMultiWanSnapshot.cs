using System;
using System.Collections.Generic;

namespace RouterPilot.Models;

public enum RouterWanRuntimeState { Online, Offline, Unknown }
public enum RouterWanConnectionType { Ethernet, Repeater, Tethering, Cellular, Unknown }
public enum RouterMultiWanMode { SingleWan, Failover, LoadBalancing, Unknown }

public sealed record RouterWanPathSnapshot(
    string Id, string Name, RouterWanConnectionType ConnectionType,
    string InterfaceName, string DeviceName, bool? Configured, bool? Enabled,
    RouterWanRuntimeState RuntimeState, bool? Healthy, bool IsDefault, bool IsActive,
    string? Gateway, string? IPv4Address, string? IPv6Address,
    int? Metric, int? Priority, int? Weight);

public sealed record RouterMultiWanSnapshot(
    RouterCapabilityState CapabilityState, bool? Enabled, RouterMultiWanMode Mode,
    string? ActivePathId, string? DefaultPathId,
    IReadOnlyList<RouterWanPathSnapshot> WanPaths, DateTimeOffset CapturedAt)
{
    public static RouterMultiWanSnapshot Unknown { get; } = new(
        RouterCapabilityState.Unknown, null, RouterMultiWanMode.Unknown, null, null,
        Array.Empty<RouterWanPathSnapshot>(), DateTimeOffset.UtcNow);
}
