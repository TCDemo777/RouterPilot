using System;
using System.Collections.Generic;

namespace RouterPilot.Models;

public sealed class DataStatisticsStatus
{
    public bool? FlowStatisticsEnabled { get; init; }
    public string DpiStatus { get; init; } = string.Empty;
    public string DpiLibraryVersion { get; init; } = string.Empty;
    public string DpiLibraryUpdateTime { get; init; } = string.Empty;

    public bool HasFlowStatisticsState => FlowStatisticsEnabled.HasValue;
    public bool IsDpiActive => string.Equals(DpiStatus, "1", StringComparison.Ordinal);
}

public sealed class DataStatisticsSnapshot
{
    public long? MaxBytes { get; init; }
    public long? PeriodSeconds { get; init; }
    public IReadOnlyList<ApplicationTrafficStat> TopApps { get; init; } = [];
}

public sealed class ApplicationTrafficStat
{
    public string ApplicationId { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    public long UploadBytes { get; init; }
    public long DownloadBytes { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<ApplicationTrafficPoint> TimeSeries { get; init; } = [];

    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : !string.IsNullOrWhiteSpace(ApplicationName)
            ? ApplicationName
            : "Unlabelled application";
}

public sealed class ApplicationTrafficPoint
{
    public DateTimeOffset? StartTimeUtc { get; init; }
    public DateTimeOffset? EndTimeUtc { get; init; }
    public long UploadBytes { get; init; }
    public long DownloadBytes { get; init; }
    public long TotalBytes { get; init; }
}

public sealed class FullApplicationStatisticsSnapshot
{
    public string Period { get; init; } = string.Empty;
    public ApplicationTrafficRow? Aggregate { get; init; }
    public IReadOnlyList<ApplicationTrafficRow> Applications { get; init; } = [];
}

public sealed class ApplicationTrafficRow
{
    public string ApplicationId { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    public long UploadBytes { get; init; }
    public long DownloadBytes { get; init; }
    public long TotalBytes { get; init; }
    public long? PacketCount { get; init; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : !string.IsNullOrWhiteSpace(ApplicationName)
            ? ApplicationName
            : "Unlabelled application";
}

public enum FullApplicationStatisticsAvailability
{
    Available,
    Unsupported,
    TemporarilyUnavailable
}

public sealed class FullApplicationStatisticsReadResult
{
    public FullApplicationStatisticsAvailability Availability { get; init; }
    public FullApplicationStatisticsSnapshot? Snapshot { get; init; }
}

public enum DataStatisticsAvailability
{
    Available,
    Disabled,
    DpiInactive,
    Unsupported,
    TemporarilyUnavailable
}

public sealed class DataStatisticsReadResult
{
    public DataStatisticsAvailability Availability { get; init; }
    public DataStatisticsStatus? Status { get; init; }
    public DataStatisticsSnapshot? Snapshot { get; init; }
}
