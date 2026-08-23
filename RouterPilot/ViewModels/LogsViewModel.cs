using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels
{
    public partial class LogsViewModel : ObservableObject
    {
        // Keeping the on-screen collection bounded prevents the WPF DataGrid
        // from creating thousands of row containers during initial navigation.
        private const int MaxVisibleEntries = 200;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly DispatcherTimer _refreshTimer;

        private readonly List<QueryLogEntry> _allEntries =
            new();
        private bool _suppressFilterApplication;

        public ObservableCollection<QueryLogEntry> Entries
        {
            get;
        } = new();

        public IReadOnlyList<string> StatusOptions { get; } =
            new[] { "All", "Allowed", "Blocked" };

        [ObservableProperty]
        private string searchText =
            string.Empty;

        [ObservableProperty]
        private string selectedStatus =
            "All";

        [ObservableProperty]
        private string domainFilter =
            string.Empty;

        [ObservableProperty]
        private string clientFilter =
            string.Empty;

        [ObservableProperty]
        private string statusMessage =
            "No query-log data loaded.";

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private bool isPaused;

        public string PauseButtonText =>
            IsPaused
                ? "Resume"
                : "Pause";

        public string LiveUpdatesStatus => IsPaused
            ? "Paused"
            : RouterPilotStatusPresentation.Active;

        public string LiveUpdatesStatusColour =>
            RouterPilotStatusPresentation.Colour(
                IsPaused
                    ? RouterPilotStatus.Disabled
                    : RouterPilotStatus.Active);

        public string DnsQueriesDisplay =>
            _adGuardAvailabilityService.IsAvailable ? Entries.Count.ToString("N0") : "N/A";

        public string DnsSourceDisplay =>
            _adGuardAvailabilityService.IsAvailable ? "AdGuard Home" : "N/A";

        public string EmptyStateTitle =>
            !_adGuardAvailabilityService.IsAvailable ? "DNS queries unavailable" :
            HasActiveFilters && _allEntries.Count > 0 ? "No matching DNS activity" :
            "No DNS requests";

        public string EmptyStateMessage =>
            !_adGuardAvailabilityService.IsAvailable
                ? "DNS query information requires AdGuard Home."
                : HasActiveFilters && _allEntries.Count > 0
                    ? "No DNS activity matches the current filters."
                    : "AdGuard Home query activity will appear here.";

        public bool HasActiveFilters =>
            !string.IsNullOrWhiteSpace(SearchText) ||
            !string.Equals(SelectedStatus, "All", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(DomainFilter) ||
            !string.IsNullOrWhiteSpace(ClientFilter);

        public LogsViewModel(
            IRouterManagerProvider routerManagerProvider,
            AdGuardAvailabilityService adGuardAvailabilityService)
        {
            _routerManagerProvider = routerManagerProvider;
            _adGuardAvailabilityService = adGuardAvailabilityService;

            _refreshTimer =
                new DispatcherTimer
                {
                    Interval =
                        TimeSpan.FromSeconds(3)
                };

            _refreshTimer.Tick +=
                RefreshTimer_Tick;
        }

        public async Task StartAsync()
        {
            await LoadLogsAsync();

            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }

        public void Stop()
        {
            _refreshTimer.Stop();
        }

        [RelayCommand]
        public async Task LoadLogsAsync()
        {
            if (IsLoading ||
                IsPaused)
            {
                return;
            }

            IsLoading =
                true;

            RefreshAvailabilityDisplays();

            if (!_adGuardAvailabilityService.IsAvailable)
            {
                _allEntries.Clear();
                Entries.Clear();
                StatusMessage = "DNS query information requires AdGuard Home.";
                IsLoading = false;
                return;
            }

            StatusMessage =
                "Loading query log...";

            try
            {
                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                List<QueryLogEntry> entries =
                    await routerManager
                        .GetQueryLogAsync(MaxVisibleEntries);

                ApplyEntries(
                    entries);
            }
            catch (Exception ex)
            {
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Query-log refresh",
                    "Unable to load query log. Check the router connection and try again.");
            }
            finally
            {
                IsLoading =
                    false;
            }
        }

        public void ApplyEntries(
            IEnumerable<QueryLogEntry> entries)
        {
            if (IsPaused)
            {
                return;
            }

            _allEntries.Clear();

            _allEntries.AddRange(
                entries);

            ApplyFilter();

            StatusMessage = HasActiveFilters
                ? $"{Entries.Count} of {_allEntries.Count} entries shown."
                : _allEntries.Count switch
                {
                    0 =>
                        "No query-log entries found.",

                    1 =>
                        "1 query-log entry loaded.",

                    _ =>
                        $"{_allEntries.Count} query-log entries loaded."
                };

            RefreshAvailabilityDisplays();
        }

        public void ApplyDomainFilter(
            string domain)
        {
            // Cross-feature navigation continues to use the visible general
            // search, while clearing local refinements from a prior session.
            _suppressFilterApplication = true;
            SelectedStatus = "All";
            DomainFilter = string.Empty;
            ClientFilter = string.Empty;
            SearchText = domain ?? string.Empty;
            _suppressFilterApplication = false;
            OnPropertyChanged(nameof(HasActiveFilters));
            ApplyFilter();
        }

        [RelayCommand]
        private void ClearFilters()
        {
            _suppressFilterApplication = true;
            SelectedStatus = "All";
            DomainFilter = string.Empty;
            ClientFilter = string.Empty;
            SearchText = string.Empty;
            _suppressFilterApplication = false;
            OnPropertyChanged(nameof(HasActiveFilters));
            ApplyFilter();
        }

        [RelayCommand]
        private void TogglePause()
        {
            IsPaused =
                !IsPaused;

            OnPropertyChanged(
                nameof(PauseButtonText));
            OnPropertyChanged(nameof(LiveUpdatesStatus));
            OnPropertyChanged(nameof(LiveUpdatesStatusColour));

            StatusMessage =
                IsPaused
                    ? "Live updates paused."
                    : "Live updates resumed.";
        }

        partial void OnSearchTextChanged(
            string value)
        {
            if (_suppressFilterApplication) return;
            OnPropertyChanged(nameof(HasActiveFilters));
            ApplyFilter();
        }

        partial void OnSelectedStatusChanged(
            string value)
        {
            if (_suppressFilterApplication) return;
            OnPropertyChanged(nameof(HasActiveFilters));
            ApplyFilter();
        }

        partial void OnDomainFilterChanged(
            string value)
        {
            if (_suppressFilterApplication) return;
            OnPropertyChanged(nameof(HasActiveFilters));
            ApplyFilter();
        }

        partial void OnClientFilterChanged(
            string value)
        {
            if (_suppressFilterApplication) return;
            OnPropertyChanged(nameof(HasActiveFilters));
            ApplyFilter();
        }

        private async void RefreshTimer_Tick(
            object? sender,
            EventArgs e)
        {
            await LoadLogsAsync();
        }

        private void ApplyFilter()
        {
            string search =
                SearchText.Trim();
            string domain =
                DomainFilter.Trim();
            string client =
                ClientFilter.Trim();

            IEnumerable<QueryLogEntry> filteredEntries =
                _allEntries;

            if (!string.IsNullOrWhiteSpace(
                    search))
            {
                filteredEntries =
                    _allEntries.Where(
                        entry =>
                            ContainsText(
                                entry.Client,
                                search) ||
                            ContainsText(
                                entry.Domain,
                                search) ||
                            ContainsText(
                                entry.Status,
                                search));
            }

            if (SelectedStatus == "Allowed")
            {
                filteredEntries = filteredEntries.Where(entry => !entry.IsBlocked);
            }
            else if (SelectedStatus == "Blocked")
            {
                filteredEntries = filteredEntries.Where(entry => entry.IsBlocked);
            }

            if (!string.IsNullOrWhiteSpace(domain))
            {
                filteredEntries = filteredEntries.Where(entry => ContainsText(entry.Domain, domain));
            }

            if (!string.IsNullOrWhiteSpace(client))
            {
                filteredEntries = filteredEntries.Where(entry => ContainsText(entry.Client, client));
            }

            List<QueryLogEntry> visibleEntries =
                filteredEntries
                    .Take(MaxVisibleEntries)
                    .ToList();

            if (!VisibleEntriesMatch(
                    visibleEntries))
            {
                Entries.Clear();

                foreach (QueryLogEntry entry
                         in visibleEntries)
                {
                    Entries.Add(
                        entry);
                }
            }

            if (!IsLoading &&
                _allEntries.Count > 0)
            {
                if (HasActiveFilters)
                {
                    StatusMessage =
                        $"{Entries.Count} of " +
                        $"{_allEntries.Count} entries shown.";
                }
                else if (_allEntries.Count >
                         MaxVisibleEntries)
                {
                    StatusMessage =
                        $"Showing the newest " +
                        $"{MaxVisibleEntries:N0} of " +
                        $"{_allEntries.Count:N0} entries.";
                }
            }

            RefreshAvailabilityDisplays();
        }

        private void RefreshAvailabilityDisplays()
        {
            OnPropertyChanged(nameof(DnsQueriesDisplay));
            OnPropertyChanged(nameof(DnsSourceDisplay));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateMessage));
        }

        private bool VisibleEntriesMatch(
            IReadOnlyList<QueryLogEntry> entries)
        {
            if (Entries.Count !=
                entries.Count)
            {
                return false;
            }

            for (int index = 0;
                 index < entries.Count;
                 index++)
            {
                QueryLogEntry current =
                    Entries[index];

                QueryLogEntry incoming =
                    entries[index];

                if (!Equals(
                        current.Time,
                        incoming.Time) ||
                    !string.Equals(
                        current.Client,
                        incoming.Client,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        current.Domain,
                        incoming.Domain,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        current.Status,
                        incoming.Status,
                        StringComparison.Ordinal) ||
                    current.IsBlocked !=
                    incoming.IsBlocked)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsText(
            string? value,
            string search)
        {
            return !string.IsNullOrWhiteSpace(
                       value) &&
                   value.Contains(
                       search,
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
