using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels;

public partial class TimelineViewModel : ObservableObject
{
    private static readonly TimeSpan PostRestorationWindow = TimeSpan.FromSeconds(60);
    // The normal dashboard cadence is 30 seconds. Five minutes gives several
    // observed samples while declining to imply continuity across app closure.
    private static readonly TimeSpan MaximumObservedInterruption = TimeSpan.FromMinutes(5);
    private readonly TimelineService _timelineService;
    private readonly CollectionViewSource _viewSource;
    private readonly ObservableCollection<TimelinePresentationItem> _presentation = new();

    public TimelineViewModel(TimelineService timelineService)
    {
        _timelineService = timelineService;
        Events = timelineService.Events;
        _viewSource = new CollectionViewSource { Source = _presentation };
        EventsView = _viewSource.View;
        EventsView.Filter = Matches;
        _timelineService.Changed += TimelineChanged;
        RebuildPresentation();
    }

    public ReadOnlyObservableCollection<TimelineEvent> Events { get; }
    public ICollectionView EventsView { get; }
    public string[] Categories { get; } = ["All", "Router", "Clients", "AdGuard", "Maintenance", "Diagnostics", "Backup", "Firmware", "Security", "Schedules"];
    public string[] Severities { get; } = ["All", "Information", "Success", "Warning", "Error"];
    public string[] DateRanges { get; } = ["Today", "Last 24 Hours", "Last 7 Days", "All"];

    [ObservableProperty] private string selectedCategory = "All";
    [ObservableProperty] private string selectedSeverity = "All";
    [ObservableProperty] private string selectedDateRange = "All";
    [ObservableProperty] private string searchText = string.Empty;

    public Visibility EmptyStateVisibility => EventsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyStateText => Events.Count == 0 ? "No timeline events yet." : "No events match the current filters.";

    partial void OnSelectedCategoryChanged(string value) => Refresh();
    partial void OnSelectedSeverityChanged(string value) => Refresh();
    partial void OnSelectedDateRangeChanged(string value) => Refresh();
    partial void OnSearchTextChanged(string value) => Refresh();

    [RelayCommand]
    private async System.Threading.Tasks.Task ClearAsync()
    {
        if (MessageBox.Show("Clear only the Timeline history? Notifications, maintenance history and diagnostics will be preserved.",
                "Clear Timeline", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await _timelineService.ClearAsync();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ExportAsync()
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json|Text (*.txt)|*.txt", FileName = "RouterPilot_Timeline_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") };
        if (dialog.ShowDialog() != true) return;
        var items = EventsView.Cast<TimelinePresentationItem>().SelectMany(item => item.SourceEvents).DistinctBy(item => item.Id).Select(item => new SafeTimelineExport(item.Timestamp, item.Category.ToString(), item.EventType.ToString(), item.Severity.ToString(), item.Title, item.Message, item.Source ?? string.Empty)).ToList();
        if (items.Count == 0) { MessageBox.Show("There are no currently filtered events to export.", "Export Timeline", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        string output = extension == ".json" ? JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }) : extension == ".txt" ? string.Join(Environment.NewLine + Environment.NewLine, items.Select(x => $"{x.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}\n[{x.Category}] {x.Title}\n{x.Severity} — {x.Message}")) : BuildCsv(items);
        await File.WriteAllTextAsync(dialog.FileName, output, new UTF8Encoding(false));
    }

    private static string BuildCsv(IEnumerable<SafeTimelineExport> items)
    {
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var rows = new List<string> { "Timestamp,Category,EventType,Severity,Title,Message,Source" };
        rows.AddRange(items.Select(x => string.Join(',', Escape(x.Timestamp.ToString("O")), Escape(x.Category), Escape(x.EventType), Escape(x.Severity), Escape(x.Title), Escape(x.Message), Escape(x.Source))));
        return string.Join(Environment.NewLine, rows);
    }

    public System.Threading.Tasks.Task MarkReadAsync() => _timelineService.MarkAllReadAsync();

    private void Refresh()
    {
        EventsView.Refresh();
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void TimelineChanged(object? sender, EventArgs e)
    {
        RebuildPresentation();
        Refresh();
    }

    private bool Matches(object item)
    {
        if (item is not TimelinePresentationItem entry)
            return false;
        if (SelectedCategory != "All" && !entry.SourceEvents.Any(source => string.Equals(source.Category.ToString(), SelectedCategory, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (SelectedSeverity != "All" && !string.Equals(entry.Severity.ToString(), SelectedSeverity, StringComparison.OrdinalIgnoreCase))
            return false;
        DateTimeOffset now = DateTimeOffset.Now;
        if (SelectedDateRange == "Today" && entry.Timestamp.LocalDateTime.Date != now.Date) return false;
        if (SelectedDateRange == "Last 24 Hours" && entry.Timestamp < now.AddHours(-24)) return false;
        if (SelectedDateRange == "Last 7 Days" && entry.Timestamp < now.AddDays(-7)) return false;
        string query = SearchText.Trim();
        return query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildPresentation()
    {
        Dictionary<string, bool> expansion = _presentation.OfType<NetworkIncident>()
            .ToDictionary(item => item.Id, item => item.IsExpanded, StringComparer.Ordinal);
        List<TimelineEvent> source = Events.OrderBy(item => item.Timestamp).ToList();
        HashSet<Guid> grouped = [];
        List<TimelinePresentationItem> next = [];

        for (int index = 0; index < source.Count; index++)
        {
            TimelineEvent loss = source[index];
            if (loss.EventType != TimelineEventType.InternetConnectionLost || grouped.Contains(loss.Id)) continue;
            TimelineEvent? nextLoss = source.Skip(index + 1).FirstOrDefault(item => item.EventType == TimelineEventType.InternetConnectionLost);
            TimelineEvent? restored = source.Skip(index + 1).FirstOrDefault(item =>
                item.EventType == TimelineEventType.InternetConnectionRestored &&
                (nextLoss is null || item.Timestamp < nextLoss.Timestamp));

            if (restored is not null && restored.Timestamp - loss.Timestamp <= MaximumObservedInterruption)
            {
                List<TimelineEvent> related = source.Where(item =>
                    item.Id == loss.Id || item.Id == restored.Id ||
                    (item.EventType == TimelineEventType.VpnDisconnected && item.Timestamp >= loss.Timestamp && item.Timestamp <= restored.Timestamp) ||
                    ((item.EventType is TimelineEventType.VpnConnected or TimelineEventType.PublicIpChanged) && item.Timestamp >= restored.Timestamp && item.Timestamp <= restored.Timestamp + PostRestorationWindow))
                    .OrderBy(item => item.Timestamp).ToList();
                grouped.UnionWith(related.Select(item => item.Id));
                string id = "network-interruption:" + loss.Id;
                next.Add(new NetworkIncident { Id = id, StartedUtc = loss.Timestamp, EndedUtc = restored.Timestamp, Events = related, IsExpanded = expansion.GetValueOrDefault(id) });
            }
            else if (restored is null)
            {
                List<TimelineEvent> related = source.Where(item =>
                    item.Id == loss.Id ||
                    (item.EventType == TimelineEventType.VpnDisconnected && item.Timestamp >= loss.Timestamp && (nextLoss is null || item.Timestamp < nextLoss.Timestamp)))
                    .OrderBy(item => item.Timestamp).ToList();
                grouped.UnionWith(related.Select(item => item.Id));
                string id = "network-interruption:" + loss.Id;
                next.Add(new NetworkIncident { Id = id, StartedUtc = loss.Timestamp, Events = related, IsExpanded = expansion.GetValueOrDefault(id) });
            }
        }

        next.AddRange(source.Where(item => !grouped.Contains(item.Id)).Select(item => new TimelineEventItem(item)));
        _presentation.Clear();
        foreach (TimelinePresentationItem item in next.OrderByDescending(item => item.Timestamp)) _presentation.Add(item);
    }

    private sealed record SafeTimelineExport(DateTimeOffset Timestamp, string Category, string EventType, string Severity, string Title, string Message, string Source);
}
