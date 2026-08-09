using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

namespace RouterPilot.Views
{
    public partial class AnalyticsView : UserControl
    {
        private readonly IInternetSpeedTestService _speedTestService;
        private readonly SettingsService _settingsService;
        private readonly DashboardViewModel _dashboard;

        public AnalyticsView(IInternetSpeedTestService speedTestService, SettingsService settingsService,
            DashboardViewModel dashboard)
        {
            InitializeComponent();
            _speedTestService = speedTestService;
            _settingsService = settingsService;
            _dashboard = dashboard;
            DataContext = _dashboard;
            SpeedTestPanel.DataContext = _speedTestService;
            _speedTestService.PropertyChanged += SpeedTestService_PropertyChanged;
            Unloaded += AnalyticsView_Unloaded;
            RefreshRecentHistory();
        }

        private async void RunSpeedTestButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("UI-01 RunSpeedTestButton_Click entered");
            if (_speedTestService.IsRunning)
            {
                MessageBox.Show("A speed test is already running.", "Internet Speed Test",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppSettings settings = _settingsService.Load();
            if (!settings.SpeedTestBandwidthWarningAcknowledged)
            {
                var warning = new SpeedTestBandwidthWarningDialog
                {
                    Owner = Window.GetWindow(this)
                };
                if (warning.ShowDialog() != true)
                {
                    return;
                }

                // Persist only after an actual confirmed run. Cancelling the
                // dialog must never silently suppress future warnings.
                if (warning.SuppressFutureWarnings)
                {
                    settings.SpeedTestBandwidthWarningAcknowledged = true;
                    _settingsService.Save(settings);
                }
            }

            try
            {
                // These named controls are deliberately updated directly. The
                // speed-test service owns execution; this view owns its immediate
                // visible state so a background notification failure cannot leave
                // the user looking at "Ready" while a test is running.
                RenderPending();
                RunSpeedTestButton.IsEnabled = false;
                await Dispatcher.Yield(DispatcherPriority.Render);
                Debug.WriteLine("UI-02 before RunAsync");
                SpeedTestResult result = await _speedTestService.RunAsync(_dashboard.RouterConnected, _dashboard.InternetConnected);
                Debug.WriteLine("UI-03 RunAsync returned");
                RenderResult(result);
                RefreshRecentHistory();
                if (result.Status == SpeedTestStatus.Error)
                {
                    MessageBox.Show(_speedTestService.FailureMessage ?? "RouterPilot could not complete the speed test.",
                        "Internet Speed Test", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"SpeedTest UI failure: {exception.GetType().Name}");
                RenderError("RouterPilot could not start the speed test.");
                MessageBox.Show("RouterPilot could not start the speed test.",
                    "Internet Speed Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                RunSpeedTestButton.IsEnabled = true;
                Debug.WriteLine("UI-04 handler finally completed");
            }
        }

        private void CancelSpeedTest_Click(object sender, RoutedEventArgs e) => _speedTestService.Cancel();

        private async void ClearSpeedTestHistory_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("SpeedTest history clear requested");
            if (_speedTestService.History.Count == 0)
            {
                MessageBox.Show("There is no speed test history to clear.", "Clear Speed Test History",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("Clear only the Internet Speed Test history? Timeline and Analytics traffic history will be preserved.",
                    "Clear Speed Test History", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await _speedTestService.ClearHistoryAsync();
                RefreshRecentHistory();
                Debug.WriteLine("SpeedTest history cleared");
            }
        }

        private void RenderPending()
        {
            SpeedTestStatusText.Text = "Status: Preparing…";
            SpeedTestErrorText.Visibility = Visibility.Collapsed;
            SpeedTestPingText.Text = "N/A";
            SpeedTestDownloadText.Text = "N/A";
            SpeedTestUploadText.Text = "N/A";
            SpeedTestSourceText.Text = "N/A";
            SpeedTestProviderText.Text = "N/A";
            SpeedTestLastTestedText.Text = "N/A";
        }

        private void RenderResult(SpeedTestResult result)
        {
            if (result.Status == SpeedTestStatus.Cancelled)
            {
                SpeedTestStatusText.Text = "Status: Cancelled";
                SpeedTestErrorText.Visibility = Visibility.Collapsed;
                return;
            }

            if (result.Status != SpeedTestStatus.Completed)
            {
                RenderError(_speedTestService.FailureMessage ?? "RouterPilot could not complete the speed test.");
                return;
            }

            SpeedTestStatusText.Text = "Status: Completed";
            SpeedTestErrorText.Visibility = Visibility.Collapsed;
            SpeedTestPingText.Text = result.PingMs is { } ping ? $"{ping:0.#} ms" : "N/A";
            SpeedTestDownloadText.Text = result.DownloadMbps is { } download ? $"{download:0.#} Mbps" : "N/A";
            SpeedTestUploadText.Text = result.UploadMbps is { } upload ? $"{upload:0.#} Mbps" : "N/A";
            SpeedTestSourceText.Text = result.Source == SpeedTestSource.Router ? "Router" : "This PC";
            SpeedTestProviderText.Text = string.IsNullOrWhiteSpace(result.Provider) ? "N/A" : result.Provider;
            SpeedTestLastTestedText.Text = result.Timestamp.LocalDateTime.ToString("g");
        }

        private void RenderError(string message)
        {
            SpeedTestStatusText.Text = "Status: Error";
            SpeedTestErrorText.Text = message;
            SpeedTestErrorText.Visibility = Visibility.Visible;
            SpeedTestPingText.Text = "N/A";
            SpeedTestDownloadText.Text = "N/A";
            SpeedTestUploadText.Text = "N/A";
            SpeedTestSourceText.Text = "N/A";
            SpeedTestProviderText.Text = "N/A";
            SpeedTestLastTestedText.Text = "N/A";
        }

        private void SpeedTestService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => SpeedTestService_PropertyChanged(sender, e));
                return;
            }

            if (e.PropertyName == nameof(IInternetSpeedTestService.ProgressText) && _speedTestService.IsRunning)
            {
                SpeedTestStatusText.Text = $"Status: {FormatStatus(_speedTestService.ProgressText)}";
            }
            else if (e.PropertyName is nameof(InternetSpeedTestService.RecentHistory) or nameof(IInternetSpeedTestService.History))
            {
                RefreshRecentHistory();
            }
        }

        private void AnalyticsView_Unloaded(object sender, RoutedEventArgs e)
        {
            _speedTestService.PropertyChanged -= SpeedTestService_PropertyChanged;
            Unloaded -= AnalyticsView_Unloaded;
        }

        private void RefreshRecentHistory()
        {
            var recent = _speedTestService.RecentHistory.Take(5).ToList();
            RecentSpeedTestsList.ItemsSource = recent;
            NoSpeedTestsText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FormatStatus(string progress)
        {
            if (progress.StartsWith("Testing download", StringComparison.OrdinalIgnoreCase)) return "Testing download…";
            if (progress.StartsWith("Testing upload", StringComparison.OrdinalIgnoreCase)) return "Testing upload…";
            if (progress.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase) ||
                progress.StartsWith("Checking", StringComparison.OrdinalIgnoreCase) ||
                progress.StartsWith("Router speed test unavailable", StringComparison.OrdinalIgnoreCase)) return "Preparing…";
            return "Running…";
        }
    }
}
