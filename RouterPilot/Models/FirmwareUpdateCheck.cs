namespace RouterPilot.Models;

public enum FirmwareUpdateCheckStatus
{
    Pending,
    UpToDate,
    UpdateAvailable,
    NotAvailable,
    Error
}

public sealed class FirmwareUpdateCheck
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseChannel { get; set; } = string.Empty;
    public DateTimeOffset? ReleaseDate { get; set; }
    public string? ReleaseNotesUrl { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? DownloadUrl { get; set; }
    public FirmwareUpdateCheckStatus Status { get; set; } = FirmwareUpdateCheckStatus.NotAvailable;
    public DateTimeOffset? LastChecked { get; set; }
    public string? ErrorCategory { get; set; }
}
