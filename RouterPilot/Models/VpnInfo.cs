using System.Collections.Generic;

namespace RouterPilot.Models;

public enum VpnConfigurationHealth
{
    Unknown,
    Healthy,
    Unlinked
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
    // -1 means the router did not associate a profile group with this tunnel.
    public int ServerConfigCount { get; init; } = -1;
    public VpnLiveStatusInfo? LiveStatus { get; init; }
    public VpnConfigurationHealth ConfigurationHealth { get; init; } = VpnConfigurationHealth.Unknown;
    public bool HasConfigurationAttention => ConfigurationHealth == VpnConfigurationHealth.Unlinked;
    public bool HasConnectionAttemptFailure { get; init; }
    public string ConfigurationAttentionTitle => "VPN profile is not linked to the Primary Tunnel.";
    public string ConfigurationAttentionDetail => "The VPN provider profile is available, but the router's Primary Tunnel is not currently associated with it.";
    public string ConnectionFailureTitle => "VPN connection did not complete";
    public string ConnectionFailureDetail => "The selected VPN server or location may be unavailable. You can retry or choose another location.";
    public string ConnectionState => HasConfigurationAttention ? "Configuration needs attention" : HasConnectionAttemptFailure ? "Connection did not complete" : LiveStatus?.ConnectionState ?? (Enabled ? "Connecting" : "Disconnected");
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
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
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
    public string ConnectionState => IsConnected ? "Connected" : Enabled ? "Connecting" : "Disconnected";
    public string EndpointDisplay => Domains.Count == 0 ? string.Empty : string.Join(", ", Domains) + (Port is > 0 ? $" : {Port}" : string.Empty);
    public string DownloadDisplay => FormatBytes(RxBytes);
    public string UploadDisplay => FormatBytes(TxBytes);
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = Math.Max(0, bytes); int unit = 0;
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
