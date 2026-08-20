using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Win32;
using RouterPilot.Models;
using RouterPilot.ViewModels;

namespace RouterPilot.Services;

public sealed class DiagnosticsExecutionService
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly DiagnosticsHistoryService _historyService;
    private readonly MaintenanceHistoryService _maintenanceHistoryService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService _settingsService;
    private readonly TimelineService _timelineService;
    private readonly IMetricHistoryService _metricHistoryService;
    private readonly INetworkHealthService _networkHealthService;
    private readonly IDataFreshnessService _dataFreshnessService;
    private readonly ClientProfileService _clientProfileService;

    public DiagnosticsExecutionService(
        IRouterManagerProvider routerManagerProvider,
        DiagnosticsHistoryService historyService,
        MaintenanceHistoryService maintenanceHistoryService,
        NotificationService notificationService,
        SettingsService settingsService,
        TimelineService timelineService,
        IMetricHistoryService metricHistoryService,
        INetworkHealthService networkHealthService,
        IDataFreshnessService dataFreshnessService,
        ClientProfileService clientProfileService)
    {
        _routerManagerProvider = routerManagerProvider;
        _historyService = historyService;
        _maintenanceHistoryService = maintenanceHistoryService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _timelineService = timelineService;
        _metricHistoryService = metricHistoryService;
        _networkHealthService = networkHealthService;
        _dataFreshnessService = dataFreshnessService;
        _clientProfileService = clientProfileService;
    }

    /// <summary>Shared, safe result used by About when diagnostics originated from Maintenance.</summary>
    public DiagnosticsExecutionResult? LatestResult { get; private set; }
    public event EventHandler? LatestResultChanged;

    public async Task<DiagnosticsExecutionResult> RunAsync(
        DiagnosticExecutionSource source,
        bool createBackup = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
            string report = await router.GetClientDiagnosticsAsync()
                .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            string? outputPath = null;
            if (createBackup)
            {
                outputPath = SelectOutputPath();
                if (string.IsNullOrWhiteSpace(outputPath))
                    return await CompleteAsync(source, DiagnosticExecutionOutcome.Cancelled,
                        "Diagnostics backup cancelled.", null, report, false);
                await CreateBundleAsync(outputPath, report, cancellationToken);
            }

            return await CompleteAsync(
                source,
                DiagnosticExecutionOutcome.Success,
                createBackup ? "Diagnostics backup created." : "Diagnostics completed.",
                outputPath,
                report,
                createBackup);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(
                source,
                DiagnosticExecutionOutcome.Cancelled,
                "Diagnostics cancelled.",
                null);
        }
        catch (Exception)
        {
            return await CompleteAsync(
                source,
                DiagnosticExecutionOutcome.Error,
                "Diagnostics failed. Check the router connection and try again.",
                null);
        }
    }

    /// <summary>Exports only the state RouterPilot already holds; it never contacts the router.</summary>
    public async Task<NetworkSnapshotExportResult> ExportNetworkSnapshotAsync(
        DashboardViewModel dashboard,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string? outputPath = SelectNetworkSnapshotOutputPath();
            if (string.IsNullOrWhiteSpace(outputPath))
                return new NetworkSnapshotExportResult(false, true, "Network snapshot export cancelled.", null);

            string markdown = BuildNetworkSnapshot(dashboard);
            await File.WriteAllTextAsync(outputPath, DiagnosticRedactor.RedactForExport(markdown),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            return new NetworkSnapshotExportResult(true, false, "Network snapshot exported successfully.", outputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new NetworkSnapshotExportResult(false, true, "Network snapshot export cancelled.", null);
        }
        catch (Exception)
        {
            // The export is an isolated, non-critical sink.
            return new NetworkSnapshotExportResult(false, false, "Network snapshot could not be exported.", null);
        }
    }

    private static string? SelectOutputPath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = "RouterPilot_Diagnostics_" +
                DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".zip"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? SelectNetworkSnapshotOutputPath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown document (*.md)|*.md",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = "RouterPilot-Network-Snapshot-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".md"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string BuildNetworkSnapshot(DashboardViewModel dashboard)
    {
        // Take local copies once: this must not trigger a router read or race mutable UI collections.
        WifiRadioInfo[] wifi = dashboard.WifiNetworks.ToArray();
        DhcpLeaseInfo[] leases = dashboard.DhcpLeases.ToArray();
        DhcpReservationInfo[] reservations = dashboard.DhcpReservations.ToArray();
        PortForwardRuleInfo[] forwards = dashboard.PortForwardRules.ToArray();
        ClientProfile[] profiles = _clientProfileService.Load().Values.ToArray();
        NetworkHealthSnapshot health = _networkHealthService.Current;
        DataFreshnessInfo[] freshness = _dataFreshnessService.GetAll().ToArray();
        var ssids = new Dictionary<string, string>(StringComparer.Ordinal);
        var healthClientAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        string SsidAlias(string? ssid)
        {
            if (string.IsNullOrWhiteSpace(ssid) || ssid == "-") return "Unavailable";
            if (!ssids.TryGetValue(ssid, out string? alias))
            {
                alias = $"SSID-{ssids.Count + 1}";
                ssids.Add(ssid, alias);
            }
            return alias;
        }
        string SanitisedHealthSummary(NetworkHealthIssue issue)
        {
            const string monitoredPrefix = "client.monitor.";
            if (issue.Id.StartsWith(monitoredPrefix, StringComparison.Ordinal))
            {
                string key = issue.Id[monitoredPrefix.Length..];
                if (!healthClientAliases.TryGetValue(key, out string? alias))
                {
                    alias = $"Client-{healthClientAliases.Count + 1:00}";
                    healthClientAliases.Add(key, alias);
                }
                return $"Monitored {alias} offline ({issue.Severity})";
            }

            // Titles are the application's typed issue labels. Never emit descriptions,
            // which may contain a device name or other user-supplied detail.
            return $"{SafeValue(issue.Title)} ({issue.Severity})";
        }

        string freshnessState = freshness.Any(item => item.State == DataFreshnessState.Stale) ? "Stale"
            : freshness.Any(item => item.State == DataFreshnessState.Unavailable) ? "Unavailable"
            : freshness.Any(item => item.State == DataFreshnessState.Fresh) ? "Fresh" : "Unknown";
        DateTimeOffset? lastSuccess = freshness.Select(item => item.LastSuccessUtc).Where(item => item.HasValue)
            .Select(item => item!.Value).OrderByDescending(item => item).Cast<DateTimeOffset?>().FirstOrDefault();
        int observedClients = dashboard.LanClients.Count;
        int knownDevices = profiles.Length;

        var builder = new StringBuilder();
        builder.AppendLine("# RouterPilot Network Snapshot");
        builder.AppendLine();
        builder.AppendLine("## Application");
        builder.AppendLine($"- RouterPilot version: {Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0)}");
        builder.AppendLine($"- Generated: {DateTimeOffset.Now:dddd, dd MMMM yyyy HH:mm zzz}");
        builder.AppendLine();
        builder.AppendLine("## Router");
        builder.AppendLine($"- Model: {SafeValue(dashboard.RouterModel)}");
        builder.AppendLine($"- Firmware: {SafeValue(dashboard.RouterFirmwareVersion)}");
        builder.AppendLine($"- Availability: {(dashboard.RouterConnected ? "Online" : "Unavailable")}");
        builder.AppendLine($"- Uptime: {SafeValue(dashboard.Uptime)}");
        builder.AppendLine($"- CPU: {SafeValue(dashboard.CpuUsageDisplay)}");
        builder.AppendLine($"- Memory: {SafeValue(dashboard.MemoryUsage)}");
        builder.AppendLine($"- Temperature: {SafeValue(dashboard.Temperature)}");
        builder.AppendLine($"- Data freshness: {freshnessState}");
        if (lastSuccess is { } at) builder.AppendLine($"- Last successful update: {at.ToLocalTime():dddd, dd MMMM yyyy HH:mm zzz}");
        if (freshnessState == "Stale") builder.AppendLine("- Note: values below may represent last-known state.");
        builder.AppendLine();
        builder.AppendLine("## Internet");
        builder.AppendLine($"- Status: {(dashboard.InternetConnected ? "Online" : "Offline")}");
        builder.AppendLine($"- Reliability: {SafeValue(dashboard.InternetReliabilityAvailability)}");
        builder.AppendLine($"- Recent outages: {SafeValue(dashboard.InternetReliabilityOutages)}");
        builder.AppendLine("- Public IP: <redacted>");
        builder.AppendLine();
        builder.AppendLine("## VPN");
        builder.AppendLine($"- State: {SafeValue(dashboard.VpnSummary.State)}");
        builder.AppendLine($"- Protocol: {SafeValue(dashboard.VpnSummary.Protocol)}");
        builder.AppendLine($"- Profile: {(dashboard.VpnSummary.IsConfigured ? "Configured" : "Not configured")}");
        builder.AppendLine();
        builder.AppendLine("## Wi-Fi");
        if (wifi.Length == 0) builder.AppendLine("- Unavailable");
        foreach (WifiRadioInfo network in wifi)
            builder.AppendLine($"- {SsidAlias(network.Ssid)} — {SafeValue(network.Band)}; {SafeValue(network.StatusDisplay)}; channel {SafeValue(network.Channel)}; width {SafeValue(network.ChannelWidth)}; security {SafeValue(network.Security)}; clients {network.ClientCount}; {SafeValue(network.GuestClassificationDisplay, "Main or unclassified")}");
        builder.AppendLine();
        builder.AppendLine("## Clients");
        builder.AppendLine($"- Known devices: {knownDevices}");
        builder.AppendLine($"- Currently observed: {observedClients}");
        builder.AppendLine($"- Not currently observed: {Math.Max(0, knownDevices - observedClients)}");
        builder.AppendLine($"- Needs review: {profiles.Count(profile => profile.NeedsReview)}");
        builder.AppendLine($"- Monitored: {profiles.Count(profile => profile.MonitorAvailability)}");
        builder.AppendLine($"- Favourites: {profiles.Count(profile => profile.IsFavorite)}");
        builder.AppendLine();
        builder.AppendLine("## DHCP");
        builder.AppendLine($"- Active leases: {leases.Length}");
        builder.AppendLine($"- Reservations: {reservations.Length}");
        builder.AppendLine();
        builder.AppendLine("## Port Forwarding");
        builder.AppendLine($"- Rules: {forwards.Length}");
        builder.AppendLine($"- Enabled: {forwards.Count(rule => rule.Enabled)}");
        builder.AppendLine($"- Dynamic target IP warnings: {forwards.Count(rule => rule.TargetStatusTitle == "Dynamic IP")}");
        builder.AppendLine($"- Target IP changed warnings: {forwards.Count(rule => rule.TargetStatusTitle == "Target IP changed")}");
        builder.AppendLine($"- Conflicts: {forwards.Count(rule => rule.TargetStatusTitle == "External port conflict")}");
        builder.AppendLine($"- Device not found warnings: {forwards.Count(rule => rule.TargetStatusTitle == "Device not found")}");
        builder.AppendLine($"- Offline target warnings: {forwards.Count(rule => rule.TargetStatusTitle == "Device offline")}");
        builder.AppendLine();
        builder.AppendLine("## AdGuard");
        builder.AppendLine($"- Service: {SafeValue(dashboard.AdGuardServiceDisplay)}");
        builder.AppendLine($"- Protection: {SafeValue(dashboard.AdGuardProtectionStatusText)}");
        builder.AppendLine($"- Requests: {SafeValue(dashboard.AdGuardQueriesDisplay)}");
        builder.AppendLine($"- Blocked: {SafeValue(dashboard.AdGuardBlockedDisplay)}");
        builder.AppendLine($"- Block rate: {SafeValue(dashboard.AdGuardBlockRateDisplay)}");
        builder.AppendLine($"- Data freshness: {freshnessState}");
        builder.AppendLine();
        builder.AppendLine("## Health");
        builder.AppendLine($"- Active issues: {health.ActiveIssueCount}");
        foreach (NetworkHealthIssue issue in health.Issues)
            builder.AppendLine($"- {SanitisedHealthSummary(issue)}");
        return builder.ToString();
    }

    private static string SafeValue(string? value, string fallback = "Unknown") =>
        string.IsNullOrWhiteSpace(value) || value == "-" ? fallback : value;

    private async Task CreateBundleAsync(
        string outputPath,
        string report,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetTempPath(),
            "RouterPilotDiagnostics_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(temporaryPath);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryPath, "diagnostics.txt"),
                DiagnosticRedactor.RedactForExport(report),
                Encoding.UTF8,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryPath, "system.txt"),
                DiagnosticRedactor.RedactForExport(BuildSystemInformation()),
                Encoding.UTF8,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryPath, "support-log.txt"),
                DiagnosticRedactor.RedactForExport(_historyService.GetLogText()),
                Encoding.UTF8,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryPath, "build.txt"),
                DiagnosticRedactor.RedactForExport(BuildInformation()),
                Encoding.UTF8,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            ZipFile.CreateFromDirectory(temporaryPath, outputPath);
        }
        finally
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
        }
    }

    private async Task<DiagnosticsExecutionResult> CompleteAsync(
        DiagnosticExecutionSource source,
        DiagnosticExecutionOutcome outcome,
        string message,
        string? outputPath,
        string? report = null,
        bool notify = false)
    {
        await _historyService.AddAsync(outcome, $"{message}{(outputPath is null ? string.Empty : " " + outputPath)}", source, outputPath);
        await _maintenanceHistoryService.AddAsync(new MaintenanceHistoryEntry
        {
            Action = notify ? MaintenanceAction.BackupDiagnostics : MaintenanceAction.RunDiagnostics,
            Source = source.ToString(),
            Outcome = outcome switch
            {
                DiagnosticExecutionOutcome.Success => MaintenanceOutcome.Success,
                DiagnosticExecutionOutcome.Cancelled => MaintenanceOutcome.Cancelled,
                _ => MaintenanceOutcome.Error
            },
            Message = message
        });

        if (EventRoutingPolicy.ShouldNotifyDiagnostics(outcome))
        {
            await _notificationService.AddAsync(new AppNotification
            {
                Title = outcome == DiagnosticExecutionOutcome.Success
                    ? "Diagnostics completed"
                    : "Diagnostics failed",
                Message = message,
                Severity = outcome == DiagnosticExecutionOutcome.Success
                    ? NotificationSeverity.Success
                    : NotificationSeverity.Error,
                Category = NotificationCategory.System,
                EventType = NotificationEventType.DiagnosticsCompleted,
                DeduplicationKey = "Diagnostics-" + source + "-" + Guid.NewGuid()
            });
        }

        var result = new DiagnosticsExecutionResult(outcome, report, message, outputPath);
        LatestResult = result;
        LatestResultChanged?.Invoke(this, EventArgs.Empty);

        if (outcome != DiagnosticExecutionOutcome.Cancelled)
        {
            bool backupCreated = outcome == DiagnosticExecutionOutcome.Success && notify;
            await _timelineService.AddAsync(new TimelineEvent
            {
                Category = TimelineCategory.Diagnostics,
                EventType = backupCreated ? TimelineEventType.DiagnosticsBackupCreated : outcome == DiagnosticExecutionOutcome.Success ? TimelineEventType.DiagnosticsCompleted : TimelineEventType.DiagnosticsFailed,
                Title = backupCreated ? "Diagnostics backup created" : outcome == DiagnosticExecutionOutcome.Success ? "Diagnostics completed" : "Diagnostics failed",
                Message = message,
                Severity = outcome == DiagnosticExecutionOutcome.Success ? TimelineSeverity.Success : TimelineSeverity.Error,
                Source = source.ToString(),
                CorrelationId = "diagnostics:" + source + ":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeduplicationKey = "diagnostics:" + Guid.NewGuid().ToString("N")
            });
        }

        return result;
    }

    private string BuildSystemInformation()
    {
        AppSettings settings = _settingsService.Load();
        InternetInstabilitySummary instability = _metricHistoryService.GetInternetInstability(
            TimeSpan.FromHours(1), DateTimeOffset.UtcNow, 3);
        bool instabilityActive = _networkHealthService.Current.Issues.Any(issue => issue.Id == "internet.unstable");
        return $"RouterPilot System Information{Environment.NewLine}" +
            $"Generated: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $".NET: {RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
            $"OS: {RuntimeInformation.OSDescription}{Environment.NewLine}" +
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}{Environment.NewLine}" +
            "Router endpoint: configured" + Environment.NewLine +
            $"Refresh interval: {settings.RefreshIntervalSeconds} seconds{Environment.NewLine}" +
            $"Recent Internet outages: {instability.OutageCount}{Environment.NewLine}" +
            "Internet instability threshold: 3 outages in 60 minutes" + Environment.NewLine +
            $"Internet instability active: {(instabilityActive ? "YES" : "NO")}{Environment.NewLine}" +
            "Password and protected settings: REDACTED";
    }

    private static string BuildInformation()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        return $"RouterPilot v{assembly.GetName().Version}{Environment.NewLine}" +
            $"Assembly version: {assembly.GetName().Version}{Environment.NewLine}" +
            $"Build location: {AppContext.BaseDirectory}{Environment.NewLine}" +
            $"Generated: {DateTimeOffset.Now:O}";
    }
}

public sealed record DiagnosticsExecutionResult(
    DiagnosticExecutionOutcome Outcome,
    string? Report,
    string Message,
    string? OutputPath);

public sealed record NetworkSnapshotExportResult(bool Success, bool Cancelled, string Message, string? OutputPath);
