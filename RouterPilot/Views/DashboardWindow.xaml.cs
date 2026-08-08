using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet.Common;

namespace RouterPilot.Views
{
    public partial class DashboardWindow : Window
    {
        private const string DashboardRefreshTask = "DashboardRefresh";
        private const string TrafficRefreshTask = "TrafficRefresh";
        private const string AdGuardScheduleTask = "AdGuardServiceSchedules";

        private readonly DashboardViewModel _viewModel;
        private readonly SettingsService _settingsService;
        private readonly NotificationService _notificationService;
        private readonly NotificationCentreViewModel _notificationCentreViewModel;
        private readonly MaintenanceViewModel _maintenanceViewModel;
        private readonly AdGuardProtectionNotificationTracker _protectionNotificationTracker;
        private readonly RefreshCoordinator _refreshCoordinator;
        private readonly AdGuardServiceScheduleService _scheduleService;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly AdGuardMaintenanceStateService _adGuardMaintenanceStateService;
        private readonly FirmwareUpdateService _firmwareUpdateService;
        private readonly SemaphoreSlim _routerManagerUsageGate = new(1, 1);
        private bool _refreshInProgress;
        private bool _trafficRefreshInProgress;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private bool _routerOnline = true;

        private NetworkTrafficSnapshot? _previousTrafficSnapshot;
        private bool _trafficBaselineRequired = true;
        private double _peakDownloadMbps;
        private double _peakUploadMbps;
        private double _downloadTotalMbps;
        private double _uploadTotalMbps;
        private int _trafficSampleCount;

        private readonly Brush _selectedNavigationBackground =
            new SolidColorBrush(
                Color.FromRgb(
                    53,
                    64,
                    77));

        private readonly Brush _unselectedNavigationForeground =
            new SolidColorBrush(
                Color.FromRgb(
                    215,
                    220,
                    226));

        public DashboardWindow()
        {
            InitializeComponent();

            _viewModel =
                new DashboardViewModel();

            DataContext =
                _viewModel;

            _notificationService = ((App)Application.Current)
                .Services.GetRequiredService<NotificationService>();
            _notificationCentreViewModel = ((App)Application.Current)
                .Services.GetRequiredService<NotificationCentreViewModel>();
            _maintenanceViewModel = ((App)Application.Current)
                .Services.GetRequiredService<MaintenanceViewModel>();
            _protectionNotificationTracker = ((App)Application.Current)
                .Services.GetRequiredService<AdGuardProtectionNotificationTracker>();
            _routerManagerProvider = ((App)Application.Current).Services
                .GetRequiredService<IRouterManagerProvider>();
            _scheduleService = ((App)Application.Current).Services
                .GetRequiredService<AdGuardServiceScheduleService>();
            _adGuardAvailabilityService = ((App)Application.Current).Services
                .GetRequiredService<AdGuardAvailabilityService>();
            _adGuardMaintenanceStateService = ((App)Application.Current).Services
                .GetRequiredService<AdGuardMaintenanceStateService>();
            _firmwareUpdateService = ((App)Application.Current).Services
                .GetRequiredService<FirmwareUpdateService>();
            _viewModel.RouterFirmwareVersion = string.IsNullOrWhiteSpace(
                _firmwareUpdateService.Current.CurrentVersion)
                ? "-"
                : _firmwareUpdateService.Current.CurrentVersion;
            _firmwareUpdateService.PropertyChanged += (_, _) =>
            {
                _viewModel.RouterFirmwareVersion = string.IsNullOrWhiteSpace(
                    _firmwareUpdateService.Current.CurrentVersion)
                    ? "-"
                    : _firmwareUpdateService.Current.CurrentVersion;
            };
            _viewModel.AdGuardMaintenanceState = _adGuardMaintenanceStateService.State;
            _adGuardMaintenanceStateService.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AdGuardMaintenanceStateService.State))
                {
                    _viewModel.AdGuardMaintenanceState = _adGuardMaintenanceStateService.State;
                }
            };
            NotificationButton.DataContext = _notificationService;

            _settingsService =
                new SettingsService();

            PageContent.Content = CreateOverviewView();

            Loaded +=
                DashboardWindow_Loaded;

            StateChanged +=
                DashboardWindow_StateChanged;

            IsVisibleChanged +=
                DashboardWindow_IsVisibleChanged;

            _refreshCoordinator = new RefreshCoordinator();
            _refreshCoordinator.Register(
                DashboardRefreshTask,
                TimeSpan.FromSeconds(30),
                cancellationToken => RunOnUiThreadAsync(
                    () => RefreshDashboard(cancellationToken)),
                enabled: false);
            _refreshCoordinator.Register(
                TrafficRefreshTask,
                TimeSpan.FromSeconds(2),
                cancellationToken => RunOnUiThreadAsync(
                    () => RefreshNetworkTrafficAsync(cancellationToken)),
                enabled: false);
            _refreshCoordinator.Register(
                AdGuardScheduleTask,
                TimeSpan.FromMinutes(1),
                cancellationToken => _scheduleService.EvaluateDueAsync(cancellationToken),
                enabled: false);

            ProtectionStateNotifier.StateChanged +=
                ProtectionStateNotifier_StateChanged;
        }

        private async void DashboardWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            await _refreshCoordinator.RunNowAsync(
                DashboardRefreshTask);

            await _refreshCoordinator.SetEnabledAsync(
                DashboardRefreshTask,
                true);

            await _refreshCoordinator.RunNowAsync(AdGuardScheduleTask);
            await _refreshCoordinator.SetEnabledAsync(AdGuardScheduleTask, true);

            if (IsVisible)
            {
                await _refreshCoordinator.SetEnabledAsync(
                    TrafficRefreshTask,
                    true);
            }
        }

        private async Task RefreshDashboard(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_refreshInProgress)
            {
                return;
            }

            _refreshInProgress = true;
            bool routerCommunicationConfirmed = false;
            bool routerManagerGateEntered = false;

            try
            {
                await _routerManagerUsageGate.WaitAsync(cancellationToken);
                routerManagerGateEntered = true;

                AppSettings settings =
                    _settingsService.Load();

                int refreshSeconds =
                    Math.Clamp(
                        settings.RefreshIntervalSeconds,
                        5,
                        3600);

                _refreshCoordinator.UpdateInterval(
                    DashboardRefreshTask,
                    TimeSpan.FromSeconds(
                        refreshSeconds));

                if (string.IsNullOrWhiteSpace(
                        settings.RouterHost) ||
                    string.IsNullOrWhiteSpace(
                        settings.Username))
                {
                    await ShowConnectionErrorAsync(
                        "Router settings are incomplete.",
                        notifyConnectivityChange: false);

                    return;
                }

                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync(
                        cancellationToken);

                RouterInfo info =
                    await router.GetRouterInfoAsync();

                cancellationToken.ThrowIfCancellationRequested();
                routerCommunicationConfirmed = true;

                _viewModel.RouterConnected =
                    true;

                _viewModel.RouterModel =
                    info.Model;

                _viewModel.Hostname =
                    info.Hostname;

                _viewModel.FirmwareVersion =
                    info.Firmware;

                // The stock GL.iNet update check is an independent, read-only
                // operation. Do not delay dashboard startup or the normal refresh.
                _ = _firmwareUpdateService.CheckAutomaticallyAsync(
                    router,
                    info.Firmware,
                    cancellationToken);

                _viewModel.Uptime =
                    info.Uptime;

                _viewModel.CpuUsage =
                    info.CpuUsage;

                _viewModel.CpuUtilisationPending =
                    info.CpuUtilisationPending;

                Debug.WriteLine(
                    $"Dashboard CPU assigned: {info.CpuUsage}");

                _viewModel.Temperature =
                    info.Temperature;

                _viewModel.LoadAverage =
                    info.LoadAverage;

                _viewModel.MemoryUsage =
                    info.MemoryUsage;

                _viewModel.UpdateStorageUsage(
                    info.StorageUsage);

                // Router and AdGuard work are independent. Start both groups
                // together, then apply each successful result separately.
                Task wifiTask = RefreshWifiNetworksAsync(router, cancellationToken);
                Task<NetworkInfo> networkTask = router.GetNetworkInfoAsync();
                Task adGuardTask = RefreshAdGuardAsync(router, cancellationToken);

                NetworkInfo? network = null;
                Exception? networkFailure = null;
                try
                {
                    network = await networkTask;
                }
                catch (Exception ex)
                {
                    networkFailure = ex;
                }

                // Both independent groups are always observed, even when the
                // router network request fails.
                await Task.WhenAll(wifiTask, adGuardTask);
                if (networkFailure is not null)
                {
                    throw networkFailure;
                }

                Debug.Assert(network is not null);

                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.InternetConnected =
                    network!.Connected;

                _viewModel.WanIp =
                    network.WanIp;

                _viewModel.Gateway =
                    network.Gateway;

                _viewModel.ExternalDns =
                    network.ExternalDns;

                _viewModel.AdvertisedDns =
                    network.AdvertisedDns;

                _viewModel.Latency =
                    network.Latency;

                _viewModel.StatusMessage = _viewModel.AdGuardAvailability ==
                    AdGuardAvailabilityState.Available
                        ? "Connected"
                        : "Connected - AdGuard Home unavailable";

                await UpdateRouterConnectivityAsync(isOnline: true);

                _viewModel.RefreshStatusIndicators();

                _viewModel.LastRefresh =
                    "Last refresh: " +
                    DateTime.Now.ToString(
                        "dd MMM yyyy HH:mm:ss");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SshAuthenticationException)
            {
                await ShowConnectionErrorAsync(
                    "SSH authentication failed.");
            }
            catch (SshConnectionException)
            {
                await ShowConnectionErrorAsync(
                    "Unable to connect to router.");
            }
            catch (Exception ex)
            {
                await ShowConnectionErrorAsync(
                    ex.Message,
                    notifyConnectivityChange: !routerCommunicationConfirmed);
            }
            finally
            {
                if (routerManagerGateEntered)
                {
                    _routerManagerUsageGate.Release();
                }

                _refreshInProgress = false;
            }
        }

        private async Task RefreshAdGuardAsync(
            RouterManager router,
            CancellationToken cancellationToken)
        {
            try
            {
                AdGuardStatus serviceStatus = await router.GetAdGuardStatusAsync();
                cancellationToken.ThrowIfCancellationRequested();

                _viewModel.AdGuardRunning = serviceStatus.IsRunning;
                _viewModel.AdGuardVersion = serviceStatus.Version;
                _viewModel.AdGuardProcess = serviceStatus.Process;
                _viewModel.AdGuardService = serviceStatus.ServiceStatus;

                if (!serviceStatus.IsRunning)
                {
                    MarkAdGuardUnavailable(AdGuardAvailabilityState.Unavailable);
                    return;
                }

                Task<AdGuardRefreshResult<AdGuardStatistics>> statisticsTask =
                    CaptureAdGuardResultAsync(router.GetAdGuardStatisticsAsync(), cancellationToken);
                Task<AdGuardRefreshResult<List<QueryLogEntry>>> rankingTask =
                    CaptureAdGuardResultAsync(router.GetQueryLogAsync(), cancellationToken);
                Task<AdGuardRefreshResult<AdGuardProtectionStatus>> protectionTask =
                    CaptureAdGuardResultAsync(router.GetAdGuardProtectionStatusAsync(), cancellationToken);

                await Task.WhenAll(statisticsTask, rankingTask, protectionTask);
                cancellationToken.ThrowIfCancellationRequested();

                AdGuardRefreshResult<AdGuardStatistics> statistics = await statisticsTask;
                AdGuardRefreshResult<List<QueryLogEntry>> rankings = await rankingTask;
                AdGuardRefreshResult<AdGuardProtectionStatus> protection = await protectionTask;

                if (protection.Value is { } protectionStatus)
                {
                    await _protectionNotificationTracker.ProcessProtectionStateAsync(
                        protectionStatus.IsEnabled,
                        ProtectionStateSource.Refresh);
                    cancellationToken.ThrowIfCancellationRequested();
                    _viewModel.AdGuardProtectionEnabled = protectionStatus.IsEnabled;
                    _viewModel.AdGuardProtectionPaused = protectionStatus.IsPaused;
                    _viewModel.AdGuardProtectionStatusKnown = true;
                    _viewModel.AdGuardProtectionRemaining = protectionStatus.IsPaused
                        ? FormatProtectionRemaining(protectionStatus.RemainingPause)
                        : string.Empty;
                }

                if (statistics.Value is { } statisticsValue)
                {
                    _viewModel.UpdateAdGuardStatistics(statisticsValue);
                    _viewModel.AdGuardQueries = statisticsValue.TotalQueries < 0
                        ? "-"
                        : statisticsValue.TotalQueries.ToString("N0");
                    _viewModel.AdGuardBlocked = statisticsValue.BlockedQueries < 0
                        ? "-"
                        : statisticsValue.BlockedQueries.ToString("N0");
                    _viewModel.AdGuardBlockRate = statisticsValue.TotalQueries < 0 ||
                                                  statisticsValue.BlockedQueries < 0
                        ? "-"
                        : statisticsValue.BlockPercentage.ToString("0.0") + "%";
                }

                if (rankings.Value is { } rankingEntries)
                {
                    _viewModel.UpdateRankingsFromQueryLog(rankingEntries, onlyWhenEmpty: false);
                }

                Exception? failure = protection.Error ?? statistics.Error ?? rankings.Error;
                if (failure is null)
                {
                    _viewModel.AdGuardAvailability = AdGuardAvailabilityState.Available;
                    _adGuardAvailabilityService.SetState(AdGuardAvailabilityState.Available);
                    if (_adGuardMaintenanceStateService.State == AdGuardMaintenanceState.Failed)
                    {
                        _adGuardMaintenanceStateService.CompleteRestart();
                    }
                    return;
                }

                MarkAdGuardUnavailable(ClassifyAdGuardFailure(failure));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkAdGuardUnavailable(ClassifyAdGuardFailure(ex));
            }
        }

        private void MarkAdGuardUnavailable(AdGuardAvailabilityState state)
        {
            _viewModel.AdGuardAvailability = state;
            _adGuardAvailabilityService.SetState(state);
            _viewModel.AdGuardRunning = false;
            _viewModel.ClearAdGuardStatistics();
        }

        private static AdGuardAvailabilityState ClassifyAdGuardFailure(Exception exception)
        {
            string message = exception.Message;
            return message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? AdGuardAvailabilityState.AuthenticationFailed
                : AdGuardAvailabilityState.Unavailable;
        }

        private static async Task<AdGuardRefreshResult<T>> CaptureAdGuardResultAsync<T>(
            Task<T> task,
            CancellationToken cancellationToken)
        {
            try
            {
                T value = await task;
                cancellationToken.ThrowIfCancellationRequested();
                return new AdGuardRefreshResult<T>(value, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new AdGuardRefreshResult<T>(default, ex);
            }
        }

        private sealed record AdGuardRefreshResult<T>(T? Value, Exception? Error);

        private async Task RefreshWifiNetworksAsync(
            RouterManager router,
            CancellationToken cancellationToken)
        {
            try
            {
                Debug.WriteLine(
                    $"[WiFiRefresh] started manager={router.GetHashCode():X8} " +
                    $"provider={_routerManagerProvider.GetType().Name}");

                List<WifiRadioInfo> wifiRadios =
                    await router.GetWifiRadiosAsync();

                cancellationToken.ThrowIfCancellationRequested();
                if (wifiRadios.Count == 0)
                {
                    // An empty discovery result is not an authoritative empty
                    // configuration.  Keep the last successful snapshot.
                    _viewModel.WifiRefreshError =
                        "Wi-Fi discovery returned no interfaces; showing the last successful network data.";
                    Debug.WriteLine(
                        "[WiFiRefresh] failed category=no-interfaces");
                    return;
                }

                Debug.Assert(wifiRadios.Count > 0);
                _viewModel.UpdateWifiRadios(wifiRadios);
                _viewModel.WifiRefreshError = string.Empty;
                Debug.WriteLine(
                    $"[WiFiRefresh] completed records={wifiRadios.Count} " +
                    $"clients={wifiRadios.Sum(network => network.ClientCount)}");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Preserve the last successful snapshot during transient
                // firmware, interface or SSH failures.
                string category = ex switch
                {
                    SshAuthenticationException => "authentication",
                    SshConnectionException => "connectivity",
                    FormatException => "parsing",
                    _ => "discovery"
                };
                _viewModel.WifiRefreshError =
                    $"Wi-Fi refresh failed ({category}); showing the last successful network data.";
                Debug.WriteLine(
                    $"[WiFiRefresh] failed category={category}");
            }
        }

        private async Task RefreshNetworkTrafficAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsVisible ||
                _trafficRefreshInProgress)
            {
                return;
            }

            _trafficRefreshInProgress = true;
            bool routerManagerGateEntered = false;

            try
            {
                await _routerManagerUsageGate.WaitAsync(cancellationToken);
                routerManagerGateEntered = true;

                AppSettings settings = _settingsService.Load();

                if (string.IsNullOrWhiteSpace(settings.RouterHost) ||
                    string.IsNullOrWhiteSpace(settings.Username))
                {
                    return;
                }

                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync(
                        cancellationToken);

                NetworkTrafficSnapshot snapshot =
                    await router.GetNetworkTrafficSnapshotAsync();

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsVisible)
                {
                    return;
                }

                UpdateNetworkTraffic(snapshot);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // The main refresh reports connection errors. A missed live
                // traffic sample should not clear the rest of the dashboard.
            }
            finally
            {
                if (routerManagerGateEntered)
                {
                    _routerManagerUsageGate.Release();
                }

                _trafficRefreshInProgress = false;
            }
        }

        private static async Task ObserveTaskAsync(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // The original exception is rethrown by the caller.
            }
        }

        private void ResetTrafficStatistics()
        {
            _previousTrafficSnapshot = null;
            _trafficBaselineRequired = true;
            _peakDownloadMbps = 0;
            _peakUploadMbps = 0;
            _downloadTotalMbps = 0;
            _uploadTotalMbps = 0;
            _trafficSampleCount = 0;
        }

        private void UpdateNetworkTraffic(
            NetworkTrafficSnapshot snapshot)
        {
            if (_trafficBaselineRequired ||
                _previousTrafficSnapshot == null)
            {
                _previousTrafficSnapshot = snapshot;
                _trafficBaselineRequired = false;
                return;
            }

            if (snapshot.ReceivedBytes <
                    _previousTrafficSnapshot.ReceivedBytes ||
                snapshot.TransmittedBytes <
                    _previousTrafficSnapshot.TransmittedBytes)
            {
                _previousTrafficSnapshot = snapshot;
                return;
            }

            double elapsedSeconds =
                Math.Max(
                    0.25,
                    (snapshot.CapturedAtUtc -
                     _previousTrafficSnapshot.CapturedAtUtc)
                    .TotalSeconds);

            long receivedDelta =
                snapshot.ReceivedBytes -
                _previousTrafficSnapshot.ReceivedBytes;

            long transmittedDelta =
                snapshot.TransmittedBytes -
                _previousTrafficSnapshot.TransmittedBytes;

            double downloadMbps =
                Math.Max(
                    0,
                    receivedDelta * 8d /
                    elapsedSeconds /
                    1_000_000d);

            double uploadMbps =
                Math.Max(
                    0,
                    transmittedDelta * 8d /
                    elapsedSeconds /
                    1_000_000d);

            _peakDownloadMbps =
                Math.Max(_peakDownloadMbps, downloadMbps);

            _peakUploadMbps =
                Math.Max(_peakUploadMbps, uploadMbps);

            _downloadTotalMbps += downloadMbps;
            _uploadTotalMbps += uploadMbps;
            _trafficSampleCount++;

            _viewModel.UpdateNetworkTraffic(
                downloadMbps,
                uploadMbps,
                _peakDownloadMbps,
                _peakUploadMbps,
                _downloadTotalMbps / _trafficSampleCount,
                _uploadTotalMbps / _trafficSampleCount,
                snapshot.InterfaceName);

            _previousTrafficSnapshot = snapshot;
        }

        public Task RefreshNowAsync()
        {
            return _refreshCoordinator
                .RunNowAsync(DashboardRefreshTask);
        }

        private void DashboardWindow_StateChanged(
            object? sender,
            EventArgs e)
        {
            if (WindowState == WindowState.Minimized &&
                Application.Current is App app)
            {
                Dispatcher.BeginInvoke(
                    new Action(app.HideDashboard));
            }
        }

        private async void DashboardWindow_IsVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                _previousTrafficSnapshot = null;
                _trafficBaselineRequired = true;

                if (IsLoaded)
                {
                    await _refreshCoordinator.SetEnabledAsync(
                        TrafficRefreshTask,
                        true);
                }

                return;
            }

            await _refreshCoordinator.SetEnabledAsync(
                TrafficRefreshTask,
                false);
        }

        protected override void OnClosing(
            CancelEventArgs e)
        {
            if (Application.Current is App app &&
                !app.IsExitRequested)
            {
                e.Cancel = true;
                app.HideDashboard();
                return;
            }

            base.OnClosing(e);
        }

        private static string FormatProtectionRemaining(
            TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return "Less than a minute remaining";
            }

            if (duration.TotalDays >= 1)
            {
                return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m remaining";
            }

            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes}m remaining";
            }

            return $"{Math.Max(1, duration.Minutes)}m remaining";
        }

        private async Task ShowConnectionErrorAsync(
            string message,
            bool notifyConnectivityChange = true)
        {
            if (notifyConnectivityChange)
                await UpdateRouterConnectivityAsync(isOnline: false);

            _viewModel.RouterConnected =
                false;

            _viewModel.InternetConnected =
                false;

            _viewModel.AdGuardRunning =
                false;

            _viewModel.ClearAdGuardStatistics();

            _viewModel.RouterModel =
                "Connection Failed";

            _viewModel.Hostname =
                "-";

            _viewModel.FirmwareVersion =
                "-";

            _viewModel.Uptime =
                "-";

            _viewModel.Temperature =
                "-";

            _viewModel.LoadAverage =
                "-";

            _viewModel.CpuUsage =
                "-";

            _viewModel.CpuUtilisationPending =
                false;

            _viewModel.MemoryUsage =
                "-";

            _viewModel.UpdateStorageUsage(
                null);

            _viewModel.AdGuardVersion =
                "-";

            _viewModel.AdGuardProcess =
                "-";

            _viewModel.AdGuardService =
                "-";

            _viewModel.AdGuardQueries =
                "-";

            _viewModel.AdGuardBlocked =
                "-";

            _viewModel.AdGuardBlockRate =
                "-";

            _viewModel.WanIp =
                "-";

            _viewModel.Gateway =
                "-";

            _viewModel.ExternalDns =
                "-";

            _viewModel.AdvertisedDns =
                "-";

            _viewModel.Latency =
                "-";

            ResetTrafficStatistics();
            _viewModel.ClearNetworkTraffic();

            _viewModel.StatusMessage =
                message;

            _viewModel.RefreshStatusIndicators();

            _viewModel.LastRefresh =
                "Last refresh: " +
                DateTime.Now.ToString(
                    "dd MMM yyyy HH:mm:ss");
        }

        private async Task UpdateRouterConnectivityAsync(bool isOnline)
        {
            if (_routerOnline == isOnline)
                return;

            _routerOnline = isOnline;

            await _notificationService.AddAsync(new AppNotification
            {
                Title = isOnline
                    ? "Router Online"
                    : "Router Offline",
                Message = isOnline
                    ? "Connection to the router has been restored."
                    : "Unable to communicate with the configured router.",
                Severity = isOnline
                    ? NotificationSeverity.Success
                    : NotificationSeverity.Error,
                Category = NotificationCategory.Router,
                EventType = isOnline
                    ? NotificationEventType.RouterRestored
                    : NotificationEventType.RouterOffline,
                DeduplicationKey = isOnline
                    ? "RouterOnline"
                    : "RouterOffline"
            });
        }

        public Task PrepareForShutdownAsync()
        {
            return _refreshCoordinator.DisposeAsync().AsTask();
        }

        private async void Refresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            await _refreshCoordinator.RunNowAsync(
                DashboardRefreshTask);
        }

        private void Overview_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content = CreateOverviewView();

            SelectNavigationButton(
                OverviewButton);
        }

        private OverviewView CreateOverviewView() => new(
            _maintenanceViewModel,
            _viewModel,
            RefreshNowAsync);

        public void NavigateToDnsActivity()
        {
            PageContent.Content = new LogsView();
            SelectNavigationButton(LogsButton);
        }

        private void Protection_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new ProtectionView();

            SelectNavigationButton(
                ProtectionButton);
        }

        private void Analytics_Click(
            object sender,
            RoutedEventArgs e)
        {
            var analyticsView = new AnalyticsView
            {
                DataContext = _viewModel
            };
            PageContent.Content = analyticsView;

            SelectNavigationButton(
                AnalyticsButton);
        }

        private void Network_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new NetworkView();

            SelectNavigationButton(
                NetworkButton);
        }

        private void Maintenance_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content = new MaintenanceView(
                _maintenanceViewModel,
                _viewModel,
                RefreshNowAsync);

            SelectNavigationButton(MaintenanceButton);
        }

        private void Clients_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new ClientsView();

            SelectNavigationButton(
                ClientsButton);
        }

        private void Logs_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new LogsView();

            SelectNavigationButton(
                LogsButton);
        }

        private void Search_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new GlobalSearchView();

            SelectNavigationButton(
                SearchButton);
        }

        private void Notification_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new NotificationCentreView(_notificationCentreViewModel);

            SelectNavigationButton(NotificationButton);
        }

        private void NavigationSettings_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content =
                new SettingsView();

            SelectNavigationButton(
                NavigationSettingsButton);
        }

        private void About_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content = new AboutView();
            SelectNavigationButton(AboutButton);
        }

        private void SelectNavigationButton(
            Button selectedButton)
        {
            Button[] navigationButtons =
            {
                OverviewButton,
                ProtectionButton,
                AnalyticsButton,
                NetworkButton,
                MaintenanceButton,
                ClientsButton,
                LogsButton,
                NotificationButton,
                SearchButton,
                NavigationSettingsButton,
                AboutButton
            };

            foreach (Button button in navigationButtons)
            {
                bool isSelected =
                    button == selectedButton;

                button.Background =
                    isSelected
                        ? _selectedNavigationBackground
                        : Brushes.Transparent;

                button.Foreground =
                    isSelected
                        ? Brushes.White
                        : _unselectedNavigationForeground;
            }
        }

        private void ProtectionStateNotifier_StateChanged(
            object? sender,
            AdGuardProtectionStatus status)
        {
            void ApplyState()
            {
                _viewModel.AdGuardProtectionEnabled =
                    status.IsEnabled;

                _viewModel.AdGuardProtectionPaused =
                    status.IsPaused;

                _viewModel.AdGuardProtectionStatusKnown =
                    true;

                _viewModel.AdGuardProtectionRemaining =
                    status.IsPaused
                        ? FormatProtectionRemaining(
                            status.RemainingPause)
                        : "";

                _viewModel.RefreshStatusIndicators();

                _viewModel.LastRefresh =
                    "Protection updated: " +
                    DateTime.Now.ToString(
                        "dd MMM yyyy HH:mm:ss");
            }

            if (Dispatcher.CheckAccess())
            {
                ApplyState();
            }
            else
            {
                Dispatcher.Invoke(ApplyState);
            }
        }

        protected override async void OnClosed(
            EventArgs e)
        {
            await PrepareForShutdownAsync();

            ProtectionStateNotifier.StateChanged -=
                ProtectionStateNotifier_StateChanged;

            _routerManagerUsageGate.Dispose();

            base.OnClosed(e);
        }

        private Task RunOnUiThreadAsync(Func<Task> callback)
        {
            if (Dispatcher.CheckAccess())
            {
                return callback();
            }

            return Dispatcher
                .InvokeAsync(callback)
                .Task
                .Unwrap();
        }
    }
}
