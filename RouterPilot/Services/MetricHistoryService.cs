using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Local, bounded history written from existing dashboard samples only.</summary>
public sealed class MetricHistoryService : IMetricHistoryService
{
    private const int DefaultRetentionDays = 30;
    private static readonly TimeSpan WriteDebounce = TimeSpan.FromSeconds(20);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<MetricSample> _samples = [];
    private readonly List<InternetAvailabilityEvent> _availability = [];
    private readonly Dictionary<MetricKind, DateTimeOffset> _lastMetricBucket = [];
    private bool _initialized;
    private bool _dirty;
    private Task? _debouncedWrite;

    public MetricHistoryService(ApplicationDataPathProvider paths) => _path = Path.Combine(paths.CurrentPath, "metric-history.json");
    public int RetentionDays => DefaultRetentionDays;
    public long StorageSizeBytes => File.Exists(_path) ? new FileInfo(_path).Length : 0;
    public event EventHandler? AvailabilityHistoryChanged;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        try
        {
            if (File.Exists(_path))
            {
                await using FileStream stream = File.OpenRead(_path);
                Store? store = await JsonSerializer.DeserializeAsync<Store>(stream);
                if (store is not null) { _samples.AddRange(store.Samples); _availability.AddRange(store.Availability); }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Metric history unavailable ({ex.GetType().Name}); starting a new local history.");
            _samples.Clear(); _availability.Clear();
        }
        Prune(DateTimeOffset.UtcNow);
        _initialized = true;
    }

    public void RecordMetric(MetricKind metric, double value, DateTimeOffset timestamp)
    {
        if (!_initialized || !double.IsFinite(value)) return;
        value = metric is MetricKind.CpuPercent or MetricKind.MemoryPercent ? Math.Clamp(value, 0, 100) : Math.Max(0, value);
        DateTimeOffset bucket = new(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute / 5 * 5, 0, TimeSpan.Zero);
        lock (_samples)
        {
            int existing = _samples.FindIndex(item => item.Metric == metric && item.Timestamp == bucket);
            if (existing >= 0) _samples[existing] = new MetricSample { Metric = metric, Timestamp = bucket, Value = value };
            else _samples.Add(new MetricSample { Metric = metric, Timestamp = bucket, Value = value });
            _dirty = true;
        }
        ScheduleFlush();
    }

    public async Task RecordInternetStateAsync(bool online, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        InternetAvailabilityState state = online ? InternetAvailabilityState.Online : InternetAvailabilityState.Offline;
        bool changed;
        lock (_availability)
        {
            changed = _availability.Count == 0 || _availability[^1].State != state;
            if (changed) { _availability.Add(new InternetAvailabilityEvent { Timestamp = timestamp, State = state }); _dirty = true; }
        }
        if (changed)
        {
            Prune(timestamp);
            ScheduleFlush();
            AvailabilityHistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<MetricSample> GetMetrics(MetricKind metric, TimeSpan range, DateTimeOffset now)
    {
        DateTimeOffset start = now - range;
        lock (_samples) return _samples.Where(item => item.Metric == metric && item.Timestamp >= start && item.Timestamp <= now).OrderBy(item => item.Timestamp).ToList();
    }

    public InternetReliabilitySummary GetReliability(TimeSpan range, DateTimeOffset now)
    {
        DateTimeOffset start = now - range;
        List<InternetAvailabilityEvent> events;
        lock (_availability) events = _availability.OrderBy(item => item.Timestamp).ToList();
        InternetAvailabilityEvent? prior = events.LastOrDefault(item => item.Timestamp <= start);
        List<InternetAvailabilityEvent> relevant = events.Where(item => item.Timestamp > start && item.Timestamp <= now).ToList();
        if (prior is null && relevant.Count == 0) return new InternetReliabilitySummary { HasSufficientHistory = false };
        // Without a state at the beginning of the requested range, monitoring
        // had not yet established whether the connection was online or offline.
        // Begin observation at the first authoritative event rather than
        // assigning that unknown interval to either state.
        InternetAvailabilityState state = prior?.State ?? relevant[0].State;
        DateTimeOffset cursor = prior is null ? relevant[0].Timestamp : start;
        TimeSpan online = TimeSpan.Zero, offline = TimeSpan.Zero, longest = TimeSpan.Zero, currentOutage = TimeSpan.Zero;
        int outages = 0; DateTimeOffset? lastStart = null; TimeSpan? lastDuration = null; DateTimeOffset stateSince = prior?.Timestamp ?? relevant[0].Timestamp;
        foreach (InternetAvailabilityEvent item in relevant)
        {
            TimeSpan duration = item.Timestamp - cursor;
            if (state == InternetAvailabilityState.Online) online += duration; else { offline += duration; currentOutage += duration; }
            if (state == InternetAvailabilityState.Offline && item.State == InternetAvailabilityState.Online) { outages++; longest = Max(longest, currentOutage); lastDuration = currentOutage; currentOutage = TimeSpan.Zero; }
            if (state == InternetAvailabilityState.Online && item.State == InternetAvailabilityState.Offline) { lastStart = item.Timestamp; currentOutage = TimeSpan.Zero; }
            state = item.State; cursor = item.Timestamp; stateSince = item.Timestamp;
        }
        TimeSpan tail = now - cursor;
        if (state == InternetAvailabilityState.Online) online += tail; else { offline += tail; currentOutage += tail; longest = Max(longest, currentOutage); }
        return new InternetReliabilitySummary { HasSufficientHistory = online + offline >= TimeSpan.FromMinutes(1), IsOnline = state == InternetAvailabilityState.Online, ObservedDuration = online + offline, OnlineDuration = online, OfflineDuration = offline, OutageCount = outages + (state == InternetAvailabilityState.Offline ? 1 : 0), LongestOutage = longest, LastOutageStartedAt = lastStart, LastOutageDuration = lastDuration, CurrentStateSince = stateSince };
    }

    public InternetInstabilitySummary GetInternetInstability(TimeSpan range, DateTimeOffset now, int threshold)
    {
        DateTimeOffset start = now - range;
        List<InternetAvailabilityEvent> events;
        lock (_availability) events = _availability.OrderBy(item => item.Timestamp).ToList();
        InternetAvailabilityEvent? prior = events.LastOrDefault(item => item.Timestamp <= start);
        List<InternetAvailabilityEvent> relevant = events.Where(item => item.Timestamp > start && item.Timestamp <= now).ToList();
        if (prior is null && relevant.Count == 0) return new InternetInstabilitySummary();

        InternetAvailabilityState state = prior?.State ?? relevant[0].State;
        DateTimeOffset cursor = prior is null ? relevant[0].Timestamp : start;
        DateTimeOffset? outageStartedAt = state == InternetAvailabilityState.Offline ? prior?.Timestamp ?? cursor : null;
        List<DateTimeOffset> outages = [];

        foreach (InternetAvailabilityEvent item in relevant)
        {
            if (state == InternetAvailabilityState.Online && item.State == InternetAvailabilityState.Offline)
                outageStartedAt = item.Timestamp;
            else if (state == InternetAvailabilityState.Offline && item.State == InternetAvailabilityState.Online)
            {
                outages.Add(outageStartedAt ?? item.Timestamp);
                outageStartedAt = null;
            }

            state = item.State;
            cursor = item.Timestamp;
        }

        // A continuous current Offline period is one outage, never one per
        // refresh sample. Unknown time is absent from this transition store.
        if (state == InternetAvailabilityState.Offline)
            outages.Add(outageStartedAt ?? cursor);

        return new InternetInstabilitySummary
        {
            OutageCount = outages.Count,
            ObservedDuration = now - (prior is null ? relevant[0].Timestamp : start),
            ThresholdReachedAt = outages.Count >= threshold ? outages[threshold - 1] : null
        };
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_samples) { _samples.Clear(); _availability.Clear(); _dirty = true; }
        await FlushAsync(cancellationToken);
        AvailabilityHistoryChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized || !_dirty) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_dirty) return;
            List<MetricSample> samples; List<InternetAvailabilityEvent> availability;
            lock (_samples) { Prune(DateTimeOffset.UtcNow); samples = _samples.ToList(); availability = _availability.ToList(); _dirty = false; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Store { Samples = samples, Availability = availability }), cancellationToken);
            File.Move(temp, _path, true);
        }
        finally { _gate.Release(); }
    }
    private void ScheduleFlush() { if (_debouncedWrite is { IsCompleted: false }) return; _debouncedWrite = Task.Run(async () => { await Task.Delay(WriteDebounce); try { await FlushAsync(); } catch { } }); }
    private void Prune(DateTimeOffset now) { DateTimeOffset cutoff = now.AddDays(-DefaultRetentionDays); _samples.RemoveAll(item => item.Timestamp < cutoff); _availability.RemoveAll(item => item.Timestamp < cutoff); }
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
    private sealed class Store { public List<MetricSample> Samples { get; init; } = []; public List<InternetAvailabilityEvent> Availability { get; init; } = []; }
}
