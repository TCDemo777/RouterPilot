namespace RouterPilot.Models;

public sealed record RouterStateSnapshot(
    int SchemaVersion,
    string SnapshotId,
    DateTimeOffset CapturedAt,
    string FriendlyName,
    string ProfileId,
    string RouterModel,
    string FirmwareVersion,
    RouterStateSystem System,
    RouterStateNetwork Network,
    RouterStateTraffic Traffic,
    RouterStateServices Services);

public sealed record RouterStateSystem(string Kernel, string Architecture, string NetworkMode);

public sealed record RouterStateNetwork(
    bool? GuestEnabled,
    bool? IoTEnabled,
    bool? GuestIgmpSnooping,
    bool? IoTIgmpSnooping,
    bool? NatMasquerade,
    bool? NatMasqueradeIpv6);

public sealed record RouterStateTraffic(
    bool? SqmEnabled,
    string SqmQueueDiscipline,
    string SqmDownload,
    string SqmUpload,
    bool? DpiConfigured);

public sealed record RouterStateServices(
    bool? WebDavEnabled,
    bool? DlnaRunning,
    bool? ZeroTierInstalled,
    bool? ZeroTierEnabled);

public sealed record RouterStateChange(
    string Category,
    string Field,
    string ChangeType,
    string? OldValue,
    string? NewValue,
    string Importance,
    string Destination);

public sealed record RouterStateComparisonJournalEntry(
    int SchemaVersion,
    string JournalId,
    DateTimeOffset ComparedAt,
    string ProfileId,
    string SnapshotId,
    string SnapshotName,
    int ChangeCount,
    int NotableCount,
    int InformationCount,
    int UnavailableFieldCount);
