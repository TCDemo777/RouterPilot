using System;
using System.Collections.Generic;
using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>Calculates bounded, in-process traffic session statistics from monotonic router counters.</summary>
public sealed class TrafficSessionAccumulator
{
    public const int HistoryCapacity = 300;
    private NetworkTrafficObservation? _previous;
    private string? _source;
    private DateTime _startedUtc;
    private long _downloaded;
    private long _uploaded;
    private long _peakDownload;
    private long _peakUpload;
    private int _samples;

    public IReadOnlyList<TrafficSessionSample> History => _history;
    private readonly List<TrafficSessionSample> _history = new(HistoryCapacity);
    public long DownloadedBytes => _downloaded;
    public long UploadedBytes => _uploaded;
    public long PeakDownloadBytesPerSecond => _peakDownload;
    public long PeakUploadBytesPerSecond => _peakUpload;
    public int SampleCount => _samples;
    public DateTime? StartedUtc => _startedUtc == default ? null : _startedUtc;

    public TrafficSessionSample? Add(NetworkTrafficObservation observation)
    {
        if (observation.ReceivedBytes < 0 || observation.TransmittedBytes < 0)
            return null;

        if (_previous is null || !string.Equals(_source, observation.InterfaceName, StringComparison.Ordinal) ||
            observation.CapturedAtUtc <= _previous.Value.CapturedAtUtc ||
            observation.CapturedAtUtc - _previous.Value.CapturedAtUtc > TimeSpan.FromMinutes(10) ||
            observation.ReceivedBytes < _previous.Value.ReceivedBytes ||
            observation.TransmittedBytes < _previous.Value.TransmittedBytes)
        {
            _previous = observation;
            _source = observation.InterfaceName;
            if (_startedUtc == default)
                _startedUtc = observation.CapturedAtUtc;
            return null;
        }

        TimeSpan elapsed = observation.CapturedAtUtc - _previous.Value.CapturedAtUtc;
        long receivedDelta = observation.ReceivedBytes - _previous.Value.ReceivedBytes;
        long transmittedDelta = observation.TransmittedBytes - _previous.Value.TransmittedBytes;
        long downloadRate = (long)Math.Max(0, Math.Round(receivedDelta / elapsed.TotalSeconds));
        long uploadRate = (long)Math.Max(0, Math.Round(transmittedDelta / elapsed.TotalSeconds));
        _downloaded = SaturatingAdd(_downloaded, receivedDelta);
        _uploaded = SaturatingAdd(_uploaded, transmittedDelta);
        _peakDownload = Math.Max(_peakDownload, downloadRate);
        _peakUpload = Math.Max(_peakUpload, uploadRate);
        _samples++;
        _previous = observation;
        TrafficSessionSample sample = new(observation.CapturedAtUtc, downloadRate, uploadRate,
            _downloaded, _uploaded, observation.InterfaceName);
        _history.Add(sample);
        if (_history.Count > HistoryCapacity)
            _history.RemoveAt(0);
        return sample;
    }

    public void Reset()
    {
        _previous = null;
        _source = null;
        _startedUtc = default;
        _downloaded = 0;
        _uploaded = 0;
        _peakDownload = 0;
        _peakUpload = 0;
        _samples = 0;
        _history.Clear();
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}

public readonly record struct TrafficSessionSample(
    DateTime TimestampUtc,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    long DownloadedBytes,
    long UploadedBytes,
    string InterfaceName);
