using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using RouterPilot.Models;
using RouterPilot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace RouterPilot.Views
{
    public partial class AboutView : UserControl
    {
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly SettingsService _settingsService;
        private readonly UpdateService _updateService;
        private readonly DiagnosticsExecutionService _diagnosticsExecutionService;
        private readonly DiagnosticsHistoryService _diagnosticsHistoryService;

        private readonly StringBuilder _supportLog =
            new StringBuilder();
        private bool _diagnosticsHistorySubscribed;

        public AboutView()
        {
            InitializeComponent();
            _routerManagerProvider = ((App)Application.Current).Services
                .GetRequiredService<IRouterManagerProvider>();
            _settingsService = ((App)Application.Current).Services
                .GetRequiredService<SettingsService>();
            _updateService = ((App)Application.Current).Services
                .GetRequiredService<UpdateService>();
            _diagnosticsExecutionService = ((App)Application.Current).Services
                .GetRequiredService<DiagnosticsExecutionService>();
            _diagnosticsHistoryService = ((App)Application.Current).Services
                .GetRequiredService<DiagnosticsHistoryService>();
            _diagnosticsExecutionService.LatestResultChanged += DiagnosticsExecution_LatestResultChanged;
            Loaded += AboutView_Loaded;
            Unloaded += AboutView_Unloaded;
            VersionTextBlock.Text = "Version " + GetApplicationVersion();
            BuildDateTextBlock.Text = "Build date: " + GetBuildDate();
            LoadChangelog();
            LoadSystemInformation();
            AppendLog("Support page opened.");
            UpdateReleaseDisplay();
        }

        private void AboutView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_diagnosticsHistorySubscribed)
            {
                _diagnosticsHistoryService.HistoryChanged +=
                    DiagnosticsHistory_CollectionChanged;
                _diagnosticsHistorySubscribed = true;
            }

            RefreshSupportLog();
        }

        private void AboutView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_diagnosticsHistorySubscribed)
            {
                return;
            }

            _diagnosticsHistoryService.HistoryChanged -=
                DiagnosticsHistory_CollectionChanged;
            _diagnosticsHistorySubscribed = false;
        }

        private void DiagnosticsHistory_CollectionChanged(
            object? sender,
            EventArgs e)
        {
            RefreshSupportLog();
        }

        private void DiagnosticsExecution_LatestResultChanged(object? sender, EventArgs e) =>
            Dispatcher.Invoke(DisplayLatestDiagnosticsResult);

        private void DisplayLatestDiagnosticsResult()
        {
            DiagnosticsExecutionResult? result = _diagnosticsExecutionService.LatestResult;
            if (result is null)
                return;

            DiagnosticsTextBox.Text = result.Outcome == DiagnosticExecutionOutcome.Success &&
                                      !string.IsNullOrWhiteSpace(result.Report)
                ? result.Report
                : result.Message;
            QueryLogWarningBorder.Visibility = result.Report?.Contains("Enabled: False", StringComparison.OrdinalIgnoreCase) == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            LatestVersionTextBlock.Text =
                RouterPilotStatusPresentation.Pending +
                " — checking GitHub Releases...";
            try
            {
                UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(manual: true);
                LatestVersionTextBlock.Text = FormatUpdateCheckResult(result);
                LastUpdateCheckTextBlock.Text = result.CheckedAt is { } checkedAt
                    ? "Last checked: " + checkedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm")
                    : "Last checked: " + RouterPilotStatusPresentation.NotAvailable;
                OpenReleaseNotesButton.IsEnabled = result.LatestRelease?.ReleaseNotesUrl is not null;
            }
            catch (OperationCanceledException)
            {
                LatestVersionTextBlock.Text =
                    RouterPilotStatusPresentation.NotAvailable +
                    " — update check cancelled.";
            }
            finally { CheckForUpdatesButton.IsEnabled = true; }
        }

        private void OpenReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            string target = _updateService.LatestRelease?.ReleaseNotesUrl?.AbsoluteUri
                ?? UpdateService.ReleasesPageUrl;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        private void UpdateReleaseDisplay()
        {
            AppSettings settings = _settingsService.Load();
            CurrentUpdateVersionTextBlock.Text = "Current version: " + GetApplicationVersion();
            LatestVersionTextBlock.Text = string.IsNullOrWhiteSpace(settings.LatestVersionSeen)
                ? "Latest available version: " + RouterPilotStatusPresentation.NotAvailable
                : "Latest available version: " + settings.LatestVersionSeen;
            LastUpdateCheckTextBlock.Text = settings.LastSuccessfulUpdateCheckUtc is { } last
                ? "Last checked: " + last.ToLocalTime().ToString("dd MMM yyyy HH:mm")
                : "Last checked: " + RouterPilotStatusPresentation.NotAvailable;
            OpenReleaseNotesButton.IsEnabled = !string.IsNullOrWhiteSpace(settings.LatestVersionSeen);
        }

        private static string FormatUpdateCheckResult(UpdateCheckResult result)
        {
            if (result.LatestRelease is not null)
                return "Latest available version: " + result.LatestRelease.Version;

            return result.Status switch
            {
                UpdateCheckStatus.Unavailable =>
                    RouterPilotStatusPresentation.NotAvailable +
                    " — " + result.Message,
                UpdateCheckStatus.Skipped =>
                    RouterPilotStatusPresentation.Pending +
                    " — " + result.Message,
                _ => result.Message
            };
        }

        private Task<RouterManager> GetRouterManagerAsync()
        {
            return _routerManagerProvider.GetRouterManagerAsync();
        }

        private async Task RunRouterToolAsync(
            string action,
            Func<RouterManager, string, Task<string>> operation)
        {
            string target =
                DiagnosticTargetBox.Text.Trim();

            DiagnosticsTextBox.Text =
                $"Running {action} for {target} from the router...";

            AppendLog(
                $"{action} requested for {target}.");

            try
            {
                string result =
                    await operation(
                        await GetRouterManagerAsync(),
                        target);

                DiagnosticsTextBox.Text =
                    string.IsNullOrWhiteSpace(result)
                        ? $"{action} completed with no output."
                        : result.Trim();

                AppendLog(
                    $"{action} completed.");
            }
            catch (Exception ex)
            {
                DiagnosticsTextBox.Text =
                    $"{action} failed ({DiagnosticRedactor.FailureCategory(ex)}).";

                AppendLog(
                    $"{action} failed ({DiagnosticRedactor.FailureCategory(ex)}).");
            }
        }

        private async void PingTool_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunRouterToolAsync(
                "Ping",
                (router, target) =>
                    router.PingAsync(target));
        }

        private async void TracerouteTool_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunRouterToolAsync(
                "Traceroute",
                (router, target) =>
                    router.TracerouteAsync(target));
        }

        private async void DnsLookupTool_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunRouterToolAsync(
                "DNS lookup",
                (router, target) =>
                    router.DnsLookupAsync(target));
        }

        private async void RunDiagnostics_Click(
            object sender,
            RoutedEventArgs e)
        {
            DiagnosticsTextBox.Text =
                "Running diagnostics...";

            DiagnosticsExecutionResult result =
                await _diagnosticsExecutionService.RunAsync(
                    DiagnosticExecutionSource.About);

            if (result.Outcome == DiagnosticExecutionOutcome.Success)
            {
                DiagnosticsTextBox.Text =
                    result.Report;

                QueryLogWarningBorder.Visibility =
                    result.Report!.Contains(
                        "Enabled: False",
                        StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            else
            {
                DiagnosticsTextBox.Text =
                    result.Message;
            }

            RefreshSupportLog();
        }

        private async void EnableQueryLog_Click(
            object sender,
            RoutedEventArgs e)
        {
            EnableQueryLogButton.IsEnabled =
                false;

            EnableQueryLogButton.Content =
                "Enabling...";

            AppendLog("Query-log repair requested.");

            try
            {
                RouterManager routerManager =
                    await GetRouterManagerAsync();

                var current =
                    await routerManager
                        .GetProtectionOptionsAsync();

                await routerManager
                    .SetQueryLogEnabledAsync(
                        true,
                        current);

                ClientRefreshNotifier.RequestRefresh();

                AppendLog(
                    "Query logging enabled; client refresh requested.");

                string report =
                    await routerManager
                        .GetClientDiagnosticsAsync();

                DiagnosticsTextBox.Text =
                    report;

                QueryLogWarningBorder.Visibility =
                    report.Contains(
                        "Enabled: False",
                        StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                DiagnosticsTextBox.Text =
                    "Unable to enable query logging.\n\n" +
                    "Failure category: " +
                    DiagnosticRedactor.FailureCategory(ex);

                QueryLogWarningBorder.Visibility =
                    Visibility.Visible;

                AppendLog(
                    "Query-log repair failed (" +
                    DiagnosticRedactor.FailureCategory(ex) + ").");
            }
            finally
            {
                EnableQueryLogButton.IsEnabled =
                    true;

                EnableQueryLogButton.Content =
                    "Enable query log";
            }
        }

        private void RefreshClients_Click(
            object sender,
            RoutedEventArgs e)
        {
            ClientRefreshNotifier.RequestRefresh();
            AppendLog("Manual client refresh requested.");
        }

        private void CopyDiagnostics_Click(
            object sender,
            RoutedEventArgs e)
        {
            CopyText(
                DiagnosticsTextBox.Text,
                "Diagnostics copied.");
        }

        private async void ExportDiagnostics_Click(
            object sender,
            RoutedEventArgs e)
        {
            await BackupDiagnosticsAsync();
        }

        private async Task BackupDiagnosticsAsync()
        {
            DiagnosticsExecutionResult result = await _diagnosticsExecutionService.RunAsync(
                DiagnosticExecutionSource.About,
                createBackup: true);
            if (!string.IsNullOrWhiteSpace(result.Report))
                DiagnosticsTextBox.Text = result.Report;
            else if (result.Outcome != DiagnosticExecutionOutcome.Success)
                DiagnosticsTextBox.Text = result.Message;
            RefreshSupportLog();
            DisplayLatestDiagnosticsResult();
        }

        private void RefreshSystem_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadSystemInformation();
            AppendLog("System information refreshed.");
        }

        private void LoadSystemInformation()
        {
            var settings =
                _settingsService.Load();

            long workingSet =
                Environment.WorkingSet;

            var builder =
                new StringBuilder();

            builder.AppendLine("RouterPilot System Information");
            builder.AppendLine(
                "Generated: " +
                DateTimeOffset.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss zzz"));
            builder.AppendLine();

            builder.AppendLine("Application");
            builder.AppendLine("-----------");
            builder.AppendLine("Version: " + GetApplicationVersion());
            builder.AppendLine(
                "Assembly: " +
                (Assembly.GetExecutingAssembly()
                    .GetName()
                    .Version?
                    .ToString() ?? "unknown"));
            builder.AppendLine(
                "Process architecture: " +
                RuntimeInformation.ProcessArchitecture);
            builder.AppendLine(
                "Memory usage: " +
                FormatBytes(
                    workingSet));
            builder.AppendLine();

            builder.AppendLine("Runtime");
            builder.AppendLine("-------");
            builder.AppendLine(
                ".NET: " +
                RuntimeInformation.FrameworkDescription);
            builder.AppendLine(
                "OS: " +
                RuntimeInformation.OSDescription);
            builder.AppendLine(
                "OS architecture: " +
                RuntimeInformation.OSArchitecture);
            builder.AppendLine(
                "64-bit process: " +
                Environment.Is64BitProcess);
            builder.AppendLine(
                "Processor count: " +
                Environment.ProcessorCount);
            builder.AppendLine();

            builder.AppendLine("Configured router");
            builder.AppendLine("-----------------");
            builder.AppendLine(
                "Address: " +
                settings.RouterHost);
            builder.AppendLine(
                "Username: " +
                settings.Username);
            builder.AppendLine(
                "Refresh interval: " +
                settings.RefreshIntervalSeconds +
                " seconds");
            builder.AppendLine(
                "Password stored: " +
                (!string.IsNullOrWhiteSpace(
                    settings.EncryptedPassword)));

            SystemTextBox.Text =
                builder.ToString();
        }

        private static string GetBuildInformation()
        {
            var assembly =
                Assembly.GetExecutingAssembly();

            return
                "RouterPilot v" + GetApplicationVersion() + "\n" +
                "Assembly version: " +
                (assembly.GetName().Version?.ToString() ?? "unknown") +
                "\nBuild location: " +
                AppContext.BaseDirectory +
                "\nGenerated: " +
                DateTimeOffset.Now.ToString("O");
        }


        private static string GetBuildDate()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;

                if (!string.IsNullOrWhiteSpace(location) &&
                    File.Exists(location))
                {
                    return File.GetLastWriteTime(location)
                        .ToString("dd MMM yyyy");
                }
            }
            catch
            {
                // A build date is helpful metadata, but failure to read it
                // must never prevent the About page from loading.
            }

            return "unknown";
        }


        private static string GetApplicationVersion()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();

            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataIndex = informationalVersion.IndexOf('+');
                return metadataIndex >= 0
                    ? informationalVersion[..metadataIndex]
                    : informationalVersion;
            }

            Version? version = assembly.GetName().Version;
            return version is null
                ? "unknown"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes =
            {
                "B",
                "KB",
                "MB",
                "GB"
            };

            double value =
                bytes;

            int index = 0;

            while (value >= 1024 &&
                   index < suffixes.Length - 1)
            {
                value /= 1024;
                index++;
            }

            return
                $"{value:F1} {suffixes[index]}";
        }

        private void CopyLog_Click(
            object sender,
            RoutedEventArgs e)
        {
            CopyText(
                SupportLogTextBox.Text,
                "Support log copied.");
        }

        private async void ClearLog_Click(
            object sender,
            RoutedEventArgs e)
        {
            _supportLog.Clear();
            await _diagnosticsHistoryService.ClearAsync();
            AppendLog("Support log cleared.");
        }

        private void CopyText(
            string text,
            string successMessage)
        {
            if (string.IsNullOrWhiteSpace(
                    text))
            {
                return;
            }

            Clipboard.SetText(
                text);

            AppendLog(
                successMessage);
        }

        private void AppendLog(string message)
        {
            _supportLog.AppendLine(
                $"[{DateTime.Now:HH:mm:ss}] {message}");

            RefreshSupportLog();
        }

        private string GetSupportLogText()
        {
            string diagnosticsLog = _diagnosticsHistoryService.GetLogText();
            if (string.IsNullOrWhiteSpace(diagnosticsLog))
            {
                return _supportLog.ToString();
            }

            return _supportLog + diagnosticsLog + Environment.NewLine;
        }

        private void RefreshSupportLog()
        {
            if (SupportLogTextBox is null)
            {
                return;
            }

            SupportLogTextBox.Text = GetSupportLogText();
            SupportLogTextBox.ScrollToEnd();
        }

        private void LoadChangelog()
        {
            string[] candidatePaths =
            {
                Path.Combine(
                    AppContext.BaseDirectory,
                    "CHANGELOG.md"),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "CHANGELOG.md")
            };

            foreach (string path in candidatePaths)
            {
                string fullPath =
                    Path.GetFullPath(
                        path);

                if (!File.Exists(
                        fullPath))
                {
                    continue;
                }

                try
                {
                    ChangelogTextBox.Text =
                        File.ReadAllText(
                            fullPath,
                            Encoding.UTF8);

                    return;
                }
                catch (Exception ex)
                {
                    ChangelogTextBox.Text =
                        "The changelog could not be read.\n\n" +
                        ex.Message;

                    return;
                }
            }

            ChangelogTextBox.Text =
                "CHANGELOG.md was not found.";
        }

        private void ReloadChangelog_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadChangelog();
            AppendLog("Changelog reloaded.");
        }

        private void OpenLicense_Click(
            object sender,
            RoutedEventArgs e)
        {
            string[] candidatePaths =
            {
                Path.Combine(
                    AppContext.BaseDirectory,
                    "LICENSE"),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "LICENSE")
            };

            foreach (string path in candidatePaths)
            {
                string fullPath =
                    Path.GetFullPath(
                        path);

                if (!File.Exists(
                        fullPath))
                {
                    continue;
                }

                Process.Start(
                    new ProcessStartInfo(
                        fullPath)
                    {
                        UseShellExecute = true
                    });

                AppendLog(
                    "Licence opened.");

                return;
            }

            MessageBox.Show(
                "The LICENSE file could not be found.",
                "Licence",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OpenThirdPartyNotices_Click(object sender, RoutedEventArgs e)
        {
            string[] candidatePaths =
            {
                Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "THIRD_PARTY_NOTICES.txt")
            };

            foreach (string path in candidatePaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    continue;

                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                AppendLog("Third-party notices opened.");
                return;
            }

            MessageBox.Show("The third-party notices file could not be found.", "Third-Party Notices",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SupportDevelopment_Click(
            object sender,
            RoutedEventArgs e)
        {
            const string sponsorsUrl =
                "https://github.com/sponsors/TCDemo777";

            AppendLog(
                "Opening GitHub Sponsors page...");

            Process.Start(
                new ProcessStartInfo(
                    sponsorsUrl)
                {
                    UseShellExecute = true
                });
        }

        private void GitHubLink_RequestNavigate(
            object sender,
            RequestNavigateEventArgs e)
        {
            Process.Start(
                new ProcessStartInfo(
                    e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });

            e.Handled = true;
        }
    }
}
