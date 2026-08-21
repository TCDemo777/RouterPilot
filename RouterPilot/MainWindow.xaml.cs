using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Diagnostics;
using System.Windows;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot
{
    public partial class MainWindow : Window
    {
        private TaskbarIcon? trayIcon;
        private SettingsService? _settingsService;
        private RouterEndpointProvider? _endpoints;
        private RouterService? _routerService;

        public MainWindow()
        {
            InitializeComponent();

            if (!TryInitialiseServices())
            {
                Application.Current.Shutdown();
                return;
            }

            Hide();
            BuildTrayIcon();
        }

        private bool TryInitialiseServices()
        {
            _settingsService = new SettingsService();
            AppSettings settings = _settingsService.Load();

            if (!settings.IsConfigured)
            {
                new Views.SettingsWindow().ShowDialog();
                settings = _settingsService.Load();
            }

            if (!settings.IsConfigured)
            {
                MessageBox.Show(
                    "A router address has not been configured. Open Settings and enter the router host or IP address.",
                    "AdGuard Tray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            try
            {
                RouterConnectionOptions options =
                    _settingsService.CreateConnectionOptions(settings);

                _endpoints = new RouterEndpointProvider(options);
                _routerService = new RouterService(_endpoints);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    OperationFailurePolicy.UserMessage(
                        ex,
                        "Router configuration validation",
                        "The saved router configuration is invalid. Open Settings and review the router connection values."),
                    "AdGuard Tray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private void BuildTrayIcon()
        {
            trayIcon = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                ToolTipText = "AdGuard Tray",
                ContextMenu = new System.Windows.Controls.ContextMenu()
            };

            var dashboard = new System.Windows.Controls.MenuItem
            {
                Header = "Dashboard"
            };
            dashboard.Click += (_, _) =>
            {
                var window = new Views.DashboardWindow();
                window.Show();
                window.Activate();
            };

            var openAdGuard = new System.Windows.Controls.MenuItem
            {
                Header = "Open AdGuard Home"
            };
            openAdGuard.Click += OpenAdGuard_Click;

            var openRouter = new System.Windows.Controls.MenuItem
            {
                Header = "Open GL.iNet Router"
            };
            openRouter.Click += (_, _) => OpenRouterPage();

            var settings = new System.Windows.Controls.MenuItem
            {
                Header = "Settings"
            };
            settings.Click += (_, _) =>
            {
                new Views.SettingsWindow().ShowDialog();
                ReloadServicesAfterSettingsChange();
            };

            var diagnostics = new System.Windows.Controls.MenuItem
            {
                Header = "Diagnostics"
            };
            diagnostics.Click += (_, _) =>
                new Views.DiagnosticsWindow().Show();

            var exit = new System.Windows.Controls.MenuItem
            {
                Header = "Exit"
            };
            exit.Click += (_, _) =>
            {
                DisposeServices();
                Application.Current.Shutdown();
            };

            trayIcon.ContextMenu.Items.Add(dashboard);
            trayIcon.ContextMenu.Items.Add(settings);
            trayIcon.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
            trayIcon.ContextMenu.Items.Add(openAdGuard);
            trayIcon.ContextMenu.Items.Add(openRouter);
            trayIcon.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
            trayIcon.ContextMenu.Items.Add(diagnostics);
            trayIcon.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
            trayIcon.ContextMenu.Items.Add(exit);
        }

        private void ReloadServicesAfterSettingsChange()
        {
            _routerService?.Dispose();
            _routerService = null;
            _endpoints = null;
            TryInitialiseServices();
        }

        private async void OpenAdGuard_Click(object sender, RoutedEventArgs e)
        {
            if (_routerService is null)
                return;

            try
            {
                await _routerService.OpenCorrectPageAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    OperationFailurePolicy.UserMessage(
                        ex,
                        "Open AdGuard Home",
                        "Unable to open AdGuard Home. Check the saved address and try again."),
                    "AdGuard Tray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OpenRouterPage()
        {
            if (_endpoints is null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _endpoints.RouterBaseUri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    OperationFailurePolicy.UserMessage(
                        ex,
                        "Open router page",
                        "Unable to open the router page. Check the saved address and try again."),
                    "AdGuard Tray",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void DisposeTrayIcon() => trayIcon?.Dispose();

        private void DisposeServices()
        {
            _routerService?.Dispose();
            _routerService = null;
            trayIcon?.Dispose();
            trayIcon = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            DisposeServices();
            base.OnClosed(e);
        }
    }
}
