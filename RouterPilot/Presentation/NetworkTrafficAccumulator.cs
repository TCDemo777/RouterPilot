namespace RouterPilot.Presentation;

/// <summary>
/// Maintains the in-session traffic baseline and deterministic rate statistics.
/// Router reads, UI updates, chart collections, and metric recording remain owned by callers.
/// </summary>
public sealed class NetworkTrafficAccumulator
{
    private NetworkTrafficObservation? _previousObservation;
    private bool _baselineRequired = true;
    private double _peakDownloadMbps;
    private double _peakUploadMbps;
    private double _downloadTotalMbps;
    private double _uploadTotalMbps;
    private int _sampleCount;

    /// <summary>
    /// Adds an observation and returns a calculated sample when a usable prior
    /// baseline exists. A first observation or a decreased counter resets the
    /// baseline without producing a rate.
    /// </summary>
    public NetworkTrafficSample? Add(NetworkTrafficObservation observation)
    {
        if (_baselineRequired || _previousObservation is null)
        {
            _previousObservation = observation;
            _baselineRequired = false;
            return null;
        }

        NetworkTrafficObservation previous = _previousObservation.Value;
        if (observation.ReceivedBytes < previous.ReceivedBytes ||
            observation.TransmittedBytes < previous.TransmittedBytes)
        {
            _previousObservation = observation;
            return null;
        }

        double elapsedSeconds = Math.Max(
            0.25,
            (observation.CapturedAtUtc - previous.CapturedAtUtc).TotalSeconds);
        long receivedDelta = observation.ReceivedBytes - previous.ReceivedBytes;
        long transmittedDelta = observation.TransmittedBytes - previous.TransmittedBytes;

        double downloadMbps = Math.Max(0, receivedDelta * 8d / elapsedSeconds / 1_000_000d);
        double uploadMbps = Math.Max(0, transmittedDelta * 8d / elapsedSeconds / 1_000_000d);

        _peakDownloadMbps = Math.Max(_peakDownloadMbps, downloadMbps);
        _peakUploadMbps = Math.Max(_peakUploadMbps, uploadMbps);
        _downloadTotalMbps += downloadMbps;
        _uploadTotalMbps += uploadMbps;
        _sampleCount++;
        _previousObservation = observation;

        return new NetworkTrafficSample(
            downloadMbps,
            uploadMbps,
            _peakDownloadMbps,
            _peakUploadMbps,
            _downloadTotalMbps / _sampleCount,
            _uploadTotalMbps / _sampleCount);
    }

    public void Reset()
    {
        ResetBaseline();
        _peakDownloadMbps = 0;
        _peakUploadMbps = 0;
        _downloadTotalMbps = 0;
        _uploadTotalMbps = 0;
        _sampleCount = 0;
    }

    /// <summary>Forces the next observation to establish a fresh rate baseline while retaining session statistics.</summary>
    public void ResetBaseline()
    {
        _previousObservation = null;
        _baselineRequired = true;
    }
}

public readonly record struct NetworkTrafficObservation(
    long ReceivedBytes,
    long TransmittedBytes,
    DateTime CapturedAtUtc);

public readonly record struct NetworkTrafficSample(
    double DownloadMbps,
    double UploadMbps,
    double PeakDownloadMbps,
    double PeakUploadMbps,
    double AverageDownloadMbps,
    double AverageUploadMbps);
