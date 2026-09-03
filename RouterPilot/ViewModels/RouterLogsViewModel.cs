using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public partial class RouterLogsViewModel : ObservableObject
{
    private readonly IRouterManagerProvider _provider;
    private readonly IRouterProfileService _profiles;
    private List<RouterLogEntry> _all = new();
    private CancellationTokenSource? _loadCancellation;
    public ObservableCollection<RouterLogEntry> Entries { get; } = new();
    public IReadOnlyList<string> SeverityOptions { get; } = ["All", "Error and above", "Warning and above", "Info", "Debug"];
    public IReadOnlyList<string> CategoryOptions { get; } = ["All", "System", "Network / WAN", "DHCP / DNS", "Wi-Fi", "Firewall", "VPN", "AdGuard", "Storage / File Sharing", "Kernel"];
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string selectedSeverity = "All";
    [ObservableProperty] private string selectedCategory = "All";
    [ObservableProperty] private string statusMessage = "Router logs have not been loaded.";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private RouterLogEntry? selectedEntry;
    [ObservableProperty] private bool hasLoaded;
    public int WarningErrorCount => _all.Count(e => e.Severity is "Emergency" or "Alert" or "Critical" or "Error" or "Warning");
    public string NewestTimestamp => _all.FirstOrDefault()?.Timestamp ?? "—";
    public IReadOnlyList<RouterLogEntry> RecentImportantEntries => _all
        .Where(e => e.Severity is "Emergency" or "Alert" or "Critical" or "Error" or "Warning")
        .Take(3).ToList();
    public string EmptyMessage => IsLoading ? "Loading router logs…" : _all.Count == 0 ? "No router log entries returned." : "No log entries match the current filters.";
    public string LoadedCount => Entries.Count.ToString("N0");
    public RouterLogsViewModel(IRouterManagerProvider provider, IRouterProfileService profiles)
    {
        _provider = provider;
        _profiles = profiles;
        _profiles.ActiveProfileChanged += Profiles_ActiveProfileChanged;
    }

    private void Profiles_ActiveProfileChanged(object? sender, EventArgs e)
    {
        _all = new();
        Entries.Clear();
        HasLoaded = false;
        SelectedEntry = null;
        StatusMessage = "Router logs have not been loaded.";
        NotifySummaryChanged();
    }
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true; _loadCancellation?.Cancel(); _loadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            string output = await (await _provider.GetRouterManagerAsync(_loadCancellation.Token)).GetRouterLogsAsync(_loadCancellation.Token);
            _all = RouterLogParser.Parse(output, 250).Reverse().ToList();
            HasLoaded = true;
            ApplyFilter(); StatusMessage = $"Showing {_all.Count:N0} bounded recent router log entries.";
            NotifySummaryChanged();
        }
        catch (OperationCanceledException) when (_loadCancellation?.IsCancellationRequested == true) { StatusMessage = "Router log refresh cancelled."; }
        catch (Exception ex) { StatusMessage = OperationFailurePolicy.UserMessage(ex, "Router log refresh", "Router logs are currently unavailable."); }
        finally { IsLoading = false; OnPropertyChanged(nameof(EmptyMessage)); NotifySummaryChanged(); }
    }
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedSeverityChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    private void ApplyFilter()
    {
        IEnumerable<RouterLogEntry> query = _all;
        if (!string.IsNullOrWhiteSpace(SearchText)) query = query.Where(e => e.SearchText.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (SelectedCategory != "All") query = query.Where(e => e.Category == SelectedCategory);
        if (SelectedSeverity == "Error and above") query = query.Where(e => e.Severity is "Emergency" or "Alert" or "Critical" or "Error");
        else if (SelectedSeverity == "Warning and above") query = query.Where(e => e.Severity is "Emergency" or "Alert" or "Critical" or "Error" or "Warning");
        else if (SelectedSeverity != "All") query = query.Where(e => e.Severity == SelectedSeverity);
        Entries.Clear(); foreach (RouterLogEntry entry in query) Entries.Add(entry);
        if (SelectedEntry is not null && !Entries.Contains(SelectedEntry)) SelectedEntry = null;
        OnPropertyChanged(nameof(LoadedCount)); OnPropertyChanged(nameof(EmptyMessage));
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(WarningErrorCount));
        OnPropertyChanged(nameof(NewestTimestamp));
        OnPropertyChanged(nameof(RecentImportantEntries));
        OnPropertyChanged(nameof(EmptyMessage));
    }
}
