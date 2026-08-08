using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class DiagnosticsExecutionService
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly DiagnosticsHistoryService _historyService;
    private readonly MaintenanceHistoryService _maintenanceHistoryService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService _settingsService;

    public DiagnosticsExecutionService(
        IRouterManagerProvider routerManagerProvider,
        DiagnosticsHistoryService historyService,
        MaintenanceHistoryService maintenanceHistoryService,
        NotificationService notificationService,
        SettingsService settingsService)
    {
        _routerManagerProvider = routerManagerProvider;
        _historyService = historyService;
        _maintenanceHistoryService = maintenanceHistoryService;
        _notificationService = notificationService;
        _settingsService = settingsService;
    }

    public async Task<DiagnosticsExecutionResult> RunAsync(
        DiagnosticExecutionSource source,
        CancellationToken cancellationToken = default)
    {
        string? outputPath = SelectOutputPath();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return await CompleteAsync(
                source,
                DiagnosticExecutionOutcome.Cancelled,
                "Diagnostics export cancelled.",
                null);
        }

        try
        {
            RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
            string report = await router.GetClientDiagnosticsAsync()
                .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            await CreateBundleAsync(outputPath, report, cancellationToken);

            return await CompleteAsync(
                source,
                DiagnosticExecutionOutcome.Success,
                "Diagnostics exported successfully.",
                outputPath,
                report);
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
        string? report = null)
    {
        await _historyService.AddAsync(outcome, $"{message}{(outputPath is null ? string.Empty : " " + outputPath)}", source, outputPath);
        await _maintenanceHistoryService.AddAsync(new MaintenanceHistoryEntry
        {
            Action = MaintenanceAction.RunDiagnostics,
            Source = source.ToString(),
            Outcome = outcome switch
            {
                DiagnosticExecutionOutcome.Success => MaintenanceOutcome.Success,
                DiagnosticExecutionOutcome.Cancelled => MaintenanceOutcome.Cancelled,
                _ => MaintenanceOutcome.Error
            },
            Message = message
        });

        if (outcome != DiagnosticExecutionOutcome.Cancelled)
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

        return new DiagnosticsExecutionResult(outcome, report, message, outputPath);
    }

    private string BuildSystemInformation()
    {
        AppSettings settings = _settingsService.Load();
        return $"RouterPilot System Information{Environment.NewLine}" +
            $"Generated: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $".NET: {RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
            $"OS: {RuntimeInformation.OSDescription}{Environment.NewLine}" +
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}{Environment.NewLine}" +
            "Router endpoint: configured" + Environment.NewLine +
            $"Refresh interval: {settings.RefreshIntervalSeconds} seconds{Environment.NewLine}" +
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
