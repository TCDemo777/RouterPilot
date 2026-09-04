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
    private readonly TimelineService _timelineService;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private FirmwareUpdateCheck _current;
    private bool _isChecking;
    private bool _hasAuthoritativeCurrentVersion;

    public FirmwareUpdateService(SettingsService settingsService,
        IRouterManagerProvider routerManagerProvider,
        NotificationService notificationService,
        TimelineService timelineService)
    {
        _settingsService = settingsService;
        _routerManagerProvider = routerManagerProvider;
        _notificationService = notificationService;
        _timelineService = timelineService;
        _current = _settingsService.Load().FirmwareUpdateCheck ?? new FirmwareUpdateCheck();
    }

    public FirmwareUpdateCheck Current => _current;
    public bool IsChecking => _isChecking;
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task CheckAutomaticallyAsync(RouterManager router,
        CancellationToken cancellationToken = default)
    {
        if (_current.LastChecked is { } checkedAt &&
            DateTimeOffset.UtcNow - checkedAt < AutomaticCheckInterval)
        {
            return Task.CompletedTask;
        }

        // CheckFirmwareUpdateAsync is the authoritative GL.iNet firmware source.
        // Do not compare its version against LuCI/OpenWrt release information.
        return CheckAsync(router, null, cancellationToken);
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
            var failed = new FirmwareUpdateCheck
            {
                CurrentVersion = _current.CurrentVersion,
                Status = FirmwareUpdateCheckStatus.Error,
                LastChecked = DateTimeOffset.UtcNow,
                ErrorCategory = ClassifyFailure(exception)
            };
            await PersistAsync(failed);
            await RecordFailureAsync(failed);
        }
    }

    private async Task CheckAsync(RouterManager router, string? knownCurrentVersion,
        CancellationToken cancellationToken)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            System.Diagnostics.Debug.WriteLine("Firmware update check started (GL.iNet read-only RPC).");
            SetChecking(true);
            FirmwareUpdateCheck result = await router.CheckFirmwareUpdateAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(result.CurrentVersion))
                result.CurrentVersion = knownCurrentVersion ?? string.Empty;

            FirmwareUpdateCheck previous = _current;
            await PersistAsync(result);
            System.Diagnostics.Debug.WriteLine($"Firmware update check completed: {result.Status}; current={(!string.IsNullOrWhiteSpace(result.CurrentVersion) ? "available" : "missing")}; latest={(!string.IsNullOrWhiteSpace(result.LatestVersion) ? "available" : "not returned") }.");

            // A persisted value may predate the authoritative GL.iNet firmware
            // source. The first successful result establishes this session's
            // baseline; only later GL.iNet A → B transitions are changes.
            bool recordFirmwareChange = _hasAuthoritativeCurrentVersion &&
                !string.IsNullOrWhiteSpace(previous.CurrentVersion) &&
                !string.IsNullOrWhiteSpace(result.CurrentVersion) &&
                !string.Equals(previous.CurrentVersion, result.CurrentVersion, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(result.CurrentVersion))
            {
                _hasAuthoritativeCurrentVersion = true;
            }

            if (recordFirmwareChange)
            {
                bool completedAdvertisedUpdate = string.Equals(previous.LatestVersion, result.CurrentVersion,
                    StringComparison.OrdinalIgnoreCase);
                await _timelineService.AddAsync(new TimelineEvent
                {
                    Category = TimelineCategory.Firmware,
                    EventType = completedAdvertisedUpdate ? TimelineEventType.FirmwareUpdateCompleted : TimelineEventType.FirmwareChanged,
                    Title = completedAdvertisedUpdate ? "Firmware update completed" : "Router firmware changed",
                    Message = $"{previous.CurrentVersion} → {result.CurrentVersion}.",
                    Severity = TimelineSeverity.Success,
                    Source = "Firmware check",
                    DeduplicationKey = $"firmware-changed:{_settingsService.Load().RouterHost}:{previous.CurrentVersion}:{result.CurrentVersion}"
                });
            }

            if (result.Status == FirmwareUpdateCheckStatus.UpdateAvailable)
            {
                AppSettings settings = _settingsService.Load();
                await _timelineService.AddAsync(new TimelineEvent
                {
                    Category = TimelineCategory.Firmware,
                    EventType = TimelineEventType.FirmwareUpdateAvailable,
                    Title = "Router firmware update available",
                    Message = $"Current: {result.CurrentVersion}; latest: {result.LatestVersion}.",
                    Severity = TimelineSeverity.Information,
                    Source = "Firmware check",
                    DeduplicationKey = $"firmware-update:{settings.RouterHost}:{result.LatestVersion}"
                });
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
                    Category = NotificationCategory.Firmware,
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
            System.Diagnostics.Debug.WriteLine($"Firmware update check failed ({ClassifyFailure(exception)}).");
            var failed = new FirmwareUpdateCheck
            {
                CurrentVersion = knownCurrentVersion ?? _current.CurrentVersion,
                Status = FirmwareUpdateCheckStatus.Error,
                LastChecked = DateTimeOffset.UtcNow,
                ErrorCategory = ClassifyFailure(exception)
            };
            await PersistAsync(failed);
            await RecordFailureAsync(failed);
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

    private async Task RecordFailureAsync(FirmwareUpdateCheck failed)
    {
        await _timelineService.AddAsync(new TimelineEvent
        {
            Category = TimelineCategory.Firmware,
            EventType = TimelineEventType.FirmwareCheckFailed,
            Title = "Firmware check failed",
            Message = "RouterPilot could not check for firmware updates.",
            Severity = TimelineSeverity.Error,
            Source = "Firmware check",
            DeduplicationKey = $"firmware-check-failed:{_settingsService.Load().RouterHost}:{failed.ErrorCategory}:{DateTimeOffset.UtcNow:yyyyMMddHH}"
        });
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
