using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace RouterPilot.Models;

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public enum NotificationCategory
{
    System,
    Router,
    Internet,
    AdGuard,
    Network,
    Device
}

public enum NotificationEventType
{
    General,
    RouterOffline,
    RouterRestored,
    NewDeviceDetected,
    ProtectionEnabled,
    ProtectionDisabled,
    MaintenanceSucceeded,
    MaintenanceFailed,
    DiagnosticsCompleted,
    ScheduleSucceeded,
    ScheduleFailed,
    FirmwareUpdateAvailable,
    MonitoredDeviceOffline,
    MonitoredDeviceRestored
}

public partial class AppNotification : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public NotificationSeverity Severity { get; init; }

    public NotificationCategory Category { get; init; }

    public NotificationEventType EventType { get; init; } = NotificationEventType.General;

    [ObservableProperty]
    private bool isRead;

    public string? ActionTarget { get; init; }

    public string? DeduplicationKey { get; init; }

    [JsonIgnore]
    public string TimestampDisplay
    {
        get
        {
            TimeSpan age = DateTimeOffset.Now - Timestamp;
            string relative = age.TotalMinutes < 1
                ? "Just now"
                : age.TotalHours < 1
                    ? $"{Math.Max(1, (int)age.TotalMinutes)}m ago"
                    : age.TotalDays < 1
                        ? $"{(int)age.TotalHours}h ago"
                        : age.TotalDays < 7
                            ? $"{(int)age.TotalDays}d ago"
                            : Timestamp.ToLocalTime().ToString("dd MMM yyyy");

            return $"{relative} · {Timestamp.ToLocalTime():dd MMM yyyy HH:mm}";
        }
    }
}
