using System;
using System.ComponentModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class FirmwareUpdateService : INotifyPropertyChanged
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(12);
    private readonly SettingsService _settingsService;
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly NotificationService _notificationService;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private FirmwareUpdateCheck _current;
    private bool _isChecking;

    public FirmwareUpdateService(SettingsService settingsService,
        IRouterManagerProvider routerManagerProvider,
        NotificationService notificationService)
    {
        _settingsService = settingsService;
        _routerManagerProvider = routerManagerProvider;
        _notificationService = notificationService;
        _current = _settingsService.Load().FirmwareUpdateCheck ?? new FirmwareUpdateCheck();
    }

    public FirmwareUpdateCheck Current => _current;
    public bool IsChecking => _isChecking;
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task CheckAutomaticallyAsync(RouterManager router, string knownCurrentVersion,
        CancellationToken cancellationToken = default)
    {
        if (_current.LastChecked is { } checkedAt &&
            DateTimeOffset.UtcNow - checkedAt < AutomaticCheckInterval)
        {
            return Task.CompletedTask;
        }

        return CheckAsync(router, knownCurrentVersion, cancellationToken);
    }

    public async Task CheckManuallyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            await CheckAsync(router, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistAsync(new FirmwareUpdateCheck
            {
                CurrentVersion = _current.CurrentVersion,
                Status = FirmwareUpdateCheckStatus.Error,
                LastChecked = DateTimeOffset.UtcNow,
                ErrorCategory = ClassifyFailure(exception)
            });
        }
    }

    private async Task CheckAsync(RouterManager router, string? knownCurrentVersion,
        CancellationToken cancellationToken)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            SetChecking(true);
            FirmwareUpdateCheck result = await router.CheckFirmwareUpdateAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(result.CurrentVersion))
                result.CurrentVersion = knownCurrentVersion ?? string.Empty;

            await PersistAsync(result);

            if (result.Status == FirmwareUpdateCheckStatus.UpdateAvailable)
            {
                AppSettings settings = _settingsService.Load();
                if (string.Equals(settings.LastNotifiedFirmwareVersion, result.LatestVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool delivered = await _notificationService.AddAsync(new AppNotification
                {
                    Title = "Router firmware update available",
                    Message = $"Current: {result.CurrentVersion}; latest: {result.LatestVersion}.",
                    Severity = NotificationSeverity.Information,
                    Category = NotificationCategory.Router,
                    EventType = NotificationEventType.FirmwareUpdateAvailable,
                    DeduplicationKey = $"FirmwareUpdate-{settings.RouterHost}-{result.LatestVersion}"
                });

                if (delivered)
                {
                    settings.LastNotifiedFirmwareVersion = result.LatestVersion;
                    settings.FirmwareUpdateCheck = result;
                    _settingsService.Save(settings);
                }
            }
            else if (result.Status == FirmwareUpdateCheckStatus.UpToDate)
            {
                AppSettings settings = _settingsService.Load();
                if (!string.IsNullOrEmpty(settings.LastNotifiedFirmwareVersion))
                {
                    settings.LastNotifiedFirmwareVersion = string.Empty;
                    settings.FirmwareUpdateCheck = result;
                    _settingsService.Save(settings);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistAsync(new FirmwareUpdateCheck
            {
                CurrentVersion = knownCurrentVersion ?? _current.CurrentVersion,
                Status = FirmwareUpdateCheckStatus.Error,
                LastChecked = DateTimeOffset.UtcNow,
                ErrorCategory = ClassifyFailure(exception)
            });
        }
        finally
        {
            SetChecking(false);
            _checkGate.Release();
        }
    }

    private Task PersistAsync(FirmwareUpdateCheck result)
    {
        _current = result;
        AppSettings settings = _settingsService.Load();
        settings.FirmwareUpdateCheck = result;
        _settingsService.Save(settings);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        return Task.CompletedTask;
    }

    private void SetChecking(bool checking)
    {
        _isChecking = checking;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecking)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        HttpRequestException => "network",
        TaskCanceledException => "timeout",
        _ => "router-rpc"
    };
}
