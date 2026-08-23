using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public sealed partial class ApplicationTrafficDetailsViewModel : ObservableObject, IDisposable
{
    private readonly DataStatisticsService _service;
    private readonly ClientInventoryState _inventory;
    private readonly ClientProfileService _profiles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _applicationId;
    private readonly string _applicationName;
    private bool _disposed;

    [ObservableProperty] private bool isLoading = true;
    [ObservableProperty] private string statusMessage = "Loading application traffic…";
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private ApplicationTrafficDetail? detail;
    [ObservableProperty] private bool isMutatingProtection;

    public ObservableCollection<ApplicationTrafficDeviceRow> Devices { get; } = new();
    public ICollectionView DevicesView { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public ISeries[] TrafficSeries { get; private set; } = [];
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }
    public bool HasDevices => !DevicesView.IsEmpty;
    public bool HasNoDevices => !IsLoading && Detail is not null && DevicesView.IsEmpty;
    public string Label => string.IsNullOrWhiteSpace(Detail?.Label) ? _applicationName : Detail.Label;
    public string ApplicationName => _applicationName;
    public bool IsProtectionBlocked => Detail?.IsBlocked == true;
    public string ProtectionState => Detail is null ? "Protection unavailable" : IsProtectionBlocked ? "Blocked" : "Not blocked";
    public string ProtectionActionText => IsProtectionBlocked ? "Unblock application" : "Block application";
    public bool CanChangeProtection => !_disposed && !IsLoading && !IsMutatingProtection && Detail is not null && !string.IsNullOrWhiteSpace(Detail.ApplicationName);
    public string Period => Detail?.PeriodSeconds switch { 3600 => "Past Hour", 86400 => "Past Day", > 0 => $"Current period ({TimeSpan.FromSeconds(Detail.PeriodSeconds.Value):g})", _ => "Current period unavailable" };
    public string Download => Detail is null ? "—" : DataStatisticsViewModel.FormatBytes(Detail.TotalDownloadBytes);
    public string Upload => Detail is null ? "—" : DataStatisticsViewModel.FormatBytes(Detail.TotalUploadBytes);
    public string Total => Detail is null ? "—" : DataStatisticsViewModel.FormatBytes(Detail.TotalBytes);

    public ApplicationTrafficDetailsViewModel(DataStatisticsService service, ClientInventoryState inventory,
        ClientProfileService profiles, string applicationId, string applicationName)
    {
        _service = service;
        _inventory = inventory;
        _profiles = profiles;
        _applicationId = applicationId;
        _applicationName = applicationName;
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(ApplicationTrafficDeviceRow.TotalBytes), ListSortDirection.Descending));
        DevicesView.Filter = item => item is ApplicationTrafficDeviceRow row && Matches(row);
        Devices.CollectionChanged += (_, _) => NotifyRowsChanged();
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !_disposed && !IsLoading && !IsMutatingProtection);
        XAxes = [new Axis { Name = "Time", Labeler = FormatTime }];
        YAxes = [new Axis { Name = "Traffic per bucket", MinLimit = 0, Labeler = value => DataStatisticsViewModel.FormatBytes((long)Math.Max(0, value)) }];
    }

    public Task LoadAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            IsLoading = true;
            StatusMessage = "Loading application traffic…";
            RefreshCommand.NotifyCanExecuteChanged();
            ApplicationTrafficDetailReadResult result = await _service.ReadApplicationDetailAsync(_applicationId, _applicationName, _lifetime.Token);
            if (result.Availability != ApplicationTrafficDetailAvailability.Available || result.Detail is null)
            {
                StatusMessage = result.Availability == ApplicationTrafficDetailAvailability.Unsupported
                    ? "Application details are not available on this router."
                    : "Application details are temporarily unavailable.";
                return;
            }

            ApplyDetail(result.Detail);
            StatusMessage = string.Empty;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { StatusMessage = "Application details are temporarily unavailable."; }
        finally
        {
            IsLoading = false;
            RefreshCommand.NotifyCanExecuteChanged();
            _gate.Release();
        }
    }

    public async Task ChangeProtectionAsync(bool blocked)
    {
        if (!CanChangeProtection || Detail is null || Detail.IsBlocked == blocked ||
            !await _gate.WaitAsync(0))
            return;

        try
        {
            IsMutatingProtection = true;
            StatusMessage = "Updating application protection…";
            ApplicationProtectionMutationResult result = await _service.SetApplicationContentProtectionAsync(
                _applicationId, Detail.ApplicationName, blocked, _lifetime.Token);
            switch (result.Availability)
            {
                case ApplicationProtectionMutationAvailability.Succeeded when result.VerifiedDetail is not null:
                    ApplyDetail(result.VerifiedDetail);
                    StatusMessage = blocked ? "Application protection enabled." : "Application protection removed.";
                    break;
                case ApplicationProtectionMutationAvailability.VerificationFailed:
                    StatusMessage = "The router accepted the request, but the application block state could not be verified.";
                    break;
                case ApplicationProtectionMutationAvailability.Unsupported:
                    StatusMessage = "Application protection is not available on this router.";
                    break;
                case ApplicationProtectionMutationAvailability.InvalidApplication:
                    StatusMessage = "Application protection is unavailable for this application.";
                    break;
                default:
                    StatusMessage = "Unable to update application protection. Check the router connection and try again.";
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            IsMutatingProtection = false;
            _gate.Release();
        }
    }

    private void ApplyDetail(ApplicationTrafficDetail value)
    {
        Detail = value;
        Devices.Clear();
        var profiles = _profiles.Load();
        foreach (ApplicationDeviceTraffic device in value.Devices)
            Devices.Add(Project(device, profiles));
        TrafficSeries = value.TimeSeries.Where(point => point.StartTimeUtc.HasValue).Any()
            ? [(ISeries)new LineSeries<ApplicationTrafficPoint> { Name = "Traffic", Values = value.TimeSeries, Mapping = (point, _) => new Coordinate(point.StartTimeUtc?.ToUnixTimeSeconds() ?? 0, point.TotalBytes), GeometrySize = 0, LineSmoothness = 0.25 }]
            : [];
        OnPropertyChanged(nameof(TrafficSeries));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Period));
        OnPropertyChanged(nameof(Download));
        OnPropertyChanged(nameof(Upload));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(IsProtectionBlocked));
        OnPropertyChanged(nameof(ProtectionState));
        OnPropertyChanged(nameof(ProtectionActionText));
        OnPropertyChanged(nameof(CanChangeProtection));
    }

    private ApplicationTrafficDeviceRow Project(ApplicationDeviceTraffic device, IReadOnlyDictionary<string, ClientProfile> profiles)
    {
        ClientInfo? live = device.CanViewClient && _inventory.Snapshot.TryGetValue(device.NormalizedMac, out ClientInfo? client) ? client : null;
        string liveName = live?.Name ?? string.Empty;
        string name = device.CanViewClient && profiles.TryGetValue(device.NormalizedMac, out ClientProfile? profile) && !string.IsNullOrWhiteSpace(profile.Nickname)
            ? profile.Nickname
            : !string.IsNullOrWhiteSpace(liveName) && liveName != "-"
                ? liveName
                : !string.IsNullOrWhiteSpace(device.Hostname) ? device.Hostname : "Unknown device";
        return new ApplicationTrafficDeviceRow
        {
            StableMacKey = device.NormalizedMac,
            MacDisplay = string.IsNullOrWhiteSpace(device.MacAddress) ? "—" : device.MacAddress,
            FriendlyName = name,
            CurrentIp = live?.IpAddress is { Length: > 0 } ip and not "-" ? ip : "—",
            RouterHostname = device.Hostname,
            DownloadBytes = device.DownloadBytes,
            UploadBytes = device.UploadBytes,
            TotalBytes = device.TotalBytes,
            PacketCount = device.PacketCount,
            LastSeenUtc = device.LastActiveUtc,
            LastSeenFallback = device.LastActiveRelative
        };
    }

    private bool Matches(ApplicationTrafficDeviceRow row) => string.IsNullOrWhiteSpace(SearchText) ||
        row.FriendlyName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        row.RouterHostname.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        row.MacDisplay.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        row.CurrentIp.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    partial void OnSearchTextChanged(string value) { DevicesView.Refresh(); NotifyRowsChanged(); }
    partial void OnIsLoadingChanged(bool value)
    {
        NotifyRowsChanged();
        OnPropertyChanged(nameof(CanChangeProtection));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMutatingProtectionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeProtection));
        RefreshCommand.NotifyCanExecuteChanged();
    }
    private void NotifyRowsChanged() { OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(HasNoDevices)); }
    private static string FormatTime(double seconds) { try { return DateTimeOffset.FromUnixTimeSeconds((long)seconds).LocalDateTime.ToString("t"); } catch { return string.Empty; } }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); _gate.Dispose(); }
}
