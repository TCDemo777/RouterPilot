using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Local-only VPN scheduling. Evaluation is driven by RouterPilot's existing
/// one-minute scheduler; it does not create a router-side job or poll the router.
/// </summary>
public sealed class VpnScheduleService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dispatcher _dispatcher;
    private readonly IVpnService _vpnService;
    private readonly IVpnSummaryService _vpnSummary;
    private readonly TimelineService _timeline;
    private readonly IClock _clock;
    private readonly string _path;
    private readonly ObservableCollection<VpnSchedule> _items = [];
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _initialized;

    public VpnScheduleService(Dispatcher dispatcher, IVpnService vpnService, IVpnSummaryService vpnSummary,
        TimelineService timeline, IClock clock, ApplicationDataPathProvider paths)
    {
        _dispatcher = dispatcher; _vpnService = vpnService; _vpnSummary = vpnSummary;
        _timeline = timeline; _clock = clock;
        _path = Path.Combine(paths.CurrentPath, "vpn-schedules.json");
        Schedules = new ReadOnlyObservableCollection<VpnSchedule>(_items);
    }

    public ReadOnlyObservableCollection<VpnSchedule> Schedules { get; }
    public event EventHandler? SchedulesChanged;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        List<VpnSchedule> loaded = [];
        try
        {
            if (File.Exists(_path))
                loaded = JsonSerializer.Deserialize<List<VpnSchedule>>(await File.ReadAllTextAsync(_path), JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to load VPN schedules ({ex.GetType().Name}); schedules remain inactive until saved again.");
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (_initialized) return;
            foreach (VpnSchedule schedule in loaded.Where(IsValidPersisted))
            {
                schedule.ExecutedOccurrences = schedule.ExecutedOccurrences
                    .Where(key => TryOccurrenceDate(key, out DateOnly date) && date >= DateOnly.FromDateTime(DateTime.Today.AddDays(-7)))
                    .Distinct(StringComparer.Ordinal).ToList();
                _items.Add(schedule);
            }
            _initialized = true;
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task<string?> SaveAsync(VpnSchedule schedule)
    {
        string? error = Validate(schedule, _items.Where(item => item.Id != schedule.Id));
        if (error is not null) return error;
        schedule.Name = string.IsNullOrWhiteSpace(schedule.Name) ? "VPN schedule" : schedule.Name.Trim();
        schedule.UpdatedUtc = _clock.UtcNow;
        if (schedule.CreatedUtc == default) schedule.CreatedUtc = schedule.UpdatedUtc;
        await _dispatcher.InvokeAsync(() =>
        {
            int index = _items.ToList().FindIndex(item => item.Id == schedule.Id);
            if (index >= 0) _items[index] = schedule; else _items.Insert(0, schedule);
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
        });
        await PersistAsync();
        return null;
    }

    public async Task DeleteAsync(Guid id)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            VpnSchedule? schedule = _items.FirstOrDefault(item => item.Id == id);
            if (schedule is not null) _items.Remove(schedule);
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
        });
        await PersistAsync();
    }

    /// <summary>Checks only the current local minute. Missed actions are never replayed.</summary>
    public async Task EvaluateDueAsync(CancellationToken token)
    {
        if (!_initialized || !await _evaluationGate.WaitAsync(0, token)) return;
        try
        {
            DateTimeOffset now = TimeZoneInfo.ConvertTime(_clock.UtcNow, _clock.LocalTimeZone);
            VpnSchedule[] schedules = await _dispatcher.InvokeAsync(() => _items.Where(item => item.IsEnabled).ToArray());
            foreach (VpnSchedule schedule in schedules)
            {
                token.ThrowIfCancellationRequested();
                foreach (VpnScheduledAction action in DueActions(schedule, now))
                {
                    string occurrence = OccurrenceKey(schedule.Id, action, now);
                    if (schedule.ExecutedOccurrences.Contains(occurrence, StringComparer.Ordinal)) continue;
                    schedule.ExecutedOccurrences.Add(occurrence); // Persist before the action to survive a restart in this minute.
                    await PersistAsync();
                    await ExecuteAsync(schedule, action, occurrence, token);
                }
            }
        }
        finally { _evaluationGate.Release(); }
    }

    public DateTimeOffset? GetNextOccurrence(VpnSchedule schedule)
    {
        if (!schedule.IsEnabled) return null;
        DateTimeOffset now = TimeZoneInfo.ConvertTime(_clock.UtcNow, _clock.LocalTimeZone);
        for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            DateTime date = now.Date.AddDays(dayOffset);
            if (!OccursOn(schedule, date.DayOfWeek)) continue;
            foreach ((VpnScheduledAction _, TimeOnly time) in Actions(schedule).OrderBy(pair => pair.Time))
            {
                DateTimeOffset occurrence = new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, now.Offset);
                if (occurrence > now) return occurrence;
            }
        }
        return null;
    }

    private async Task ExecuteAsync(VpnSchedule schedule, VpnScheduledAction action, string occurrence, CancellationToken token)
    {
        IReadOnlyList<VpnTunnelInfo> tunnels = _vpnSummary.Tunnels;
        IReadOnlyList<VpnClientProfileInfo> profiles = _vpnSummary.Profiles;
        if (tunnels.Count != 1)
        {
            await RecordAsync("Scheduled VPN action skipped", "RouterPilot could not identify exactly one VPN tunnel.", TimelineSeverity.Warning, occurrence, token);
            return;
        }

        VpnTunnelInfo tunnel = tunnels[0];
        bool isEnabled = tunnel.Enabled || string.Equals(tunnel.ConnectionState, "Connected", StringComparison.Ordinal) || string.Equals(tunnel.ConnectionState, "Connecting", StringComparison.Ordinal);
        if (action == VpnScheduledAction.Enable && isEnabled) return;
        if (action == VpnScheduledAction.Disable && !isEnabled) return;

        int serverConfigCount = ServerConfigCount(tunnel, profiles);
        if (action == VpnScheduledAction.Enable && (serverConfigCount == 0 || serverConfigCount > 1))
        {
            await RecordAsync("Scheduled VPN connection skipped",
                serverConfigCount > 1 ? "Multiple VPN servers are configured." : "No VPN server is configured for this profile.",
                TimelineSeverity.Warning, occurrence, token);
            return;
        }

        VpnOperationResult result = await _vpnService.SetTunnelEnabledAsync(tunnel.TunnelId, action == VpnScheduledAction.Enable, token);
        if (result.Success)
        {
            await RecordAsync(action == VpnScheduledAction.Enable ? "Scheduled VPN connection started" : "Scheduled VPN disconnected",
                string.IsNullOrWhiteSpace(tunnel.ActiveProfileName) ? tunnel.Name : tunnel.ActiveProfileName,
                TimelineSeverity.Success, occurrence, token);
        }
        else
        {
            await RecordAsync(action == VpnScheduledAction.Enable ? "Scheduled VPN connection failed" : "Scheduled VPN disconnect failed",
                string.IsNullOrWhiteSpace(result.Message) ? "RouterPilot could not update the VPN tunnel." : result.Message,
                TimelineSeverity.Warning, occurrence, token);
        }
    }

    private static int ServerConfigCount(VpnTunnelInfo tunnel, IReadOnlyList<VpnClientProfileInfo> profiles)
    {
        List<VpnClientProfileInfo> linked = profiles.Where(profile => tunnel.ProfileGroupIds.Contains(profile.GroupId)).ToList();
        int serverConfigCount = linked.Count == 1 ? linked[0].ServerConfigCount : -1;
        return serverConfigCount;
    }

    private async Task RecordAsync(string title, string message, TimelineSeverity severity, string occurrence, CancellationToken token) =>
        await _timeline.AddAsync(new TimelineEvent { Category = TimelineCategory.Schedules, EventType = TimelineEventType.MaintenanceCompleted, Title = title, Message = message, Severity = severity, Source = "VPN schedule", DeduplicationKey = "vpn-schedule:" + occurrence }, token);

    private static IEnumerable<VpnScheduledAction> DueActions(VpnSchedule schedule, DateTimeOffset now)
    {
        if (!OccursOn(schedule, now.DayOfWeek)) yield break;
        TimeOnly current = TimeOnly.FromDateTime(now.DateTime);
        if (schedule.EnableTime == current) yield return VpnScheduledAction.Enable;
        if (schedule.DisableTime == current) yield return VpnScheduledAction.Disable;
    }
    private static IEnumerable<(VpnScheduledAction Action, TimeOnly Time)> Actions(VpnSchedule schedule)
    {
        if (schedule.EnableTime is { } enable) yield return (VpnScheduledAction.Enable, enable);
        if (schedule.DisableTime is { } disable) yield return (VpnScheduledAction.Disable, disable);
    }
    private static bool OccursOn(VpnSchedule schedule, DayOfWeek day) => (schedule.Days & ToFlag(day)) != 0;
    private static ScheduleDays ToFlag(DayOfWeek day) => day switch { DayOfWeek.Monday => ScheduleDays.Monday, DayOfWeek.Tuesday => ScheduleDays.Tuesday, DayOfWeek.Wednesday => ScheduleDays.Wednesday, DayOfWeek.Thursday => ScheduleDays.Thursday, DayOfWeek.Friday => ScheduleDays.Friday, DayOfWeek.Saturday => ScheduleDays.Saturday, _ => ScheduleDays.Sunday };
    private static string OccurrenceKey(Guid id, VpnScheduledAction action, DateTimeOffset local) => $"{id:N}:{action}:{local:yyyy-MM-dd-HH-mm}";
    private static bool TryOccurrenceDate(string key, out DateOnly date)
    {
        date = default;
        string[] parts = key.Split(':');
        return parts.Length == 3 && parts[2].Length >= 10 && DateOnly.TryParseExact(parts[2][..10], "yyyy-MM-dd", out date);
    }
    private static bool IsValidPersisted(VpnSchedule schedule) => schedule.Id != Guid.Empty && schedule.Days != ScheduleDays.None && (schedule.EnableTime is not null || schedule.DisableTime is not null);
    private static string? Validate(VpnSchedule schedule, IEnumerable<VpnSchedule> otherSchedules)
    {
        if (schedule.Days == ScheduleDays.None) return "Choose at least one day.";
        if (schedule.EnableTime is null && schedule.DisableTime is null) return "Choose an enable or disable time.";
        if (schedule.EnableTime == schedule.DisableTime && schedule.EnableTime is not null) return "Enable and disable times cannot be the same.";
        foreach (VpnSchedule other in otherSchedules.Where(item => item.IsEnabled))
        {
            if ((schedule.Days & other.Days) == ScheduleDays.None) continue;
            if (schedule.EnableTime is not null && other.DisableTime == schedule.EnableTime) return "Another enabled schedule disables VPN at the same time.";
            if (schedule.DisableTime is not null && other.EnableTime == schedule.DisableTime) return "Another enabled schedule enables VPN at the same time.";
        }
        return null;
    }

    private async Task PersistAsync(CancellationToken token = default)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            List<VpnSchedule> snapshot = await _dispatcher.InvokeAsync(() => _items.ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), token);
            File.Move(temporary, _path, true);
        }
        finally { _saveGate.Release(); }
    }

    public Task FlushAsync(CancellationToken token = default) => PersistAsync(token);
}
