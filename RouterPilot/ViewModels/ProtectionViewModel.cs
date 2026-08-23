using System;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels
{
    public sealed class ProtectionViewModel : ObservableObject, IDisposable, IAsyncDisposable
    {
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly AdGuardProtectionNotificationTracker _protectionNotificationTracker;
        private readonly BlockedServiceMutationService _blockedServiceMutations;
        private readonly AdGuardServiceScheduleService _scheduleService;
        private readonly IAdGuardServiceCatalogueProvider _serviceCatalogue;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly AdGuardMaintenanceStateService _adGuardMaintenanceStateService;
        private readonly DispatcherTimer _timer;
        private readonly SemaphoreSlim _protectionStateGate = new(1, 1);
        private readonly CancellationTokenSource _disposalCancellation = new();
        private readonly object _disposeLock = new();
        private Task? _disposeTask;
        private bool _disposed;
        private bool _isBusy;
        private bool _isAdGuardAvailable = true;
        private bool _isInitialising;
        private RouterPilotStatus _protectionStatus = RouterPilotStatus.Pending;
        private string _statusText = RouterPilotStatusPresentation.Pending;
        private string _statusDetail = "Reading AdGuard Home settings.";
        private string _remaining = "";
        private string _message = "";
        private string _blockedServicesStatus = "Loading available services...";
        private string _blockedServicesSearch = "";
        private bool _showBlockedOnly;
        private string _selectedBlockedServiceCategory = "All categories";
        private string _profileName = "Custom";
        private bool _filteringEnabled;
        private bool _safeBrowsingEnabled;
        private bool _safeSearchEnabled;
        private bool _parentalEnabled;
        private bool _queryLogEnabled;
        private string _newRuleDomain = "";
        private string _newRewriteDomain = "";
        private string _newRewriteAnswer = "";
        private CustomFilteringRule? _selectedRule;
        private DnsRewriteRule? _selectedRewrite;
        private AdGuardProtectionOptions _options = new();
        private AdGuardBlockedServicesConfig _blockedConfig = new();
        private int _statisticsRefreshTick;
        private string _totalQueriesText = "—";
        private string _blockedQueriesText = "—";
        private string _blockPercentageText = "—";
        private string _topBlockedDomain = "No blocked domains yet";
        private bool _hasTopBlockedDomain;
        private string _queryLogSearch = "";
        private bool _showBlockedQueriesOnly;
        private string _queryLogStatus = "Loading recent DNS activity...";
        private string _filterRulesSearch = "";
        private string _filterRulesType = "All";
        private bool _hasFilteringRulesData;

        public ProtectionViewModel(
            IRouterManagerProvider routerManagerProvider,
            AdGuardProtectionNotificationTracker protectionNotificationTracker,
            BlockedServiceMutationService blockedServiceMutations,
            AdGuardServiceScheduleService scheduleService,
            IAdGuardServiceCatalogueProvider serviceCatalogue,
            AdGuardServiceScheduleViewModel schedules,
            AdGuardAvailabilityService adGuardAvailabilityService,
            AdGuardMaintenanceStateService adGuardMaintenanceStateService)
        {
            _routerManagerProvider = routerManagerProvider;
            _protectionNotificationTracker = protectionNotificationTracker;
            _blockedServiceMutations = blockedServiceMutations;
            _scheduleService = scheduleService;
            _serviceCatalogue = serviceCatalogue;
            _adGuardAvailabilityService = adGuardAvailabilityService;
            _adGuardMaintenanceStateService = adGuardMaintenanceStateService;
            _adGuardMaintenanceStateService.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ControlsEnabled));
                NotifyCommands();
            };
            Schedules = schedules;
            _scheduleService.BlockedServicesChanged += ScheduleService_BlockedServicesChanged;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += async (_, _) => await RefreshTimedDataAsync();

            RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsBusy);
            EnableProtectionCommand = new AsyncRelayCommand(() => RunStatusActionAsync("Enabling protection...", "Protection enabled.", r => r.EnableProtectionAsync(), processNotification: true), () => ControlsEnabled);
            DisableProtectionCommand = new AsyncRelayCommand(DisableProtectionAsync, () => ControlsEnabled);
            ResumeProtectionCommand = new AsyncRelayCommand(() => RunStatusActionAsync("Resuming protection...", "Protection resumed.", r => r.ResumeProtectionAsync()), () => !IsBusy);
            Pause30Command = new AsyncRelayCommand(() => PauseAsync(TimeSpan.FromMinutes(30)), () => !IsBusy);
            Pause1HourCommand = new AsyncRelayCommand(() => PauseAsync(TimeSpan.FromHours(1)), () => !IsBusy);
            Pause4HoursCommand = new AsyncRelayCommand(() => PauseAsync(TimeSpan.FromHours(4)), () => !IsBusy);
            PauseUntilTomorrowCommand = new AsyncRelayCommand(PauseUntilTomorrowAsync, () => !IsBusy);
            ApplyStandardProfileCommand = new AsyncRelayCommand(() => ApplyProfileAsync("Standard", true, true, false, false, true), () => ControlsEnabled);
            ApplyFamilyProfileCommand = new AsyncRelayCommand(() => ApplyProfileAsync("Family", true, true, true, true, true), () => ControlsEnabled);
            ApplyPrivacyProfileCommand = new AsyncRelayCommand(() => ApplyProfileAsync("Privacy", true, true, false, true, false), () => ControlsEnabled);
            SaveBlockedServicesCommand = new AsyncRelayCommand(SaveBlockedServicesAsync, () => ControlsEnabled);
            SelectAllServicesCommand = new RelayCommand(() => SetAllBlockedServices(true), () => ControlsEnabled);
            ClearAllServicesCommand = new RelayCommand(() => SetAllBlockedServices(false), () => ControlsEnabled);
            BlockedServiceCategories.Add("All categories");
            BlockedServicesView = CollectionViewSource.GetDefaultView(BlockedServices);
            BlockedServicesView.Filter = FilterBlockedService;
            BlockedServicesView.SortDescriptions.Add(
                new SortDescription(
                    nameof(BlockedServiceItem.Name),
                    ListSortDirection.Ascending));
            FilteringRulesView = CollectionViewSource.GetDefaultView(FilteringRules);
            FilteringRulesView.Filter = FilterFilteringRule;
            FilteringRules.CollectionChanged += FilteringRules_CollectionChanged;
            QueryLogView = CollectionViewSource.GetDefaultView(QueryLogEntries);
            QueryLogView.Filter = FilterQueryLogEntry;
            RefreshQueryLogCommand = new AsyncRelayCommand(() => RefreshQueryLogAsync(true), () => !IsBusy);
            AddDenyRuleCommand = new AsyncRelayCommand(() => AddRuleAsync(false), () => !IsBusy);
            AddAllowRuleCommand = new AsyncRelayCommand(() => AddRuleAsync(true), () => !IsBusy);
            DeleteRuleCommand = new AsyncRelayCommand(DeleteRuleAsync, () => !IsBusy && SelectedRule is not null);
            AddRewriteCommand = new AsyncRelayCommand(AddRewriteAsync, () => !IsBusy);
            DeleteRewriteCommand = new AsyncRelayCommand(DeleteRewriteAsync, () => !IsBusy && SelectedRewrite is not null);
        }

        public ObservableCollection<BlockedServiceItem> BlockedServices { get; } = new();
        public AdGuardServiceScheduleViewModel Schedules { get; }
        public ObservableCollection<string> BlockedServiceCategories { get; } = new();
        public ICollectionView BlockedServicesView { get; }
        public ObservableCollection<CustomFilteringRule> FilteringRules { get; } = new();
        public ICollectionView FilteringRulesView { get; }
        public ObservableCollection<DnsRewriteRule> DnsRewrites { get; } = new();
        public ObservableCollection<QueryLogEntry> QueryLogEntries { get; } = new();
        public ICollectionView QueryLogView { get; }

        public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(ControlsEnabled)); NotifyCommands(); } } }
        public bool ControlsEnabled => !IsBusy && IsAdGuardAvailable &&
            _adGuardMaintenanceStateService.State != AdGuardMaintenanceState.Restarting;
        public bool IsAdGuardAvailable
        {
            get => _isAdGuardAvailable;
            private set
            {
                if (!SetProperty(ref _isAdGuardAvailable, value)) return;
                OnPropertyChanged(nameof(ControlsEnabled));
                OnPropertyChanged(nameof(AdGuardAvailabilityMessage));
                if (value)
                {
                    _adGuardAvailabilityService.SetState(AdGuardAvailabilityState.Available);
                }
                else
                {
                    if (_adGuardAvailabilityService.State == AdGuardAvailabilityState.Available)
                    {
                        _adGuardAvailabilityService.SetState(AdGuardAvailabilityState.Unavailable);
                    }

                    SetProtectionStatus(RouterPilotStatus.NotAvailable);
                    StatusDetail = "AdGuard Home is unavailable. Router monitoring remains active.";
                    Remaining = string.Empty;
                    TotalQueriesText = "N/A";
                    BlockedQueriesText = "N/A";
                    BlockPercentageText = "N/A";
                    TopBlockedDomain = "N/A";
                    HasTopBlockedDomain = false;
                    QueryLogEntries.Clear();
                    QueryLogStatus = "DNS query information requires AdGuard Home.";
                }
                NotifyCommands();
            }
        }
        public string AdGuardAvailabilityMessage => IsAdGuardAvailable
            ? string.Empty
            : "AdGuard Home is unavailable. Router monitoring remains active.";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        public string StatusColour => RouterPilotStatusPresentation.Colour(_protectionStatus);
        public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
        public string Remaining { get => _remaining; private set => SetProperty(ref _remaining, value); }
        public string Message { get => _message; private set => SetProperty(ref _message, value); }
        public string BlockedServicesStatus { get => _blockedServicesStatus; private set => SetProperty(ref _blockedServicesStatus, value); }
        public string BlockedServicesSearch { get => _blockedServicesSearch; set { if (SetProperty(ref _blockedServicesSearch, value)) BlockedServicesView.Refresh(); } }
        public bool ShowBlockedOnly { get => _showBlockedOnly; set { if (SetProperty(ref _showBlockedOnly, value)) BlockedServicesView.Refresh(); } }
        public string SelectedBlockedServiceCategory
        {
            get => _selectedBlockedServiceCategory;
            set
            {
                if (SetProperty(ref _selectedBlockedServiceCategory, value))
                    BlockedServicesView.Refresh();
            }
        }
        public string BlockedServicesSelectionSummary => $"{BlockedServices.Count(s => s.IsBlocked)} selected";
        public string ProfileName { get => _profileName; private set => SetProperty(ref _profileName, value); }
        public string TotalQueriesText { get => _totalQueriesText; private set => SetProperty(ref _totalQueriesText, value); }
        public string BlockedQueriesText { get => _blockedQueriesText; private set => SetProperty(ref _blockedQueriesText, value); }
        public string BlockPercentageText { get => _blockPercentageText; private set => SetProperty(ref _blockPercentageText, value); }
        public string TopBlockedDomain { get => _topBlockedDomain; private set => SetProperty(ref _topBlockedDomain, value); }
        public bool HasTopBlockedDomain { get => _hasTopBlockedDomain; private set => SetProperty(ref _hasTopBlockedDomain, value); }
        public string QueryLogSearch { get => _queryLogSearch; set { if (SetProperty(ref _queryLogSearch, value)) QueryLogView.Refresh(); } }
        public bool ShowBlockedQueriesOnly { get => _showBlockedQueriesOnly; set { if (SetProperty(ref _showBlockedQueriesOnly, value)) QueryLogView.Refresh(); } }
        public string QueryLogStatus { get => _queryLogStatus; private set => SetProperty(ref _queryLogStatus, value); }
        public string FilterRulesSearch { get => _filterRulesSearch; set { if (SetProperty(ref _filterRulesSearch, value)) FilteringRulesView.Refresh(); } }
        public string FilterRulesType { get => _filterRulesType; set { if (SetProperty(ref _filterRulesType, value)) FilteringRulesView.Refresh(); } }
        public bool HasFilteringRulesData { get => _hasFilteringRulesData; private set => SetProperty(ref _hasFilteringRulesData, value); }
        public int TotalFilteringRuleCount => FilteringRules.Count;
        public int BlockFilteringRuleCount => FilteringRules.Count(rule => rule.Type == "Block");
        public int AllowFilteringRuleCount => FilteringRules.Count(rule => rule.Type == "Allow");
        public int CustomFilteringRuleCount => FilteringRules.Count(rule => rule.Type == "Custom");
        public string FilteringUpdateIntervalDisplay => FormatHours(_options.FilteringIntervalHours);
        public string QueryLogRetentionDisplay => FormatHours(_options.QueryLogInterval);
        public int IgnoredQueryLogEntryCount => _options.QueryLogIgnored.Length;
        public bool QueryLogAnonymizeClientIp => _options.QueryLogAnonymizeClientIp;
        public bool SafeSearchBing => _options.SafeSearch.Bing;
        public bool SafeSearchDuckDuckGo => _options.SafeSearch.DuckDuckGo;
        public bool SafeSearchEcosia => _options.SafeSearch.Ecosia;
        public bool SafeSearchGoogle => _options.SafeSearch.Google;
        public bool SafeSearchPixabay => _options.SafeSearch.Pixabay;
        public bool SafeSearchYandex => _options.SafeSearch.Yandex;
        public bool SafeSearchYouTube => _options.SafeSearch.YouTube;
        public string SafeSearchBingDisplay => FormatOnOff(SafeSearchBing);
        public string SafeSearchDuckDuckGoDisplay => FormatOnOff(SafeSearchDuckDuckGo);
        public string SafeSearchEcosiaDisplay => FormatOnOff(SafeSearchEcosia);
        public string SafeSearchGoogleDisplay => FormatOnOff(SafeSearchGoogle);
        public string SafeSearchPixabayDisplay => FormatOnOff(SafeSearchPixabay);
        public string SafeSearchYandexDisplay => FormatOnOff(SafeSearchYandex);
        public string SafeSearchYouTubeDisplay => FormatOnOff(SafeSearchYouTube);
        public string FilteringStateDisplay => FormatOnOff(FilteringEnabled);
        public string QueryLogStateDisplay => FormatOnOff(QueryLogEnabled);
        public string SafeBrowsingStateDisplay => FormatOnOff(SafeBrowsingEnabled);
        public string ParentalStateDisplay => FormatOnOff(ParentalEnabled);
        public string SafeSearchStateDisplay => FormatOnOff(SafeSearchEnabled);

        public bool FilteringEnabled { get => _filteringEnabled; set { if (SetProperty(ref _filteringEnabled, value)) { OnPropertyChanged(nameof(FilteringStateDisplay)); if (!_isInitialising) _ = UpdateOptionAsync("DNS filtering", r => r.SetFilteringEnabledAsync(value)); } } }
        public bool SafeBrowsingEnabled { get => _safeBrowsingEnabled; set { if (SetProperty(ref _safeBrowsingEnabled, value)) { OnPropertyChanged(nameof(SafeBrowsingStateDisplay)); if (!_isInitialising) _ = UpdateOptionAsync("Safe Browsing", r => r.SetSafeBrowsingEnabledAsync(value)); } } }
        public bool SafeSearchEnabled { get => _safeSearchEnabled; set { if (SetProperty(ref _safeSearchEnabled, value)) { OnPropertyChanged(nameof(SafeSearchStateDisplay)); if (!_isInitialising) _ = UpdateOptionAsync("Safe Search", r => r.SetSafeSearchEnabledAsync(value, _options.SafeSearch)); } } }
        public bool ParentalEnabled { get => _parentalEnabled; set { if (SetProperty(ref _parentalEnabled, value)) { OnPropertyChanged(nameof(ParentalStateDisplay)); if (!_isInitialising) _ = UpdateOptionAsync("Parental Control", r => r.SetParentalEnabledAsync(value)); } } }
        public bool QueryLogEnabled { get => _queryLogEnabled; set { if (SetProperty(ref _queryLogEnabled, value)) { OnPropertyChanged(nameof(QueryLogStateDisplay)); if (!_isInitialising) _ = UpdateOptionAsync("Query logging", r => r.SetQueryLogEnabledAsync(value, _options)); } } }

        public string NewRuleDomain { get => _newRuleDomain; set => SetProperty(ref _newRuleDomain, value); }
        public string NewRewriteDomain { get => _newRewriteDomain; set => SetProperty(ref _newRewriteDomain, value); }
        public string NewRewriteAnswer { get => _newRewriteAnswer; set => SetProperty(ref _newRewriteAnswer, value); }
        public CustomFilteringRule? SelectedRule { get => _selectedRule; set { if (SetProperty(ref _selectedRule, value)) NotifyCommands(); } }
        public DnsRewriteRule? SelectedRewrite { get => _selectedRewrite; set { if (SetProperty(ref _selectedRewrite, value)) NotifyCommands(); } }

        public IAsyncRelayCommand RefreshAllCommand { get; }
        public IAsyncRelayCommand EnableProtectionCommand { get; }
        public IAsyncRelayCommand DisableProtectionCommand { get; }
        public IAsyncRelayCommand ResumeProtectionCommand { get; }
        public IAsyncRelayCommand Pause30Command { get; }
        public IAsyncRelayCommand Pause1HourCommand { get; }
        public IAsyncRelayCommand Pause4HoursCommand { get; }
        public IAsyncRelayCommand PauseUntilTomorrowCommand { get; }
        public IAsyncRelayCommand ApplyStandardProfileCommand { get; }
        public IAsyncRelayCommand ApplyFamilyProfileCommand { get; }
        public IAsyncRelayCommand ApplyPrivacyProfileCommand { get; }
        public IAsyncRelayCommand SaveBlockedServicesCommand { get; }
        public IRelayCommand SelectAllServicesCommand { get; }
        public IRelayCommand ClearAllServicesCommand { get; }
        public IAsyncRelayCommand RefreshQueryLogCommand { get; }
        public IAsyncRelayCommand AddDenyRuleCommand { get; }
        public IAsyncRelayCommand AddAllowRuleCommand { get; }
        public IAsyncRelayCommand DeleteRuleCommand { get; }
        public IAsyncRelayCommand AddRewriteCommand { get; }
        public IAsyncRelayCommand DeleteRewriteCommand { get; }

        public async Task StartAsync()
        {
            _timer.Start();
            await RefreshAllAsync();
        }

        public void Stop() => _timer.Stop();
        public void Dispose()
        {
            lock (_disposeLock)
            {
                BeginDisposal();
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeLock)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            BeginDisposal();
            await _protectionStateGate.WaitAsync();
            _protectionStateGate.Release();
            _protectionStateGate.Dispose();
            _disposalCancellation.Dispose();
        }

        private void BeginDisposal()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _scheduleService.BlockedServicesChanged -= ScheduleService_BlockedServicesChanged;
            _disposalCancellation.Cancel();
        }

        private async Task RefreshAllAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Message = "Refreshing all protection settings...";
            try
            {
                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync();
                AdGuardProtectionStatus status = await router.GetAdGuardProtectionStatusAsync();
                AdGuardStatistics statistics = await router.GetAdGuardStatisticsAsync();
                _options = await router.GetProtectionOptionsAsync();
                NotifyConfigurationDetails();
                bool catalogueRefreshed = await _serviceCatalogue.RefreshAsync(router, _disposalCancellation.Token);
                _blockedConfig = await router.GetBlockedServicesConfigAsync();
                var rules = await router.GetCustomFilteringRulesAsync();
                var rewrites = await router.GetDnsRewritesAsync();
                var queryLog = await router.GetQueryLogAsync();

                ApplyStatus(status);
                ApplyStatistics(statistics);
                _isInitialising = true;
                FilteringEnabled = _options.FilteringEnabled;
                SafeBrowsingEnabled = _options.SafeBrowsingEnabled;
                SafeSearchEnabled = _options.SafeSearchEnabled;
                ParentalEnabled = _options.ParentalEnabled;
                QueryLogEnabled = _options.QueryLogEnabled;
                _isInitialising = false;
                DetermineProfile();

                ApplyBlockedServices(_serviceCatalogue.Services, _blockedConfig);
                Debug.Assert(BlockedServices.Count == _serviceCatalogue.Services.Count);
                Debug.Assert(Schedules.AvailableServices.Count == _serviceCatalogue.Services.Count);
                Debug.WriteLine($"[AdGuardCatalogue] provider={_serviceCatalogue.Services.Count} manual={BlockedServices.Count} scheduleEditors={Schedules.AvailableServices.Count}");

                BlockedServiceCategories.Clear();
                BlockedServiceCategories.Add("All categories");
                foreach (string category in BlockedServices
                    .Select(service => service.Category)
                    .Where(category => !string.IsNullOrWhiteSpace(category))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(category => category))
                {
                    BlockedServiceCategories.Add(category);
                }

                if (!BlockedServiceCategories.Contains(SelectedBlockedServiceCategory))
                    SelectedBlockedServiceCategory = "All categories";

                BlockedServicesView.Refresh();
                OnPropertyChanged(nameof(BlockedServicesSelectionSummary));
                BlockedServicesStatus = !catalogueRefreshed
                    ? BlockedServices.Count == 0
                        ? _serviceCatalogue.LastError ?? "No blocked-service catalogue was returned by this AdGuard Home build."
                        : (_serviceCatalogue.LastError ?? "The service catalogue could not be refreshed.") + " Showing the last successful list."
                    : $"{BlockedServices.Count} services available. Select services and save your changes.";
                FilteringRules.Clear();
                foreach (var rule in rules) FilteringRules.Add(rule);
                HasFilteringRulesData = true;
                DnsRewrites.Clear();
                foreach (var rewrite in rewrites) DnsRewrites.Add(rewrite);
                ApplyQueryLog(queryLog);
                IsAdGuardAvailable = true;
                Message = "Protection settings refreshed.";
            }
            catch (Exception)
            {
                HasFilteringRulesData = false;
                IsAdGuardAvailable = false;
                if (BlockedServices.Count == 0)
                    BlockedServicesStatus = "Blocked services could not be loaded. Use Refresh all to try again.";
                Message = "AdGuard Home is unavailable. Router monitoring remains active.";
            }
            finally { _isInitialising = false; IsBusy = false; }
        }


        private async Task RefreshTimedDataAsync()
        {
            if (IsBusy) return;

            await RefreshProtectionStatusAsync(false);
            _statisticsRefreshTick++;

            // The timer runs every three seconds. Refresh statistics every
            // fifteen seconds to keep the dashboard current without making
            // unnecessary requests to AdGuard Home.
            if (_statisticsRefreshTick < 5) return;
            _statisticsRefreshTick = 0;

            try
            {
                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync();
                ApplyStatistics(await router.GetAdGuardStatisticsAsync());
                await RefreshQueryLogAsync(false);
            }
            catch
            {
                IsAdGuardAvailable = false;
                // Keep the last successful statistics visible when a
                // transient router or AdGuard request fails.
            }
        }

        private void ApplyStatistics(AdGuardStatistics statistics)
        {
            TotalQueriesText = statistics.TotalQueries.ToString("N0");
            BlockedQueriesText = statistics.BlockedQueries.ToString("N0");
            BlockPercentageText = statistics.BlockPercentage.ToString("0.0") + "%";
            TopBlockedDomain = statistics.TopBlockedDomains.FirstOrDefault()?.Name
                ?? "No blocked domains yet";
            HasTopBlockedDomain = statistics.TopBlockedDomains.Count > 0;
        }


        private async Task RefreshQueryLogAsync(bool showMessage)
        {
            try
            {
                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync();
                ApplyQueryLog(await router.GetQueryLogAsync());
                if (showMessage) Message = "Recent DNS activity refreshed.";
            }
            catch (Exception ex)
            {
                QueryLogStatus = "Recent DNS activity is unavailable.";
                if (showMessage)
                    Message = OperationFailurePolicy.UserMessage(
                        ex,
                        "DNS activity refresh",
                        "Unable to refresh DNS activity. Check the router connection and try again.");
            }
        }

        private void ApplyQueryLog(System.Collections.Generic.IEnumerable<QueryLogEntry> entries)
        {
            QueryLogEntries.Clear();
            foreach (QueryLogEntry entry in entries.Take(200))
                QueryLogEntries.Add(entry);

            QueryLogView.Refresh();
            QueryLogStatus = QueryLogEntries.Count == 0
                ? (QueryLogEnabled ? "No recent DNS requests were returned." : "Query logging is currently disabled.")
                : $"Showing {QueryLogEntries.Count:N0} recent DNS requests.";
        }

        private bool FilterQueryLogEntry(object item)
        {
            if (item is not QueryLogEntry entry) return false;
            if (ShowBlockedQueriesOnly && !entry.IsBlocked) return false;

            string search = QueryLogSearch.Trim();
            return search.Length == 0 ||
                   entry.Domain.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   entry.Client.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   entry.ClientName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   entry.ClientAddress.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   entry.Status.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        private async Task RefreshProtectionStatusAsync(bool showMessage)
        {
            if (IsBusy) return;
            try { RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(); ApplyStatus(await router.GetAdGuardProtectionStatusAsync()); IsAdGuardAvailable = true; if (showMessage) Message = "Protection status refreshed."; }
            catch (Exception) { IsAdGuardAvailable = false; StatusDetail = "AdGuard Home is unavailable. Router monitoring remains active."; if (showMessage) Message = StatusDetail; }
        }

        private async Task DisableProtectionAsync()
        {
            if (MessageBox.Show("Disable protection until it is manually enabled again?", "Disable Protection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunStatusActionAsync("Disabling protection...", "Protection disabled.", r => r.DisableProtectionAsync(), processNotification: true);
        }

        private Task PauseAsync(TimeSpan duration) => RunStatusActionAsync($"Pausing protection for {FormatDuration(duration)}...", $"Protection paused for {FormatDuration(duration)}.", r => r.PauseProtectionAsync(duration));
        private Task PauseUntilTomorrowAsync()
        {
            TimeSpan duration = DateTime.Today.AddDays(1) - DateTime.Now;
            if (duration <= TimeSpan.Zero) duration = TimeSpan.FromHours(24);
            return RunStatusActionAsync("Pausing protection until tomorrow...", "Protection paused until tomorrow.", r => r.PauseProtectionAsync(duration));
        }

        private async Task RunStatusActionAsync(
            string busy,
            string success,
            Func<RouterManager, Task<AdGuardProtectionStatus>> action,
            bool processNotification = false)
        {
            if (_disposed ||
                !await _protectionStateGate.WaitAsync(0))
            {
                return;
            }

            bool ownsBusyState = false;

            try
            {
                if (_disposed || IsBusy)
                {
                    return;
                }

                IsBusy = true;
                ownsBusyState = true;
                Message = busy;

                CancellationToken cancellationToken =
                    _disposalCancellation.Token;
                cancellationToken.ThrowIfCancellationRequested();

                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync(
                        cancellationToken);
                AdGuardProtectionStatus status =
                    await action(router);

                cancellationToken.ThrowIfCancellationRequested();

                if (processNotification)
                {
                    await _protectionNotificationTracker
                        .ProcessProtectionStateAsync(
                            status.IsEnabled,
                            ProtectionStateSource.ManualAction);
                }

                cancellationToken.ThrowIfCancellationRequested();
                ApplyStatus(status);
                IsAdGuardAvailable = true;

                // Notify the already-open Overview immediately rather than
                // waiting for its scheduled refresh.
                ProtectionStateNotifier.Publish(status);

                Message = success;
            }
            catch (OperationCanceledException)
                when (_disposalCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                IsAdGuardAvailable = false;
                Message = OperationFailurePolicy.UserMessage(
                    ex,
                    "Protection command",
                    "Protection command could not be completed. Check the router connection and try again.");
            }
            finally
            {
                if (ownsBusyState)
                {
                    IsBusy = false;
                }

                _protectionStateGate.Release();
            }
        }

        private async Task UpdateOptionAsync(string label, Func<RouterManager, Task> action)
        {
            if (IsBusy) return;
            IsBusy = true; Message = $"Updating {label}...";
            try { RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(); await action(router); Message = $"{label} updated."; _options = await router.GetProtectionOptionsAsync(); NotifyConfigurationDetails(); DetermineProfile(); }
            catch (Exception ex) { Message = OperationFailurePolicy.UserMessage(ex, $"Protection option update ({label})", $"Unable to update {label}. Check the router connection and try again."); await RefreshOptionsOnlyAsync(); }
            finally { IsBusy = false; }
        }

        private async Task RefreshOptionsOnlyAsync()
        {
            try
            {
                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync();
                _options = await router.GetProtectionOptionsAsync();
                NotifyConfigurationDetails();
                _isInitialising = true;
                FilteringEnabled = _options.FilteringEnabled; SafeBrowsingEnabled = _options.SafeBrowsingEnabled; SafeSearchEnabled = _options.SafeSearchEnabled; ParentalEnabled = _options.ParentalEnabled; QueryLogEnabled = _options.QueryLogEnabled;
                DetermineProfile();
            }
            finally { _isInitialising = false; }
        }

        private async Task ApplyProfileAsync(string name, bool filtering, bool safeBrowsing, bool parental, bool safeSearch, bool queryLog)
        {
            if (IsBusy) return;
            IsBusy = true; Message = $"Applying {name} profile...";
            try
            {
                RouterManager r =
                    await _routerManagerProvider.GetRouterManagerAsync();
                await r.SetFilteringEnabledAsync(filtering);
                await r.SetSafeBrowsingEnabledAsync(safeBrowsing);
                await r.SetParentalEnabledAsync(parental);
                await r.SetSafeSearchEnabledAsync(safeSearch, _options.SafeSearch);
                await r.SetQueryLogEnabledAsync(queryLog, _options);
                await RefreshOptionsOnlyAsync();
                ProfileName = name; Message = $"{name} profile applied.";
            }
            catch (Exception ex) { Message = OperationFailurePolicy.UserMessage(ex, "Protection profile update", "Unable to apply profile. Check the router connection and try again."); }
            finally { IsBusy = false; }
        }

        private bool FilterBlockedService(object item)
        {
            if (item is not BlockedServiceItem service) return false;
            if (ShowBlockedOnly && !service.IsBlocked) return false;

            if (!string.Equals(
                    SelectedBlockedServiceCategory,
                    "All categories",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    service.Category,
                    SelectedBlockedServiceCategory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(BlockedServicesSearch) ||
                   service.Name.Contains(BlockedServicesSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
                   service.Id.Contains(BlockedServicesSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
                   service.Category.Contains(BlockedServicesSearch.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void BlockedService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BlockedServiceItem.IsBlocked)) return;
            OnPropertyChanged(nameof(BlockedServicesSelectionSummary));
            if (ShowBlockedOnly) BlockedServicesView.Refresh();
        }

        private void SetAllBlockedServices(bool blocked)
        {
            foreach (BlockedServiceItem service in BlockedServicesView.Cast<BlockedServiceItem>())
                service.IsBlocked = blocked;
            OnPropertyChanged(nameof(BlockedServicesSelectionSummary));
            Message = blocked ? "All visible services selected." : "All visible services cleared.";
        }

        private async Task SaveBlockedServicesAsync()
        {
            if (IsBusy) return;
            IsBusy = true; Message = "Saving blocked services...";
            try
            {
                BlockedServiceMutationResult? result = await _blockedServiceMutations.TryApplyManualChangesAsync(
                    _blockedConfig.EnabledIds,
                    BlockedServices.Where(s => s.IsBlocked).Select(s => s.Id));
                if (result is null) { Message = "Another blocked-service change is already running. Try again shortly."; return; }
                ApplyBlockedServices(result.Services, result.Config);
                Message = "Blocked services updated.";
            }
            catch (Exception ex) { Message = OperationFailurePolicy.UserMessage(ex, "Blocked-service update", "Unable to update blocked services. Check the router connection and try again."); }
            finally { IsBusy = false; }
        }

        private void ScheduleService_BlockedServicesChanged(object? sender, BlockedServiceMutationResult result)
        {
            Application.Current.Dispatcher.InvokeAsync(() => ApplyBlockedServices(result.Services, result.Config));
        }

        private void ApplyBlockedServices(IEnumerable<BlockedServiceItem> services, AdGuardBlockedServicesConfig config)
        {
            _blockedConfig = config;
            List<BlockedServiceItem> catalogue = services.ToList();
            if (catalogue.Count > 0)
            {
                foreach (BlockedServiceItem oldService in BlockedServices) oldService.PropertyChanged -= BlockedService_PropertyChanged;
                BlockedServices.Clear();
                foreach (BlockedServiceItem definition in catalogue.OrderBy(s => s.Name))
                {
                    var service = new BlockedServiceItem
                    {
                        Id = definition.Id,
                        Name = definition.Name,
                        Category = definition.Category,
                        IconSvg = definition.IconSvg,
                        GroupId = definition.GroupId,
                        IsBlocked = config.EnabledIds.Contains(definition.Id)
                    };
                    service.PropertyChanged += BlockedService_PropertyChanged;
                    BlockedServices.Add(service);
                }
            }
            else
            {
                // The catalogue and the blocked ID set are independent.  Keep
                // the last complete catalogue if AdGuard temporarily returns
                // no service metadata, and only refresh its selected state.
                foreach (BlockedServiceItem service in BlockedServices)
                    service.IsBlocked = config.EnabledIds.Contains(service.Id);
                if (BlockedServices.Count > 0)
                    BlockedServicesStatus = "The service catalogue could not be refreshed. Showing the last successful list.";
            }
            BlockedServicesView.Refresh();
            OnPropertyChanged(nameof(BlockedServicesSelectionSummary));
        }

        private async Task AddRuleAsync(bool allow)
        {
            string domain = NormaliseDomain(NewRuleDomain);
            if (domain.Length == 0) { Message = "Enter a domain first."; return; }
            string rule = allow ? $"@@||{domain}^" : $"||{domain}^";
            var all = FilteringRules.Select(r => r.Rule).Append(rule).Distinct(StringComparer.Ordinal).ToArray();
            await SaveRulesAsync(all, allow ? "Allow rule added." : "Block rule added.");
            NewRuleDomain = "";
        }

        private async Task DeleteRuleAsync()
        {
            if (SelectedRule is null) return;
            await SaveRulesAsync(FilteringRules.Where(r => !ReferenceEquals(r, SelectedRule)).Select(r => r.Rule).ToArray(), "Rule deleted.");
        }

        private async Task SaveRulesAsync(string[] rules, string success)
        {
            if (IsBusy) return;
            IsBusy = true; Message = "Saving custom filtering rules...";
            try
            {
                RouterManager router =
                    await _routerManagerProvider.GetRouterManagerAsync();
                await router.SetCustomFilteringRulesAsync(rules);
                FilteringRules.Clear(); foreach (var rule in await router.GetCustomFilteringRulesAsync()) FilteringRules.Add(rule); HasFilteringRulesData = true;
                Message = success;
            }
            catch (Exception ex) { Message = OperationFailurePolicy.UserMessage(ex, "Filtering-rule save", "Unable to save filtering rules. Check the router connection and try again."); }
            finally { IsBusy = false; }
        }

        private async Task AddRewriteAsync()
        {
            string domain = NormaliseDomain(NewRewriteDomain); string answer = NewRewriteAnswer.Trim();
            if (domain.Length == 0 || answer.Length == 0) { Message = "Enter both a domain and an answer."; return; }
            if (IsBusy) return;
            IsBusy = true; Message = "Adding DNS rewrite...";
            try { RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(); await router.AddDnsRewriteAsync(domain, answer); await ReloadRewritesAsync(router); NewRewriteDomain = ""; NewRewriteAnswer = ""; Message = "DNS rewrite added."; }
            catch (Exception ex) { Message = OperationFailurePolicy.UserMessage(ex, "DNS rewrite add", "Unable to add DNS rewrite. Check the router connection and try again."); }
            finally { IsBusy = false; }
        }

        private async Task DeleteRewriteAsync()
        {
            if (SelectedRewrite is null || IsBusy) return;
            IsBusy = true; Message = "Deleting DNS rewrite...";
            try { RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(); await router.DeleteDnsRewriteAsync(SelectedRewrite.Domain, SelectedRewrite.Answer); await ReloadRewritesAsync(router); Message = "DNS rewrite deleted."; }
            catch (Exception ex) { Message = OperationFailurePolicy.UserMessage(ex, "DNS rewrite delete", "Unable to delete DNS rewrite. Check the router connection and try again."); }
            finally { IsBusy = false; }
        }
        private async Task ReloadRewritesAsync(RouterManager? router = null) { router ??= await _routerManagerProvider.GetRouterManagerAsync(); DnsRewrites.Clear(); foreach (var x in await router.GetDnsRewritesAsync()) DnsRewrites.Add(x); }

        private void ApplyStatus(AdGuardProtectionStatus status)
        {
            if (status.IsEnabled) { SetProtectionStatus(RouterPilotStatus.Active); StatusDetail = "DNS filtering and protection are active."; Remaining = ""; }
            else if (status.IsPaused) { SetProtectionStatus(RouterPilotStatus.Pending); StatusDetail = "Protection is temporarily paused."; Remaining = "Remaining: " + FormatRemaining(status.RemainingPause); }
            else { SetProtectionStatus(RouterPilotStatus.Disabled); StatusDetail = "Protection is disabled until manually enabled."; Remaining = ""; }
        }

        private void SetProtectionStatus(RouterPilotStatus status)
        {
            if (_protectionStatus == status)
                return;

            _protectionStatus = status;
            StatusText = RouterPilotStatusPresentation.Text(status);
            OnPropertyChanged(nameof(StatusColour));
        }

        private void DetermineProfile()
        {
            ProfileName = FilteringEnabled && SafeBrowsingEnabled && ParentalEnabled && SafeSearchEnabled && QueryLogEnabled ? "Family" :
                          FilteringEnabled && SafeBrowsingEnabled && !ParentalEnabled && SafeSearchEnabled && !QueryLogEnabled ? "Privacy" :
                          FilteringEnabled && SafeBrowsingEnabled && !ParentalEnabled && !SafeSearchEnabled && QueryLogEnabled ? "Standard" : "Custom";
        }

        private bool FilterFilteringRule(object item)
        {
            if (item is not CustomFilteringRule rule) return false;
            if (!string.Equals(FilterRulesType, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.Type, FilterRulesType, StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrWhiteSpace(FilterRulesSearch) ||
                   rule.Rule.Contains(FilterRulesSearch.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void FilteringRules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            FilteringRulesView.Refresh();
            OnPropertyChanged(nameof(TotalFilteringRuleCount));
            OnPropertyChanged(nameof(BlockFilteringRuleCount));
            OnPropertyChanged(nameof(AllowFilteringRuleCount));
            OnPropertyChanged(nameof(CustomFilteringRuleCount));
        }

        private void NotifyConfigurationDetails()
        {
            foreach (string property in new[]
                     {
                         nameof(FilteringUpdateIntervalDisplay), nameof(QueryLogRetentionDisplay),
                         nameof(IgnoredQueryLogEntryCount), nameof(QueryLogAnonymizeClientIp),
                         nameof(SafeSearchBing), nameof(SafeSearchDuckDuckGo), nameof(SafeSearchEcosia),
                         nameof(SafeSearchGoogle), nameof(SafeSearchPixabay), nameof(SafeSearchYandex),
                         nameof(SafeSearchYouTube), nameof(FilteringStateDisplay), nameof(QueryLogStateDisplay),
                         nameof(SafeBrowsingStateDisplay), nameof(ParentalStateDisplay), nameof(SafeSearchStateDisplay),
                         nameof(SafeSearchBingDisplay), nameof(SafeSearchDuckDuckGoDisplay), nameof(SafeSearchEcosiaDisplay),
                         nameof(SafeSearchGoogleDisplay), nameof(SafeSearchPixabayDisplay), nameof(SafeSearchYandexDisplay),
                         nameof(SafeSearchYouTubeDisplay)
                     })
                OnPropertyChanged(property);
        }

        private void NotifyCommands()
        {
            foreach (var command in new[] { RefreshAllCommand, EnableProtectionCommand, DisableProtectionCommand, ResumeProtectionCommand, Pause30Command, Pause1HourCommand, Pause4HoursCommand, PauseUntilTomorrowCommand, ApplyStandardProfileCommand, ApplyFamilyProfileCommand, ApplyPrivacyProfileCommand, SaveBlockedServicesCommand, RefreshQueryLogCommand, AddDenyRuleCommand, AddAllowRuleCommand, DeleteRuleCommand, AddRewriteCommand, DeleteRewriteCommand }) command.NotifyCanExecuteChanged();
            SelectAllServicesCommand.NotifyCanExecuteChanged();
            ClearAllServicesCommand.NotifyCanExecuteChanged();
        }
        private static string NormaliseDomain(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();
        private static string FormatRemaining(TimeSpan d) => d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h {d.Minutes}m" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{Math.Max(1, d.Minutes)}m";
        private static string FormatDuration(TimeSpan d) => d.TotalHours >= 1 ? (d.TotalHours == 1 ? "1 hour" : $"{d.TotalHours:0.#} hours") : $"{d.TotalMinutes:0} minutes";
        private static string FormatHours(double hours) => hours <= 0 ? "Not reported" :
            hours % 24 == 0 ? (hours / 24 == 1 ? "1 day" : $"{hours / 24:0.#} days") :
            (hours == 1 ? "1 hour" : $"{hours:0.#} hours");
        private static string FormatOnOff(bool enabled) => enabled ? "On" : "Off";
    }
}
