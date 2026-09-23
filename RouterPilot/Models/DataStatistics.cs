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

public sealed class ApplicationTrafficDetail
{
    public string ApplicationId { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string Identifier { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string LogoUrl { get; init; } = string.Empty;
    public bool? IsBlocked { get; init; }
    public long? PeriodSeconds { get; init; }
    public long TotalUploadBytes { get; init; }
    public long TotalDownloadBytes { get; init; }
    public DateTimeOffset? MetadataStartUtc { get; init; }
    public DateTimeOffset? MetadataEndUtc { get; init; }
    public IReadOnlyList<ApplicationDeviceTraffic> Devices { get; init; } = [];
    public IReadOnlyList<ApplicationTrafficPoint> TimeSeries { get; init; } = [];

    public long TotalBytes => TotalDownloadBytes > long.MaxValue - TotalUploadBytes
        ? long.MaxValue
        : TotalDownloadBytes + TotalUploadBytes;
}

public sealed class ApplicationDeviceTraffic
{
    public string MacAddress { get; init; } = string.Empty;
    public string NormalizedMac { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public long UploadBytes { get; init; }
    public long DownloadBytes { get; init; }
    public long TotalBytes { get; init; }
    public long? PacketCount { get; init; }
    public long? RecordCount { get; init; }
    public DateTimeOffset? LastActiveUtc { get; init; }
    public string LastActiveRelative { get; init; } = string.Empty;
    public bool CanViewClient => ClientIdentity.IsMacKey(NormalizedMac);
    public string DisplayName { get; set; } = string.Empty;
    public string LastActiveDisplay => LastActiveUtc is { } time
        ? time.LocalDateTime.ToString("g")
        : LastActiveRelative;
}

public sealed class ApplicationTrafficDeviceRow
{
    public string StableMacKey { get; init; } = string.Empty;
    public string MacDisplay { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = "Unknown device";
    public string CurrentIp { get; init; } = "—";
    public string RouterHostname { get; init; } = string.Empty;
    public long DownloadBytes { get; init; }
    public long UploadBytes { get; init; }
    public long TotalBytes { get; init; }
    public long? PacketCount { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public string LastSeenFallback { get; init; } = string.Empty;
    public bool CanViewClient => ClientIdentity.IsMacKey(StableMacKey);
    public string LastSeenDisplay => LastSeenUtc is { } time ? time.LocalDateTime.ToString("g") : LastSeenFallback;
}

public enum ApplicationProtectionMutationAvailability
{
    Succeeded,
    Busy,
    InvalidApplication,
    Unsupported,
    WriteFailed,
    VerificationFailed
}

public sealed class ApplicationProtectionMutationResult
{
    public ApplicationProtectionMutationAvailability Availability { get; init; }
    public ApplicationTrafficDetail? VerifiedDetail { get; init; }
}

public static class ApplicationProtectionVerification
{
    public static bool Matches(ApplicationTrafficDetail? detail, bool expectedBlocked) =>
        detail is not null && detail.IsBlocked == expectedBlocked;
}

public enum ApplicationTrafficDetailAvailability
{
    Available,
    Unsupported,
    TemporarilyUnavailable
}

public sealed class ApplicationTrafficDetailReadResult
{
    public ApplicationTrafficDetailAvailability Availability { get; init; }
    public ApplicationTrafficDetail? Detail { get; init; }
}

public enum DataStatisticsAvailability
{
    Available,
    Disabled,
    DpiInactive,
    Unsupported,
    TemporarilyUnavailable
}

/// <summary>What a normal Data Statistics read established about the router interface.</summary>
public enum DataStatisticsCapabilitySupport
{
    Supported,
    Unsupported,
    Unknown
}

/// <summary>The flow-statistics and DPI operating state observed during a read.</summary>
public enum DataStatisticsOperatingState
{
    EnabledAndDpiActive,
    Disabled,
    DpiInactive,
    Unknown
}

/// <summary>Whether the normal read produced top-application data.</summary>
public enum DataStatisticsReadAvailability
{
    Available,
    NotReadBecauseDisabled,
    NotReadBecauseDpiInactive,
    TemporarilyUnavailable,
    Unknown
}

/// <summary>Identifies the active router context at the start of a Data Statistics read.</summary>
public sealed record DataStatisticsContextStamp(string RouterProfileId, long Version);

/// <summary>
/// Immutable, Data Statistics-specific facts established by one normal read.
/// This intentionally remains local to the domain rather than defining a shared capability vocabulary.
/// </summary>
public sealed record DataStatisticsCapabilityReadFact(
    DataStatisticsCapabilitySupport Support,
    DataStatisticsOperatingState OperatingState,
    DataStatisticsReadAvailability ReadAvailability,
    DataStatisticsContextStamp Context,
    DataStatisticsStatus? Status,
    DataStatisticsSnapshot? Snapshot,
    NetworkTrafficSnapshot? TrafficSnapshot)
{
    public DataStatisticsAvailability ToLegacyAvailability() => (Support, OperatingState, ReadAvailability) switch
    {
        (DataStatisticsCapabilitySupport.Supported, DataStatisticsOperatingState.EnabledAndDpiActive,
            DataStatisticsReadAvailability.Available) => DataStatisticsAvailability.Available,
        (DataStatisticsCapabilitySupport.Supported, DataStatisticsOperatingState.Disabled, _) =>
            DataStatisticsAvailability.Disabled,
        (DataStatisticsCapabilitySupport.Supported, DataStatisticsOperatingState.DpiInactive, _) =>
            DataStatisticsAvailability.DpiInactive,
        (DataStatisticsCapabilitySupport.Unsupported, _, _) => DataStatisticsAvailability.Unsupported,
        (DataStatisticsCapabilitySupport.Unknown, _, DataStatisticsReadAvailability.TemporarilyUnavailable) =>
            DataStatisticsAvailability.TemporarilyUnavailable,
        _ => throw new InvalidOperationException("The Data Statistics semantic fact has no legacy availability mapping.")
    };
}

public sealed class DataStatisticsReadResult
{
    public DataStatisticsAvailability Availability { get; init; }
    public DataStatisticsStatus? Status { get; init; }
    public DataStatisticsSnapshot? Snapshot { get; init; }
    public NetworkTrafficSnapshot? TrafficSnapshot { get; init; }
    public DataStatisticsCapabilityReadFact? CapabilityFact { get; init; }
}
