using System;
using System.Diagnostics;
using System.Windows;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.Tray;
using RouterPilot.Views;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot
{
    public partial class App : Application
    {
        private DashboardWindow? _dashboardWindow;
        private TrayManager? _trayManager;
        private bool _trayNoticeShown;
        private ServiceProvider? _services;
        private SingleInstanceCoordinator? _singleInstance;
        private bool _activationRequestedDuringStartup;

        public IServiceProvider Services => _services
            ?? throw new InvalidOperationException("Application services are not available.");

        public bool IsExitRequested { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                if (!SingleInstanceCoordinator.TryAcquire(
                        RequestDashboardActivation,
                        out _singleInstance))
                {
                    Shutdown();
                    return;
                }
            }
            catch (Exception)
            {
                MessageBox.Show(
                    "RouterPilot could not verify whether another instance is running.",
                    "RouterPilot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            var applicationDataPaths = new ApplicationDataPathProvider();
            applicationDataPaths.MigrateLegacyData();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(applicationDataPaths);
            serviceCollection.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Dispatcher));
            serviceCollection.AddSingleton<SettingsService>();
            serviceCollection.AddSingleton<IPublicIpService, PublicIpService>();
            serviceCollection.AddSingleton<DashboardPreferencesService>();
            serviceCollection.AddSingleton<ISshHostKeyTrustService,
                SshHostKeyTrustService>();
            serviceCollection.AddSingleton<IRouterCertificateTrustService,
                RouterCertificateTrustService>();
            serviceCollection.AddSingleton<AdGuardTransportSecurityService>();
            serviceCollection.AddSingleton<IToastNotificationService, WindowsToastNotificationService>();
            serviceCollection.AddSingleton<IRouterManagerProvider,
                RouterManagerProvider>();
            serviceCollection.AddSingleton(
                sp => new NotificationService(
                    Dispatcher,
                    sp.GetRequiredService<ApplicationDataPathProvider>(),
                    settingsService: sp.GetRequiredService<SettingsService>(),
                    toastNotificationService: sp.GetRequiredService<IToastNotificationService>()));
            serviceCollection.AddSingleton(
                sp => new MaintenanceHistoryService(
                    Dispatcher,
                    sp.GetRequiredService<ApplicationDataPathProvider>()));
            serviceCollection.AddSingleton(
                sp => new TimelineService(
                    Dispatcher,
                    sp.GetRequiredService<ApplicationDataPathProvider>()));
            serviceCollection.AddSingleton<IMetricHistoryService, MetricHistoryService>();
            serviceCollection.AddSingleton<IDataFreshnessService, DataFreshnessService>();
            serviceCollection.AddSingleton<IClientPresenceHistoryService, ClientPresenceHistoryService>();
            serviceCollection.AddSingleton<ClientProfileService>();
            serviceCollection.AddSingleton<ClientInventoryState>();
            serviceCollection.AddSingleton<ClientInventoryCoordinator>();
            serviceCollection.AddSingleton<KnownDeviceForgetService>();
            serviceCollection.AddSingleton<INetworkHealthService, NetworkHealthService>();
            serviceCollection.AddSingleton<FavouriteDeviceMonitoringService>();
            serviceCollection.AddSingleton(sp => new DiagnosticsHistoryService(Dispatcher));
            serviceCollection.AddSingleton<DiagnosticsExecutionService>();
            serviceCollection.AddSingleton<IBackupRestoreService, BackupRestoreService>();
            serviceCollection.AddSingleton<MaintenanceOperationService>();
            serviceCollection.AddSingleton<FirmwareUpdateService>();
            serviceCollection.AddSingleton<IInternetSpeedTestService, InternetSpeedTestService>();
            serviceCollection.AddSingleton<DataStatisticsService>();
            serviceCollection.AddTransient<ApplicationTrafficDetailsViewModel>();
            serviceCollection.AddSingleton<DhcpReservationValidator>();
            serviceCollection.AddSingleton<IDhcpReservationService, DhcpReservationService>();
            serviceCollection.AddSingleton<IPortForwardService, PortForwardService>();
            serviceCollection.AddSingleton<ILanClientService, LanClientService>();
            serviceCollection.AddSingleton<IVpnService, VpnService>();
            serviceCollection.AddSingleton<IVpnLiveStatusService, VpnLiveStatusService>();
            serviceCollection.AddSingleton<IVpnSummaryService, VpnSummaryService>();
            serviceCollection.AddSingleton(sp => new VpnScheduleService(
                Dispatcher,
                sp.GetRequiredService<IVpnService>(),
                sp.GetRequiredService<IVpnSummaryService>(),
                sp.GetRequiredService<TimelineService>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ApplicationDataPathProvider>()));
            serviceCollection.AddSingleton<VpnViewModel>();
            serviceCollection.AddSingleton<MaintenanceViewModel>();
            serviceCollection.AddSingleton<UpdateService>();
            serviceCollection.AddSingleton<IClock, SystemClock>();
            serviceCollection.AddSingleton<BlockedServiceMutationService>();
            serviceCollection.AddSingleton<IAdGuardServiceCatalogueProvider>(sp =>
                new AdGuardServiceCatalogueProvider(
                    sp.GetRequiredService<IRouterManagerProvider>(), Dispatcher));
            serviceCollection.AddSingleton(sp => new AdGuardServiceScheduleService(
                Dispatcher,
                sp.GetRequiredService<BlockedServiceMutationService>(),
                sp.GetRequiredService<NotificationService>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ApplicationDataPathProvider>()));
            serviceCollection.AddSingleton<AdGuardServiceScheduleViewModel>();
            serviceCollection.AddSingleton<AdGuardProtectionNotificationTracker>();
            serviceCollection.AddSingleton<AdGuardAvailabilityService>();
            serviceCollection.AddSingleton<AdGuardMaintenanceStateService>();
            serviceCollection.AddSingleton<NewDeviceNotificationTracker>();
            serviceCollection.AddSingleton<NotificationCentreViewModel>();
            serviceCollection.AddSingleton<TimelineViewModel>();
            // The dashboard window and read-only projections share this single
            // application state instance.
            serviceCollection.AddSingleton<DashboardViewModel>();
            serviceCollection.AddTransient<ClientsViewModel>();
            serviceCollection.AddTransient<KnownDevicesViewModel>();
            serviceCollection.AddTransient<LogsViewModel>();
            // This is existing Analytics state, retained so read-only surfaces can project it.
            serviceCollection.AddSingleton<DataStatisticsViewModel>();
            // The Overview and Network Health tab project the same read-only state.
            serviceCollection.AddSingleton<NetworkHealthViewModel>();
            serviceCollection.AddSingleton<ProtectionViewModel>();
            serviceCollection.AddTransient<GlobalSearchViewModel>();
            serviceCollection.AddTransient<SettingsViewModel>();
            _services = serviceCollection.BuildServiceProvider();

            await Services.GetRequiredService<NotificationService>()
                .InitializeAsync();
            await Services.GetRequiredService<MaintenanceHistoryService>()
                .InitializeAsync();
            _ = Services.GetRequiredService<TimelineService>().InitializeAsync();
            await Services.GetRequiredService<IMetricHistoryService>().InitializeAsync();
            _ = Services.GetRequiredService<IClientPresenceHistoryService>();
            _ = Services.GetRequiredService<IInternetSpeedTestService>().InitializeAsync();
            await Services.GetRequiredService<AdGuardServiceScheduleService>()
                .InitializeAsync();
            await Services.GetRequiredService<VpnScheduleService>().InitializeAsync();

            AppSettings savedSettings = Services
                .GetRequiredService<SettingsService>()
                .Load();
            ThemeService.Initialize(savedSettings.Theme);

            if (!HasUsableSavedSettings())
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Window settingsWindow = CreateFirstRunSettingsWindow();
                MainWindow = settingsWindow;
                settingsWindow.Show();
                return;
            }

            StartMainApplication();
        }

        public void CompleteFirstRun(Window settingsWindow)
        {
            if (!HasUsableSavedSettings())
            {
                MessageBox.Show(
                    "The router settings are incomplete or the saved password could not be read.",
                    "RouterPilot Setup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            StartMainApplication();
            settingsWindow.Close();
        }

        private void StartMainApplication()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (_dashboardWindow is null)
            {
                _dashboardWindow = new DashboardWindow();
                _dashboardWindow.Closed += (_, _) =>
                {
                    if (IsExitRequested)
                        _dashboardWindow = null;
                };
            }

            _trayManager ??= new TrayManager(
                ShowDashboard,
                RefreshDashboard,
                ExitApplication);

            MainWindow = _dashboardWindow;
            bool activateAfterStartup = _activationRequestedDuringStartup;
            _activationRequestedDuringStartup = false;
            ShowDashboard();

            if (activateAfterStartup)
                Dispatcher.BeginInvoke(ShowDashboard);
        }

        private void RequestDashboardActivation()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (IsExitRequested)
                    return;

                if (_dashboardWindow is null)
                {
                    _activationRequestedDuringStartup = true;
                    return;
                }

                _activationRequestedDuringStartup = false;
                ShowDashboard();
            });
        }

        public void HideDashboard()
        {
            if (_dashboardWindow is null || IsExitRequested)
                return;

            _dashboardWindow.Hide();

            if (!_trayNoticeShown)
            {
                _trayManager?.ShowStillRunningMessage();
                _trayNoticeShown = true;
            }
        }

        public void ShowDashboard()
        {
            if (_dashboardWindow is null)
                return;

            if (!_dashboardWindow.IsVisible)
                _dashboardWindow.Show();

            if (_dashboardWindow.WindowState == WindowState.Minimized)
                _dashboardWindow.WindowState = WindowState.Normal;

            _dashboardWindow.Activate();
            _dashboardWindow.Topmost = true;
            _dashboardWindow.Topmost = false;
            _dashboardWindow.Focus();
        }

        private void RefreshDashboard() => _ = RefreshDashboardAsync();

        private async Task RefreshDashboardAsync()
        {
            try
            {
                ShowDashboard();
                if (_dashboardWindow is not null)
                    await _dashboardWindow.RefreshNowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray dashboard refresh failed ({DiagnosticRedactor.FailureCategory(ex)}).");
            }
        }

        private void ExitApplication() => _ = ExitApplicationSafelyAsync();

        private async Task ExitApplicationSafelyAsync()
        {
            try
            {
                await ExitApplicationAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray application exit failed ({DiagnosticRedactor.FailureCategory(ex)}).");
            }
        }

        public Task RestartAsync() => ExitApplicationAsync(Environment.ProcessPath);

        private async Task ExitApplicationAsync(string? restartPath = null)
        {
            IsExitRequested = true;
            _trayManager?.Dispose();
            _trayManager = null;

            if (_dashboardWindow is not null)
            {
                await _dashboardWindow.PrepareForShutdownAsync();
                _dashboardWindow.Close();
            }

            _dashboardWindow = null;

            if (_services is not null)
            {
                try
                {
                    await _services.GetRequiredService<AdGuardServiceScheduleService>().DisposeAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unable to flush AdGuard service schedules during shutdown ({ex.GetType().Name}).");
                }

                try
                {
                    await _services
                        .GetRequiredService<MaintenanceHistoryService>()
                        .FlushAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Unable to flush maintenance history during shutdown ({ex.GetType().Name}).");
                }

                try
                {
                    await _services.GetRequiredService<TimelineService>().FlushAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unable to flush Timeline history during shutdown ({ex.GetType().Name}).");
                }

                try
                {
                    await _services.GetRequiredService<VpnScheduleService>().FlushAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unable to flush VPN schedules during shutdown ({ex.GetType().Name}).");
                }

                try
                {
                    await _services.GetRequiredService<IMetricHistoryService>().FlushAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unable to flush metric history during shutdown ({ex.GetType().Name}).");
                }

                try
                {
                    await _services
                        .GetRequiredService<NotificationService>()
                        .DisposeAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Unable to flush notification history during shutdown ({ex.GetType().Name}).");
                }

                try
                {
                    await _services.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Unable to dispose application services cleanly ({ex.GetType().Name}).");
                }

                _services = null;
            }

            if (_singleInstance is not null)
            {
                await _singleInstance.DisposeAsync();
                _singleInstance = null;
            }

            if (!string.IsNullOrWhiteSpace(restartPath))
            {
                Process.Start(new ProcessStartInfo(restartPath)
                {
                    UseShellExecute = true
                });
            }

            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_services is not null)
            {
                try
                {
                    Services.GetRequiredService<IClientPresenceHistoryService>().CloseSession();
                }
                catch
                {
                    // Shutdown must continue even if optional local history cannot be flushed.
                }
            }
            _trayManager?.Dispose();
            _singleInstance?.Dispose();
            _singleInstance = null;
            base.OnExit(e);
        }

        private bool HasUsableSavedSettings()
        {
            try
            {
                var settingsService = Services.GetRequiredService<SettingsService>();
                AppSettings settings = settingsService.Load();

                if (string.IsNullOrWhiteSpace(settings.RouterHost) ||
                    string.IsNullOrWhiteSpace(settings.Username))
                    return false;

                if (!settings.RememberPassword ||
                    string.IsNullOrWhiteSpace(settings.EncryptedPassword))
                    return false;

                string password = settingsService.DecryptPassword(
                    settings.EncryptedPassword);

                return !string.IsNullOrWhiteSpace(password);
            }
            catch
            {
                return false;
            }
        }

        private static Window CreateFirstRunSettingsWindow()
        {
            return new Window
            {
                Title = "RouterPilot Setup",
                Width = 920,
                Height = 700,
                MinWidth = 760,
                MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new SettingsView()
            };
        }
    }
}
