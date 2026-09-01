using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels
{
    public partial class ClientsViewModel : ObservableObject
    {
        private const int RecentActivityCapacity = 50;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly ClientProfileService _clientProfileService;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly SettingsService _settingsService;
        private readonly TimelineService _timelineService;
        private readonly FavouriteDeviceMonitoringService _favouriteDeviceMonitoring;
        private readonly IClientPresenceHistoryService _presenceHistory;
        private readonly IDataFreshnessService _dataFreshnessService;
        private readonly ClientInventoryState _clientInventoryState;
        private readonly ClientInventoryCoordinator _clientInventoryCoordinator;
        private readonly IDeviceIdentityResolver _deviceIdentityResolver;
        private readonly IMdnsIdentityService _mdnsIdentityService;
        private readonly ClientIdentityEnrichmentCoordinator _identityEnrichmentCoordinator;
        private readonly AppSettings _settings;
        private readonly Dictionary<string, ClientProfile> _clientProfiles;
        private readonly bool _clientProfileStoreReliable;
        private DateTime _lastProfileSaveUtc = DateTime.MinValue;
        private readonly List<ClientInfo> _allClients = new();
        private readonly Dictionary<string, WifiClientInfo> _liveClientLookup =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ClientActivityItem>> _clientActivityHistory =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (int Total, int Blocked)> _lastActivityTotals =
            new(StringComparer.OrdinalIgnoreCase);
        private string _activityClientKey = string.Empty;

        public ObservableCollection<ClientInfo> Clients { get; } = new();
        public ObservableCollection<ClientInfo> NewDevices { get; } = new();
        public ObservableCollection<ClientActivityItem> SelectedClientActivity { get; } = new();
        public bool HasNewDevices => NewDevices.Count > 0;
        public bool HasSelectedClientActivity => SelectedClientActivity.Count > 0;

        [ObservableProperty] private bool isKnownDevicesMode;
        public string KnownDevicesButtonText => IsKnownDevicesMode ? "All Clients" : "Known Clients";
        public string ClientModeLabel => IsKnownDevicesMode ? "Known Clients" : "All Clients";
        public void ToggleKnownDevicesMode()
        {
            IsKnownDevicesMode = !IsKnownDevicesMode;
            SelectedClient = null;
            ApplyFilterAndSort();
        }
        partial void OnIsKnownDevicesModeChanged(bool value)
        {
            OnPropertyChanged(nameof(KnownDevicesButtonText));
            OnPropertyChanged(nameof(ClientModeLabel));
        }

        public void ReloadProfileState()
        {
            Dictionary<string, ClientProfile> persistedProfiles = _clientProfileService.Load();
            if (!_clientProfileService.LastLoadSucceeded)
            {
                return;
            }

            _clientProfiles.Clear();
            foreach ((string key, ClientProfile profile) in persistedProfiles)
            {
                _clientProfiles[key] = profile;
            }

            RebuildNewDevices(_allClients);
        }

        public IReadOnlyList<string> SortOptions { get; } =
            new[]
            {
                "IP address",
                "Blocked queries",
                "Last seen",
                "Total queries",
                "Block rate",
                "Name",
                "Manufacturer",
                "Device type"
            };

        [ObservableProperty]
        private string searchText = string.Empty;

        [ObservableProperty]
        private string selectedSortOption = "Blocked queries";

        [ObservableProperty]
        private bool sortDescending = true;

        [ObservableProperty]
        private bool showFavoritesOnly;

        [ObservableProperty]
        private bool hideClientsWithoutIp;

        [ObservableProperty]
        private bool hideUnknownDevices;

        [ObservableProperty]
        private bool onlineDevicesOnly;

        private int totalClientCount;
        private int visibleClientCount;
        public int TotalClientCount => totalClientCount;
        public int VisibleClientCount => visibleClientCount;
        public string ClientCountText => $"Showing {VisibleClientCount:N0} of {TotalClientCount:N0} {(TotalClientCount == 1 ? "client" : "clients")}";

        [ObservableProperty]
        private ClientInfo? selectedClient;

        [ObservableProperty]
        private string selectedClientWifiNetwork = "—";

        [ObservableProperty]
        private string selectedClientSignal = "—";

        [ObservableProperty]
        private string profileNickname = string.Empty;

        [ObservableProperty]
        private string profileNotes = string.Empty;

        [ObservableProperty]
        private string profileCategory = string.Empty;

        [ObservableProperty]
        private string statusMessage = "No client data loaded.";

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private bool isPinging;

        [ObservableProperty]
        private bool isWaking;

        [ObservableProperty]
        private string pingResult = "Select a client to run a connectivity check.";

        public string SortDirectionText =>
            SortDescending ? "Descending" : "Ascending";

        public ClientsViewModel(
            IRouterManagerProvider routerManagerProvider,
            AdGuardAvailabilityService adGuardAvailabilityService,
            SettingsService settingsService,
            TimelineService timelineService,
            FavouriteDeviceMonitoringService favouriteDeviceMonitoring,
            IClientPresenceHistoryService presenceHistory,
            IDataFreshnessService dataFreshnessService,
            ClientInventoryState clientInventoryState,
            ClientInventoryCoordinator clientInventoryCoordinator,
            IDeviceIdentityResolver deviceIdentityResolver,
            IMdnsIdentityService mdnsIdentityService)
        {
            _routerManagerProvider = routerManagerProvider;
            _adGuardAvailabilityService = adGuardAvailabilityService;
            _settingsService = settingsService;
            _timelineService = timelineService;
            _favouriteDeviceMonitoring = favouriteDeviceMonitoring;
            _presenceHistory = presenceHistory;
            _dataFreshnessService = dataFreshnessService;
            _clientInventoryState = clientInventoryState;
            _clientInventoryCoordinator = clientInventoryCoordinator;
            _deviceIdentityResolver = deviceIdentityResolver;
            _mdnsIdentityService = mdnsIdentityService;
            _identityEnrichmentCoordinator = new ClientIdentityEnrichmentCoordinator(deviceIdentityResolver, mdnsIdentityService);
            _settings = _settingsService.Load();
            _clientProfileService = new ClientProfileService();
            _clientProfiles = _clientProfileService.Load();
            _clientProfileStoreReliable = _clientProfileService.LastLoadSucceeded;
            AdGuardDataAvailability = _adGuardAvailabilityService.State;
            SelectedClientActivity.CollectionChanged += (_, _) =>
                OnPropertyChanged(nameof(HasSelectedClientActivity));

        }

        [RelayCommand]
        public async Task LoadClientsAsync()
        {
            if (IsLoading)
            {
                return;
            }

            IsLoading = true;
            StatusMessage = "Loading clients...";
            _dataFreshnessService.Configure("Clients", TimeSpan.FromSeconds(10));
            _dataFreshnessService.MarkAttempt("Clients");

            try
            {
                if (await _clientInventoryCoordinator.EnsureAuthoritativeInventoryAsync())
                {
                    string? sharedSelectedKey = SelectedClient is null ? null : ClientKey(SelectedClient);
                    _allClients.Clear();
                    _allClients.AddRange(_clientInventoryState.Snapshot.Values);
                    // The shared inventory is the authoritative router snapshot,
                    // but it is intentionally transport-only.  Run the same
                    // identity projection used by the direct refresh path before
                    // exposing it to cards; otherwise router-provided values such
                    // as an OS label can leak through as the visible device name.
                    foreach (ClientInfo client in _allClients)
                    {
                        EnrichClient(client);
                    }
                    ApplyFilterAndSort(sharedSelectedKey);
                    _ = EnrichOnlineManufacturersAsync(_allClients.ToList());
                    _ = EnrichMdnsAsync(_allClients.ToList());
                    AdGuardDataAvailability = _adGuardAvailabilityService.State;
                    _dataFreshnessService.MarkSuccess("Clients");
                    StatusMessage = string.Empty;
                    return;
                }

                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();

                string? selectedKey = SelectedClient is null
                    ? null
                    : ClientKey(SelectedClient);

                Task<ClientRefreshResult<List<ClientInfo>>> adGuardClientsTask =
                    CaptureClientRefreshAsync(routerManager.GetAdGuardClientsAsync());
                Task<ClientRefreshResult<List<WifiRadioInfo>>> wifiNetworksTask =
                    CaptureClientRefreshAsync(routerManager.GetWifiRadiosAsync());
                Task<ClientRefreshResult<List<WifiClientInfo>>> inventoryTask =
                    CaptureClientRefreshAsync(routerManager.GetGlClientInventoryAsync());

                await Task.WhenAll(adGuardClientsTask, wifiNetworksTask, inventoryTask);

                ClientRefreshResult<List<ClientInfo>> adGuardResult = await adGuardClientsTask;
                ClientRefreshResult<List<WifiRadioInfo>> wifiResult = await wifiNetworksTask;
                ClientRefreshResult<List<WifiClientInfo>> inventoryResult = await inventoryTask;

                if (wifiResult.Error is not null)
                    throw wifiResult.Error;
                if (inventoryResult.Error is not null)
                    throw inventoryResult.Error;

                List<WifiRadioInfo> wifiNetworks = wifiResult.Value ?? [];

                // Flatten the per-network client lists while explicitly carrying
                // the parent SSID/band/interface onto each client.  The Network
                // view can display a client under an SSID even when the GL.iNet
                // payload omits SSID on the child object, so relying on the child
                // record alone loses the network name in the Clients view.
                List<WifiClientInfo> liveClients = wifiNetworks
                    .SelectMany(network => network.Clients.Select(client =>
                    {
                        client.Ssid = HasUsefulValue(client.Ssid)
                            ? client.Ssid
                            : network.Ssid;
                        client.Band = HasUsefulValue(client.Band)
                            ? client.Band
                            : network.Band;
                        client.Interface = HasUsefulValue(client.Interface)
                            ? client.Interface
                            : network.Interface;
                        return client;
                    }))
                    .ToList();

                // Retain Ethernet and any firmware-only clients that are not
                // represented in the Wi-Fi network collection.
                List<WifiClientInfo> inventoryClients = inventoryResult.Value ?? [];

                foreach (WifiClientInfo inventoryClient in inventoryClients)
                {
                    bool alreadyPresent = liveClients.Any(item =>
                        ClientIdentityEquals(item, inventoryClient));

                    if (!alreadyPresent)
                    {
                        liveClients.Add(inventoryClient);
                    }
                }

                RebuildLiveClientLookup(liveClients);

                // The shared availability service is authoritative for whether
                // AdGuard Home is reachable. An empty client-enrichment result
                // only means no matching client data was returned.
                AdGuardDataAvailability = _adGuardAvailabilityService.State;

                List<ClientInfo> clients = BuildRouterClients(liveClients);
                ApplyAdGuardEnrichment(clients, adGuardResult.Value ?? [], AdGuardDataAvailability);

                await InitializeOrDetectNewDevicesAsync(clients);

                foreach (ClientInfo client in clients)
                {
                    ApplyLiveConnectionDetails(client, liveClients);
                    EnrichClient(client);
                    if (client.AdGuardDataAvailability == AdGuardAvailabilityState.Available)
                    {
                        RecordActivitySnapshot(client);
                    }
                }

                RebuildNewDevices(clients);

                _allClients.Clear();
                _allClients.AddRange(clients);
                _clientInventoryState.Update(_allClients);
                _clientInventoryCoordinator.MarkAuthoritativelyLoaded();

                ApplyFilterAndSort(selectedKey);
                _ = EnrichOnlineManufacturersAsync(_allClients.ToList());
                _ = EnrichMdnsAsync(_allClients.ToList());
                SaveProfiles();
                _presenceHistory.Observe(clients);
                _favouriteDeviceMonitoring.Observe(clients);
                _dataFreshnessService.MarkSuccess("Clients");

                StatusMessage = AdGuardDataAvailability != AdGuardAvailabilityState.Available
                    ? $"{_allClients.Count:N0} router-connected client(s) loaded. AdGuard enrichment is unavailable."
                    : _allClients.Count switch
                {
                    0 => "No clients were returned by AdGuard Home.",
                    1 => "1 client loaded.",
                    _ => $"{_allClients.Count} clients loaded."
                };
            }
            catch (Exception ex)
            {
                _dataFreshnessService.MarkUnavailable("Clients");
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Client refresh",
                    "Unable to load clients. Check the router connection and try again.");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static async Task<ClientRefreshResult<T>> CaptureClientRefreshAsync<T>(Task<T> task)
        {
            try
            {
                return new ClientRefreshResult<T>(await task, null);
            }
            catch (Exception ex)
            {
                return new ClientRefreshResult<T>(default, ex);
            }
        }

        private sealed record ClientRefreshResult<T>(T? Value, Exception? Error);

        public AdGuardAvailabilityState AdGuardDataAvailability
        {
            get => _adGuardDataAvailability;
            private set
            {
                if (!SetProperty(ref _adGuardDataAvailability, value)) return;
                OnPropertyChanged(nameof(DnsActivityAvailabilityMessage));
            }
        }

        private AdGuardAvailabilityState _adGuardDataAvailability =
            AdGuardAvailabilityState.Unavailable;

        partial void OnIsLoadingChanged(bool value) =>
            OnPropertyChanged(nameof(DnsActivityAvailabilityMessage));

        public string DnsActivityAvailabilityMessage => IsLoading
            ? "DNS activity is loading. Router client information remains available."
            : AdGuardDataAvailability switch
        {
            AdGuardAvailabilityState.Available => string.Empty,
            AdGuardAvailabilityState.NotConfigured =>
                "DNS activity is unavailable because AdGuard Home is not configured. Router client information remains available.",
            AdGuardAvailabilityState.AuthenticationFailed =>
                "DNS activity is unavailable because AdGuard Home authentication failed. Router client information remains available.",
            _ => "DNS activity is unavailable because AdGuard Home is not running or cannot be reached. Router client information remains available."
        };

        private static List<ClientInfo> BuildRouterClients(IEnumerable<WifiClientInfo> liveClients)
        {
            return liveClients
                .Where(client => ClientIdentity.NormalizeMac(client.MacAddress).Length == 12 || HasUsefulValue(client.IpAddress))
                .GroupBy(
                    client => ClientIdentity.NormalizeMac(client.MacAddress).Length == 12
                        ? "mac:" + ClientIdentity.NormalizeMac(client.MacAddress)
                        : "ip:" + client.IpAddress.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(client => new ClientInfo
                {
                    Name = client.Name,
                    RouterName = client.Name,
                    MacAddress = client.MacAddress,
                    IpAddress = client.IpAddress,
                    WifiNetwork = client.Ssid,
                    ConnectionType = client.Band,
                    SignalStrength = client.Signal,
                    LiveInterface = client.Interface,
                    QueryLogAvailable = false
                })
                .ToList();
        }

        private static void ApplyAdGuardEnrichment(
            IEnumerable<ClientInfo> routerClients,
            IReadOnlyList<ClientInfo> adGuardClients,
            AdGuardAvailabilityState availability)
        {
            foreach (ClientInfo routerClient in routerClients)
            {
                ClientInfo? enrichment = null;
                string mac = ClientIdentity.NormalizeMac(routerClient.MacAddress);
                if (mac.Length == 12)
                {
                    enrichment = adGuardClients.FirstOrDefault(client =>
                        ClientIdentity.NormalizeMac(client.MacAddress).Equals(mac, StringComparison.OrdinalIgnoreCase));
                }

                if (enrichment is null && HasUsefulValue(routerClient.IpAddress))
                {
                    enrichment = adGuardClients.FirstOrDefault(client =>
                        HasUsefulValue(client.IpAddress) &&
                        ClientIdentity.EndpointEquals(client.IpAddress, routerClient.IpAddress));
                }

                routerClient.AdGuardDataAvailability =
                    enrichment is null ? AdGuardAvailabilityState.Unavailable : availability;
                if (enrichment is null || availability != AdGuardAvailabilityState.Available)
                    continue;

                routerClient.TotalQueries = enrichment.TotalQueries;
                routerClient.BlockedQueries = enrichment.BlockedQueries;
                routerClient.LastSeen = enrichment.LastSeen;
                routerClient.QueryLogAvailable = enrichment.QueryLogAvailable;
                if (HasUsefulValue(enrichment.Name))
                    routerClient.AdGuardName = enrichment.Name;
            }
        }

        private async Task InitializeOrDetectNewDevicesAsync(IEnumerable<ClientInfo> clients)
        {
            if (!_clientProfileStoreReliable)
            {
                // An unreadable profile store must never be interpreted as a new
                // installation: preserve live client presentation, but do not detect
                // or overwrite persistent device identity until the store is repaired.
                return;
            }

            List<ClientInfo> macClients = clients
                .Where(client => ClientIdentity.NormalizeMac(client.MacAddress).Length == 12)
                .GroupBy(client => ClientIdentity.NormalizeMac(client.MacAddress), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (!_settings.NewDeviceDetectionInitialized)
            {
                // First feature-enabled run is a baseline: persisted profiles and the
                // current router inventory are established as known, never alerted.
                foreach (ClientProfile existing in _clientProfiles.Values)
                {
                    existing.IsKnown = true;
                    existing.NeedsReview = false;
                }

                foreach (ClientInfo client in macClients)
                {
                    string key = ClientIdentity.NormalizeMac(client.MacAddress);
                    if (!_clientProfiles.TryGetValue(key, out ClientProfile? profile))
                    {
                        profile = new ClientProfile { Key = key };
                        _clientProfiles[key] = profile;
                    }

                    profile.IsKnown = true;
                    profile.NeedsReview = false;
                    profile.FirstSeenUtc = profile.FirstSeenUtc == default ? DateTime.UtcNow : profile.FirstSeenUtc;
                    profile.LastSeenUtc = DateTime.UtcNow;
                    UpdateProfileObservation(profile, client);
                }

                SaveProfiles(force: true);
                _settings.NewDeviceDetectionInitialized = true;
                _settingsService.Save(_settings);
                return;
            }

            List<ClientInfo> newlyDetected = [];
            foreach (ClientInfo client in macClients)
            {
                string key = ClientIdentity.NormalizeMac(client.MacAddress);
                if (_clientProfiles.ContainsKey(key))
                {
                    continue;
                }

                _clientProfiles[key] = new ClientProfile
                {
                    Key = key,
                    IsKnown = false,
                    NeedsReview = true,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    LastKnownName = UsefulClientName(client),
                    LastKnownIpAddress = client.IpAddress,
                    LastKnownConnectionSummary = client.ConnectionSummary
                };
                newlyDetected.Add(client);
            }

            if (newlyDetected.Count == 0) return;

            // Persist before emitting the Timeline event, so restart cannot retrigger it.
            SaveProfiles(force: true);
            foreach (ClientInfo client in newlyDetected)
            {
                string key = ClientIdentity.NormalizeMac(client.MacAddress);
                long lifecycle = _clientProfiles.TryGetValue(key, out ClientProfile? profile) && profile.FirstSeenUtc != default
                    ? profile.FirstSeenUtc.Ticks
                    : DateTime.UtcNow.Ticks;
                await _timelineService.AddAsync(new TimelineEvent
                {
                    Category = TimelineCategory.Clients,
                    EventType = TimelineEventType.NewDeviceDetected,
                    Title = "New device detected",
                    Message = NewDeviceTimelineMessage(client),
                    Severity = TimelineSeverity.Information,
                    RelatedEntityId = key,
                    DeduplicationKey = $"new-device:{key}:{lifecycle}"
                });
            }
        }

        private void RebuildNewDevices(IEnumerable<ClientInfo> currentClients)
        {
            Dictionary<string, ClientInfo> currentByMac = currentClients
                .Where(client => ClientIdentity.NormalizeMac(client.MacAddress).Length == 12)
                .GroupBy(client => ClientIdentity.NormalizeMac(client.MacAddress), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            NewDevices.Clear();
            foreach (ClientProfile profile in _clientProfiles.Values
                         .Where(profile => profile.NeedsReview)
                         .OrderByDescending(profile => profile.FirstSeenUtc))
            {
                if (currentByMac.TryGetValue(profile.Key, out ClientInfo? current))
                {
                    NewDevices.Add(current);
                    continue;
                }

                NewDevices.Add(new ClientInfo
                {
                    Name = string.IsNullOrWhiteSpace(profile.Nickname) ?
                        (string.IsNullOrWhiteSpace(profile.LastKnownName) ? FormatMac(profile.Key) : profile.LastKnownName) : profile.Nickname,
                    RouterName = profile.LastKnownName,
                    MacAddress = FormatMac(profile.Key),
                    IpAddress = string.IsNullOrWhiteSpace(profile.LastKnownIpAddress) ? "-" : profile.LastKnownIpAddress,
                    ConnectionType = "Unknown",
                    FirstSeenUtc = profile.FirstSeenUtc,
                    LastObservedUtc = profile.LastSeenUtc,
                    NeedsReview = true,
                    HealthText = "Offline",
                    HealthColour = "#687386"
                });
            }

            OnPropertyChanged(nameof(HasNewDevices));
        }

        private static string UsefulClientName(ClientInfo client) =>
            HasUsefulValue(client.Name) ? client.Name :
            HasUsefulValue(client.RouterName) ? client.RouterName : FormatMac(ClientIdentity.NormalizeMac(client.MacAddress));

        private static string NewDeviceTimelineMessage(ClientInfo client)
        {
            List<string> details = [UsefulClientName(client)];
            if (HasUsefulValue(client.IpAddress)) details.Add(client.IpAddress);
            if (!string.IsNullOrWhiteSpace(client.ConnectionSummary)) details.Add(client.ConnectionSummary);
            return string.Join(" • ", details);
        }

        private static string FormatMac(string normalizedMac) => normalizedMac.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(index => normalizedMac.Substring(index * 2, 2)))
            : normalizedMac;

        public void SelectSortOption(string? option)
        {
            if (string.IsNullOrWhiteSpace(option))
            {
                return;
            }

            // Use the generated ObservableProperty setter so change
            // notifications and OnSelectedSortOptionChanged run correctly.
            SelectedSortOption = option;
        }

        public void ToggleFavorite(ClientInfo? client)
        {
            if (client is null)
            {
                return;
            }

            ClientProfile profile = GetOrCreateProfile(client);
            profile.IsFavorite = !profile.IsFavorite;
            profile.LastSeenUtc = DateTime.UtcNow;
            client.IsFavorite = profile.IsFavorite;

            SaveProfiles(force: true);
            ApplyFilterAndSort();
        }

        [RelayCommand]
        private void SaveSelectedClientProfile()
        {
            if (SelectedClient is null)
            {
                StatusMessage = "Select a client before saving a profile.";
                return;
            }

            ClientProfile profile = GetOrCreateProfile(SelectedClient);
            profile.Nickname = ProfileNickname.Trim();
            profile.Notes = ProfileNotes.Trim();
            profile.Category = ProfileCategory.Trim();
            profile.LastSeenUtc = DateTime.UtcNow;

            ApplyProfile(SelectedClient, profile);
            SaveProfiles(force: true);
            ApplyFilterAndSort(ClientKey(SelectedClient));
            StatusMessage = $"Profile saved for {SelectedClient.Name}.";
        }

        [RelayCommand]
        private void ClearSelectedClientProfile()
        {
            if (SelectedClient is null)
            {
                return;
            }

            string key = ClientKey(SelectedClient);
            bool wasFavorite = SelectedClient.IsFavorite;
            _clientProfiles.Remove(key);
            if (wasFavorite)
            {
                _clientProfiles[key] = new ClientProfile
                {
                    Key = key,
                    IsFavorite = true,
                    FirstSeenUtc = SelectedClient.FirstSeenUtc == default
                        ? DateTime.UtcNow
                        : SelectedClient.FirstSeenUtc,
                    LastSeenUtc = DateTime.UtcNow
                };
            }

            SelectedClient.Name = HasUsefulValue(SelectedClient.RouterName)
                ? SelectedClient.RouterName
                : SelectedClient.Name;
            SelectedClient.Notes = string.Empty;
            SelectedClient.CustomCategory = string.Empty;
            SelectedClient.IsFavorite = wasFavorite;

            ProfileNickname = string.Empty;
            ProfileNotes = string.Empty;
            ProfileCategory = string.Empty;

            SaveProfiles(force: true);
            ApplyFilterAndSort(key);
            StatusMessage = "Custom client profile cleared.";
        }

        [RelayCommand]
        private async Task PingSelectedClientAsync()
        {
            if (SelectedClient is null)
            {
                PingResult = "Select a client first.";
                return;
            }

            if (IsPinging)
            {
                return;
            }

            IsPinging = true;
            PingResult = $"Pinging {SelectedClient.IpAddress}...";

            try
            {
                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                PingResult = await routerManager.PingClientAsync(
                    SelectedClient.IpAddress);
                AddActivityEvent(
                    SelectedClient,
                    "Ping",
                    "Connectivity check completed",
                    PingResult);
            }
            catch (Exception ex)
            {
                PingResult = OperationFailurePolicy.UserMessage(
                    ex,
                    "Client ping",
                    "Ping could not be completed. Check the router connection and try again.");
            }
            finally
            {
                IsPinging = false;
            }
        }


        [RelayCommand]
        private async Task WakeSelectedClientAsync()
        {
            if (SelectedClient is null)
            {
                PingResult = "Select a client first.";
                return;
            }

            if (IsWaking)
            {
                return;
            }

            IsWaking = true;
            PingResult = $"Sending Wake-on-LAN to {SelectedClient.Name}...";

            try
            {
                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                PingResult = await routerManager.WakeClientAsync(
                    SelectedClient.MacAddress);
                AddActivityEvent(
                    SelectedClient,
                    "Wake",
                    "Wake-on-LAN request sent",
                    PingResult);
            }
            catch (Exception ex)
            {
                PingResult = OperationFailurePolicy.UserMessage(
                    ex,
                    "Wake-on-LAN request",
                    "Wake-on-LAN could not be completed. Check the router connection and try again.");
            }
            finally
            {
                IsWaking = false;
            }
        }

        [RelayCommand]
        private void ToggleSortDirection()
        {
            SortDescending = !SortDescending;
            OnPropertyChanged(nameof(SortDirectionText));
            ApplyFilterAndSort();
        }

        partial void OnSelectedClientChanged(ClientInfo? value)
        {
            PingResult = value is null
                ? "Select a client to run a connectivity check."
                : $"Ready to ping or wake {value.Name} ({value.IpAddress}).";

            UpdateSelectedClientConnectionDetails(value);
            string activityClientKey = value is null ? string.Empty : ClientKey(value);
            if (!activityClientKey.Equals(_activityClientKey, StringComparison.OrdinalIgnoreCase))
            {
                _activityClientKey = activityClientKey;
                LoadSelectedClientActivity(value);
            }
            LoadProfileEditor(value);
        }

        private async Task RefreshSelectedClientWifiDetailsAsync(ClientInfo? client)
        {
            if (client is null)
            {
                return;
            }

            string selectionKey = ClientKey(client);
            SelectedClientWifiNetwork = "Looking up…";
            SelectedClientSignal = "Looking up…";

            try
            {
                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                WifiClientInfo? live = await routerManager.GetWifiClientDetailsAsync(
                    client.MacAddress,
                    client.IpAddress);

                if (SelectedClient is null ||
                    !ClientKey(SelectedClient).Equals(selectionKey, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SelectedClientWifiNetwork = live is not null && HasUsefulValue(live.Ssid)
                    ? live.Ssid
                    : HasUsefulValue(client.WifiNetwork) ? client.WifiNetwork : "—";

                SelectedClientSignal = live is not null && HasUsefulValue(live.Signal)
                    ? live.Signal
                    : HasUsefulValue(client.SignalStrength) ? client.SignalStrength : "Not reported";
            }
            catch
            {
                if (SelectedClient is not null &&
                    ClientKey(SelectedClient).Equals(selectionKey, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateSelectedClientConnectionDetails(client);
                }
            }
        }

        private void RecordActivitySnapshot(ClientInfo client)
        {
            string key = ClientKey(client);
            var current = (client.TotalQueries, client.BlockedQueries);

            if (_lastActivityTotals.TryGetValue(key, out var previous))
            {
                int queryDelta = Math.Max(0, current.TotalQueries - previous.Total);
                int blockedDelta = Math.Max(0, current.BlockedQueries - previous.Blocked);

                if (queryDelta > 0 || blockedDelta > 0)
                {
                    AddActivityEvent(
                        client,
                        "DNS",
                        $"+{queryDelta} queries · +{blockedDelta} blocked",
                        $"Totals: {current.TotalQueries} queries, {current.BlockedQueries} blocked");
                }
            }
            else
            {
                AddActivityEvent(
                    client,
                    "Snapshot",
                    "Client activity loaded",
                    $"{current.TotalQueries} queries · {current.BlockedQueries} blocked");
            }

            _lastActivityTotals[key] = current;
        }

        private void AddActivityEvent(
            ClientInfo client,
            string eventType,
            string summary,
            string detail)
        {
            string key = ClientKey(client);
            if (!_clientActivityHistory.TryGetValue(key, out List<ClientActivityItem>? history))
            {
                history = new List<ClientActivityItem>();
                _clientActivityHistory[key] = history;
            }

            history.Insert(0, new ClientActivityItem
            {
                Timestamp = DateTime.Now,
                EventType = eventType,
                Summary = summary,
                Detail = detail
            });

            if (history.Count > RecentActivityCapacity)
            {
                history.RemoveRange(RecentActivityCapacity, history.Count - RecentActivityCapacity);
            }

            if (SelectedClient is not null &&
                ClientKey(SelectedClient).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                SelectedClientActivity.Insert(0, history[0]);
                while (SelectedClientActivity.Count > RecentActivityCapacity)
                {
                    SelectedClientActivity.RemoveAt(SelectedClientActivity.Count - 1);
                }
            }
        }

        private void LoadSelectedClientActivity(ClientInfo? client)
        {
            SelectedClientActivity.Clear();

            if (client is null)
            {
                return;
            }

            string key = ClientKey(client);
            if (!_clientActivityHistory.TryGetValue(key, out List<ClientActivityItem>? history))
            {
                return;
            }

            foreach (ClientActivityItem item in history)
            {
                SelectedClientActivity.Add(item);
            }
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyFilterAndSort();
        }

        partial void OnSelectedSortOptionChanged(string value)
        {
            ApplyFilterAndSort();
        }

        partial void OnShowFavoritesOnlyChanged(bool value)
        {
            ApplyFilterAndSort();
        }

        partial void OnHideClientsWithoutIpChanged(bool value)
        {
            ApplyFilterAndSort();
        }

        partial void OnHideUnknownDevicesChanged(bool value)
        {
            ApplyFilterAndSort();
        }

        partial void OnOnlineDevicesOnlyChanged(bool value)
        {
            ApplyFilterAndSort();
        }

        partial void OnSortDescendingChanged(bool value)
        {
            OnPropertyChanged(nameof(SortDirectionText));
        }

        private void ApplyFilterAndSort(string? preferredSelectionKey = null)
        {
            string? selectionKey = preferredSelectionKey ??
                (SelectedClient is null ? null : ClientKey(SelectedClient));
            string search = SearchText.Trim();

            List<ClientInfo> modeClients = IsKnownDevicesMode
                ? _clientProfiles.Values.Where(profile => profile.IsKnown)
                    .Select(profile => new KnownDeviceInfo
                    {
                        Profile = profile,
                        IdentityResolver = _deviceIdentityResolver,
                        CurrentClient = _clientInventoryState.Snapshot.TryGetValue(
                            ClientIdentity.NormalizeMac(profile.Key), out ClientInfo? live) ? live : null
                    })
                    .Select(device => device.ToClientInfo())
                    .ToList()
                : _allClients.ToList();

            totalClientCount = modeClients.Count;
            OnPropertyChanged(nameof(TotalClientCount));
            OnPropertyChanged(nameof(ClientCountText));
            List<ClientInfo> filteredClients = ClientFilterService.Apply(
                modeClients,
                new ClientFilterOptions(SearchText, ShowFavoritesOnly, HideClientsWithoutIp, HideUnknownDevices, OnlineDevicesOnly),
                IsAuthoritativelyOnline);
            visibleClientCount = filteredClients.Count;
            OnPropertyChanged(nameof(VisibleClientCount));
            OnPropertyChanged(nameof(ClientCountText));
            IEnumerable<ClientInfo> query = filteredClients;

            query = SelectedSortOption switch
            {
                "Blocked queries" => SortDescending
                    ? query.OrderByDescending(x => x.BlockedQueries)
                    : query.OrderBy(x => x.BlockedQueries),

                "Last seen" => SortDescending
                    ? query.OrderByDescending(x => LastSeenSortKey(x.LastSeen))
                    : query.OrderBy(x => LastSeenSortKey(x.LastSeen)),

                "Total queries" => SortDescending
                    ? query.OrderByDescending(x => x.TotalQueries)
                    : query.OrderBy(x => x.TotalQueries),

                "Block rate" => SortDescending
                    ? query.OrderByDescending(x => x.BlockRate)
                    : query.OrderBy(x => x.BlockRate),

                "Name" => SortDescending
                    ? query.OrderByDescending(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase),

                "Manufacturer" => SortDescending
                    ? query.OrderByDescending(
                        x => x.Manufacturer,
                        StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(
                        x => x.Manufacturer,
                        StringComparer.OrdinalIgnoreCase),

                "Device type" => SortDescending
                    ? query.OrderByDescending(
                        x => x.DeviceType,
                        StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(
                        x => x.DeviceType,
                        StringComparer.OrdinalIgnoreCase),

                _ => SortDescending
                    ? query.OrderByDescending(x => IpSortKey(x.IpAddress))
                    : query.OrderBy(x => IpSortKey(x.IpAddress))
            };

            // Favourites remain first without changing the selected sort.
            query = query
                .OrderByDescending(x => x.IsFavorite)
                .ThenBy(x => 0);

            // Reapply requested ordering inside favourite/non-favourite groups.
            query = ApplyGroupedSort(query);

            Clients.Clear();

            foreach (ClientInfo client in query)
            {
                Clients.Add(client);
            }

            if (!string.IsNullOrWhiteSpace(selectionKey))
            {
                SelectedClient = Clients.FirstOrDefault(client =>
                    ClientKey(client).Equals(
                        selectionKey,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (!IsLoading)
            {
                StatusMessage =
                    $"{VisibleClientCount} of {TotalClientCount} {(TotalClientCount == 1 ? "client" : "clients")} shown · " +
                    $"sorted by {SelectedSortOption.ToLowerInvariant()} " +
                    $"({SortDirectionText.ToLowerInvariant()}).";
            }
        }

        private IEnumerable<ClientInfo> ApplyGroupedSort(
            IEnumerable<ClientInfo> source)
        {
            IOrderedEnumerable<ClientInfo> grouped =
                source.OrderByDescending(x => x.IsFavorite);

            return SelectedSortOption switch
            {
                "Blocked queries" => SortDescending
                    ? grouped.ThenByDescending(x => x.BlockedQueries)
                    : grouped.ThenBy(x => x.BlockedQueries),

                "Last seen" => SortDescending
                    ? grouped.ThenByDescending(x => LastSeenSortKey(x.LastSeen))
                    : grouped.ThenBy(x => LastSeenSortKey(x.LastSeen)),

                "Total queries" => SortDescending
                    ? grouped.ThenByDescending(x => x.TotalQueries)
                    : grouped.ThenBy(x => x.TotalQueries),

                "Block rate" => SortDescending
                    ? grouped.ThenByDescending(x => x.BlockRate)
                    : grouped.ThenBy(x => x.BlockRate),

                "Name" => SortDescending
                    ? grouped.ThenByDescending(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase)
                    : grouped.ThenBy(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase),

                "Manufacturer" => SortDescending
                    ? grouped.ThenByDescending(
                        x => x.Manufacturer,
                        StringComparer.OrdinalIgnoreCase)
                    : grouped.ThenBy(
                        x => x.Manufacturer,
                        StringComparer.OrdinalIgnoreCase),

                "Device type" => SortDescending
                    ? grouped.ThenByDescending(
                        x => x.DeviceType,
                        StringComparer.OrdinalIgnoreCase)
                    : grouped.ThenBy(
                        x => x.DeviceType,
                        StringComparer.OrdinalIgnoreCase),

                _ => SortDescending
                    ? grouped.ThenByDescending(x => IpSortKey(x.IpAddress))
                    : grouped.ThenBy(x => IpSortKey(x.IpAddress))
            };
        }

        private static bool ClientIdentityEquals(
            WifiClientInfo left,
            WifiClientInfo right)
        {
            string leftMac = ClientIdentity.NormalizeMac(left.MacAddress);
            string rightMac = ClientIdentity.NormalizeMac(right.MacAddress);

            if (leftMac.Length == 12 && rightMac.Length == 12)
            {
                return leftMac.Equals(
                    rightMac,
                    StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(left.IpAddress) &&
                   left.IpAddress != "-" &&
                   left.IpAddress.Equals(
                       right.IpAddress,
                       StringComparison.OrdinalIgnoreCase);
        }

        private void RebuildLiveClientLookup(IEnumerable<WifiClientInfo> liveClients)
        {
            _liveClientLookup.Clear();

            foreach (WifiClientInfo live in liveClients
                .OrderByDescending(item => HasUsefulValue(item.Ssid))
                .ThenByDescending(item => HasUsefulValue(item.Signal)))
            {
                string macKey = ClientIdentity.NormalizeMac(live.MacAddress);
                if (macKey.Length == 12 && !_liveClientLookup.ContainsKey("mac:" + macKey))
                {
                    _liveClientLookup["mac:" + macKey] = live;
                }

                if (!string.IsNullOrWhiteSpace(live.IpAddress) && live.IpAddress != "-")
                {
                    string ipKey = "ip:" + live.IpAddress.Trim();
                    if (!_liveClientLookup.ContainsKey(ipKey))
                    {
                        _liveClientLookup[ipKey] = live;
                    }
                }

                string nameKey = NormaliseClientName(live.Name);
                if (nameKey.Length > 0 && !_liveClientLookup.ContainsKey("name:" + nameKey))
                {
                    _liveClientLookup["name:" + nameKey] = live;
                }
            }

            UpdateSelectedClientConnectionDetails(SelectedClient);
        }

        private void UpdateSelectedClientConnectionDetails(ClientInfo? client)
        {
            if (client is null)
            {
                SelectedClientWifiNetwork = "—";
                SelectedClientSignal = "—";
                return;
            }

            WifiClientInfo? live = null;
            string macKey = ClientIdentity.NormalizeMac(client.MacAddress);
            if (macKey.Length == 12)
            {
                _liveClientLookup.TryGetValue("mac:" + macKey, out live);
            }

            if (live is null && !string.IsNullOrWhiteSpace(client.IpAddress) && client.IpAddress != "-")
            {
                _liveClientLookup.TryGetValue("ip:" + client.IpAddress.Trim(), out live);
            }

            if (live is null)
            {
                string nameKey = NormaliseClientName(client.Name);
                if (nameKey.Length > 0)
                {
                    _liveClientLookup.TryGetValue("name:" + nameKey, out live);
                }
            }

            SelectedClientWifiNetwork = live is not null && HasUsefulValue(live.Ssid)
                ? live.Ssid
                : HasUsefulValue(client.WifiNetwork) ? client.WifiNetwork : "—";

            SelectedClientSignal = live is not null && HasUsefulValue(live.Signal)
                ? live.Signal
                : HasUsefulValue(client.SignalStrength) ? client.SignalStrength : "—";
        }

        private static void ApplyLiveConnectionDetails(
            ClientInfo client,
            IEnumerable<WifiClientInfo> liveClients)
        {
            string clientMac = ClientIdentity.NormalizeMac(client.MacAddress);

            WifiClientInfo? live = liveClients
                .Where(item =>
                {
                    string itemMac = ClientIdentity.NormalizeMac(item.MacAddress);

                    bool macMatches =
                        clientMac.Length == 12 &&
                        itemMac.Length == 12 &&
                        itemMac.Equals(
                            clientMac,
                            StringComparison.OrdinalIgnoreCase);

                    bool ipMatches =
                        !string.IsNullOrWhiteSpace(client.IpAddress) &&
                        client.IpAddress != "-" &&
                        item.IpAddress.Equals(
                            client.IpAddress,
                            StringComparison.OrdinalIgnoreCase);

                    string clientName = NormaliseClientName(client.Name);
                    string liveName = NormaliseClientName(item.Name);
                    bool nameMatches =
                        clientName.Length > 0 &&
                        liveName.Length > 0 &&
                        clientName.Equals(liveName, StringComparison.OrdinalIgnoreCase);

                    return macMatches || ipMatches || nameMatches;
                })
                // Prefer the per-SSID record used by the Network tab over the
                // more limited GL.iNet inventory fallback.
                .OrderByDescending(item => HasUsefulValue(item.Ssid))
                .ThenByDescending(item => HasUsefulValue(item.Signal))
                .ThenByDescending(item => HasUsefulValue(item.Interface))
                .FirstOrDefault();

            if (live is null)
            {
                return;
            }

            if (client.MacAddress == "-") client.MacAddress = live.MacAddress;
            if (client.IpAddress == "-") client.IpAddress = live.IpAddress;
            if ((client.Name == "-" || client.Name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) &&
                !live.Name.Equals("Unknown device", StringComparison.OrdinalIgnoreCase))
            {
                client.Name = live.Name;
            }

            client.ConnectionType = live.Band;
            client.WifiNetwork = live.Ssid;
            client.SignalStrength = live.Signal;
            client.LiveInterface = live.Interface;
        }

        private static IEnumerable<ClientInfo> BuildConnectedClientList(
            IEnumerable<ClientInfo> clients,
            IEnumerable<WifiClientInfo> liveClients)
        {
            List<ClientInfo> knownClients = clients.ToList();

            foreach (WifiClientInfo live in liveClients)
            {
                string liveMac = ClientIdentity.NormalizeMac(live.MacAddress);
                if (liveMac.Length != 12)
                    continue;

                ClientInfo? client = knownClients.FirstOrDefault(item =>
                    ClientIdentity.NormalizeMac(item.MacAddress).Equals(
                        liveMac,
                        StringComparison.OrdinalIgnoreCase));

                yield return client ?? new ClientInfo
                {
                    Name = live.Name,
                    RouterName = live.Name,
                    MacAddress = live.MacAddress,
                    IpAddress = live.IpAddress,
                    WifiNetwork = live.Ssid,
                    ConnectionType = live.Band
                };
            }
        }
        private static string NormaliseClientName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value == "-" ||
                value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Unknown device", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return new string(value
                .Trim()
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private static bool HasUsefulValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value != "-" &&
            !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("Unknown network", StringComparison.OrdinalIgnoreCase);

        private void EnrichClient(ClientInfo client)
        {
            string combined =
                $"{client.Name} {client.Manufacturer}".ToLowerInvariant();

            (client.DeviceIcon, client.DeviceType) =
                DetectDevice(combined);

            client.Manufacturer = _deviceIdentityResolver.ResolveManufacturer(
                client.MacAddress, client.Name, client.Manufacturer);

            if (!HasUsefulValue(client.RouterName))
            {
                client.RouterName = client.Name;
            }

            ClientProfile profile = GetOrCreateProfile(client);
            client.OperatingSystem = _deviceIdentityResolver.ResolveOperatingSystem(
                client.RouterName, client.AdGuardName, client.MdnsName, profile.LastKnownName) ?? string.Empty;
            string resolvedName = _deviceIdentityResolver.ResolveFriendlyName(new DeviceIdentitySignals(
                profile.Nickname,
                client.RouterName,
                null,
                null,
                client.AdGuardName,
                profile.LastKnownName,
                client.IpAddress));
            client.NameSource = ResolveNameSource(profile, client);
            // Always apply the shared resolution result.  Keeping the raw router
            // value when resolution returns Unknown device is what allowed
            // platform labels such as "Windows" to remain visible as names.
            client.Name = resolvedName;
            LogIdentityResolution(client, profile, resolvedName);
            profile.LastSeenUtc = DateTime.UtcNow;
            UpdateProfileObservation(profile, client);
            ApplyProfile(client, profile);

            (client.HealthText, client.HealthColour) =
                DetectHealth(client);
        }

        private static string ResolveNameSource(ClientProfile profile, ClientInfo client)
        {
            if (HasUsefulValue(profile.Nickname)) return "Personalized";
            if (HasUsefulValue(client.RouterName)) return "Router";
            if (HasUsefulValue(client.MdnsName)) return "mDNS";
            if (HasUsefulValue(client.AdGuardName)) return "AdGuard";
            if (HasUsefulValue(profile.LastKnownName)) return "Previously known";
            return "Unknown";
        }

        private static (string Icon, string Type) DetectDevice(string value)
        {
            if (ContainsAny(value, "iphone", "ipad", "ios", "apple-mobile"))
            {
                return ("📱", "Apple mobile device");
            }

            if (ContainsAny(value, "android", "pixel", "galaxy", "phone"))
            {
                return ("📱", "Mobile device");
            }

            if (ContainsAny(value, "xbox", "playstation", "ps4", "ps5",
                "nintendo", "switch"))
            {
                return ("🎮", "Games console");
            }

            if (ContainsAny(value, "tv", "roku", "firestick", "chromecast",
                "appletv"))
            {
                return ("📺", "Media or smart TV");
            }

            if (ContainsAny(value, "printer", "epson", "brother", "laserjet"))
            {
                return ("▣", "Printer");
            }

            if (ContainsAny(value, "raspberry", "linux", "ubuntu", "debian",
                "server", "nas", "synology"))
            {
                return ("◆", "Server or Linux device");
            }

            if (ContainsAny(value, "laptop", "desktop", "windows", "pc",
                "macbook", "imac"))
            {
                return ("▰", "Computer");
            }

            return ("●", "Unknown device");
        }

        private static (string Text, string Colour) DetectHealth(
            ClientInfo client)
        {
            if (client.AdGuardDataAvailability != AdGuardAvailabilityState.Available)
            {
                // Rows are created only from the current authoritative router
                // snapshot, so DNS availability does not define online state.
                return ("Online", "#16803C");
            }

            if (DateTime.TryParse(
                client.LastSeen,
                out DateTime lastSeen))
            {
                TimeSpan age = DateTime.Now - lastSeen;

                if (age <= TimeSpan.FromMinutes(5))
                {
                    return ("Online", "#16803C");
                }

                if (age <= TimeSpan.FromHours(1))
                {
                    return ("Recently active", "#B26A00");
                }

                return ("Offline", "#687386");
            }

            if (client.TotalQueries > 0)
            {
                return ("Active", "#16803C");
            }

            return ("Unknown", "#687386");
        }

        private ClientProfile GetOrCreateProfile(ClientInfo client)
        {
            string key = ClientKey(client);
            if (!_clientProfiles.TryGetValue(key, out ClientProfile? profile))
            {
                profile = new ClientProfile
                {
                    Key = key,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                };
                _clientProfiles[key] = profile;
            }

            return profile;
        }

        private static void ApplyProfile(ClientInfo client, ClientProfile profile)
        {
            client.FirstSeenUtc = profile.FirstSeenUtc;
            client.LastObservedUtc = profile.LastSeenUtc;
            client.Notes = profile.Notes;
            client.CustomCategory = profile.Category;
            client.IsFavorite = profile.IsFavorite;
            client.MonitorAvailability = profile.MonitorAvailability;
            client.NeedsReview = profile.NeedsReview;

            if (!string.IsNullOrWhiteSpace(profile.Nickname))
            {
                client.Name = profile.Nickname;
            }

            if (!string.IsNullOrWhiteSpace(profile.Category))
            {
                client.DeviceType = profile.Category;
            }
        }

        private static void UpdateProfileObservation(ClientProfile profile, ClientInfo client)
        {
            profile.LastKnownName = UsefulClientName(client);
            profile.LastKnownIpAddress = HasUsefulValue(client.IpAddress) ? client.IpAddress : profile.LastKnownIpAddress;
            profile.LastKnownConnectionSummary = !string.IsNullOrWhiteSpace(client.ConnectionSummary)
                ? client.ConnectionSummary
                : profile.LastKnownConnectionSummary;
        }

        private void LoadProfileEditor(ClientInfo? client)
        {
            if (client is null)
            {
                ProfileNickname = string.Empty;
                ProfileNotes = string.Empty;
                ProfileCategory = string.Empty;
                return;
            }

            ClientProfile profile = GetOrCreateProfile(client);
            ProfileNickname = profile.Nickname;
            ProfileNotes = profile.Notes;
            ProfileCategory = profile.Category;
        }

        private void SaveProfiles(bool force = false)
        {
            if (!_clientProfileStoreReliable)
            {
                return;
            }

            if (!force && DateTime.UtcNow - _lastProfileSaveUtc < TimeSpan.FromMinutes(1))
            {
                return;
            }

            try
            {
                if (_clientProfileService.Save(_clientProfiles.Values))
                {
                    _lastProfileSaveUtc = DateTime.UtcNow;
                }
                else
                {
                    StatusMessage = "Client profile changed, but it could not be saved.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Client profile save",
                    "Client profile changed, but it could not be saved.");
            }
        }

        private static string ClientKey(ClientInfo client)
        {
            if (!string.IsNullOrWhiteSpace(client.MacAddress) &&
                client.MacAddress != "-")
            {
                return ClientIdentity.NormalizeMac(client.MacAddress);
            }

            return client.IpAddress.Trim();
        }
        private static bool ContainsAny(
            string value,
            params string[] terms)
        {
            return terms.Any(term =>
                value.Contains(
                    term,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string? value, string search) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(
                search,
                StringComparison.OrdinalIgnoreCase);

        private static bool HasUsableClientIp(string? value)
            => ClientFilterService.HasUsableIp(value);

        private void LogIdentityResolution(ClientInfo client, ClientProfile profile, string resolvedName)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Identity resolution: mac={MaskMac(client.MacAddress)} ip={client.IpAddress} " +
                $"router={client.RouterName ?? string.Empty} adguard={client.AdGuardName ?? string.Empty} " +
                $"persisted={profile.LastKnownName} final={resolvedName}");
        }

        private string MaskMac(string? mac)
        {
            if (!_deviceIdentityResolver.TryParseMac(mac, out ParsedMacAddress? parsed) || parsed is null)
                return "(invalid)";
            return parsed.Canonical[..6] + "******";
        }

        private async Task EnrichOnlineManufacturersAsync(IReadOnlyList<ClientInfo> clients)
        {
            try
            {
                List<(ClientInfo Client, string Manufacturer)> results =
                    await _identityEnrichmentCoordinator.ResolveManufacturersAsync(clients);
                if (results.Count == 0) return;

                void ApplyResults()
                {
                    bool changed = false;
                    foreach ((ClientInfo client, string manufacturer) in results)
                    {
                        if (!_allClients.Contains(client) || client.Manufacturer == manufacturer) continue;
                        client.Manufacturer = manufacturer;
                        changed = true;
                    }
                    if (changed)
                        ApplyFilterAndSort(SelectedClient is null ? null : ClientKey(SelectedClient));
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(ApplyResults);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MAC vendor enrichment unavailable: {ex.GetType().Name}");
            }
        }

        private async Task EnrichMdnsAsync(IReadOnlyList<ClientInfo> clients)
        {
            using SemaphoreSlim gate = new(4, 4);
            try
            {
                List<(ClientInfo Client, string Hostname)> results = (await Task.WhenAll(clients.Select(async client =>
                {
                    if (!HasUsableClientIp(client.IpAddress)) return (client, string.Empty);
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { return (client, await _mdnsIdentityService.ResolveHostnameAsync(client.IpAddress).ConfigureAwait(false) ?? string.Empty); }
                    finally { gate.Release(); }
                }))).Where(item => !string.IsNullOrWhiteSpace(item.Item2)).ToList();
                if (results.Count == 0) return;

                void ApplyResults()
                {
                    bool changed = false;
                    foreach ((ClientInfo client, string hostname) in results)
                    {
                        if (!_allClients.Contains(client)) continue;
                        if (string.Equals(client.MdnsName, hostname, StringComparison.OrdinalIgnoreCase)) continue;
                        client.MdnsName = hostname;
                        ClientProfile profile = GetOrCreateProfile(client);
                        string resolved = _deviceIdentityResolver.ResolveFriendlyName(new DeviceIdentitySignals(
                            profile.Nickname, client.RouterName, null, hostname, client.AdGuardName,
                            profile.LastKnownName, client.IpAddress));
                        client.OperatingSystem = _deviceIdentityResolver.ResolveOperatingSystem(
                            client.RouterName, client.AdGuardName, hostname, profile.LastKnownName) ?? client.OperatingSystem;
                        if (!string.Equals(resolved, "Unknown device", StringComparison.OrdinalIgnoreCase))
                            client.Name = resolved;
                        client.NameSource = ResolveNameSource(profile, client);
                        UpdateProfileObservation(profile, client);
                        changed = true;
                    }
                    if (changed)
                    {
                        SaveProfiles();
                        ApplyFilterAndSort(SelectedClient is null ? null : ClientKey(SelectedClient));
                    }
                }
                if (System.Windows.Application.Current is { } app)
                    app.Dispatcher.Invoke(ApplyResults);
                else
                    ApplyResults();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"mDNS enrichment unavailable: {ex.GetType().Name}");
            }
        }

        private static bool IsUnknownDeviceName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string name = value.Trim();
            return name.Equals("-", StringComparison.Ordinal) ||
                   name.Equals("—", StringComparison.Ordinal) ||
                   name.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Unknown device", StringComparison.OrdinalIgnoreCase);
        }

        // Online state is established by the existing router inventory/enrichment
        // path.  This filter only projects that state; it performs no new probe.
        private bool IsAuthoritativelyOnline(ClientInfo client)
        {
            // All Clients is built exclusively from the current live-router
            // snapshot. Known Clients use the same snapshot for correlation;
            // persisted known records absent from it are offline.
            if (!IsKnownDevicesMode) return _allClients.Contains(client);
            string key = ClientIdentity.NormalizeHexMac(client.MacAddress);
            return key.Length == 12 && _clientInventoryState.Snapshot.ContainsKey(key);
        }

        private static bool IsOnlineStatus(string? status) =>
            string.Equals(status, "Online", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Recently active", StringComparison.OrdinalIgnoreCase);

        private static long IpSortKey(string? value)
        {
            if (!System.Net.IPAddress.TryParse(
                value,
                out var address))
            {
                return long.MaxValue;
            }

            byte[] bytes = address.GetAddressBytes();

            if (bytes.Length != 4)
            {
                return long.MaxValue - 1;
            }

            return ((long)bytes[0] << 24) |
                   ((long)bytes[1] << 16) |
                   ((long)bytes[2] << 8) |
                   bytes[3];
        }

        private static DateTime LastSeenSortKey(string? value)
        {
            return DateTime.TryParse(
                value,
                out DateTime parsed)
                    ? parsed
                    : DateTime.MinValue;
        }
    }
}
