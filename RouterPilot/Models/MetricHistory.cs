namespace RouterPilot.Models;

public enum MetricKind { CpuPercent, MemoryPercent, WanDownloadMbps, WanUploadMbps }
public enum InternetAvailabilityState { Online, Offline }

public sealed class MetricSample
{
    public DateTimeOffset Timestamp { get; init; }
    public MetricKind Metric { get; init; }
    public double Value { get; init; }
}

public sealed class InternetAvailabilityEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public InternetAvailabilityState State { get; init; }
}

public sealed class InternetReliabilitySummary
{
    public bool HasSufficientHistory { get; init; }
    public bool? IsOnline { get; init; }
    public TimeSpan ObservedDuration { get; init; }
    public TimeSpan OnlineDuration { get; init; }
    public TimeSpan OfflineDuration { get; init; }
    public int OutageCount { get; init; }
    public TimeSpan LongestOutage { get; init; }
    public DateTimeOffset? LastOutageStartedAt { get; init; }
    public TimeSpan? LastOutageDuration { get; init; }
    public DateTimeOffset? CurrentStateSince { get; init; }
    public double AvailabilityPercent => ObservedDuration <= TimeSpan.Zero ? 0 : OnlineDuration.TotalSeconds / ObservedDuration.TotalSeconds * 100;
}
