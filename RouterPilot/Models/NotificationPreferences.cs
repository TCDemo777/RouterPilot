using System;
using System.Collections.Generic;

namespace RouterPilot.Models;

public sealed class NotificationPreferences
{
    public bool Enabled { get; set; } = true;
    public bool NotificationCentreEnabled { get; set; } = true;
    public bool WindowsToastsEnabled { get; set; } = true;
    public bool MonitoredDeviceAvailabilityEnabled { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public TimeOnly QuietHoursStart { get; set; } = new(22, 0);
    public TimeOnly QuietHoursEnd { get; set; } = new(7, 0);
    public Dictionary<NotificationEventType, bool> Events { get; set; } = new();

    public bool IsEnabled(NotificationEventType eventType) => eventType is NotificationEventType.MonitoredDeviceOffline or NotificationEventType.MonitoredDeviceRestored
        ? MonitoredDeviceAvailabilityEnabled : !Events.TryGetValue(eventType, out bool enabled) || enabled;

    public bool IsQuietHours(DateTimeOffset now) => QuietHoursEnabled &&
        (QuietHoursStart <= QuietHoursEnd
            ? now.TimeOfDay >= QuietHoursStart.ToTimeSpan() && now.TimeOfDay < QuietHoursEnd.ToTimeSpan()
            : now.TimeOfDay >= QuietHoursStart.ToTimeSpan() || now.TimeOfDay < QuietHoursEnd.ToTimeSpan());
}
