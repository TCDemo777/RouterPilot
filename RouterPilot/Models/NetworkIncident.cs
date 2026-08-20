using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RouterPilot.Models;

/// <summary>Presentation-only Timeline item. It is never persisted as history.</summary>
public abstract class TimelinePresentationItem : ObservableObject
{
    public abstract DateTimeOffset Timestamp { get; }
    public abstract TimelineSeverity Severity { get; }
    public abstract string SearchText { get; }
    public abstract IReadOnlyList<TimelineEvent> SourceEvents { get; }
}

public sealed class TimelineEventItem(TimelineEvent eventItem) : TimelinePresentationItem
{
    public TimelineEvent Event { get; } = eventItem;
    public override DateTimeOffset Timestamp => Event.Timestamp;
    public override TimelineSeverity Severity => Event.Severity;
    public override string SearchText => string.Join(' ', Event.Title, Event.Message, Event.Category, Event.EventType, Event.Source ?? string.Empty);
    public override IReadOnlyList<TimelineEvent> SourceEvents => [Event];
}

public sealed partial class NetworkIncident : TimelinePresentationItem
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public required IReadOnlyList<TimelineEvent> Events { get; init; }
    public bool IsOngoing => EndedUtc is null;
    public TimeSpan? Duration => EndedUtc - StartedUtc;
    public override DateTimeOffset Timestamp => StartedUtc;
    public override TimelineSeverity Severity => IsOngoing ? TimelineSeverity.Warning : TimelineSeverity.Information;
    public string Title => "Network interruption";
    public string DurationDisplay => IsOngoing ? "Ongoing" : FormatDuration(Duration!.Value);
    public string TimeRangeDisplay => IsOngoing
        ? $"Lost {StartedUtc.ToLocalTime():HH:mm}"
        : $"{StartedUtc.ToLocalTime():HH:mm} – {EndedUtc!.Value.ToLocalTime():HH:mm}";
    public string TimestampDisplay => StartedUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public string Summary
    {
        get
        {
            if (IsOngoing) return $"Internet connectivity was lost at {StartedUtc.ToLocalTime():HH:mm}.";
            var clauses = new List<string> { $"Internet connectivity was lost and restored after {FormatDuration(Duration!.Value)}." };
            if (Events.Any(item => item.EventType == TimelineEventType.VpnDisconnected)) clauses.Add("VPN disconnected during the interruption.");
            if (Events.Any(item => item.EventType == TimelineEventType.VpnConnected)) clauses.Add("VPN reconnected after connectivity was restored.");
            if (Events.Any(item => item.EventType == TimelineEventType.PublicIpChanged)) clauses.Add("Public IP changed after connectivity returned.");
            return string.Join(' ', clauses);
        }
    }
    public override string SearchText => string.Join(' ', Title, Summary, Events.Select(item => string.Join(' ', item.Title, item.Message, item.EventType, item.Source ?? string.Empty)));
    public override IReadOnlyList<TimelineEvent> SourceEvents => Events;
    [ObservableProperty] private bool isExpanded;

    private static string FormatDuration(TimeSpan duration) => duration < TimeSpan.FromMinutes(1)
        ? $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} sec"
        : duration < TimeSpan.FromHours(1)
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s"
            : $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
}
