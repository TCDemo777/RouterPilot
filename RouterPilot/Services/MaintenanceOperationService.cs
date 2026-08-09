using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class MaintenanceOperationService
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly NotificationService _notificationService;
    private readonly MaintenanceHistoryService _historyService;
    private readonly DiagnosticsExecutionService _diagnosticsExecutionService;
    private readonly AdGuardMaintenanceStateService _adGuardMaintenanceStateService;
    private readonly IBackupRestoreService _backupRestoreService;
    private readonly TimelineService _timelineService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public MaintenanceOperationService(
        IRouterManagerProvider routerManagerProvider,
        NotificationService notificationService,
        MaintenanceHistoryService historyService,
        DiagnosticsExecutionService diagnosticsExecutionService,
        AdGuardMaintenanceStateService adGuardMaintenanceStateService,
        IBackupRestoreService backupRestoreService,
        TimelineService timelineService)
    {
        _routerManagerProvider = routerManagerProvider;
        _notificationService = notificationService;
        _historyService = historyService;
        _diagnosticsExecutionService = diagnosticsExecutionService;
        _adGuardMaintenanceStateService = adGuardMaintenanceStateService;
        _backupRestoreService = backupRestoreService;
        _timelineService = timelineService;
    }

    public Task<MaintenanceOperationResult> CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        ExecuteLocalAsync(
            MaintenanceAction.CreateBackup,
            token => _backupRestoreService.CreateBackupAsync(destinationPath, token),
            cancellationToken);

    public Task<MaintenanceOperationResult> RestoreBackupAsync(
        BackupInspection inspection,
        IReadOnlyCollection<string> selectedFiles,
        CancellationToken cancellationToken = default) =>
        ExecuteLocalAsync(
            MaintenanceAction.RestoreBackup,
            token => _backupRestoreService.RestoreAsync(inspection, selectedFiles, token),
            cancellationToken);

    private async Task<MaintenanceOperationResult> ExecuteLocalAsync(
        MaintenanceAction action,
        Func<CancellationToken, Task<BackupOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken))
            return MaintenanceOperationResult.Cancelled("Another maintenance action is already running.");

        Guid executionId = Guid.NewGuid();
        try
        {
            BackupOperationResult operationResult = await operation(cancellationToken);
            MaintenanceOperationResult result = new(
                operationResult.Succeeded ? MaintenanceOutcome.Success : MaintenanceOutcome.Error,
                operationResult.Message,
                operationResult.BackupPath,
                operationResult.BackupSizeBytes);
            await RecordAsync(action, result, executionId);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MaintenanceOperationResult result = MaintenanceOperationResult.Cancelled("Maintenance action cancelled.");
            await RecordAsync(action, result, executionId);
            return result;
        }
        catch (Exception)
        {
            MaintenanceOperationResult result = MaintenanceOperationResult.Error("RouterPilot could not complete this maintenance action.");
            await RecordAsync(action, result, executionId);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<MaintenanceOperationResult> ExecuteAsync(
        MaintenanceAction action,
        Func<Task> refreshAll,
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken))
        {
            return MaintenanceOperationResult.Cancelled(
                "Another maintenance action is already running.");
        }

        Guid executionId = Guid.NewGuid();
        try
        {
            string message = action switch
            {
                MaintenanceAction.RefreshAll => await RefreshAllAsync(refreshAll),
                MaintenanceAction.RestartWifi => await RestartWifiAsync(cancellationToken),
                MaintenanceAction.RestartAdGuard => await RestartAdGuardAsync(refreshAll, cancellationToken),
                MaintenanceAction.ReconnectWan => await ReconnectWanAsync(cancellationToken),
                MaintenanceAction.RebootRouter => await RebootRouterAsync(cancellationToken),
                MaintenanceAction.RunDiagnostics => await RunDiagnosticsAsync(cancellationToken),
                MaintenanceAction.BackupDiagnostics => await BackupDiagnosticsAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };

            MaintenanceOperationResult result = MaintenanceOperationResult.Success(message);
            if (action is not (MaintenanceAction.RunDiagnostics or MaintenanceAction.BackupDiagnostics))
            {
                await RecordAsync(action, result, executionId);
            }
            return result;
        }
        catch (MaintenanceOperationCancelledException)
        {
            return MaintenanceOperationResult.Cancelled("Diagnostics export cancelled.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MaintenanceOperationResult result = MaintenanceOperationResult.Cancelled("Maintenance action cancelled.");
            if (action is not (MaintenanceAction.RunDiagnostics or MaintenanceAction.BackupDiagnostics))
            {
                await RecordAsync(action, result, executionId);
            }
            return result;
        }
        catch (Exception)
        {
            MaintenanceOperationResult result = MaintenanceOperationResult.Error(
                action == MaintenanceAction.RestartAdGuard
                    ? "AdGuard Home could not be restarted."
                    : "RouterPilot could not complete this maintenance action.");
            if (action is not (MaintenanceAction.RunDiagnostics or MaintenanceAction.BackupDiagnostics))
            {
                await RecordAsync(action, result, executionId);
            }
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<string> RestartWifiAsync(CancellationToken cancellationToken)
    {
        RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
        string response = await router.RestartWifiAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        if (!response.Contains("successfully", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();

        if ((await router.GetWifiRadiosAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).Count == 0)
            throw new InvalidOperationException();

        return "Wi-Fi restarted and router interfaces are available.";
    }

    private async Task<string> RestartAdGuardAsync(
        Func<Task> refreshAll,
        CancellationToken cancellationToken)
    {
        RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
        AdGuardStatus before = await router.GetAdGuardStatusAsync()
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        _adGuardMaintenanceStateService.BeginRestart();

        try
        {
            await router.RestartAdGuardAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            bool transitionObserved = false;
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    AdGuardStatus current = await router.GetAdGuardStatusAsync()
                        .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                    transitionObserved |= !current.IsRunning ||
                        !string.Equals(current.Process, before.Process, StringComparison.Ordinal);

                    if (transitionObserved && current.IsRunning)
                    {
                        _ = await router.GetAdGuardProtectionStatusAsync()
                            .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                        _adGuardMaintenanceStateService.CompleteRestart();
                        await refreshAll();
                        return $"AdGuard Home restarted successfully (verified in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0}s).";
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    transitionObserved = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            throw new InvalidOperationException();
        }
        catch
        {
            _adGuardMaintenanceStateService.FailRestart();
            await refreshAll();
            throw;
        }

    }

    private async Task<string> ReconnectWanAsync(CancellationToken cancellationToken)
    {
        RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
        string response = await router.RestartWanAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        if (!response.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
            !(await router.GetNetworkInfoAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).Connected)
        {
            throw new InvalidOperationException();
        }

        return "WAN reconnected and internet connectivity is available.";
    }

    private async Task<string> RebootRouterAsync(CancellationToken cancellationToken)
    {
        RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
        await router.RebootRouterAsync().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        return "Reboot request accepted. RouterPilot will confirm recovery through normal refreshes.";
    }

    private async Task<string> RunDiagnosticsAsync(CancellationToken cancellationToken)
    {
        DiagnosticsExecutionResult result = await _diagnosticsExecutionService.RunAsync(
            DiagnosticExecutionSource.Maintenance,
            createBackup: false,
            cancellationToken: cancellationToken);
        return result.Outcome switch
        {
            DiagnosticExecutionOutcome.Success =>
                "Diagnostics completed. Open About to view the detailed support report.",
            DiagnosticExecutionOutcome.Cancelled => throw new MaintenanceOperationCancelledException(),
            _ => throw new InvalidOperationException(result.Message)
        };
    }

    private async Task<string> BackupDiagnosticsAsync(CancellationToken cancellationToken)
    {
        DiagnosticsExecutionResult result = await _diagnosticsExecutionService.RunAsync(
            DiagnosticExecutionSource.Maintenance,
            createBackup: true,
            cancellationToken: cancellationToken);
        return result.Outcome switch
        {
            DiagnosticExecutionOutcome.Success => "Diagnostics backup created.",
            DiagnosticExecutionOutcome.Cancelled => throw new MaintenanceOperationCancelledException(),
            _ => throw new InvalidOperationException(result.Message)
        };
    }

    private static async Task<string> RefreshAllAsync(Func<Task> refreshAll)
    {
        await refreshAll();
        return "RouterPilot refreshed all current dashboard data.";
    }

    private async Task RecordAsync(
        MaintenanceAction action,
        MaintenanceOperationResult result,
        Guid executionId)
    {
        await _historyService.AddAsync(new MaintenanceHistoryEntry
        {
            Id = executionId,
            Action = action,
            Outcome = result.Outcome,
            Message = result.Message,
            OutputPath = result.OutputPath,
            OutputSizeBytes = result.OutputSizeBytes
        });

        if (EventRoutingPolicy.ShouldNotify(action, result.Outcome))
        {
            await _notificationService.AddAsync(new AppNotification
            {
            Title = action == MaintenanceAction.RestartAdGuard
                ? result.Outcome == MaintenanceOutcome.Success
                    ? "AdGuard Home restarted successfully"
                    : "AdGuard Home could not be restarted"
                : result.Outcome == MaintenanceOutcome.Success
                    ? "Maintenance action completed"
                    : "Maintenance action failed",
            Message = action == MaintenanceAction.RestartAdGuard
                ? result.Message
                : MaintenanceActionPresentation.Title(action) + ": " + result.Message,
            Severity = result.Outcome == MaintenanceOutcome.Success
                ? NotificationSeverity.Success
                : result.Outcome == MaintenanceOutcome.Cancelled
                    ? NotificationSeverity.Warning
                    : NotificationSeverity.Error,
            Category = action switch
            {
                MaintenanceAction.RestartAdGuard => NotificationCategory.AdGuard,
                MaintenanceAction.CreateBackup or MaintenanceAction.RestoreBackup => NotificationCategory.System,
                _ => NotificationCategory.Router
            },
            EventType = result.Outcome == MaintenanceOutcome.Success
                ? NotificationEventType.MaintenanceSucceeded
                : NotificationEventType.MaintenanceFailed,
                DeduplicationKey = "Maintenance-" + action + "-" + executionId
            });
        }

        if (action != MaintenanceAction.RunDiagnostics)
        {
            bool isBackup = action is MaintenanceAction.CreateBackup or MaintenanceAction.RestoreBackup;
            string title = action switch
            {
                MaintenanceAction.CreateBackup when result.Outcome == MaintenanceOutcome.Success => "Backup created",
                MaintenanceAction.RestoreBackup when result.Outcome == MaintenanceOutcome.Success => "Restore completed",
                _ => MaintenanceActionPresentation.Title(action) + (result.Outcome == MaintenanceOutcome.Success ? " completed" : " failed")
            };
            await _timelineService.AddAsync(new TimelineEvent
            {
                Category = isBackup ? TimelineCategory.Backup : TimelineCategory.Maintenance,
                EventType = action == MaintenanceAction.CreateBackup ? TimelineEventType.BackupCreated :
                    action == MaintenanceAction.RestoreBackup ? TimelineEventType.RestoreCompleted :
                    result.Outcome == MaintenanceOutcome.Success ? TimelineEventType.MaintenanceCompleted : TimelineEventType.MaintenanceFailed,
                Title = title,
                Message = result.Message,
                Severity = result.Outcome == MaintenanceOutcome.Success ? TimelineSeverity.Success :
                    result.Outcome == MaintenanceOutcome.Cancelled ? TimelineSeverity.Warning : TimelineSeverity.Error,
                Source = "Maintenance",
                CorrelationId = executionId.ToString("N"),
                DeduplicationKey = "maintenance:" + executionId.ToString("N")
            });
        }
    }
}

file sealed class MaintenanceOperationCancelledException : Exception;

public sealed record MaintenanceOperationResult(
    MaintenanceOutcome Outcome,
    string Message,
    string? OutputPath = null,
    long? OutputSizeBytes = null)
{
    public static MaintenanceOperationResult Success(string message) => new(MaintenanceOutcome.Success, message);
    public static MaintenanceOperationResult Error(string message) => new(MaintenanceOutcome.Error, message);
    public static MaintenanceOperationResult Cancelled(string message) => new(MaintenanceOutcome.Cancelled, message);
}
