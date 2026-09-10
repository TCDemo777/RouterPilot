using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class NotificationService : INotifyPropertyChanged, IAsyncDisposable
{
    private const int MaximumNotifications = 500;
    private const string LegacyWelcomeDeduplicationKey = "routerpilot-welcome";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dispatcher _dispatcher;
    private readonly string _storeFile;
    private readonly ObservableCollection<AppNotification> _notifications = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly object _deduplicationLock = new();
    private readonly Dictionary<string, DateTimeOffset> _deduplicationTimes =
        new(StringComparer.Ordinal);
    private long _saveVersion;
    private CancellationTokenSource? _debounceCancellation;
    private Task? _pendingSaveTask;
    private Task? _disposeTask;
    private bool _disposalStarted;
    private readonly SettingsService? _settingsService;
    private readonly IToastNotificationService? _toastNotificationService;
    private readonly string _applicationVersion;

    public NotificationService(
        Dispatcher dispatcher,
        string? dataFolder,
        TimeSpan? deduplicationQuietPeriod = null)
        : this(dispatcher, applicationDataPaths: null, dataFolder, deduplicationQuietPeriod)
    {
    }

    public NotificationService(
        Dispatcher dispatcher,
        ApplicationDataPathProvider? applicationDataPaths = null,
        string? dataFolder = null,
        TimeSpan? deduplicationQuietPeriod = null,
        SettingsService? settingsService = null,
        IToastNotificationService? toastNotificationService = null,
        string? applicationVersion = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        string folder = dataFolder ??
            (applicationDataPaths ?? new ApplicationDataPathProvider()).CurrentPath;
        _storeFile = Path.Combine(folder, "notifications.json");
        DeduplicationQuietPeriod = deduplicationQuietPeriod ?? TimeSpan.FromMinutes(5);
        _settingsService = settingsService;
        _toastNotificationService = toastNotificationService;
        _applicationVersion = NormalizeApplicationVersion(applicationVersion ?? GetApplicationVersion());
        Notifications = new ReadOnlyObservableCollection<AppNotification>(
            _notifications);
    }

    public ReadOnlyObservableCollection<AppNotification> Notifications { get; }

    public TimeSpan DeduplicationQuietPeriod { get; set; }

    public int UnreadCount => _notifications.Count(notification => !notification.IsRead);

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync()
    {
        await ReloadAsync().ConfigureAwait(false);

        string welcomeKey = WelcomeDeduplicationKeyFor(_applicationVersion);
        bool existingWelcome = await _dispatcher.InvokeAsync(() =>
        {
            if (_notifications.Any(notification => string.Equals(
                    notification.DeduplicationKey,
                    welcomeKey,
                    StringComparison.Ordinal)))
            {
                return true;
            }

            // Earlier builds stored one non-versioned welcome record. Adopt it
            // as the acknowledgement for the first version using this policy,
            // without duplicating an existing user's unread/read state or
            // sending another toast during migration.
            AppNotification? legacyWelcome = _notifications.FirstOrDefault(
                notification => string.Equals(
                    notification.DeduplicationKey,
                    LegacyWelcomeDeduplicationKey,
                    StringComparison.Ordinal));
            if (legacyWelcome is null)
                return false;

            lock (_deduplicationLock)
                _deduplicationTimes.Remove(LegacyWelcomeDeduplicationKey);
            legacyWelcome.DeduplicationKey = welcomeKey;
            RememberDeduplication(legacyWelcome);
            QueueSave();
            return true;
        });

        if (!existingWelcome)
        {
            await AddAsync(new AppNotification
            {
                Title = "Welcome to RouterPilot",
                Message = "Important router and network events will appear here.",
                Severity = NotificationSeverity.Information,
                Category = NotificationCategory.System,
                DeduplicationKey = welcomeKey
            });
        }
    }

    public static string WelcomeDeduplicationKeyFor(string applicationVersion) =>
        "routerpilot-welcome:" + NormalizeApplicationVersion(applicationVersion);

    private static string GetApplicationVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        int metadata = value.IndexOf('+');
        return metadata >= 0 ? value[..metadata] : value;
    }

    private static string NormalizeApplicationVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim();

    /// <summary>Reloads persisted history after an explicit data restore without creating a welcome item.</summary>
    public async Task<int> ReloadAsync()
    {
        List<AppNotification> loaded = await LoadAsync().ConfigureAwait(false);

        await _dispatcher.InvokeAsync(() =>
        {
            _notifications.Clear();
            _deduplicationTimes.Clear();
            foreach (AppNotification notification in loaded
                         .OrderByDescending(item => item.Timestamp)
                         .Take(MaximumNotifications))
            {
                _notifications.Add(notification);
                RememberDeduplication(notification);
            }

            OnPropertyChanged(nameof(UnreadCount));
        });

        return loaded.Count;
    }

    public Task<bool> AddAsync(AppNotification notification) => AddAsync(notification, preferencesOverride: null);

    /// <summary>
    /// Delivers feedback for an explicit user action while retaining the global
    /// notification and delivery-channel preferences. This intentionally bypasses
    /// only the event category preference.
    /// </summary>
    public Task<bool> AddManualFeedbackAsync(AppNotification notification) =>
        AddAsync(notification, preferencesOverride: null, bypassCategoryPreference: true);

    public async Task<bool> AddAsync(
        AppNotification notification,
        NotificationPreferences? preferencesOverride,
        bool bypassCategoryPreference = false)
    {
        ArgumentNullException.ThrowIfNull(notification);

        NotificationDeliveryChannels channels = GetDeliveryChannels(notification, preferencesOverride, bypassCategoryPreference);
        if (!channels.HasAny)
        {
            return false;
        }

        if (!TryReserveDeduplication(notification))
            return false;

        bool storedInCentre = false;
        if (channels.NotificationCentre)
        {
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    _notifications.Insert(0, notification);
                    while (_notifications.Count > MaximumNotifications)
                        _notifications.RemoveAt(_notifications.Count - 1);

                    RememberDeduplication(notification);
                    OnPropertyChanged(nameof(UnreadCount));
                    QueueSave();
                });

                storedInCentre = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to add Notification Centre entry: {ex.GetType().Name}");
            }
        }

        bool toastDelivered = false;
        if (channels.WindowsToast && _toastNotificationService is not null)
        {
            try
            {
                ToastDeliveryResult result = await _toastNotificationService
                    .SendAsync(notification.Title, notification.Message)
                    .ConfigureAwait(false);
                toastDelivered = result == ToastDeliveryResult.Delivered;

                if (!toastDelivered)
                {
                    Debug.WriteLine($"Windows toast delivery result: {result}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Windows toast dispatch failed: {ex.GetType().Name}");
            }
        }

        return storedInCentre || toastDelivered;
    }

    private NotificationDeliveryChannels GetDeliveryChannels(
        AppNotification notification,
        NotificationPreferences? preferencesOverride = null,
        bool bypassCategoryPreference = false)
    {
        if (_settingsService is null && preferencesOverride is null)
        {
            return new NotificationDeliveryChannels(
                NotificationCentre: true,
                WindowsToast: false);
        }

        NotificationPreferences preferences = preferencesOverride ?? _settingsService!.Load().NotificationPreferences
            ?? new NotificationPreferences();

        if (!preferences.Allows(notification, bypassCategoryPreference))
        {
            return default;
        }

        return new NotificationDeliveryChannels(
            NotificationCentre: preferences.NotificationCentreEnabled,
            WindowsToast: _toastNotificationService is not null &&
                preferences.WindowsToastsEnabled &&
                !preferences.IsQuietHours(DateTimeOffset.Now));
    }

    private readonly record struct NotificationDeliveryChannels(
        bool NotificationCentre,
        bool WindowsToast)
    {
        public bool HasAny => NotificationCentre || WindowsToast;
    }

    public Task MarkReadAsync(AppNotification? notification) =>
        MutateAsync(() =>
        {
            if (notification is not null && _notifications.Contains(notification))
                notification.IsRead = true;
        });

    public Task MarkAllReadAsync() => MutateAsync(() =>
    {
        foreach (AppNotification notification in _notifications)
            notification.IsRead = true;
    });

    public Task RemoveAsync(AppNotification? notification) =>
        MutateAsync(() =>
        {
            if (notification is not null)
                _notifications.Remove(notification);
        });

    public Task ClearAllAsync() => MutateAsync(_notifications.Clear);

    /// <summary>Removes only local notification history and deduplication state for one device identity.</summary>
    public Task RemoveDeviceNotificationsAsync(string normalizedMac) =>
        MutateAsync(() =>
        {
            string key = ClientIdentity.NormalizeMac(normalizedMac);
            if (key.Length != 12) return;

            foreach (AppNotification notification in _notifications
                         .Where(item => string.Equals(item.ActionTarget, key, StringComparison.OrdinalIgnoreCase) ||
                                        (item.DeduplicationKey?.Contains(key, StringComparison.OrdinalIgnoreCase) ?? false))
                         .ToList())
                _notifications.Remove(notification);

            lock (_deduplicationLock)
            {
                foreach (string deduplicationKey in _deduplicationTimes.Keys
                             .Where(item => item.Contains(key, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                    _deduplicationTimes.Remove(deduplicationKey);
            }
        });

    private async Task MutateAsync(Action mutation)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            mutation();
            OnPropertyChanged(nameof(UnreadCount));
            QueueSave();
        });
    }

    private bool TryReserveDeduplication(AppNotification notification)
    {
        if (string.IsNullOrWhiteSpace(notification.DeduplicationKey))
            return true;

        lock (_deduplicationLock)
        {
            if (_deduplicationTimes.TryGetValue(
                    notification.DeduplicationKey,
                    out DateTimeOffset lastSeen) &&
                notification.Timestamp - lastSeen < DeduplicationQuietPeriod)
            {
                return false;
            }

            _deduplicationTimes[notification.DeduplicationKey] =
                notification.Timestamp;
            return true;
        }
    }

    private void RememberDeduplication(AppNotification notification)
    {
        if (string.IsNullOrWhiteSpace(notification.DeduplicationKey))
            return;

        lock (_deduplicationLock)
            _deduplicationTimes[notification.DeduplicationKey] = notification.Timestamp;
    }

    private async Task<List<AppNotification>> LoadAsync()
    {
        try
        {
            if (!File.Exists(_storeFile))
                return new List<AppNotification>();

            await using FileStream stream = File.OpenRead(_storeFile);
            return await JsonSerializer.DeserializeAsync<List<AppNotification>>(
                       stream,
                       JsonOptions).ConfigureAwait(false)
                   ?? new List<AppNotification>();
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException)
        {
            Debug.WriteLine($"Unable to load notification history ({ex.GetType().Name}).");
            return new List<AppNotification>();
        }
    }

    private void QueueSave()
    {
        CancellationTokenSource? oldCancellation;
        Task? oldSaveTask;

        lock (_lifecycleLock)
        {
            if (_disposalStarted)
                return;

            (oldCancellation, oldSaveTask) = DetachPendingSaveLocked();
            long version = Interlocked.Increment(ref _saveVersion);
            var cancellation = new CancellationTokenSource();
            _debounceCancellation = cancellation;
            _pendingSaveTask = SaveLatestAsync(
                version,
                cancellation.Token);
        }

        CancelSafely(oldCancellation);
        _ = DisposeAfterCompletionAsync(
            oldCancellation,
            oldSaveTask);
    }

    private async Task SaveLatestAsync(
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);

            if (version != Interlocked.Read(ref _saveVersion))
                return;

            List<AppNotification> snapshot =
                await CreateSnapshotAsync().ConfigureAwait(false);

            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (version != Interlocked.Read(ref _saveVersion))
                    return;

                await WriteAtomicallyAsync(snapshot).ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException)
        {
            Debug.WriteLine($"Unable to save notification history ({ex.GetType().Name}).");
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
                return _disposeTask.WaitAsync(cancellationToken);
        }

        return FlushCoreAsync(cancellationToken);
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            CancellationTokenSource? pendingCancellation;
            Task? pendingSave;

            lock (_lifecycleLock)
            {
                (pendingCancellation, pendingSave) =
                    DetachPendingSaveLocked();
            }

            CancelSafely(pendingCancellation);

            try
            {
                if (pendingSave is not null)
                {
                    await pendingSave.ConfigureAwait(false);
                }
            }
            finally
            {
                pendingCancellation?.Dispose();
            }

            List<AppNotification> snapshot =
                await CreateSnapshotAsync().ConfigureAwait(false);

            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteAtomicallyAsync(snapshot, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private (CancellationTokenSource? Cancellation, Task? SaveTask)
        DetachPendingSaveLocked()
    {
        CancellationTokenSource? cancellation = _debounceCancellation;
        Task? saveTask = _pendingSaveTask;
        _debounceCancellation = null;
        _pendingSaveTask = null;
        return (cancellation, saveTask);
    }

    private static void CancelSafely(
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A detached owner may have completed cleanup concurrently.
        }
    }

    private static async Task DisposeAfterCompletionAsync(
        CancellationTokenSource? cancellation,
        Task? saveTask)
    {
        try
        {
            if (saveTask is not null)
            {
                await saveTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to save notification history ({ex.GetType().Name}).");
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task<List<AppNotification>> CreateSnapshotAsync()
    {
        if (_dispatcher.CheckAccess())
            return _notifications.ToList();

        return await _dispatcher.InvokeAsync(
            () => _notifications.ToList());
    }

    private async Task WriteAtomicallyAsync(
        List<AppNotification> snapshot,
        CancellationToken cancellationToken = default)
    {
        string folder = Path.GetDirectoryName(_storeFile)!;
        Directory.CreateDirectory(folder);
        string temporaryFile = _storeFile + ".tmp";
        string backupFile = _storeFile + ".bak";

        await using (var stream = new FileStream(
                         temporaryFile,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(_storeFile))
            File.Replace(temporaryFile, _storeFile, backupFile, true);
        else
            File.Move(temporaryFile, _storeFile);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposalStarted = true;
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);

        _saveGate.Dispose();
        _flushGate.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
