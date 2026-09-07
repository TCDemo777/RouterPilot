namespace RouterPilot.Models;

public enum TailscaleUpdateStatus
{
    NotChecked,
    Checking,
    UpdateAvailable,
    UpToDate,
    Unavailable
}

/// <summary>Router-authoritative Tailscale state paired with the official stable-release check.</summary>
public sealed record TailscaleMaintenanceSnapshot(
    string? InstalledVersion,
    string? LatestStableVersion,
    TailscaleState ServiceState,
    TailscaleUpdateStatus UpdateStatus,
    string? ErrorCategory,
    DateTimeOffset? CheckedAt)
{
    public static readonly TailscaleMaintenanceSnapshot Empty = new(
        null, null, TailscaleState.Unavailable, TailscaleUpdateStatus.NotChecked, null, null);
}

/// <summary>Sanitized, bounded read-only evidence for the community updater's restore preconditions.</summary>
public sealed record TailscaleRecoveryState(
    bool? CommunityUpdaterDetected,
    bool? ConfigurationBackupDetected,
    long? LatestConfigurationBackupSizeBytes,
    bool? FirmwareTailscaleBinaryAvailable,
    bool? FirmwareTailscaledBinaryAvailable,
    bool? CurrentTailscaleBinaryAvailable,
    bool? CurrentTailscaledBinaryAvailable,
    bool? GlTailscaleScriptAvailable,
    bool? StatefulFilteringAdjustmentDetected,
    bool? ServiceRunning)
{
    public static readonly TailscaleRecoveryState Unknown = new(null, null, null, null, null, null, null, null, null, null);
    public bool? RestoreAvailable => FirmwareTailscaleBinaryAvailable is true && FirmwareTailscaledBinaryAvailable is true;
}
