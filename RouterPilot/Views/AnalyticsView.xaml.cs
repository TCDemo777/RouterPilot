using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class AnalyticsView : UserControl
    {
        private readonly IInternetSpeedTestService _speedTestService;
        private readonly SettingsService _settingsService;
        private readonly DashboardViewModel _dashboard;
        private readonly DataStatisticsViewModel _dataStatistics;
        private readonly IMetricHistoryService _metricHistoryService;
        private TimeSpan _selectedHistoryRange = TimeSpan.FromHours(1);

        public AnalyticsView(IInternetSpeedTestService speedTestService, SettingsService settingsService,
            DashboardViewModel dashboard, DataStatisticsViewModel dataStatistics)
        {
            InitializeComponent();
            _speedTestService = speedTestService;
            _settingsService = settingsService;
            _dashboard = dashboard;
            _dataStatistics = dataStatistics;
            _metricHistoryService = ((App)Application.Current).Services.GetRequiredService<IMetricHistoryService>();
            DataContext = _dashboard;
            DataStatisticsContent.DataContext = _dataStatistics;
            SpeedTestPanel.DataContext = _speedTestService;
            _speedTestService.PropertyChanged += SpeedTestService_PropertyChanged;
            Unloaded += AnalyticsView_Unloaded;
            RefreshRecentHistory();
            RefreshReliability();
            AnalyticsTabs.SelectedIndex = 0;
            UpdateAnalyticsTabVisibility();
        }

        private async void AnalyticsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, AnalyticsTabs)) return;

            UpdateAnalyticsTabVisibility();
            if (AnalyticsTabs.SelectedIndex == 1)
            {
                // Selecting this tab is the intentional activation point for the
                // existing idempotent Data Statistics lazy load.
                await _dataStatistics.EnsureLoadedAsync();
            }
        }

        private void UpdateAnalyticsTabVisibility()
        {
            // The content blocks are kept in their existing views and bindings;
            // inactive blocks are removed from layout instead of leaving blank space.
            if (OverviewContent is null || DataStatisticsContent is null || DnsProtectionContent is null)
                return;

            OverviewContent.Visibility = AnalyticsTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            DataStatisticsContent.Visibility = AnalyticsTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            DnsProtectionContent.Visibility = AnalyticsTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HistoryRange_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;
            _selectedHistoryRange = tag switch { "24H" => TimeSpan.FromDays(1), "7D" => TimeSpan.FromDays(7), "30D" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(1) };
            RefreshReliability();
        }

        private void RefreshReliability()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            InternetReliabilitySummary summary = _metricHistoryService.GetReliability(_selectedHistoryRange, now);
            _dashboard.InternetReliabilityAvailability = summary.HasSufficientHistory ? $"{summary.AvailabilityPercent:0.##}%" : "Not enough data";
            _dashboard.InternetReliabilityStatus = summary.IsOnline is true ? "Online" : summary.IsOnline is false ? "Offline" : "Checking";
            _dashboard.InternetReliabilityUptime = summary.CurrentStateSince is { } since
                ? (summary.IsOnline is true ? $"At least {FormatDuration(now - since)}" : $"Outage {FormatDuration(now - since)}")
                : "< 1 min";
            _dashboard.InternetReliabilityObserved = summary.ObservedDuration > TimeSpan.Zero ? $"{FormatDuration(summary.ObservedDuration)} of selected {FormatDuration(_selectedHistoryRange)}" : "No observed time yet";
            _dashboard.InternetReliabilityOutages = summary.OutageCount.ToString();
            _dashboard.InternetReliabilityDowntime = summary.OfflineDuration > TimeSpan.Zero ? FormatDuration(summary.OfflineDuration) : "None";
            _dashboard.InternetReliabilityLongestOutage = summary.LongestOutage > TimeSpan.Zero ? FormatDuration(summary.LongestOutage) : "None";
            _dashboard.InternetReliabilityLastOutage = summary.LastOutageStartedAt is { } start && summary.LastOutageDuration is { } duration ? $"{start.LocalDateTime:g} • {duration:h\\:mm\\:ss}" : "No outages observed";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.FromMinutes(1)) return "< 1 min";
            if (duration < TimeSpan.FromHours(1)) return $"{(int)duration.TotalMinutes} min";
            if (duration < TimeSpan.FromDays(1)) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
            return $"{duration.Days}d {duration.Hours}h";
        }

        private async void ClearMetricHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Clear local CPU, memory, WAN, and Internet reliability history? Timeline and speed-test history will not be changed.", "Clear Metric History", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            await _metricHistoryService.ClearAsync();
            _dashboard.InternetReliabilityAvailability = "Not enough data";
            _dashboard.InternetReliabilityStatus = "Checking";
            _dashboard.InternetReliabilityUptime = "< 1 min";
            _dashboard.InternetReliabilityOutages = "0";
            _dashboard.InternetReliabilityDowntime = "None";
            _dashboard.InternetReliabilityObserved = "No observed time yet";
            _dashboard.InternetReliabilityLongestOutage = "None";
            _dashboard.InternetReliabilityLastOutage = "No outages observed";
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
            // Data Statistics is application-owned shared state. The DI
            // container disposes it during application shutdown, not when a
            // transient Analytics view leaves the visual tree.
        }

        private void TopApplication_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ApplicationTrafficStat app })
                OpenApplicationTrafficDetails(app.ApplicationId, app.ApplicationName);
        }

        private void AllApplications_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid { SelectedItem: ApplicationTrafficRow app })
                OpenApplicationTrafficDetails(app.ApplicationId, app.ApplicationName);
        }

        private void OpenApplicationTrafficDetails(string applicationId, string applicationName)
        {
            if (string.IsNullOrWhiteSpace(applicationId) || string.IsNullOrWhiteSpace(applicationName))
                return;

            var window = new ApplicationTrafficDetailsWindow(applicationId, applicationName)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private async void RefreshApplicationDetail_Click(object sender, RoutedEventArgs e)
        {
            if (_dataStatistics.SelectedDetail is { } detail)
                await _dataStatistics.OpenApplicationDetailAsync(detail.ApplicationId, detail.ApplicationName);
        }

        private void ViewDetailDeviceClient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string mac } && Window.GetWindow(this) is DashboardWindow dashboard)
                dashboard.OpenClientDetailsForDeviceIdentity(mac);
        }

        private void ViewDomainActivity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string domain } &&
                Window.GetWindow(this) is DashboardWindow dashboard)
            {
                dashboard.NavigateToDnsActivityForDomain(domain);
            }
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
