namespace RouterPilot.Models;

public sealed record RouterAdvancedSnapshot(
    string NetworkMode,
    bool? IoTEnabled,
    bool? GuestEnabled,
    bool? GuestIgmpSnooping,
    bool? IoTIgmpSnooping,
    bool? NatMasquerade,
    bool? NatMasqueradeIpv6,
    bool? SqmEnabled,
    string SqmQueueDiscipline,
    string SqmDownload,
    string SqmUpload,
    bool? DpiConfigured,
    bool? DpiRunning,
    bool? ZeroTierInstalled,
    bool? ZeroTierEnabled,
    bool? WebDavEnabled,
    bool? WebDavWanAccess,
    bool? WebDavRuntime,
    bool? DlnaConfigured,
    bool? DlnaRunning,
    DateTimeOffset CapturedAt)
{
    public static RouterAdvancedSnapshot Unknown => new(
        "Unknown", null, null, null, null, null, null,
        null, "Unknown", "Unknown", "Unknown",
        null, null, null, null, null, null, null, null, null,
        DateTimeOffset.UtcNow);
}
