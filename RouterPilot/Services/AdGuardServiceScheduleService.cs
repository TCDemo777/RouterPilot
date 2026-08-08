using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class AdGuardServiceScheduleService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ObservableCollection<AdGuardServiceSchedule> _items = [];
    private readonly ObservableCollection<AdGuardServiceWindow> _windows = [];
    private readonly ObservableCollection<AdGuardServiceSchedule> _advancedItems = [];
    private readonly Dispatcher _dispatcher;
    private readonly BlockedServiceMutationService _mutations;
    private readonly NotificationService _notifications;
    private readonly IClock _clock;
    private readonly AdGuardServiceScheduleCalculator _calculator;
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _path;
    private readonly object _disposeSync = new();
    private bool _disposing;
    private bool _disposed;
    private Task? _disposeTask;

    public AdGuardServiceScheduleService(
        Dispatcher dispatcher, BlockedServiceMutationService mutations,
        NotificationService notifications, IClock clock)
        : this(
            dispatcher,
            mutations,
            notifications,
            clock,
            new ApplicationDataPathProvider())
    {
    }

    public AdGuardServiceScheduleService(
        Dispatcher dispatcher, BlockedServiceMutationService mutations,
        NotificationService notifications, IClock clock,
        ApplicationDataPathProvider applicationDataPaths)
    {
        _dispatcher = dispatcher;
        _mutations = mutations;
        _notifications = notifications;
        _clock = clock;
        _calculator = new(clock);
        _path = Path.Combine(applicationDataPaths.CurrentPath, "adguard-service-schedules.json");
        Schedules = new ReadOnlyObservableCollection<AdGuardServiceSchedule>(_items);
        Windows = new ReadOnlyObservableCollection<AdGuardServiceWindow>(_windows);
        AdvancedSchedules = new ReadOnlyObservableCollection<AdGuardServiceSchedule>(_advancedItems);
    }

    public ReadOnlyObservableCollection<AdGuardServiceSchedule> Schedules { get; }
    public ReadOnlyObservableCollection<AdGuardServiceWindow> Windows { get; }
    public ReadOnlyObservableCollection<AdGuardServiceSchedule> AdvancedSchedules { get; }
    public TimeSpan MissedOccurrenceGracePeriod { get; set; } = TimeSpan.FromMinutes(30);
    public event EventHandler<BlockedServiceMutationResult>? BlockedServicesChanged;

    public async Task InitializeAsync()
    {
        await ReloadAsync();
    }

    /// <summary>Reloads schedules after an explicit data restore.</summary>
    public async Task ReloadAsync()
    {
        List<AdGuardServiceSchedule> loaded = [];
        try
        {
            if (File.Exists(_path))
                loaded = JsonSerializer.Deserialize<List<AdGuardServiceSchedule>>(await File.ReadAllTextAsync(_path), JsonOptions) ?? [];
        }
        catch (Exception ex) { Debug.WriteLine($"Unable to load AdGuard service schedules ({ex.GetType().Name})."); }

        await _dispatcher.InvokeAsync(() =>
        {
            _items.Clear();
            _windows.Clear();
            _advancedItems.Clear();
            foreach (AdGuardServiceSchedule item in loaded)
            {
                Normalize(item);
                item.NextExecutionLocal = item.IsEnabled ? _calculator.Next(item, _clock.UtcNow) : null;
                _items.Add(item);
            }
            RebuildViews();
        });
    }

    public async Task SaveScheduleAsync(AdGuardServiceSchedule schedule)
    {
        Normalize(schedule);
        schedule.NextExecutionLocal = schedule.IsEnabled ? _calculator.Next(schedule, _clock.UtcNow) : null;
        await _dispatcher.InvokeAsync(() =>
        {
            int index = _items.ToList().FindIndex(x => x.Id == schedule.Id);
            if (index >= 0) _items[index] = schedule; else _items.Insert(0, schedule);
            RebuildViews();
        });
        await SaveAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _dispatcher.InvokeAsync(() => { AdGuardServiceSchedule? item = _items.FirstOrDefault(x => x.Id == id); if (item is not null) _items.Remove(item); RebuildViews(); });
        await SaveAsync();
    }

    public async Task DuplicateAsync(AdGuardServiceSchedule source)
    {
        AdGuardServiceSchedule copy = Clone(source);
        copy.Id = Guid.NewGuid(); copy.GroupId = null; copy.Name += " (copy)";
        copy.CreatedUtc = _clock.UtcNow; copy.LastExecutedUtc = null; copy.LastAttemptedOccurrenceUtc = null;
        await SaveScheduleAsync(copy);
    }

    public async Task CreateAllowedWindowAsync(string serviceId, string serviceName, TimeOnly allowAt, TimeOnly blockAt, ScheduleDays days)
    {
        await SaveWindowAsync(new() { Name = serviceName, ServiceIds = [serviceId], AllowTime = allowAt, BlockTime = blockAt, Recurrence = AdGuardServiceScheduleRecurrence.SelectedDays, SelectedDays = days, CreatedUtc = _clock.UtcNow });
    }

    public async Task SaveWindowAsync(AdGuardServiceWindow window, CancellationToken token = default)
    {
        if (!await _evaluationGate.WaitAsync(0, token)) throw new InvalidOperationException("A scheduled service change is currently running.");
        try
        {
            Normalize(window);
            await _dispatcher.InvokeAsync(() =>
            {
                AdGuardServiceSchedule? oldAllow = _items.FirstOrDefault(x => x.Id == window.AllowScheduleId);
                AdGuardServiceSchedule? oldBlock = _items.FirstOrDefault(x => x.Id == window.BlockScheduleId);
                AdGuardServiceSchedule allow = CreateWindowSchedule(window, AdGuardServiceScheduleAction.Allow, oldAllow);
                AdGuardServiceSchedule block = CreateWindowSchedule(window, AdGuardServiceScheduleAction.Block, oldBlock);
                ReplaceSchedule(allow);
                ReplaceSchedule(block);
                RebuildViews();
            });
            await SaveAsync(token);
        }
        finally { _evaluationGate.Release(); }
    }

    public async Task DeleteWindowAsync(AdGuardServiceWindow window, CancellationToken token = default)
    {
        if (!await _evaluationGate.WaitAsync(0, token)) throw new InvalidOperationException("A scheduled service change is currently running.");
        try
        {
            await _dispatcher.InvokeAsync(() => { _items.RemoveWhere(x => x.GroupId == window.Id); RebuildViews(); });
            await SaveAsync(token);
        }
        finally { _evaluationGate.Release(); }
    }

    public async Task DuplicateWindowAsync(AdGuardServiceWindow source, CancellationToken token = default)
    {
        AdGuardServiceWindow copy = CloneWindow(source);
        copy.Id = Guid.NewGuid(); copy.AllowScheduleId = Guid.NewGuid(); copy.BlockScheduleId = Guid.NewGuid();
        copy.Name += " (copy)"; copy.CreatedUtc = _clock.UtcNow; copy.LastActionUtc = null; copy.LastResult = null; copy.LastError = null;
        await SaveWindowAsync(copy, token);
    }

    public async Task SetWindowEnabledAsync(AdGuardServiceWindow window, bool enabled, CancellationToken token = default)
    {
        AdGuardServiceWindow copy = CloneWindow(window); copy.IsEnabled = enabled;
        await SaveWindowAsync(copy, token);
    }

    public Task RunWindowNowAsync(AdGuardServiceWindow window, AdGuardServiceScheduleAction action, CancellationToken token = default)
    {
        AdGuardServiceSchedule? schedule = _items.FirstOrDefault(x => x.GroupId == window.Id && x.Action == action);
        return schedule is null ? Task.FromException(new InvalidOperationException("The linked schedule is unavailable.")) : RunNowAsync(schedule, token);
    }

    public async Task EvaluateDueAsync(CancellationToken token)
    {
        if (!await _evaluationGate.WaitAsync(0, token)) return;
        try
        {
            DateTimeOffset now = _clock.UtcNow;
            AdGuardServiceSchedule[] snapshot = await _dispatcher.InvokeAsync(() => _items.Where(x => x.IsEnabled).ToArray());
            foreach (AdGuardServiceSchedule schedule in snapshot)
            {
                token.ThrowIfCancellationRequested();
                DateTimeOffset? due = _calculator.DueOccurrence(schedule, now, MissedOccurrenceGracePeriod);
                if (due is null) { schedule.NextExecutionLocal = _calculator.Next(schedule, now); continue; }
                DateTimeOffset occurrenceUtc = due.Value.ToUniversalTime();
                if (schedule.LastExecutedUtc == occurrenceUtc || schedule.LastAttemptedOccurrenceUtc == occurrenceUtc) continue;
                await ExecuteAsync(schedule, occurrenceUtc, false, token);
            }
        }
        finally { _evaluationGate.Release(); }
    }

    public async Task RunNowAsync(AdGuardServiceSchedule schedule, CancellationToken token = default)
    {
        if (_disposing || _disposed) throw new OperationCanceledException("Schedule service is shutting down.");
        if (!await _evaluationGate.WaitAsync(0, token))
            throw new InvalidOperationException("Another scheduled service change is already running.");
        try { await ExecuteAsync(schedule, _clock.UtcNow, true, token); }
        finally { _evaluationGate.Release(); }
    }

    private async Task ExecuteAsync(AdGuardServiceSchedule schedule, DateTimeOffset occurrenceUtc, bool runNow, CancellationToken token)
    {
        if (!runNow)
        {
            schedule.LastAttemptedOccurrenceUtc = occurrenceUtc;
            await SaveAsync();
        }
        try
        {
            BlockedServiceMutationResult result = await _mutations.ApplyAsync(schedule.ServiceIds, schedule.Action, token);
            schedule.LastExecutedUtc = occurrenceUtc;
            if (!runNow)
            {
                if (schedule.Recurrence == AdGuardServiceScheduleRecurrence.Once) schedule.IsEnabled = false;
                schedule.NextExecutionLocal = schedule.IsEnabled ? _calculator.Next(schedule, occurrenceUtc) : null;
            }
            schedule.LastError = null; schedule.LastErrorUtc = null;
            BlockedServicesChanged?.Invoke(this, result);
            await RefreshItemAsync(schedule);
            await SaveAsync();
            string names = string.IsNullOrWhiteSpace(schedule.ServiceDisplay) ? schedule.Name : schedule.ServiceDisplay;
            await _notifications.AddAsync(new AppNotification
            {
                Title = "Scheduled service change completed",
                Message = $"{names} {(schedule.Action == AdGuardServiceScheduleAction.Allow ? "is now allowed" : "is now blocked")}.",
                Severity = NotificationSeverity.Success, Category = NotificationCategory.AdGuard,
                EventType = NotificationEventType.ScheduleSucceeded,
                DeduplicationKey = $"AdGuardSchedule:{schedule.Id}:{occurrenceUtc.UtcTicks}:{(runNow ? "manual" : "scheduled")}" 
            });
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            schedule.LastError = "RouterPilot could not update the scheduled AdGuard services.";
            schedule.LastErrorUtc = _clock.UtcNow;
            await RefreshItemAsync(schedule);
            await SaveAsync();
            await _notifications.AddAsync(new AppNotification
            {
                Title = "Scheduled service change failed",
                Message = "RouterPilot could not update the scheduled AdGuard services.",
                Severity = NotificationSeverity.Warning, Category = NotificationCategory.AdGuard,
                EventType = NotificationEventType.ScheduleFailed,
                DeduplicationKey = $"AdGuardScheduleFailed:{schedule.Id}:{occurrenceUtc.UtcTicks}"
            });
            if (runNow) throw;
        }
    }

    public async Task FlushAsync(CancellationToken token = default) => await SaveAsync(token);

    private async Task SaveAsync(CancellationToken token = default)
    {
        if (_disposed) return;
        await _saveGate.WaitAsync(token);
        try
        {
            List<AdGuardServiceSchedule> snapshot = await _dispatcher.InvokeAsync(() => _items.ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, JsonOptions), token);
            File.Move(temp, _path, true);
        }
        finally { _saveGate.Release(); }
    }

    private async Task RefreshItemAsync(AdGuardServiceSchedule schedule) => await _dispatcher.InvokeAsync(() =>
    {
        int index = _items.IndexOf(schedule);
        if (index >= 0) _items[index] = schedule;
        RebuildViews();
    });

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposing = true;
        await _evaluationGate.WaitAsync();
        try { await FlushAsync(); }
        finally
        {
            _disposed = true;
            _evaluationGate.Release();
            _evaluationGate.Dispose();
            _saveGate.Dispose();
        }
    }

    private static void Normalize(AdGuardServiceSchedule item) => item.ServiceIds = item.ServiceIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static void Normalize(AdGuardServiceWindow item) => item.ServiceIds = item.ServiceIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static AdGuardServiceSchedule Clone(AdGuardServiceSchedule s) => new() { Id = s.Id, GroupId = s.GroupId, Name = s.Name, ServiceIds = [.. s.ServiceIds], Action = s.Action, LocalTime = s.LocalTime, Recurrence = s.Recurrence, SelectedDays = s.SelectedDays, OneTimeDate = s.OneTimeDate, IsEnabled = s.IsEnabled, CreatedUtc = s.CreatedUtc, ServiceDisplay = s.ServiceDisplay };
    private static ScheduleDays ShiftDays(ScheduleDays days) { ScheduleDays shifted = ScheduleDays.None; foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>()) { ScheduleDays flag = day switch { DayOfWeek.Monday => ScheduleDays.Monday, DayOfWeek.Tuesday => ScheduleDays.Tuesday, DayOfWeek.Wednesday => ScheduleDays.Wednesday, DayOfWeek.Thursday => ScheduleDays.Thursday, DayOfWeek.Friday => ScheduleDays.Friday, DayOfWeek.Saturday => ScheduleDays.Saturday, _ => ScheduleDays.Sunday }; if ((days & flag) != 0) shifted |= day switch { DayOfWeek.Monday => ScheduleDays.Tuesday, DayOfWeek.Tuesday => ScheduleDays.Wednesday, DayOfWeek.Wednesday => ScheduleDays.Thursday, DayOfWeek.Thursday => ScheduleDays.Friday, DayOfWeek.Friday => ScheduleDays.Saturday, DayOfWeek.Saturday => ScheduleDays.Sunday, _ => ScheduleDays.Monday }; } return shifted; }

    private AdGuardServiceSchedule CreateWindowSchedule(AdGuardServiceWindow window, AdGuardServiceScheduleAction action, AdGuardServiceSchedule? previous)
    {
        bool block = action == AdGuardServiceScheduleAction.Block;
        bool nextDay = block && window.BlockTime <= window.AllowTime;
        DateOnly? date = window.OneTimeDate;
        if (nextDay && date is not null) date = date.Value.AddDays(1);
        return new()
        {
            Id = block ? window.BlockScheduleId : window.AllowScheduleId, GroupId = window.Id,
            Name = window.Name, ServiceIds = [.. window.ServiceIds], Action = action,
            LocalTime = block ? window.BlockTime : window.AllowTime, Recurrence = window.Recurrence,
            SelectedDays = nextDay ? ShiftDays(window.SelectedDays) : window.SelectedDays,
            OneTimeDate = date, IsEnabled = window.IsEnabled, CreatedUtc = window.CreatedUtc,
            LastExecutedUtc = previous?.LastExecutedUtc, LastAttemptedOccurrenceUtc = previous?.LastAttemptedOccurrenceUtc,
            LastError = previous?.LastError, LastErrorUtc = previous?.LastErrorUtc,
            ServiceDisplay = window.ServiceDisplay
        };
    }

    private void ReplaceSchedule(AdGuardServiceSchedule schedule)
    {
        schedule.NextExecutionLocal = schedule.IsEnabled ? _calculator.Next(schedule, _clock.UtcNow) : null;
        int index = _items.ToList().FindIndex(x => x.Id == schedule.Id);
        if (index >= 0) _items[index] = schedule; else _items.Insert(0, schedule);
    }

    private void RebuildViews()
    {
        _windows.Clear(); _advancedItems.Clear();
        HashSet<Guid> pairedIds = [];
        foreach (IGrouping<Guid, AdGuardServiceSchedule> group in _items.Where(x => x.GroupId.HasValue).GroupBy(x => x.GroupId!.Value))
        {
            AdGuardServiceSchedule[] pair = group.ToArray();
            AdGuardServiceSchedule? allow = pair.SingleOrDefault(x => x.Action == AdGuardServiceScheduleAction.Allow);
            AdGuardServiceSchedule? block = pair.SingleOrDefault(x => x.Action == AdGuardServiceScheduleAction.Block);
            if (pair.Length != 2 || allow is null || block is null || !IsConsistentPair(allow, block)) continue;
            pairedIds.Add(allow.Id); pairedIds.Add(block.Id);
            DateOnly? startDate = allow.OneTimeDate;
            bool crosses = block.LocalTime <= allow.LocalTime;
            _windows.Add(new()
            {
                Id = group.Key, AllowScheduleId = allow.Id, BlockScheduleId = block.Id, Name = WindowName(allow, block),
                ServiceIds = [.. allow.ServiceIds], AllowTime = allow.LocalTime, BlockTime = block.LocalTime,
                Recurrence = allow.Recurrence, SelectedDays = allow.SelectedDays, OneTimeDate = startDate,
                IsEnabled = allow.IsEnabled || block.IsEnabled, CreatedUtc = allow.CreatedUtc,
                LastActionUtc = Latest(allow.LastExecutedUtc, block.LastExecutedUtc),
                LastError = LatestError(allow, block), LastResult = Latest(allow.LastExecutedUtc, block.LastExecutedUtc) is DateTimeOffset last ? $"Completed {last.ToLocalTime():dd MMM HH:mm}" : null,
                NextExecutionLocal = Earliest(allow.NextExecutionLocal, block.NextExecutionLocal),
                NextAction = NextAction(allow, block), ServiceDisplay = allow.ServiceDisplay
            });
        }
        foreach (AdGuardServiceSchedule item in _items.Where(x => !pairedIds.Contains(x.Id))) _advancedItems.Add(item);
    }

    private static bool IsConsistentPair(AdGuardServiceSchedule allow, AdGuardServiceSchedule block)
    {
        if (!allow.ServiceIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(block.ServiceIds) || allow.Recurrence != block.Recurrence) return false;
        bool nextDay = block.LocalTime <= allow.LocalTime;
        if (allow.Recurrence == AdGuardServiceScheduleRecurrence.SelectedDays && block.SelectedDays != (nextDay ? ShiftDays(allow.SelectedDays) : allow.SelectedDays)) return false;
        if (allow.Recurrence == AdGuardServiceScheduleRecurrence.Once && block.OneTimeDate != (nextDay ? allow.OneTimeDate?.AddDays(1) : allow.OneTimeDate)) return false;
        return true;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) => first is null ? second : second is null ? first : first > second ? first : second;
    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second) => first is null ? second : second is null ? first : first < second ? first : second;
    private static string? LatestError(AdGuardServiceSchedule allow, AdGuardServiceSchedule block) => allow.LastErrorUtc >= block.LastErrorUtc ? allow.LastError : block.LastError;
    private static AdGuardServiceScheduleAction? NextAction(AdGuardServiceSchedule allow, AdGuardServiceSchedule block)
    {
        if (allow.NextExecutionLocal is null) return block.NextExecutionLocal is null ? null : AdGuardServiceScheduleAction.Block;
        if (block.NextExecutionLocal is null) return AdGuardServiceScheduleAction.Allow;
        return allow.NextExecutionLocal <= block.NextExecutionLocal ? AdGuardServiceScheduleAction.Allow : AdGuardServiceScheduleAction.Block;
    }
    private static string WindowName(AdGuardServiceSchedule allow, AdGuardServiceSchedule block)
    {
        const string allowPrefix = "Allow "; const string blockPrefix = "Block ";
        if (allow.Name.StartsWith(allowPrefix, StringComparison.OrdinalIgnoreCase) && block.Name.StartsWith(blockPrefix, StringComparison.OrdinalIgnoreCase) &&
            allow.Name[allowPrefix.Length..].Equals(block.Name[blockPrefix.Length..], StringComparison.OrdinalIgnoreCase)) return allow.Name[allowPrefix.Length..];
        return allow.Name;
    }
    private static AdGuardServiceWindow CloneWindow(AdGuardServiceWindow w) => new() { Id = w.Id, AllowScheduleId = w.AllowScheduleId, BlockScheduleId = w.BlockScheduleId, Name = w.Name, ServiceIds = [.. w.ServiceIds], AllowTime = w.AllowTime, BlockTime = w.BlockTime, Recurrence = w.Recurrence, SelectedDays = w.SelectedDays, OneTimeDate = w.OneTimeDate, IsEnabled = w.IsEnabled, CreatedUtc = w.CreatedUtc, ServiceDisplay = w.ServiceDisplay };
}

file static class ScheduleCollectionExtensions
{
    public static void RemoveWhere<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        for (int index = collection.Count - 1; index >= 0; index--) if (predicate(collection[index])) collection.RemoveAt(index);
    }
}
