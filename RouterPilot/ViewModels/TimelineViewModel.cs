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
    private readonly TimelineService _timelineService;
    private readonly CollectionViewSource _viewSource;

    public TimelineViewModel(TimelineService timelineService)
    {
        _timelineService = timelineService;
        Events = timelineService.Events;
        _viewSource = new CollectionViewSource { Source = Events };
        EventsView = _viewSource.View;
        EventsView.Filter = Matches;
        _timelineService.Changed += TimelineChanged;
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
        var items = EventsView.Cast<TimelineEvent>().Select(item => new SafeTimelineExport(item.Timestamp, item.Category.ToString(), item.EventType.ToString(), item.Severity.ToString(), item.Title, item.Message, item.Source ?? string.Empty)).ToList();
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

    private void TimelineChanged(object? sender, EventArgs e) => Refresh();

    private bool Matches(object item)
    {
        if (item is not TimelineEvent entry)
            return false;
        if (SelectedCategory != "All" && !string.Equals(entry.Category.ToString(), SelectedCategory, StringComparison.OrdinalIgnoreCase))
            return false;
        if (SelectedSeverity != "All" && !string.Equals(entry.Severity.ToString(), SelectedSeverity, StringComparison.OrdinalIgnoreCase))
            return false;
        DateTimeOffset now = DateTimeOffset.Now;
        if (SelectedDateRange == "Today" && entry.Timestamp.LocalDateTime.Date != now.Date) return false;
        if (SelectedDateRange == "Last 24 Hours" && entry.Timestamp < now.AddHours(-24)) return false;
        if (SelectedDateRange == "Last 7 Days" && entry.Timestamp < now.AddDays(-7)) return false;
        string query = SearchText.Trim();
        return query.Length == 0 || string.Join(' ', entry.Title, entry.Message, entry.Category, entry.EventType, entry.Source ?? string.Empty)
            .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SafeTimelineExport(DateTimeOffset Timestamp, string Category, string EventType, string Severity, string Title, string Message, string Source);
}
