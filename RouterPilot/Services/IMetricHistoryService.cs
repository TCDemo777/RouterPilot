using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IMetricHistoryService
{
    Task InitializeAsync();
    void RecordMetric(MetricKind metric, double value, DateTimeOffset timestamp);
    Task RecordInternetStateAsync(bool online, DateTimeOffset timestamp, CancellationToken cancellationToken = default);
    IReadOnlyList<MetricSample> GetMetrics(MetricKind metric, TimeSpan range, DateTimeOffset now);
    InternetReliabilitySummary GetReliability(TimeSpan range, DateTimeOffset now);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    int RetentionDays { get; }
    long StorageSizeBytes { get; }
}
