using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RouterPilot.Models;
using RouterPilot.Presentation;
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
        private const string PublicIpRefreshTask = "PublicIpRefresh";
        private const string AdGuardScheduleTask = "AdGuardServiceSchedules";
        private const string VpnScheduleTask = "VpnSchedules";
        private const string RouterFreshnessSource = "Router status";
        private const string InternetFreshnessSource = "Internet / WAN";
        private const string WifiFreshnessSource = "Wi-Fi";
        private const string DhcpFreshnessSource = "DHCP";
        private static readonly TimeSpan PortForwardDhcpFreshnessWindow = TimeSpan.FromSeconds(8);
        private const string AdGuardFreshnessSource = "AdGuard";
        private const string VpnFreshnessSource = "VPN";
        private const string TrafficFreshnessSource = "Network traffic";
        private static readonly TimeSpan InternetInstabilityWindow = TimeSpan.FromHours(1);
        private const int InternetInstabilityThreshold = 3;

        private readonly DashboardViewModel _viewModel;
        private readonly SettingsService _settingsService;
        private readonly NotificationService _notificationService;
        private readonly NotificationCentreViewModel _notificationCentreViewModel;
        private readonly MaintenanceViewModel _maintenanceViewModel;
        private readonly AdGuardProtectionNotificationTracker _protectionNotificationTracker;
        private readonly RefreshCoordinator _refreshCoordinator;
        private readonly AdGuardServiceScheduleService _scheduleService;
        private readonly VpnScheduleService _vpnScheduleService;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly AdGuardMaintenanceStateService _adGuardMaintenanceStateService;
        private readonly FirmwareUpdateService _firmwareUpdateService;
        private readonly IVpnSummaryService _vpnSummaryService;
        private readonly IPublicIpService _publicIpService;
        private readonly INetworkHealthService _networkHealthService;
        private readonly IDataFreshnessService _dataFreshnessService;
        private readonly IMetricHistoryService _metricHistoryService;
        private readonly TimelineService _timelineService;
        private readonly ClientProfileService _clientProfileService = new();
        private readonly SemaphoreSlim _routerManagerUsageGate = new(1, 1);
        private bool _refreshInProgress;
        private bool _trafficRefreshInProgress;
        private bool _initialFirmwareCheckScheduled;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private bool _routerOnline = true;
        private int _vpnNetworkContextRefreshQueued;
        private bool _healthSourcesReady;
        private bool? _observedInternetState;
        private bool _vpnStateObserved;

        private readonly NetworkTrafficAccumulator _trafficAccumulator = new();

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
            HeaderVersion.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "-");

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
            _vpnScheduleService = ((App)Application.Current).Services
                .GetRequiredService<VpnScheduleService>();
            _adGuardAvailabilityService = ((App)Application.Current).Services
                .GetRequiredService<AdGuardAvailabilityService>();
            _adGuardMaintenanceStateService = ((App)Application.Current).Services
                .GetRequiredService<AdGuardMaintenanceStateService>();
            _firmwareUpdateService = ((App)Application.Current).Services
                .GetRequiredService<FirmwareUpdateService>();
            _vpnSummaryService = ((App)Application.Current).Services
                .GetRequiredService<IVpnSummaryService>();
            _publicIpService = ((App)Application.Current).Services
                .GetRequiredService<IPublicIpService>();
            _networkHealthService = ((App)Application.Current).Services
                .GetRequiredService<INetworkHealthService>();
            _dataFreshnessService = ((App)Application.Current).Services
                .GetRequiredService<IDataFreshnessService>();
            _metricHistoryService = ((App)Application.Current).Services
                .GetRequiredService<IMetricHistoryService>();
            _timelineService = ((App)Application.Current).Services
                .GetRequiredService<TimelineService>();
            _vpnSummaryService.SummaryChanged += VpnSummaryService_SummaryChanged;
            _publicIpService.ResultChanged += PublicIpService_ResultChanged;
            _publicIpService.PublicIpChanged += PublicIpService_PublicIpChanged;
            _networkHealthService.SnapshotChanged += NetworkHealthService_SnapshotChanged;
            _dataFreshnessService.Changed += DataFreshnessService_Changed;
            _metricHistoryService.AvailabilityHistoryChanged += MetricHistoryService_AvailabilityHistoryChanged;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            _viewModel.VpnSummary = _vpnSummaryService.Current;
            ApplyPublicIpResult(_publicIpService.Current);
            _viewModel.NetworkHealth = _networkHealthService.Current;
            TimelineButton.DataContext = ((App)Application.Current).Services.GetRequiredService<TimelineService>();
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
                UpdateFirmwareHealthState();
            };
            UpdateFirmwareHealthState();
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
                PublicIpRefreshTask,
                TimeSpan.FromMinutes(10),
                cancellationToken => RefreshPublicIpAsync(forceRefresh: false, cancellationToken: cancellationToken),
                enabled: false);
            _refreshCoordinator.Register(
                AdGuardScheduleTask,
                TimeSpan.FromMinutes(1),
                cancellationToken => _scheduleService.EvaluateDueAsync(cancellationToken),
                enabled: false);
            _refreshCoordinator.Register(
                VpnScheduleTask,
                TimeSpan.FromMinutes(1),
                cancellationToken => _vpnScheduleService.EvaluateDueAsync(cancellationToken),
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

            if (_viewModel.InternetConnected)
            {
                _ = _refreshCoordinator.RunNowAsync(PublicIpRefreshTask);
            }
            await _refreshCoordinator.SetEnabledAsync(PublicIpRefreshTask, true);

            await _refreshCoordinator.RunNowAsync(AdGuardScheduleTask);
            await _refreshCoordinator.SetEnabledAsync(AdGuardScheduleTask, true);
            await _refreshCoordinator.RunNowAsync(VpnScheduleTask);
            await _refreshCoordinator.SetEnabledAsync(VpnScheduleTask, true);

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
                TimeSpan dashboardInterval = TimeSpan.FromSeconds(refreshSeconds);
                foreach (string source in new[] { RouterFreshnessSource, InternetFreshnessSource, WifiFreshnessSource, DhcpFreshnessSource, AdGuardFreshnessSource, VpnFreshnessSource })
                {
                    _dataFreshnessService.Configure(source, dashboardInterval);
                    _dataFreshnessService.MarkAttempt(source);
                }

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
                _dataFreshnessService.MarkSuccess(RouterFreshnessSource);

                _viewModel.RouterConnected =
                    true;

                _viewModel.RouterModel =
                    info.Model;

                _viewModel.Hostname =
                    info.Hostname;

                _viewModel.FirmwareVersion =
                    info.Firmware;

                // A changed installed version makes the old cached availability
                // result unresolved until the single startup check completes.
                if (!string.IsNullOrWhiteSpace(info.Firmware) &&
                    !string.Equals(_firmwareUpdateService.Current.CurrentVersion, info.Firmware,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _viewModel.FirmwareUpdateStatus = FirmwareUpdateCheckStatus.Pending;
                    _viewModel.FirmwareLatestVersion = string.Empty;
                }

                ScheduleInitialFirmwareCheck(router, info.Firmware);

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

                _viewModel.MemoryUsed =
                    info.MemoryUsed;

                _viewModel.MemoryCache =
                    info.MemoryCache;

                _viewModel.UpdateStorageUsage(
                    info.StorageUsage);

                // Router and AdGuard work are independent. Start both groups
                // together, then apply each successful result separately.
                Task wifiTask = RefreshWifiNetworksAsync(router, cancellationToken);
                Task dhcpTask = RefreshDhcpAsync(router, cancellationToken);
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
                await Task.WhenAll(wifiTask, dhcpTask, adGuardTask);
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
                _dataFreshnessService.MarkSuccess(InternetFreshnessSource);

                await _vpnSummaryService.RefreshAsync(cancellationToken);
                if (_vpnSummaryService.Current.IsAvailable)
                    _dataFreshnessService.MarkSuccess(VpnFreshnessSource);
                else
                    _dataFreshnessService.MarkUnavailable(VpnFreshnessSource);

                _viewModel.StatusMessage = _viewModel.AdGuardAvailability ==
                    AdGuardAvailabilityState.Available
                        ? "Connected"
                        : "Connected - AdGuard Home unavailable";

                await UpdateRouterConnectivityAsync(isOnline: true);

                _healthSourcesReady = true;
                // History is a best-effort sink. It must never participate in
                // the router refresh success/failure path or replace live state.
                _ = RecordMetricAndReliabilityHistoryAsync();
                EvaluateNetworkHealth();

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
                    OperationFailurePolicy.UserMessage(
                        ex,
                        "Dashboard router refresh",
                        "Unable to communicate with the router."),
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

        private void ScheduleInitialFirmwareCheck(RouterManager router, string currentVersion)
        {
            if (_initialFirmwareCheckScheduled)
                return;

            _initialFirmwareCheckScheduled = true;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                if (IsLoaded && _viewModel.RouterConnected)
                    await _firmwareUpdateService.CheckAutomaticallyAsync(router, currentVersion);
            });
        }

        private void UpdateFirmwareHealthState()
        {
            FirmwareUpdateCheck cached = _firmwareUpdateService.Current;
            _viewModel.FirmwareUpdateStatus = cached.Status;
            _viewModel.FirmwareLatestVersion = cached.LatestVersion ?? string.Empty;
        }

        private async Task RefreshAdGuardAsync(
            RouterManager router,
            CancellationToken cancellationToken)
        {
            try
            {
                AdGuardStatus serviceStatus = await router.GetAdGuardStatusAsync();
                cancellationToken.ThrowIfCancellationRequested();
                _dataFreshnessService.MarkSuccess(AdGuardFreshnessSource);

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
                _dataFreshnessService.MarkUnavailable(AdGuardFreshnessSource);
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
                _dataFreshnessService.MarkSuccess(WifiFreshnessSource);
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
                _dataFreshnessService.MarkUnavailable(WifiFreshnessSource);
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
            _dataFreshnessService.Configure(TrafficFreshnessSource, TimeSpan.FromSeconds(2));
            _dataFreshnessService.MarkAttempt(TrafficFreshnessSource);

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
                _dataFreshnessService.MarkSuccess(TrafficFreshnessSource);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                _dataFreshnessService.MarkUnavailable(TrafficFreshnessSource);
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
            _trafficAccumulator.Reset();
        }

        private void UpdateNetworkTraffic(
            NetworkTrafficSnapshot snapshot)
        {
            NetworkTrafficSample? traffic = _trafficAccumulator.Add(new NetworkTrafficObservation(
                snapshot.ReceivedBytes,
                snapshot.TransmittedBytes,
                snapshot.CapturedAtUtc));
            if (traffic is null)
            {
                return;
            }

            _viewModel.UpdateNetworkTraffic(
                traffic.Value.DownloadMbps,
                traffic.Value.UploadMbps,
                traffic.Value.PeakDownloadMbps,
                traffic.Value.PeakUploadMbps,
                traffic.Value.AverageDownloadMbps,
                traffic.Value.AverageUploadMbps,
                snapshot.InterfaceName);
            _metricHistoryService.RecordMetric(MetricKind.WanDownloadMbps, traffic.Value.DownloadMbps, snapshot.CapturedAtUtc);
            _metricHistoryService.RecordMetric(MetricKind.WanUploadMbps, traffic.Value.UploadMbps, snapshot.CapturedAtUtc);
        }

        public async Task RefreshNowAsync()
        {
            await _refreshCoordinator.RunNowAsync(DashboardRefreshTask);
            if (_viewModel.InternetConnected)
            {
                await RefreshPublicIpAsync(forceRefresh: true, CancellationToken.None);
            }
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
            try
            {
                if (IsVisible)
                {
                    _trafficAccumulator.ResetBaseline();

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
            catch (ObjectDisposedException)
            {
                // Visibility can change after shutdown has disposed the refresh
                // coordinator. An async event handler must not crash the app.
            }
        }

        /// <summary>Performs one user-requested DHCP refresh without scheduling work.</summary>
        public async Task<bool> RefreshDhcpStateAsync(bool forceConfigurationRefresh, CancellationToken cancellationToken = default)
        {
            DataFreshnessInfo freshness = _dataFreshnessService.Get(DhcpFreshnessSource);
            if (!forceConfigurationRefresh && freshness.LastSuccessUtc is { } lastSuccess && DateTimeOffset.UtcNow - lastSuccess <= PortForwardDhcpFreshnessWindow)
                return true;

            RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            return await RefreshDhcpAsync(router, cancellationToken, forceConfigurationRefresh: true);
        }

        private async Task<bool> RefreshDhcpAsync(
            RouterManager router,
            CancellationToken cancellationToken,
            bool forceConfigurationRefresh = false)
        {
            try
            {
                DhcpSnapshot snapshot = await router.GetDhcpSnapshotAsync(forceConfigurationRefresh);
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.UpdateDhcpSnapshot(snapshot, _clientProfileService.Load());
                _dataFreshnessService.MarkSuccess(DhcpFreshnessSource);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _dataFreshnessService.MarkUnavailable(DhcpFreshnessSource);
                Debug.WriteLine($"[DhcpRefresh] failed category={ex.GetType().Name}");
                return false;
            }
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
            foreach (string source in new[] { RouterFreshnessSource, InternetFreshnessSource, WifiFreshnessSource, DhcpFreshnessSource, AdGuardFreshnessSource, VpnFreshnessSource })
                _dataFreshnessService.MarkUnavailable(source);
            bool hasPreviousRouterSample = _dataFreshnessService.Get(RouterFreshnessSource).LastSuccessUtc is not null;
            if (hasPreviousRouterSample)
            {
                _viewModel.StatusMessage = $"Refresh failed: {message} Showing the last successful data.";
                _dataFreshnessService.Refresh();
                EvaluateNetworkHealth();
                return;
            }
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

            _viewModel.MemoryUsed =
                "-";

            _viewModel.MemoryCache =
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
            _vpnSummaryService.MarkUnavailable();

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

            if (!isOnline && _healthSourcesReady)
            {
                _viewModel.RouterConnected = false;
                EvaluateNetworkHealth();
            }

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

            if (isOnline && _viewModel.InternetConnected)
            {
                _ = RefreshPublicIpAsync(forceRefresh: true, CancellationToken.None);
            }
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

        public void NavigateToHealthTarget(string target)
        {
            if (string.Equals(target, "protection", StringComparison.OrdinalIgnoreCase))
                Protection_Click(this, new RoutedEventArgs());
            else if (string.Equals(target, "analytics", StringComparison.OrdinalIgnoreCase))
                Analytics_Click(this, new RoutedEventArgs());
            else
                Network_Click(this, new RoutedEventArgs());
        }

        public void NavigateToDnsActivity()
        {
            PageContent.Content = new LogsView();
            SelectNavigationButton(LogsButton);
        }

        public void NavigateToGlobalSearchResult(GlobalSearchResult result)
        {
            switch (result.NavigationTarget)
            {
                case "overview": Overview_Click(this, new RoutedEventArgs()); break;
                case "clients":
                    if (string.Equals(result.Category, "Client", StringComparison.OrdinalIgnoreCase))
                    {
                        OpenGlobalSearchClientDetails(result);
                    }
                    else
                    {
                        Clients_Click(this, new RoutedEventArgs());
                    }
                    break;
                case "known-device":
                    var profile = new ClientProfileService().Load().GetValueOrDefault(result.EntityId);
                    if (profile is not null)
                    {
                        var known = new KnownDeviceInfo { Profile = profile };
                        new ClientDetailsWindow(known.ToClientInfo(), allowLiveRefresh: false) { Owner = this }.ShowDialog();
                    }
                    else ShowKnownDevices();
                    break;
                case "protection": Protection_Click(this, new RoutedEventArgs()); break;
                case "analytics": Analytics_Click(this, new RoutedEventArgs()); break;
                case "timeline": Timeline_Click(this, new RoutedEventArgs()); break;
                case "settings": NavigationSettings_Click(this, new RoutedEventArgs()); break;
                case "health": NavigateToHealthTarget("network"); break;
                case "wifi": case "dhcp": case "port-forward": case "vpn":
                    var network = new NetworkView();
                    PageContent.Content = network;
                    SelectNavigationButton(NetworkButton);
                    network.NavigateToSection(result.NavigationTarget);
                    break;
            }
        }

        private void OpenGlobalSearchClientDetails(GlobalSearchResult result)
        {
            string macKey = ClientIdentity.NormalizeHexMac(result.EntityId);
            if (macKey.Length != 12)
            {
                return;
            }

            ClientInventoryState inventory =
                ((App)Application.Current).Services.GetRequiredService<ClientInventoryState>();

            if (inventory.Snapshot.TryGetValue(macKey, out ClientInfo? liveClient))
            {
                OpenClientDetailsFromGlobalSearch(liveClient);
                return;
            }

            // Resolve again when activated so a device that disappeared since the
            // search result was built can still open its existing offline details.
            ClientProfile? profile = _clientProfileService.Load().GetValueOrDefault(macKey);
            if (profile is not null)
            {
                var known = new KnownDeviceInfo { Profile = profile };
                OpenClientDetailsFromGlobalSearch(known.ToClientInfo(), allowLiveRefresh: false);
            }
        }

        private void OpenClientDetailsFromGlobalSearch(ClientInfo client, bool allowLiveRefresh = true)
        {
            new ClientDetailsWindow(client, allowLiveRefresh) { Owner = this }.ShowDialog();
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
            var analyticsView = new AnalyticsView(
                ((App)Application.Current).Services.GetRequiredService<IInternetSpeedTestService>(),
                ((App)Application.Current).Services.GetRequiredService<SettingsService>(),
                _viewModel);
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
            ShowClients();
        }

        public void ShowClients()
        {
            PageContent.Content = new ClientsView();
            SelectNavigationButton(ClientsButton);
        }

        public void ShowKnownDevices()
        {
            PageContent.Content = new KnownDevicesView();
            SelectNavigationButton(ClientsButton);
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

        private void Timeline_Click(
            object sender,
            RoutedEventArgs e)
        {
            PageContent.Content = new TimelineView();
            SelectNavigationButton(TimelineButton);
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
                TimelineButton,
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
            _vpnSummaryService.SummaryChanged -= VpnSummaryService_SummaryChanged;
            _publicIpService.ResultChanged -= PublicIpService_ResultChanged;
            _publicIpService.PublicIpChanged -= PublicIpService_PublicIpChanged;
            _networkHealthService.SnapshotChanged -= NetworkHealthService_SnapshotChanged;
            _dataFreshnessService.Changed -= DataFreshnessService_Changed;
            _metricHistoryService.AvailabilityHistoryChanged -= MetricHistoryService_AvailabilityHistoryChanged;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;

            _routerManagerUsageGate.Dispose();

            base.OnClosed(e);
        }

        private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            _dataFreshnessService.BeginReestablishmentWindow(TimeSpan.FromMinutes(2));
        }

        private void VpnSummaryService_SummaryChanged(VpnSummaryState summary)
        {
            // A live VPN summary is an authoritative update even when it did
            // not originate from the dashboard refresh request.
            if (summary.IsAvailable)
                _dataFreshnessService.MarkSuccess(VpnFreshnessSource);
            else
                _dataFreshnessService.MarkUnavailable(VpnFreshnessSource);
            void Apply()
            {
                string previousState = _viewModel.VpnSummary.State;
                _viewModel.VpnSummary = summary;

                if (_vpnStateObserved && IsVpnNetworkContextTransition(previousState, summary.State))
                {
                    bool connected = string.Equals(summary.State, "Connected", StringComparison.Ordinal);
                    _ = _timelineService.AddAsync(new TimelineEvent
                    {
                        Category = TimelineCategory.Router,
                        EventType = connected ? TimelineEventType.VpnConnected : TimelineEventType.VpnDisconnected,
                        Title = connected ? "VPN connected" : "VPN disconnected",
                        Message = summary.ProfileName ?? "VPN tunnel",
                        Severity = connected ? TimelineSeverity.Success : TimelineSeverity.Warning,
                        Source = "VPN"
                    });
                }
                _vpnStateObserved = true;

                if (IsVpnNetworkContextTransition(previousState, summary.State))
                {
                    _ = RefreshNetworkContextForVpnTransitionAsync();
                    _ = RefreshPublicIpAsync(forceRefresh: true, CancellationToken.None);
                }
            }
            if (Dispatcher.CheckAccess()) Apply();
            else _ = Dispatcher.InvokeAsync(Apply);
        }

        private static bool IsVpnNetworkContextTransition(string previousState, string currentState) =>
            (string.Equals(currentState, "Connected", StringComparison.Ordinal) &&
             !string.Equals(previousState, "Connected", StringComparison.Ordinal)) ||
            (string.Equals(previousState, "Connected", StringComparison.Ordinal) &&
             string.Equals(currentState, "Disconnected", StringComparison.Ordinal));

        private async Task RefreshNetworkContextForVpnTransitionAsync()
        {
            if (!IsLoaded || Interlocked.Exchange(ref _vpnNetworkContextRefreshQueued, 1) != 0)
            {
                return;
            }

            try
            {
                // Let the authoritative VPN state settle before re-reading the
                // existing WAN/DNS source once. This is deliberately not polling.
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                await _routerManagerUsageGate.WaitAsync();
                try
                {
                    RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(CancellationToken.None);
                    NetworkInfo network = await router.GetNetworkInfoAsync();
                    _viewModel.InternetConnected = network.Connected;
                    _viewModel.WanIp = network.WanIp;
                    _viewModel.Gateway = network.Gateway;
                    _viewModel.ExternalDns = network.ExternalDns;
                    _viewModel.AdvertisedDns = network.AdvertisedDns;
                    _viewModel.Latency = network.Latency;
                    _viewModel.RefreshStatusIndicators();
                }
                finally
                {
                    _routerManagerUsageGate.Release();
                }
            }
            catch
            {
                // The normal scheduled refresh remains the recovery path.
            }
            finally
            {
                Interlocked.Exchange(ref _vpnNetworkContextRefreshQueued, 0);
            }
        }

        private async Task RefreshPublicIpAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!_viewModel.InternetConnected)
            {
                return;
            }

            try
            {
                await _publicIpService.RefreshAsync(forceRefresh, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void PublicIpService_ResultChanged(PublicIpResult result)
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyPublicIpResult(result);
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => ApplyPublicIpResult(result));
            }
        }

        private void ApplyPublicIpResult(PublicIpResult result)
        {
            _viewModel.PublicIp = result.PublicIp ?? string.Empty;
            _viewModel.PublicIpStatus = result.Status;
        }

        private async void PublicIpService_PublicIpChanged(string? previousIp, string currentIp)
        {
            string vpnState = _viewModel.VpnSummary.State;
            string context = string.Equals(vpnState, "Connected", StringComparison.Ordinal) ? "VPN active" : string.Equals(vpnState, "Disconnected", StringComparison.Ordinal) ? "VPN inactive" : "VPN state changing";
            await _timelineService.AddAsync(new TimelineEvent { Category = TimelineCategory.Router, EventType = TimelineEventType.PublicIpChanged, Title = "Public IP changed", Message = string.IsNullOrWhiteSpace(previousIp) ? context : $"{previousIp} → {currentIp} • {context}", Severity = TimelineSeverity.Information, DeduplicationKey = $"public-ip:{previousIp}:{currentIp}" });
        }

        private async Task RecordMetricAndReliabilityHistoryAsync()
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (_viewModel.CpuUsage != "-") _metricHistoryService.RecordMetric(MetricKind.CpuPercent, _viewModel.CpuPercentage, now);
                if (_viewModel.MemoryUsage != "-") _metricHistoryService.RecordMetric(MetricKind.MemoryPercent, _viewModel.MemoryPercentage, now);
                bool online = _viewModel.InternetConnected;
                bool changed = _observedInternetState.HasValue && _observedInternetState.Value != online;
                await _metricHistoryService.RecordInternetStateAsync(online, now);
                if (changed) await _timelineService.AddAsync(new TimelineEvent { Category = TimelineCategory.Router, EventType = online ? TimelineEventType.InternetConnectionRestored : TimelineEventType.InternetConnectionLost, Title = online ? "Internet connection restored" : "Internet connection lost", Message = online ? "Internet connectivity is available again." : "Internet connectivity is unavailable.", Severity = online ? TimelineSeverity.Success : TimelineSeverity.Warning, DeduplicationKey = $"internet:{(online ? "online" : "offline")}:{now:yyyyMMddHHmm}" });
                _observedInternetState = online;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Metric history recording failed without affecting dashboard refresh ({ex.GetType().Name}).");
            }
        }

        private void EvaluateNetworkHealth()
        {
            IReadOnlyList<DataFreshnessInfo> stale = _dataFreshnessService.GetAll()
                .Where(info => info.State == DataFreshnessState.Stale && info.Source != TrafficFreshnessSource)
                .ToList();
            DataFreshnessInfo? oldestStale = stale.MinBy(info => info.LastSuccessUtc);
            DateTimeOffset freshnessDetectedAt = oldestStale?.LastSuccessUtc ?? DateTimeOffset.UtcNow;
            _networkHealthService.SetDataFreshnessIssues(stale.Count == 0
                ? []
                : [new NetworkHealthIssue(
                    "data.refresh_delayed",
                    NetworkHealthSeverity.Warning,
                    "System",
                    "Data refresh delayed",
                    $"{string.Join(", ", stale.Select(info => info.Source))} has not updated for {FormatFreshnessAge(oldestStale?.LastSuccessUtc)}.",
                    "overview",
                    freshnessDetectedAt,
                    DateTimeOffset.UtcNow,
                    freshnessDetectedAt.UtcTicks.ToString())]);
            InternetInstabilitySummary instability = _metricHistoryService.GetInternetInstability(
                InternetInstabilityWindow,
                DateTimeOffset.UtcNow,
                InternetInstabilityThreshold);
            _networkHealthService.Evaluate(new NetworkHealthInput(
                _healthSourcesReady, _viewModel.RouterConnected, _viewModel.InternetConnected,
                _viewModel.AdGuardMaintenanceState, _viewModel.CpuHistory.ToList(), _viewModel.MemoryHistory.ToList(),
                instability.OutageCount, instability.ObservedDuration, instability.ThresholdReachedAt));
        }

        private void DataFreshnessService_Changed()
        {
            void Apply()
            {
                DataFreshnessInfo router = _dataFreshnessService.Get(RouterFreshnessSource);
                _viewModel.DataFreshnessFooter = router.State switch
                {
                    DataFreshnessState.Fresh => "Updated " + FormatFreshnessAge(router.LastSuccessUtc) + " ago",
                    DataFreshnessState.Stale => "Stale • " + FormatFreshnessAge(router.LastSuccessUtc),
                    DataFreshnessState.Unavailable => "Data unavailable",
                    _ => "Loading data"
                };
                _viewModel.DataFreshnessColour = RouterPilotStatusPresentation.Colour(router.State switch
                {
                    DataFreshnessState.Fresh => RouterPilotStatus.Active,
                    DataFreshnessState.Stale => RouterPilotStatus.Pending,
                    DataFreshnessState.Unavailable => RouterPilotStatus.Error,
                    _ => RouterPilotStatus.Pending
                });
                if (_healthSourcesReady) EvaluateNetworkHealth();
            }
            if (Dispatcher.CheckAccess()) Apply(); else _ = Dispatcher.InvokeAsync(Apply);
        }

        private static string FormatFreshnessAge(DateTimeOffset? timestamp)
        {
            if (timestamp is null) return "unknown";
            TimeSpan age = DateTimeOffset.UtcNow - timestamp.Value;
            return age < TimeSpan.FromSeconds(5) ? "just now" : age < TimeSpan.FromMinutes(1) ? $"{Math.Max(1, (int)age.TotalSeconds)} sec" : $"{Math.Max(1, (int)age.TotalMinutes)} min";
        }

        private void MetricHistoryService_AvailabilityHistoryChanged(object? sender, EventArgs e)
        {
            if (!_healthSourcesReady || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.InvokeAsync(EvaluateNetworkHealth);
        }

        private void NetworkHealthService_SnapshotChanged(NetworkHealthSnapshot snapshot)
        {
            if (Dispatcher.CheckAccess()) _viewModel.NetworkHealth = snapshot;
            else _ = Dispatcher.InvokeAsync(() => _viewModel.NetworkHealth = snapshot);
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
