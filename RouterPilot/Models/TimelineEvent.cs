using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RouterPilot.Models;

public enum TimelineCategory
{
    Router,
    Clients,
    AdGuard,
    Maintenance,
    Diagnostics,
    Backup,
    Firmware,
    Security,
    Schedules
}

public enum TimelineSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public enum TimelineEventType
{
    MaintenanceCompleted,
    MaintenanceFailed,
    BackupCreated,
    RestoreCompleted,
    DiagnosticsCompleted,
    DiagnosticsBackupCreated,
    DiagnosticsFailed,
    FirmwareUpdateAvailable,
    FirmwareCheckFailed
    ,
    FirmwareChanged,
    FirmwareUpdateCompleted
}

/// <summary>Safe, user-facing cross-application activity record.</summary>
public partial class TimelineEvent : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public TimelineCategory Category { get; init; }
    public TimelineEventType EventType { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public TimelineSeverity Severity { get; init; } = TimelineSeverity.Information;
    public string? Source { get; init; }
    public string? CorrelationId { get; init; }
    public string? RelatedEntityId { get; init; }
    public string? DeduplicationKey { get; init; }

    [ObservableProperty]
    private bool isRead;

    [JsonIgnore]
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("dd MMM yyyy HH:mm");
}
