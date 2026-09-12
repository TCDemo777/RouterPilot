using System.Collections.Generic;

namespace RouterPilot.Models;

public enum VpnConfigurationHealth
{
    Unknown,
    Healthy,
    Unlinked
}

public enum VpnProfileInventoryState
{
    Unknown,
    Available,
    Unavailable
}

public enum VpnProfileActivityState
{
    Unknown,
    Active,
    Inactive
}

public enum VpnRoutingPolicyState
{
    Unknown,
    Available,
    Unavailable
}

/// <summary>Current presence reported by the shared, aggregate GL.iNet client inventory.</summary>
public enum VpnRoutingDevicePresence
{
    Unknown,
    Online,
    Offline
}

public enum VpnRoutingDeviceStatus
{
    Unknown,
    Offline,
    DeviceOnline,
    UsingVpn
}

/// <summary>Read-only router policy assignment. The MAC remains the stable identity; the display is presentation-only.</summary>
public sealed class VpnRoutingDeviceAssignment
{
    public string ClientIdentity { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public VpnRoutingDevicePresence Presence { get; init; } = VpnRoutingDevicePresence.Unknown;
    public VpnRoutingDeviceStatus Status { get; init; } = VpnRoutingDeviceStatus.Unknown;
    public string StatusDisplay => Status switch
    {
        VpnRoutingDeviceStatus.UsingVpn => "Using VPN",
        VpnRoutingDeviceStatus.DeviceOnline => "Device online",
        VpnRoutingDeviceStatus.Offline => "Offline",
        _ => "Unknown"
    };

    public static VpnRoutingDeviceAssignment WithTunnelConnection(
        VpnRoutingDeviceAssignment assignment, bool tunnelConnected) => new()
    {
        ClientIdentity = assignment.ClientIdentity,
        DisplayName = assignment.DisplayName,
        IsResolved = assignment.IsResolved,
        Presence = assignment.Presence,
        Status = assignment.Presence switch
        {
            VpnRoutingDevicePresence.Offline => VpnRoutingDeviceStatus.Offline,
            VpnRoutingDevicePresence.Online when tunnelConnected => VpnRoutingDeviceStatus.UsingVpn,
            VpnRoutingDevicePresence.Online => VpnRoutingDeviceStatus.DeviceOnline,
            _ => VpnRoutingDeviceStatus.Unknown
        }
    };
}

public sealed class VpnInventorySnapshot
{
    public IReadOnlyList<VpnTunnelInfo> Tunnels { get; init; } = [];
    public IReadOnlyList<VpnClientProfileInfo> Profiles { get; init; } = [];
    public VpnProfileInventoryState ProfileInventoryState { get; init; } = VpnProfileInventoryState.Unknown;
}

public sealed class VpnTunnelInfo
{
    public string Id { get; init; } = string.Empty;
    public int TunnelId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool KillSwitch { get; init; }
    public string StateDisplay => Enabled ? "Enabled" : "Disabled";
    public string KillSwitchDisplay => KillSwitch ? "Kill Switch: On" : "Kill Switch: Off";
    public string Protocol { get; init; } = "Unknown";
    public string InterfaceName { get; init; } = string.Empty;
    public IReadOnlyList<int> ProfileGroupIds { get; init; } = [];
    // The currently reported profile group for this tunnel, when the live
    // router status still identifies one. Group identity, not peer ID, is the
    // stable configuration correlation key.
    public int? SelectedProfileGroupId { get; init; }
    public bool SelectedProfileGroupExists { get; init; }
    public string ActiveProfileName { get; init; } = string.Empty;
    public string LinkedProfilesDisplay { get; init; } = string.Empty;
    public string ConfiguredProfileName { get; init; } = string.Empty;
    public string ConfiguredLocation { get; init; } = string.Empty;
    public bool HasConfiguredLocation => !string.IsNullOrWhiteSpace(ConfiguredLocation);
    public string? FromType { get; init; }
    public string? ToType { get; init; }
    public bool? Masquerade { get; init; }
    public bool? LocalAccess { get; init; }
    public string? ServicePolicy { get; init; }
    public VpnRoutingPolicyState RoutingPolicyState { get; init; } = VpnRoutingPolicyState.Unknown;
    public VpnInternetRoutingScope InternetRoutingScope { get; init; } = VpnInternetRoutingScope.Unknown;
    // Router-supplied identities are retained separately from friendly names.
    public IReadOnlyList<string> RoutingDeviceIdentities { get; init; } = [];
    public IReadOnlyList<VpnRoutingDeviceAssignment> RoutingDevices { get; init; } = [];
    public bool HasRoutingInformation => RoutingPolicyState == VpnRoutingPolicyState.Available;
    public string RoutingScopeDisplay => InternetRoutingScope switch
    {
        VpnInternetRoutingScope.DefaultInternet => "All devices / Default Internet route",
        VpnInternetRoutingScope.SelectedDevices => "Selected devices",
        VpnInternetRoutingScope.BypassOrExclusion => "Bypass or exclusion policy",
        _ => "Unavailable"
    };
    public string RoutingDevicesHeading => RoutingDevices.Count == 0 ? string.Empty : RoutingDevices.Count == 1 ? "Devices assigned to VPN" : $"Devices assigned to VPN ({RoutingDevices.Count})";
    public string RoutingEmptyDisplay => InternetRoutingScope == VpnInternetRoutingScope.SelectedDevices && RoutingDevices.Count == 0 ? "No devices currently assigned" : string.Empty;
    public bool HasRoutingDevices => RoutingDevices.Count > 0;
    public bool HasRoutingEmptyDisplay => !string.IsNullOrEmpty(RoutingEmptyDisplay);
    // -1 means the router did not associate a profile group with this tunnel.
    public int ServerConfigCount { get; init; } = -1;
    public VpnLiveStatusInfo? LiveStatus { get; init; }
    public VpnConfigurationHealth ConfigurationHealth { get; init; } = VpnConfigurationHealth.Unknown;
    public bool HasConfigurationAttention => ConfigurationHealth == VpnConfigurationHealth.Unlinked;
    public bool HasConnectionAttemptFailure { get; init; }
    public VpnTransitionIntent TransitionIntent { get; init; }
    public string ConfigurationAttentionTitle => "VPN profile is not linked to the Primary Tunnel.";
    public string ConfigurationAttentionDetail => "The VPN provider profile is available, but the router's Primary Tunnel is not currently associated with it.";
    public string ConnectionFailureTitle => "VPN connection did not complete";
    public string ConnectionFailureDetail => "The selected VPN server or location may be unavailable. You can retry or choose another location.";
    public string ConnectionState => HasConfigurationAttention ? "Configuration needs attention" : HasConnectionAttemptFailure ? "Connection did not complete" : TransitionIntent switch
    {
        VpnTransitionIntent.Connecting => "Connecting",
        VpnTransitionIntent.Disconnecting => "Disconnecting",
        _ => LiveStatus?.ConnectionState ?? (Enabled ? "Transitioning" : "Disconnected")
    };
    public string ActionDisplay => ConnectionState switch
    {
        "Connecting" => "Connecting…",
        "Disconnecting" => "Disconnecting…",
        _ when Enabled => "Disconnect",
        _ => "Connect"
    };
    public bool CanToggle => TransitionIntent == VpnTransitionIntent.None && (Enabled || CanConnect);
    public bool HasLiveConnection => LiveStatus?.IsConnected == true;
    public string LiveLocation => LiveStatus?.LocationDisplay ?? string.Empty;
    public string LiveServerName => LiveStatus?.ServerName ?? string.Empty;
    public string LiveVirtualIp => LiveStatus?.VirtualIpv4 ?? string.Empty;
    public string LiveEndpoint => LiveStatus?.EndpointDisplay ?? string.Empty;
    public string LiveDownload => LiveStatus?.DownloadDisplay ?? string.Empty;
    public string LiveUpload => LiveStatus?.UploadDisplay ?? string.Empty;
    public bool HasServerSelectionLimitation => !Enabled && (ServerConfigCount == 0 || ServerConfigCount > 1);
    public bool CanConnect => !HasConfigurationAttention && (Enabled || !HasServerSelectionLimitation);
    public string ServerSelectionLimitationText => ServerConfigCount == 0
        ? "No VPN server is configured for this profile."
        : "Multiple VPN servers configured";
    public string ServerSelectionLimitationDetail => ServerConfigCount > 1
        ? "RouterPilot currently supports connecting VPN profiles with a single allocated server. Select or configure a single server in the GL.iNet interface, then refresh RouterPilot."
        : string.Empty;
}

public sealed class VpnLiveStatusInfo
{
    public int TunnelId { get; init; }
    public bool Enabled { get; init; }
    public int Status { get; init; }
    public string Protocol { get; init; } = "Unknown";
    public long? RxBytes { get; init; }
    public long? TxBytes { get; init; }
    public string? PeerName { get; init; }
    public IReadOnlyList<string> Domains { get; init; } = [];
    public int? GroupId { get; init; }
    public int? PeerId { get; init; }
    public string? Via { get; init; }
    public int? Port { get; init; }
    public string? TunnelName { get; init; }
    public string? VirtualIpv4 { get; init; }
    public string? LocationDisplay { get; init; }
    public string? ServerName { get; init; }
    public bool IsConnected => Status == 1;
    public string ConnectionState => IsConnected ? "Connected" : Enabled ? "Transitioning" : "Disconnected";
    public string EndpointDisplay => Domains.Count == 0 ? string.Empty : string.Join(", ", Domains) + (Port is > 0 ? $" : {Port}" : string.Empty);
    public string DownloadDisplay => FormatBytes(RxBytes);
    public string UploadDisplay => FormatBytes(TxBytes);
    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = Math.Max(0, bytes.Value); int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

public sealed class VpnConfigMetadata
{
    public string Protocol { get; init; } = "Unknown";
    public int GroupId { get; init; }
    public int PeerId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public bool IsProvider { get; init; }
}

public sealed class VpnClientProfileInfo
{
    public int GroupId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = "Unknown";
    public bool IsUsedByTunnel { get; init; }
    public IReadOnlyList<int> TunnelIds { get; init; } = [];
    public string UsedByDisplay { get; init; } = string.Empty;
    public int ServerConfigCount { get; init; }
    // Current router configuration metadata, not a durable peer selection.
    public int? CurrentPeerId { get; init; }
    public string CurrentLocation { get; init; } = string.Empty;
    // Profile inventory and active tunnel state are intentionally separate.
    // A configured but inactive profile remains a real profile.
    public VpnProfileActivityState ActivityState { get; init; } = VpnProfileActivityState.Unknown;
    public string ActivityStateDisplay => ActivityState switch
    {
        VpnProfileActivityState.Active => "Active",
        VpnProfileActivityState.Inactive => "Inactive",
        _ => "State unavailable"
    };
}

public sealed class VpnOperationResult
{
    public bool Success { get; init; }
    public string FailureCategory { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int TunnelId { get; init; }
    public bool RollbackAttempted { get; init; }
    public bool RollbackVerified { get; init; }
}
