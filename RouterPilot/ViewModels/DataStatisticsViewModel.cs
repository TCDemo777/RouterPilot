using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public sealed partial class DataStatisticsViewModel : ObservableObject, IDisposable
{
    private const int ChartApplicationLimit = 5;
    private readonly DataStatisticsService _dataStatisticsService;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private bool _loaded;
    private bool _disposed;

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string statusTitle = "Data Statistics";
    [ObservableProperty] private string statusDetail = "Open this section to read the router's current application traffic statistics.";
    [ObservableProperty] private RouterPilotStatus status = RouterPilotStatus.Pending;
    [ObservableProperty] private string currentPeriod = "Current period unavailable";
    [ObservableProperty] private string dpiLibrary = "Unavailable";

    public ObservableCollection<ApplicationTrafficStat> TopApps { get; } = new();
    public ISeries[] TrafficSeries { get; private set; } = [];
    public Axis[] TrafficXAxes { get; }
    public Axis[] TrafficYAxes { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    public bool HasTopApps => TopApps.Count > 0;
    public bool HasNoTopApps => !IsLoading && Status == RouterPilotStatus.Active && TopApps.Count == 0;
    public string TopAppsEmptyText => "No application traffic is available for the current period.";

    public DataStatisticsViewModel(DataStatisticsService dataStatisticsService)
    {
        _dataStatisticsService = dataStatisticsService;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !_disposed);
        TrafficXAxes =
        [
            new Axis
            {
                Name = "Time",
                Labeler = FormatTimeLabel,
                MinStep = TimeSpan.FromMinutes(1).TotalSeconds
            }
        ];
        TrafficYAxes =
        [
            new Axis
            {
                Name = "Traffic per bucket",
                MinLimit = 0,
                Labeler = value => FormatBytes((long)Math.Max(0, value))
            }
        ];
    }

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0))
            return;

        try
        {
            IsLoading = true;
            RefreshCommand.NotifyCanExecuteChanged();
            DataStatisticsReadResult readResult = await _dataStatisticsService
                .ReadAsync(_disposeCancellation.Token);
            _loaded = true;
            Apply(readResult);
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TopApps.Clear();
            TrafficSeries = [];
            Status = RouterPilotStatus.Error;
            StatusTitle = "Data Statistics temporarily unavailable";
            StatusDetail = OperationFailurePolicy.UserMessage(
                exception,
                "Data Statistics refresh",
                "RouterPilot could not read Data Statistics. Check the router connection and try again.");
            NotifyPresentationChanged();
        }
        finally
        {
            IsLoading = false;
            RefreshCommand.NotifyCanExecuteChanged();
            _refreshGate.Release();
        }
    }

    private void Apply(DataStatisticsReadResult result)
    {
        TopApps.Clear();
        TrafficSeries = [];
        DataStatisticsStatus? routerStatus = result.Status;
        DpiLibrary = FormatDpiLibrary(routerStatus);

        switch (result.Availability)
        {
            case DataStatisticsAvailability.Available:
                Status = RouterPilotStatus.Active;
                StatusTitle = "Data Statistics active";
                StatusDetail = "Application traffic classified by the router's DPI engine.";
                DataStatisticsSnapshot snapshot = result.Snapshot ?? new DataStatisticsSnapshot();
                CurrentPeriod = FormatPeriod(snapshot.PeriodSeconds);
                foreach (ApplicationTrafficStat app in snapshot.TopApps)
                    TopApps.Add(app);
                TrafficSeries = BuildTrafficSeries(snapshot.TopApps);
                break;

            case DataStatisticsAvailability.Disabled:
                Status = RouterPilotStatus.Disabled;
                StatusTitle = "Data Statistics is disabled";
                StatusDetail = "Data Statistics is disabled on the router.";
                CurrentPeriod = "Current period unavailable";
                break;

            case DataStatisticsAvailability.DpiInactive:
                Status = RouterPilotStatus.Pending;
                StatusTitle = "Data Statistics is unavailable";
                StatusDetail = "The router's DPI engine is not currently active.";
                CurrentPeriod = "Current period unavailable";
                break;

            case DataStatisticsAvailability.Unsupported:
                Status = RouterPilotStatus.Disabled;
                StatusTitle = "Data Statistics is not available";
                StatusDetail = "This router does not expose the required Data Statistics read interface.";
                CurrentPeriod = "Current period unavailable";
                break;

            default:
                Status = RouterPilotStatus.Error;
                StatusTitle = "Data Statistics temporarily unavailable";
                StatusDetail = "RouterPilot could not read Data Statistics. Try Refresh again.";
                CurrentPeriod = "Current period unavailable";
                break;
        }

        NotifyPresentationChanged();
    }

    private static ISeries[] BuildTrafficSeries(IReadOnlyList<ApplicationTrafficStat> apps) => apps
        .Take(ChartApplicationLimit)
        .Where(app => app.TimeSeries.Count > 0)
        .Select(app => (ISeries)new LineSeries<ApplicationTrafficPoint>
        {
            Name = DisplayName(app),
            Values = app.TimeSeries.Where(point => point.StartTimeUtc.HasValue).ToArray(),
            Mapping = (point, _) => new Coordinate(
                point.StartTimeUtc!.Value.ToUnixTimeSeconds(),
                point.TotalBytes),
            GeometrySize = 0,
            LineSmoothness = 0.25,
            XToolTipLabelFormatter = point => point.Model is ApplicationTrafficPoint model && model.StartTimeUtc is { } time
                ? $"Time: {time.LocalDateTime:t}"
                : "Time: -",
            YToolTipLabelFormatter = point => point.Model is ApplicationTrafficPoint model
                ? $"Traffic: {FormatBytes(model.TotalBytes)}"
                : "Traffic: 0 B"
        })
        .ToArray();

    public static string DisplayName(ApplicationTrafficStat app) =>
        !string.IsNullOrWhiteSpace(app.Label) ? app.Label :
        !string.IsNullOrWhiteSpace(app.ApplicationName) ? app.ApplicationName :
        "Unlabelled application";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string FormatPeriod(long? seconds) => seconds switch
    {
        3600 => "Past Hour",
        86400 => "Past Day",
        > 0 => $"Current period ({TimeSpan.FromSeconds(seconds.Value):g})",
        _ => "Current period unavailable"
    };

    private static string FormatDpiLibrary(DataStatisticsStatus? status)
    {
        if (status is null || string.IsNullOrWhiteSpace(status.DpiLibraryVersion))
            return "Unavailable";

        string? updateTime = FormatDpiUpdateTime(status.DpiLibraryUpdateTime);
        return string.IsNullOrWhiteSpace(updateTime)
            ? status.DpiLibraryVersion
            : $"{status.DpiLibraryVersion} · updated {updateTime}";
    }

    private static string? FormatDpiUpdateTime(string value)
    {
        if (long.TryParse(value, out long unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                    .LocalDateTime
                    .ToString("g");
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(value, out DateTimeOffset timestamp)
            ? timestamp.LocalDateTime.ToString("g")
            : null;
    }

    private static string FormatTimeLabel(double unixSeconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds)
                .LocalDateTime
                .ToString("t");
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(TrafficSeries));
        OnPropertyChanged(nameof(HasTopApps));
        OnPropertyChanged(nameof(HasNoTopApps));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoTopApps));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
        _refreshGate.Dispose();
    }
}
