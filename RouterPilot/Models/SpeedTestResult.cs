namespace RouterPilot.Models;

public enum SpeedTestSource
{
    Router,
    ThisPc
}

public enum SpeedTestStatus
{
    Ready,
    Pending,
    Completed,
    Cancelled,
    Error,
    Unsupported
}

public sealed class SpeedTestResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public SpeedTestSource Source { get; set; }
    public double? PingMs { get; set; }
    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }
    public TimeSpan Duration { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ServerDescription { get; set; }
    public SpeedTestStatus Status { get; set; } = SpeedTestStatus.Ready;
    public string? SafeFailureCategory { get; set; }

    public string PingDisplay => PingMs is { } ping ? $"{ping:0.#} ms" : "N/A";
    public string DownloadDisplay => DownloadMbps is { } download ? $"{download:0.#} Mbps ↓" : "N/A";
    public string UploadDisplay => UploadMbps is { } upload ? $"{upload:0.#} Mbps ↑" : "N/A";
    public string SourceDisplay => Source == SpeedTestSource.Router ? "Router" : "This PC";
}

public sealed class RouterSpeedTestCapability
{
    public bool IsSupported { get; init; }
    public string? DetectedBinary { get; init; }
    public string SafeStatus { get; init; } = "unavailable";
}
