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
    bool? DpiRunning,
    bool? ZeroTierConfigured,
    bool? ZeroTierEnabled,
    bool? WebDavEnabled,
    bool? WebDavWanAccess,
    bool? DlnaRunning,
    DateTimeOffset CapturedAt)
{
    public static RouterAdvancedSnapshot Unknown => new("Unknown", null, null, null, null, null, null, null, "Unknown", null, null, null, null, null, null, DateTimeOffset.UtcNow);
}
