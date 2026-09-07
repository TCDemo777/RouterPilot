namespace RouterPilot.Models;

public enum AdGuardHomeUpdateStatus
{
    NotChecked,
    Checking,
    UpdateAvailable,
    UpToDate,
    Unavailable
}

public sealed record AdGuardHomeMaintenanceSnapshot(
    string? InstalledVersion,
    string? LatestStableVersion,
    bool? ServiceRunning,
    AdGuardHomeUpdateStatus UpdateStatus,
    string? ErrorCategory,
    DateTimeOffset? CheckedAt)
{
    public static readonly AdGuardHomeMaintenanceSnapshot Empty = new(
        null, null, null, AdGuardHomeUpdateStatus.NotChecked, null, null);
}

public sealed record AdGuardHomeRecoveryState(
    bool? CommunityUpdaterDetected,
    bool? BackupDetected,
    long? BackupSizeBytes,
    bool? ArchiveReadable,
    bool? ArchiveHasOriginalBinary,
    bool? ArchiveHasOriginalConfiguration,
    bool? ArchivePathsSafe,
    bool? UpdaterHelperDetected,
    bool? RcLocalIntegrationDetected,
    bool? SysupgradeIntegrationDetected,
    bool? AdGuardBinaryDetected,
    bool? AdGuardConfigurationDetected,
    bool? ServiceRunning)
{
    public static readonly AdGuardHomeRecoveryState Unknown = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null);
}
