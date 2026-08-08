using System;
using System.Threading.Tasks;
using System.Windows;
using RouterPilot.Models;
using RouterPilot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class DiagnosticsWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly IRouterCertificateTrustService _certificateTrustService;

        public DiagnosticsWindow()
        {
            InitializeComponent();

            _settingsService =
                new SettingsService();
            _routerManagerProvider = ((App)Application.Current).Services
                .GetRequiredService<IRouterManagerProvider>();
            _certificateTrustService = ((App)Application.Current).Services
                .GetRequiredService<IRouterCertificateTrustService>();
        }
                private async Task<RouterManager> CreateRouterManagerAsync()
        {
            var settings =
                _settingsService.Load();

            string password =
                _settingsService.DecryptPassword(
                    settings.EncryptedPassword);

            using GLInetSessionService sessionService =
                new GLInetSessionService(
                    settings.RouterHost,
                    settings.Username,
                    password,
                    _certificateTrustService);

            string adminToken =
                await sessionService.GetAdminTokenAsync();

            if (string.IsNullOrWhiteSpace(adminToken))
            {
                throw new InvalidOperationException(
                    "The router login succeeded but no Admin-Token was returned.");
            }

            return await _routerManagerProvider.GetRouterManagerAsync();
        }

        private async Task RunDiagnosticAsync(
            string action,
            Func<RouterManager, string, Task<string>> diagnostic)
        {
            string target = TargetBox.Text.Trim();
            OutputBox.Text = $"Running {action} for {target} from the router...";

            try
            {
                RouterManager routerManager =
                    await CreateRouterManagerAsync();

                string result =
                    await diagnostic(routerManager, target);

                OutputBox.Text =
                    string.IsNullOrWhiteSpace(result)
                        ? $"{action} completed with no output."
                        : result.Trim();
            }
            catch (Exception ex)
            {
                OutputBox.Text = FormatFailure(ex);
            }
        }

        private async void PingButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunDiagnosticAsync(
                "ping",
                (router, target) => router.PingAsync(target));
        }

        private async void TracerouteButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunDiagnosticAsync(
                "traceroute",
                (router, target) => router.TracerouteAsync(target));
        }

        private async void DnsLookupButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunDiagnosticAsync(
                "DNS lookup",
                (router, target) => router.DnsLookupAsync(target));
        }

        private async void RouterInfoButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OutputBox.Text =
                "Logging into the router and loading router information...";

            try
            {
                RouterManager routerManager =
                    await CreateRouterManagerAsync();

                RouterInfo info =
                    await routerManager.GetRouterInfoAsync();

                OutputBox.Text =
$@"Router Information

Model
------
{info.Model}

Hostname
--------
{info.Hostname}

Firmware
--------
{info.Firmware}

Uptime
------
{info.Uptime}

CPU utilisation
---------------
{info.CpuUsage}

Logical processors
------------------
{info.LogicalProcessorCount?.ToString() ?? "-"}

Load average (1 / 5 / 15 minutes)
---------------------------------
{info.LoadAverage}

Memory
------
{info.MemoryUsage}

Storage
-------
{info.StorageUsage}

WAN IP
------
{info.WanIp}

Gateway
-------
{info.Gateway}

DNS
---
{info.DnsServer}

Latency
-------
{info.Latency}";
            }
            catch (Exception ex)
            {
                OutputBox.Text = FormatFailure(ex);
            }
        }

        private async void AdGuardStatusButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OutputBox.Text =
                "Logging into the router and checking AdGuard Home...";

            try
            {
                RouterManager routerManager =
                    await CreateRouterManagerAsync();

                AdGuardStatus status =
                    await routerManager.GetAdGuardStatusAsync();

                OutputBox.Text =
$@"AdGuard Home Status

Running
-------
{status.IsRunning}

Service
-------
{status.ServiceStatus}

Version
-------
{status.Version}

Process
-------
{status.Process}";
            }
            catch (Exception ex)
            {
                OutputBox.Text = FormatFailure(ex);
            }
        }

        private async void LogsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OutputBox.Text =
                "Logging into the router and loading logs...";

            try
            {
                RouterManager routerManager =
                    await CreateRouterManagerAsync();

                OutputBox.Text =
                    await routerManager.GetLogsAsync();
            }
            catch (Exception ex)
            {
                OutputBox.Text = FormatFailure(ex);
            }
        }

        private async void RestartButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OutputBox.Text =
                "Logging into the router and restarting AdGuard Home...";

            try
            {
                RouterManager routerManager =
                    await CreateRouterManagerAsync();

                await routerManager.RestartAdGuardAsync();

                OutputBox.Text =
                    "AdGuard Home restarted successfully.";
            }
            catch (Exception ex)
            {
                OutputBox.Text = FormatFailure(ex);
            }
        }

        private async void RefreshButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OutputBox.Text =
                "Testing automatic GL.iNet login...";

            try
            {
                var settings =
                    _settingsService.Load();

                string password =
                    _settingsService.DecryptPassword(
                        settings.EncryptedPassword);

                using GLInetSessionService sessionService =
                    new GLInetSessionService(
                        settings.RouterHost,
                        settings.Username,
                        password,
                        _certificateTrustService);

                string adminToken =
                    await sessionService.GetAdminTokenAsync();

                OutputBox.Text =
$@"Automatic GL.iNet Login

Result
------
Login successful

Token received
--------------
Yes

Token length
------------
{adminToken.Length}

The token has not been displayed for security.";
            }
            catch (Exception ex)
            {
                OutputBox.Text = FormatFailure(ex);
            }
        }

        private static string FormatFailure(Exception exception) =>
            "Operation failed (" +
            DiagnosticRedactor.FailureCategory(exception) +
            ").";
    }
}
