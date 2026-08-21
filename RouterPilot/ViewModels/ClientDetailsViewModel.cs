using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels
{
    public partial class ClientDetailsViewModel : ObservableObject, IDisposable
    {
        private const int RecentDnsHistoryLimit = 50;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly ClientProfileService _clientProfileService;
        private readonly IClientPresenceHistoryService _presenceHistory;
        private readonly KnownDeviceForgetService _knownDeviceForgetService;
        private readonly ClientInventoryState _clientInventory;
        private readonly Dictionary<string, ClientProfile> _clientProfiles;
        private readonly DispatcherTimer _refreshTimer;
        private readonly ClientInfo _client;
        private readonly DhcpLeaseInfo? _dhcpLease;
        private readonly DhcpReservationInfo? _dhcpReservation;
        private readonly IReadOnlyList<PortForwardRuleInfo> _portForwardRules;
        private int _relatedPortForwardCount;
        private DnsQueryLogReadState _queryLogReadState;

        private enum DnsQueryLogReadState
        {
            Unknown,
            Available,
            Unavailable
        }

        private enum DnsActivityPresentationState
        {
            LoadingOrUnknown,
            Paused,
            AdGuardUnavailable,
            QueryLogDisabled,
            QueryLogUnavailable,
            AvailableWithData,
            AvailableNoMatchingActivity
        }

        public event EventHandler? DeviceForgotten;

        public ObservableCollection<QueryLogEntry> RecentQueries { get; } =
            new();

        public ObservableCollection<DomainStat> TopDomains { get; } =
            new();

        public ObservableCollection<DomainStat> TopBlockedDomains { get; } =
            new();
        public ObservableCollection<PresenceTimelineItem> RecentPresence { get; } = new();
        public ObservableCollection<DailyAvailabilityItem> DailyAvailability { get; } = new();
        public ObservableCollection<ClientPortForwardAssociation> RelatedPortForwards { get; } = new();
        [ObservableProperty] private AvailabilityRange selectedAvailabilityRange = AvailabilityRange.Hours24;
        public bool Is24HourRange => SelectedAvailabilityRange == AvailabilityRange.Hours24;
        public bool Is7DayRange => SelectedAvailabilityRange == AvailabilityRange.Days7;

        public string ClientName =>
            string.IsNullOrWhiteSpace(ProfileNickname)
                ? _client.Name
                : ProfileNickname;
        private ClientInfo? LiveClient =>
            ClientIdentity.IsMacKey(_client.MacAddress) &&
            _clientInventory.Snapshot.TryGetValue(ClientIdentity.NormalizeMac(_client.MacAddress), out ClientInfo? client)
                ? client
                : null;
        private ClientProfile? Profile =>
            _clientProfiles.GetValueOrDefault(ClientIdentity.NormalizeMac(_client.MacAddress));
        public bool IsCurrentlyObserved => LiveClient is not null;
        public string IpAddress => LiveClient?.IpAddress ??
            (!string.IsNullOrWhiteSpace(Profile?.LastKnownIpAddress)
                ? Profile.LastKnownIpAddress
                : "Unavailable");
        public string IpAddressLabel => IsCurrentlyObserved ? "IP ADDRESS" : "LAST KNOWN IP";
        public string IpAddressToolTip => IsCurrentlyObserved
            ? IpAddress
            : "Last IP address reported before this device went offline.";
        public string MacAddress => _client.MacAddress;
        public string LastSeen => _client.LastSeen;
        public string TotalQueriesDisplay => _client.TotalQueriesDisplay;
        public string BlockedQueriesDisplay => _client.BlockedQueriesDisplay;
        public string BlockRateDisplay => _client.BlockRateDisplay;
        public bool IsEthernetConnection => LiveClient?.IsEthernetConnection == true;
        public bool IsWifiConnection => LiveClient?.IsWifiConnection == true;
        public string ConnectionType => IsEthernetConnection ? "Ethernet" : "Wi-Fi";
        public string ConnectionLabel => IsCurrentlyObserved ? "CONNECTION" : "LAST CONNECTION";
        public string ConnectionSummary => LiveClient?.ConnectionSummary ??
            (!string.IsNullOrWhiteSpace(Profile?.LastKnownConnectionSummary)
                ? Profile.LastKnownConnectionSummary
                : "No previous connection details");
        public string ConnectionToolTip => IsCurrentlyObserved
            ? ConnectionSummary
            : "Last connection reported before this device went offline.";
        public string WifiNetwork => LiveClient?.WifiNetwork ?? "-";
        public string WifiBand => LiveClient?.ConnectionType ?? "-";
        public string WifiInterface => LiveClient?.LiveInterface ?? "-";
        public bool HasSignal => LiveClient?.HasSignalSummary == true;
        public string SignalQuality => LiveClient?.SignalQuality ?? "—";
        public string SignalStrength => LiveClient?.SignalStrength ?? "—";
        public string SignalSummary => LiveClient?.SignalSummary ?? string.Empty;
        public string HealthText => LiveClient?.HealthText ?? "Offline";
        public string HealthColour => LiveClient?.HealthColour ?? RouterPilotStatusPresentation.Colour(RouterPilotStatus.NotAvailable);
        public string FirstObserved => FormatObserved(_client.FirstSeenUtc);
        public string LastObserved => FormatObserved(_client.LastObservedUtc);
        public bool NeedsReview => _client.NeedsReview;
        public string CurrentPresenceStatus => _presenceHistory.GetCurrentPeriod(_client.MacAddress)?.State.ToString() ?? "Unknown";
        public string CurrentPresenceStatusColour => RouterPilotStatusPresentation.Colour(CurrentPresenceStatus switch
        {
            "Online" => RouterPilotStatus.Active,
            "Offline" => RouterPilotStatus.Error,
            _ => RouterPilotStatus.Pending
        });
        public string ObservedOnlineToday => FormatDuration(_presenceHistory.GetObservedOnlineToday(_client.MacAddress, DateTimeOffset.UtcNow));
        public string CurrentObservedOnline => _presenceHistory.GetCurrentPeriod(_client.MacAddress) is { State: ClientPresenceState.Online } period
            ? $"At least {FormatDuration(DateTimeOffset.UtcNow - period.StartedAt)}" : "—";
        public string LastOfflinePeriod
        {
            get
            {
                ClientPresencePeriod? last = _presenceHistory.GetRecent(_client.MacAddress, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow)
                    .Where(period => period.State == ClientPresenceState.Offline && period.EndedAt is not null)
                    .OrderByDescending(period => period.EndedAt).FirstOrDefault();
                return last is null ? "No offline period observed" : $"{last.StartedAt.LocalDateTime:t} – {last.EndedAt!.Value.LocalDateTime:t} • {FormatDuration(last.EndedAt.Value - last.StartedAt)}";
            }
        }
        public string RecentAvailability => "Online and Offline periods are RouterPilot observations. Unmonitored time is Unknown.";

        // Leases describe current router state, so only present them while the
        // client is in the existing live inventory. A reservation remains useful
        // configuration context for an offline known device.
        public bool HasDhcpReservation => _dhcpReservation is not null;
        public bool HasDhcpLease => IsCurrentlyObserved && _dhcpLease is not null;
        public bool HasDhcpDetails => HasDhcpLease || HasDhcpReservation;
        public string DhcpIpAddress => _dhcpReservation?.IpAddress ?? _dhcpLease?.IpAddress ?? "Not reported";
        public string DhcpAddressLabel => HasDhcpReservation ? "RESERVED ADDRESS" : "DHCP ADDRESS";
        public string DhcpLeaseType => _dhcpReservation is not null || _dhcpLease?.IsStatic == true ? "Reserved" : "Dynamic";
        public string DhcpLeaseRemaining => _dhcpLease?.RemainingLease ?? "Not reported";
        public string DhcpReservation => _dhcpReservation is null ? "Not configured" : _dhcpReservation.Enabled ? "Enabled" : "Disabled";
        public string DhcpScope => _dhcpLease?.ScopeDisplay ?? _dhcpReservation?.ScopeDisplay ?? "Not reported";
        public string DhcpSummary => $"{DhcpLeaseType} • {DhcpScope}";
        public bool HasCurrentReservedAddressMismatch =>
            HasDhcpReservation &&
            IsCurrentlyObserved &&
            HasUsefulAddress(LiveClient?.IpAddress) &&
            !SameText(LiveClient?.IpAddress, _dhcpReservation?.IpAddress);
        public bool HasDhcpAddressPresentation => HasDhcpDetails && !HasCurrentReservedAddressMismatch;
        public string DhcpCurrentAddress => LiveClient?.IpAddress ?? "Not reported";
        public string DhcpReservedAddress => _dhcpReservation?.IpAddress ?? "Not reported";
        public bool HasDhcpLeaseRemaining =>
            HasDhcpLease &&
            !string.IsNullOrWhiteSpace(DhcpLeaseRemaining) &&
            !string.Equals(DhcpLeaseRemaining, "N/A", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DhcpLeaseRemaining, "Not reported", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DhcpLeaseRemaining, "Static", StringComparison.OrdinalIgnoreCase);

        public bool HasRelatedPortForwards => RelatedPortForwards.Count > 0;
        public int RelatedPortForwardCount => _relatedPortForwardCount;
        public int AdditionalRelatedPortForwardCount => Math.Max(0, RelatedPortForwardCount - RelatedPortForwards.Count);
        public bool HasAdditionalRelatedPortForwards => AdditionalRelatedPortForwardCount > 0;
        public string RelatedPortForwardSummary => RelatedPortForwardCount == 1
            ? "1 rule targeting this address"
            : $"{RelatedPortForwardCount} rules targeting this address";

        public bool HasRecentQueries => RecentQueries.Count > 0;
        public bool HasTopDomains => TopDomains.Count > 0;
        public bool HasTopBlockedDomains => TopBlockedDomains.Count > 0;
        private DnsActivityPresentationState DnsActivityState =>
            IsLoading ? DnsActivityPresentationState.LoadingOrUnknown :
            IsPaused ? DnsActivityPresentationState.Paused :
            _adGuardAvailabilityService.State != AdGuardAvailabilityState.Available ? DnsActivityPresentationState.AdGuardUnavailable :
            !_client.QueryLogAvailable ? DnsActivityPresentationState.QueryLogDisabled :
            _queryLogReadState == DnsQueryLogReadState.Available ?
                HasRecentQueries ? DnsActivityPresentationState.AvailableWithData : DnsActivityPresentationState.AvailableNoMatchingActivity :
            _queryLogReadState == DnsQueryLogReadState.Unavailable ? DnsActivityPresentationState.QueryLogUnavailable :
            DnsActivityPresentationState.LoadingOrUnknown;
        public bool DnsActivityContentAvailable =>
            _queryLogReadState == DnsQueryLogReadState.Available &&
            _adGuardAvailabilityService.State == AdGuardAvailabilityState.Available &&
            _client.QueryLogAvailable;
        public bool DnsActivityContextVisible =>
            DnsActivityState != DnsActivityPresentationState.AvailableWithData;
        public bool IsDnsSummaryAvailable =>
            _adGuardAvailabilityService.State == AdGuardAvailabilityState.Available &&
            _client.AdGuardDataAvailability == AdGuardAvailabilityState.Available;
        public string DnsActivityContextHeading => DnsActivityState switch
        {
            DnsActivityPresentationState.Paused => "DNS refresh paused",
            DnsActivityPresentationState.AvailableNoMatchingActivity => "No DNS activity recorded",
            DnsActivityPresentationState.LoadingOrUnknown => "Loading DNS activity",
            _ => "DNS activity unavailable"
        };
        public string DnsActivityAvailabilityMessage => DnsActivityState switch
        {
            DnsActivityPresentationState.LoadingOrUnknown => "DNS activity has not been loaded for this device.",
            DnsActivityPresentationState.Paused => "Automatic Client Details DNS refresh is paused. Existing activity remains visible.",
            DnsActivityPresentationState.AdGuardUnavailable => "AdGuard is currently unavailable.",
            DnsActivityPresentationState.QueryLogDisabled => "AdGuard query logging is disabled.",
            DnsActivityPresentationState.QueryLogUnavailable => "Query-log data is not currently available.",
            DnsActivityPresentationState.AvailableNoMatchingActivity => "No DNS activity recorded for this device.",
            _ => "Live values from the AdGuard Home query log."
        };
        public string ActivityAvailabilityToolTip => DnsActivityAvailabilityMessage;
        public string DnsActivityHeaderText =>
            DnsActivityState == DnsActivityPresentationState.AvailableWithData
                ? "Live DNS activity and client statistics"
                : DnsActivityContextHeading;
        public string DnsTotalQueriesDisplay =>
            IsDnsSummaryAvailable ? TotalQueriesDisplay : RouterPilotStatusPresentation.NotAvailable;
        public string DnsBlockedQueriesDisplay =>
            IsDnsSummaryAvailable ? BlockedQueriesDisplay : RouterPilotStatusPresentation.NotAvailable;
        public string DnsBlockRateDisplay =>
            IsDnsSummaryAvailable ? BlockRateDisplay : RouterPilotStatusPresentation.NotAvailable;
        public string DnsSummaryCaption =>
            IsDnsSummaryAvailable ? "Requests from this client" : "DNS data unavailable";
        public string DnsBlockSummaryCaption =>
            IsDnsSummaryAvailable ? "Protection actions" : "DNS data unavailable";
        public string DnsLastSeenDisplay =>
            IsDnsSummaryAvailable ? $"Last seen: {LastSeen}" : DnsActivityContextHeading;
        public string RequestedDomainsEmptyMessage =>
            DnsActivityContentAvailable ? "No requested domains recorded." : DnsActivityAvailabilityMessage;
        public string BlockedDomainsEmptyMessage =>
            DnsActivityContentAvailable ? "No blocked domains recorded." : DnsActivityAvailabilityMessage;
        public string RecentQueriesEmptyMessage =>
            DnsActivityContentAvailable ? "No recent DNS requests recorded." : DnsActivityAvailabilityMessage;

        [ObservableProperty]
        private string statusMessage =
            "Loading client activity...";

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private bool isPaused;

        [ObservableProperty]
        private string profileNickname = string.Empty;

        [ObservableProperty]
        private string profileCategory = string.Empty;

        [ObservableProperty]
        private string profileNotes = string.Empty;

        [ObservableProperty]
        private bool isFavorite;

        [ObservableProperty]
        private bool monitorAvailability;
        [ObservableProperty]
        private bool isForgettingDevice;
        private bool loadingProfile;
        private bool _disposed;

        public string AvailabilityMonitoringStatus => MonitorAvailability
            ? "On — based on RouterPilot observations while the app is running."
            : "Off";

        public bool CanForgetDevice => !IsForgettingDevice && !IsCurrentlyObserved && _clientProfiles.ContainsKey(ClientKey(_client));

        public string PauseButtonText =>
            IsPaused ? "Resume" : "Pause";

        partial void OnIsLoadingChanged(bool value) => NotifyDnsActivityPresentation();

        partial void OnIsPausedChanged(bool value)
        {
            OnPropertyChanged(nameof(PauseButtonText));
            NotifyDnsActivityPresentation();
        }

        public ClientDetailsViewModel(
            ClientInfo client,
            IRouterManagerProvider routerManagerProvider,
            AdGuardAvailabilityService adGuardAvailabilityService,
            IClientPresenceHistoryService presenceHistory,
            KnownDeviceForgetService knownDeviceForgetService,
            ClientInventoryState clientInventory,
            IEnumerable<DhcpLeaseInfo>? dhcpLeases = null,
            IEnumerable<DhcpReservationInfo>? dhcpReservations = null,
            IEnumerable<PortForwardRuleInfo>? portForwardRules = null)
        {
            _client = client;
            _routerManagerProvider = routerManagerProvider;
            _adGuardAvailabilityService = adGuardAvailabilityService;
            _presenceHistory = presenceHistory;
            _knownDeviceForgetService = knownDeviceForgetService;
            _clientInventory = clientInventory;
            _clientProfileService = new ClientProfileService();
            _clientProfiles = _clientProfileService.Load();

            IEnumerable<DhcpLeaseInfo> availableLeases = dhcpLeases ?? Enumerable.Empty<DhcpLeaseInfo>();
            IEnumerable<DhcpReservationInfo> availableReservations = dhcpReservations ?? Enumerable.Empty<DhcpReservationInfo>();
            _dhcpLease = availableLeases.FirstOrDefault(lease => ClientIdentity.IsMacKey(lease.MacAddress) && ClientIdentity.MacEquals(lease.MacAddress, client.MacAddress))
                ?? availableLeases.FirstOrDefault(lease => SameText(lease.IpAddress, client.IpAddress));
            _dhcpReservation = availableReservations.FirstOrDefault(reservation => ClientIdentity.IsMacKey(reservation.MacAddress) && ClientIdentity.MacEquals(reservation.MacAddress, client.MacAddress))
                ?? availableReservations.FirstOrDefault(reservation => SameText(reservation.IpAddress, client.IpAddress));
            _portForwardRules = (portForwardRules ?? Enumerable.Empty<PortForwardRuleInfo>()).ToArray();

            LoadProfile();
            IsFavorite = _client.IsFavorite;
            RefreshPresencePresentation();
            RefreshRelatedPortForwards();

            _refreshTimer =
                new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };

            _refreshTimer.Tick += RefreshTimer_Tick;
            _clientInventory.Changed += ClientInventoryState_Changed;
        }

        public async Task StartAsync()
        {
            if (_disposed) return;
            await RefreshAsync();

            if (!_disposed && !_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }

        public void Stop()
        {
            _refreshTimer.Stop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _clientInventory.Changed -= ClientInventoryState_Changed;
        }

        private void ClientInventoryState_Changed(object? sender, EventArgs e)
        {
            if (_disposed) return;

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(RefreshLiveConnectionPresentation);
                return;
            }

            RefreshLiveConnectionPresentation();
        }

        private void RefreshLiveConnectionPresentation()
        {
            if (_disposed) return;

            OnPropertyChanged(nameof(IsCurrentlyObserved));
            OnPropertyChanged(nameof(IpAddress));
            OnPropertyChanged(nameof(IpAddressLabel));
            OnPropertyChanged(nameof(IpAddressToolTip));
            OnPropertyChanged(nameof(IsEthernetConnection));
            OnPropertyChanged(nameof(IsWifiConnection));
            OnPropertyChanged(nameof(ConnectionType));
            OnPropertyChanged(nameof(ConnectionLabel));
            OnPropertyChanged(nameof(ConnectionSummary));
            OnPropertyChanged(nameof(ConnectionToolTip));
            OnPropertyChanged(nameof(WifiNetwork));
            OnPropertyChanged(nameof(WifiBand));
            OnPropertyChanged(nameof(WifiInterface));
            OnPropertyChanged(nameof(HasSignal));
            OnPropertyChanged(nameof(SignalQuality));
            OnPropertyChanged(nameof(SignalStrength));
            OnPropertyChanged(nameof(SignalSummary));
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(HealthColour));
            OnPropertyChanged(nameof(HasDhcpLease));
            OnPropertyChanged(nameof(HasDhcpDetails));
            OnPropertyChanged(nameof(DhcpIpAddress));
            OnPropertyChanged(nameof(DhcpAddressLabel));
            OnPropertyChanged(nameof(DhcpLeaseType));
            OnPropertyChanged(nameof(DhcpLeaseRemaining));
            OnPropertyChanged(nameof(DhcpSummary));
            OnPropertyChanged(nameof(HasCurrentReservedAddressMismatch));
            OnPropertyChanged(nameof(HasDhcpAddressPresentation));
            OnPropertyChanged(nameof(DhcpCurrentAddress));
            OnPropertyChanged(nameof(HasDhcpLeaseRemaining));
            RefreshRelatedPortForwards();
            OnPropertyChanged(nameof(CanForgetDevice));
            ForgetDeviceCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            if (_disposed ||
                IsLoading ||
                IsPaused)
            {
                return;
            }

            IsLoading = true;
            StatusMessage =
                "Refreshing client activity...";

            try
            {
                if (_adGuardAvailabilityService.State != AdGuardAvailabilityState.Available)
                {
                    _queryLogReadState = DnsQueryLogReadState.Unavailable;
                    ApplyEntries(new List<QueryLogEntry>());
                    StatusMessage =
                        "DNS activity is unavailable. Router client information remains available.";
                    return;
                }

                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                AdGuardQueryLogReadResult queryLogResult =
                    await routerManager.GetQueryLogResultAsync();

                if (_disposed) return;

                _queryLogReadState = queryLogResult.IsAvailable
                    ? DnsQueryLogReadState.Available
                    : DnsQueryLogReadState.Unavailable;

                ApplyEntries(
                    queryLogResult.Entries
                        .Where(MatchesClient)
                        .ToList());
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                _queryLogReadState = DnsQueryLogReadState.Unavailable;
                NotifyDnsActivityPresentation();
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Client activity refresh",
                    "Unable to load client activity. Check the router connection and try again.");
            }
            finally
            {
                if (!_disposed) IsLoading = false;
            }
        }


        [RelayCommand]
        private void SaveProfile()
        {
            string key = ClientKey(_client);
            if (!_clientProfiles.TryGetValue(key, out ClientProfile? profile))
            {
                profile = new ClientProfile
                {
                    Key = key,
                    FirstSeenUtc = _client.FirstSeenUtc == default
                        ? DateTime.UtcNow
                        : _client.FirstSeenUtc
                };
                _clientProfiles[key] = profile;
            }

            profile.Nickname = ProfileNickname.Trim();
            profile.Category = ProfileCategory.Trim();
            profile.Notes = ProfileNotes.Trim();
            profile.IsFavorite = IsFavorite;
            profile.MonitorAvailability = MonitorAvailability;
            profile.LastSeenUtc = DateTime.UtcNow;

            bool saved = _clientProfileService.Save(_clientProfiles.Values);

            if (!string.IsNullOrWhiteSpace(profile.Nickname))
            {
                _client.Name = profile.Nickname;
            }

            _client.CustomCategory = profile.Category;
            _client.Notes = profile.Notes;
            _client.IsFavorite = IsFavorite;
            _client.MonitorAvailability = MonitorAvailability;

            OnPropertyChanged(nameof(ClientName));
            OnPropertyChanged(nameof(AvailabilityMonitoringStatus));
            if (!saved)
            {
                StatusMessage = "Profile updated for this session, but it could not be saved.";
                return;
            }
            ClientRefreshNotifier.RequestRefresh();
            ClientRefreshNotifier.NotifyProfileStateChanged();
            StatusMessage = $"Profile saved for {ClientName}.";
        }

        [RelayCommand]
        private void ClearProfile()
        {
            string key = ClientKey(_client);
            bool wasFavorite = _client.IsFavorite;

            _clientProfiles.Remove(key);
            if (wasFavorite)
            {
                _clientProfiles[key] = new ClientProfile
                {
                    Key = key,
                    IsFavorite = true,
                    FirstSeenUtc = _client.FirstSeenUtc == default
                        ? DateTime.UtcNow
                        : _client.FirstSeenUtc,
                    LastSeenUtc = DateTime.UtcNow
                };
            }

            ProfileNickname = string.Empty;
            ProfileCategory = string.Empty;
            ProfileNotes = string.Empty;
            IsFavorite = wasFavorite;
            _client.CustomCategory = string.Empty;
            _client.Notes = string.Empty;

            if (!_clientProfileService.Save(_clientProfiles.Values))
            {
                StatusMessage = "Profile updated for this session, but it could not be saved.";
                return;
            }
            OnPropertyChanged(nameof(ClientName));
            ClientRefreshNotifier.RequestRefresh();
            ClientRefreshNotifier.NotifyProfileStateChanged();
            StatusMessage = "Custom client profile cleared.";
        }

        [RelayCommand]
        private void ClearPresenceHistory()
        {
            if (MessageBox.Show("Clear presence history for this device? This does not affect its profile, monitoring, DNS activity, or Timeline.", "Clear presence history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!_presenceHistory.Clear(_client.MacAddress))
            {
                StatusMessage = "RouterPilot could not clear this device's presence history.";
                return;
            }
            RefreshPresencePresentation();
        }

        [RelayCommand(CanExecute = nameof(CanForgetDevice))]
        private async Task ForgetDeviceAsync()
        {
            if (!CanForgetDevice)
            {
                StatusMessage = IsCurrentlyObserved
                    ? "This device is currently on the network. Disconnect it before forgetting its saved history."
                    : "This device is no longer available to forget.";
                return;
            }

            string message = $"Forget {ClientName}?\n\nRouterPilot will remove its saved device history and local preferences from this PC.\n\nThis will not remove or change DHCP reservations, Port Forward rules, router client records, Wi-Fi, AdGuard, or VPN configuration.\n\nIf the device appears again, RouterPilot will detect it as a new device.";
            if (MessageBox.Show(message, "Forget Device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsForgettingDevice = true;
            try
            {
                KnownDeviceForgetResult result = await _knownDeviceForgetService.ForgetAsync(ClientKey(_client));
                if (_disposed) return;
                StatusMessage = result.Message;
                if (result.Success) DeviceForgotten?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Forget Device",
                    "RouterPilot could not forget this device's saved history.");
            }
            finally
            {
                if (!_disposed) IsForgettingDevice = false;
            }
        }

        private void RefreshPresencePresentation()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow, start = now.AddHours(-24);
            List<ClientPresencePeriod> periods = _presenceHistory.GetRecent(_client.MacAddress, start, now).OrderBy(period => period.StartedAt).ToList();
            RecentPresence.Clear();
            DateTimeOffset cursor = start;
            foreach (ClientPresencePeriod period in periods)
            {
                DateTimeOffset periodStart = period.StartedAt < start ? start : period.StartedAt;
                DateTimeOffset periodEnd = (period.EndedAt ?? now) > now ? now : period.EndedAt ?? now;
                if (periodStart > cursor) RecentPresence.Add(new PresenceTimelineItem("Unknown", cursor, periodStart));
                if (periodEnd > periodStart) RecentPresence.Add(new PresenceTimelineItem(period.State.ToString(), periodStart, periodEnd));
                if (periodEnd > cursor) cursor = periodEnd;
            }
            if (cursor < now) RecentPresence.Add(new PresenceTimelineItem("Unknown", cursor, now));
            DailyAvailability.Clear();
            foreach (ClientDailyAvailability day in _presenceHistory.GetDailyAvailability(_client.MacAddress, 7, now)) DailyAvailability.Add(new DailyAvailabilityItem(day));
            OnPropertyChanged(nameof(CurrentPresenceStatus));
            OnPropertyChanged(nameof(CurrentPresenceStatusColour));
            OnPropertyChanged(nameof(ObservedOnlineToday));
            OnPropertyChanged(nameof(CurrentObservedOnline));
            OnPropertyChanged(nameof(LastOfflinePeriod));
        }

        partial void OnSelectedAvailabilityRangeChanged(AvailabilityRange value)
        {
            OnPropertyChanged(nameof(Is24HourRange));
            OnPropertyChanged(nameof(Is7DayRange));
        }

        [RelayCommand] private void Select24HourRange() => SelectedAvailabilityRange = AvailabilityRange.Hours24;
        [RelayCommand] private void Select7DayRange() => SelectedAvailabilityRange = AvailabilityRange.Days7;

        [RelayCommand]
        private void MarkKnown()
        {
            string key = ClientKey(_client);
            if (key.Length != 12 || !_clientProfiles.TryGetValue(key, out ClientProfile? profile))
            {
                StatusMessage = "This device does not have a persistent MAC profile to review.";
                return;
            }

            profile.IsKnown = true;
            profile.NeedsReview = false;
            profile.LastSeenUtc = DateTime.UtcNow;
            _client.NeedsReview = false;
            if (!_clientProfileService.Save(_clientProfiles.Values))
            {
                StatusMessage = "This device could not be marked as known because its profile could not be saved.";
                return;
            }
            OnPropertyChanged(nameof(NeedsReview));
            ClientRefreshNotifier.NotifyProfileStateChanged();
            StatusMessage = $"{ClientName} marked as known.";
        }

        private void LoadProfile()
        {
            string key = ClientKey(_client);
            if (!_clientProfiles.TryGetValue(key, out ClientProfile? profile))
            {
                return;
            }

            loadingProfile = true;
            ProfileNickname = profile.Nickname;
            ProfileCategory = profile.Category;
            ProfileNotes = profile.Notes;
            MonitorAvailability = profile.MonitorAvailability;
            loadingProfile = false;
            _client.NeedsReview = profile.NeedsReview;
            OnPropertyChanged(nameof(NeedsReview));
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

        [RelayCommand]
        private void CopyIp()
        {
            CopyToClipboard(IpAddress, "IP address");
        }

        [RelayCommand]
        private void CopyMac()
        {
            CopyToClipboard(MacAddress, "MAC address");
        }

        private void CopyToClipboard(string? value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-")
            {
                StatusMessage = $"No {label.ToLowerInvariant()} is available to copy.";
                return;
            }

            Clipboard.SetText(value);
            StatusMessage = $"{label} copied to the clipboard.";
        }

        [RelayCommand]
        private void TogglePause()
        {
            IsPaused = !IsPaused;

            OnPropertyChanged(
                nameof(PauseButtonText));

            StatusMessage =
                IsPaused
                    ? "Live updates paused."
                    : "Live updates resumed.";
        }

        private bool MatchesClient(
            QueryLogEntry entry)
        {
            return SameText(
                       entry.ClientAddress,
                       _client.IpAddress) ||
                   SameText(
                       entry.ClientName,
                       _client.Name) ||
                   SameText(
                       entry.Client,
                       _client.IpAddress) ||
                   SameText(
                       entry.Client,
                       _client.Name) ||
                   ContainsIdentifier(
                       entry.Client,
                       _client.IpAddress);
        }

        private void ApplyEntries(
            List<QueryLogEntry> entries)
        {
            RecentQueries.Clear();

            foreach (QueryLogEntry entry in entries
                         .OrderByDescending(entry => entry.Timestamp ?? DateTimeOffset.MinValue)
                         .Take(RecentDnsHistoryLimit))
            {
                RecentQueries.Add(entry);
            }

            ReplaceStats(
                TopDomains,
                BuildDomainStats(
                    entries,
                    blockedOnly: false));

            ReplaceStats(
                TopBlockedDomains,
                BuildDomainStats(
                    entries,
                    blockedOnly: true));

            OnPropertyChanged(nameof(HasRecentQueries));
            OnPropertyChanged(nameof(HasTopDomains));
            OnPropertyChanged(nameof(HasTopBlockedDomains));
            NotifyDnsActivityPresentation();

            StatusMessage =
                entries.Count switch
                {
                    0 =>
                        "No recent DNS activity found for this client.",
                    1 =>
                    "1 recent DNS request loaded.",
                    _ =>
                        entries.Count > RecentDnsHistoryLimit
                            ? $"Showing the latest {RecentDnsHistoryLimit} of {entries.Count} DNS requests."
                            : $"{entries.Count} recent DNS requests loaded."
                };
        }

        private static IEnumerable<DomainStat> BuildDomainStats(
            IEnumerable<QueryLogEntry> entries,
            bool blockedOnly)
        {
            List<DomainStat> results = entries
                .Where(
                    entry =>
                        (!blockedOnly || entry.IsBlocked) &&
                        !string.IsNullOrWhiteSpace(entry.Domain) &&
                        entry.Domain != "-")
                .GroupBy(
                    entry => entry.Domain,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        new DomainStat
                        {
                            Domain = group.Key,
                            Count = group.Count()
                        })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Domain)
                .Take(5)
                .ToList();

            int maximum = results.Count == 0 ? 1 : results.Max(item => item.Count);
            for (int index = 0; index < results.Count; index++)
            {
                DomainStat item = results[index];
                item.Rank = index + 1;
                item.Percentage = Math.Max(4d, item.Count * 100d / maximum);
            }

            return results;
        }

        private static void ReplaceStats(
            ObservableCollection<DomainStat> target,
            IEnumerable<DomainStat> source)
        {
            target.Clear();

            foreach (DomainStat item in source)
            {
                target.Add(item);
            }
        }

        private static bool SameText(
            string? first,
            string? second)
        {
            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(second) ||
                second == "-")
            {
                return false;
            }

            return string.Equals(
                first.Trim(),
                second.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private void NotifyDnsActivityPresentation()
        {
            OnPropertyChanged(nameof(DnsActivityContentAvailable));
            OnPropertyChanged(nameof(DnsActivityContextVisible));
            OnPropertyChanged(nameof(IsDnsSummaryAvailable));
            OnPropertyChanged(nameof(DnsActivityContextHeading));
            OnPropertyChanged(nameof(DnsActivityAvailabilityMessage));
            OnPropertyChanged(nameof(ActivityAvailabilityToolTip));
            OnPropertyChanged(nameof(DnsActivityHeaderText));
            OnPropertyChanged(nameof(DnsTotalQueriesDisplay));
            OnPropertyChanged(nameof(DnsBlockedQueriesDisplay));
            OnPropertyChanged(nameof(DnsBlockRateDisplay));
            OnPropertyChanged(nameof(DnsSummaryCaption));
            OnPropertyChanged(nameof(DnsBlockSummaryCaption));
            OnPropertyChanged(nameof(DnsLastSeenDisplay));
            OnPropertyChanged(nameof(RequestedDomainsEmptyMessage));
            OnPropertyChanged(nameof(BlockedDomainsEmptyMessage));
            OnPropertyChanged(nameof(RecentQueriesEmptyMessage));
        }

        private void RefreshRelatedPortForwards()
        {
            string? currentAddress = IsCurrentlyObserved && HasUsefulAddress(LiveClient?.IpAddress)
                ? LiveClient!.IpAddress.Trim()
                : null;
            string? reservedAddress = _dhcpReservation is { Enabled: true } && HasUsefulAddress(_dhcpReservation.IpAddress)
                ? _dhcpReservation.IpAddress.Trim()
                : null;

            var matches = new List<ClientPortForwardAssociation>();
            foreach (PortForwardRuleInfo rule in _portForwardRules)
            {
                bool targetsCurrentAddress = currentAddress is not null && SameText(rule.DestinationIp, currentAddress);
                bool targetsReservedAddress = reservedAddress is not null && SameText(rule.DestinationIp, reservedAddress);
                if (!targetsCurrentAddress && !targetsReservedAddress) continue;

                string addressContext = targetsCurrentAddress && targetsReservedAddress
                    ? "Targets current and reserved address"
                    : targetsCurrentAddress
                        ? "Targets current address"
                        : "Targets reserved address";
                matches.Add(new ClientPortForwardAssociation(rule, addressContext));
            }

            _relatedPortForwardCount = matches.Count;
            RelatedPortForwards.Clear();
            foreach (ClientPortForwardAssociation match in matches.Take(5))
            {
                RelatedPortForwards.Add(match);
            }

            OnPropertyChanged(nameof(HasRelatedPortForwards));
            OnPropertyChanged(nameof(RelatedPortForwardCount));
            OnPropertyChanged(nameof(AdditionalRelatedPortForwardCount));
            OnPropertyChanged(nameof(HasAdditionalRelatedPortForwards));
            OnPropertyChanged(nameof(RelatedPortForwardSummary));
        }

        private static bool HasUsefulAddress(string? address) =>
            !string.IsNullOrWhiteSpace(address) &&
            !string.Equals(address, "-", StringComparison.Ordinal);

        private static string FormatDuration(TimeSpan duration) => duration < TimeSpan.FromMinutes(1) ? "< 1 min" : duration < TimeSpan.FromHours(1) ? $"{(int)duration.TotalMinutes} min" : $"{(int)duration.TotalHours}h {duration.Minutes}m";

        public sealed record PresenceTimelineItem(string State, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
        {
            public double Width => Math.Max(4, (EndedAt - StartedAt).TotalHours / 24 * 360);
            public string ToolTip => $"{State}\n{StartedAt.LocalDateTime:t}–{EndedAt.LocalDateTime:t}\n{FormatDuration(EndedAt - StartedAt)}";
        }
        public sealed record DailyAvailabilityItem(ClientDailyAvailability Value)
        {
            public string Day => Value.DayStart.LocalDateTime.ToString("ddd dd MMM").ToUpperInvariant();
            public string Summary => Value.Observed == TimeSpan.Zero ? "No observation" : $"Online {FormatDuration(Value.Online)} • Offline {FormatDuration(Value.Offline)} • Unobserved {FormatDuration(Value.Unobserved)}";
            public string Percentage => Value.ObservedAvailabilityPercent is null ? "No observation" : $"{Value.ObservedAvailabilityPercent.Value:0}%";
            public string ToolTip => Value.ObservedAvailabilityPercent is null ? $"{Value.DayStart.LocalDateTime:D}\nNo observation" : $"{Value.DayStart.LocalDateTime:D}\nObserved online: {FormatDuration(Value.Online)}\nObserved offline: {FormatDuration(Value.Offline)}\nUnobserved: {FormatDuration(Value.Unobserved)}\nObserved availability: {Value.ObservedAvailabilityPercent.Value:0.0}%";
        }
        public sealed class ClientPortForwardAssociation
        {
            public ClientPortForwardAssociation(PortForwardRuleInfo rule, string addressContext)
            {
                Rule = rule;
                AddressContext = addressContext;
            }

            public PortForwardRuleInfo Rule { get; }
            public string AddressContext { get; }
            public string RuleName => string.IsNullOrWhiteSpace(Rule.Name) ? "Unnamed rule" : Rule.Name;
            public string ProtocolAndPort
            {
                get
                {
                    string protocol = string.IsNullOrWhiteSpace(Rule.Protocol) ? "Protocol not reported" : Rule.Protocol.ToUpperInvariant();
                    string externalPort = string.IsNullOrWhiteSpace(Rule.ExternalPort) ? "Port not reported" : Rule.ExternalPort;
                    return string.IsNullOrWhiteSpace(Rule.InternalPort) || SameText(Rule.ExternalPort, Rule.InternalPort)
                        ? $"{protocol} {externalPort}"
                        : $"{protocol} {externalPort} → {Rule.InternalPort}";
                }
            }
            public string EnabledDisplay => Rule.Enabled ? "Enabled" : "Disabled";
        }
        public enum AvailabilityRange { Hours24, Days7 }

        partial void OnMonitorAvailabilityChanged(bool value)
        {
            OnPropertyChanged(nameof(AvailabilityMonitoringStatus));
            if (loadingProfile) return;
            string key = ClientKey(_client);
            if (key.Length != 12) return;
            if (!_clientProfiles.TryGetValue(key, out ClientProfile? profile))
            {
                profile = new ClientProfile { Key = key, FirstSeenUtc = _client.FirstSeenUtc == default ? DateTime.UtcNow : _client.FirstSeenUtc };
                _clientProfiles[key] = profile;
            }
            profile.MonitorAvailability = value;
            profile.LastSeenUtc = DateTime.UtcNow;
            _client.MonitorAvailability = value;
            if (_clientProfileService.Save(_clientProfiles.Values))
            {
                ClientRefreshNotifier.RequestRefresh();
            }
            else
            {
                StatusMessage = "Availability monitoring preference changed for this session, but it could not be saved.";
            }
        }

        partial void OnIsForgettingDeviceChanged(bool value)
        {
            OnPropertyChanged(nameof(CanForgetDevice));
            ForgetDeviceCommand.NotifyCanExecuteChanged();
        }

        private static string FormatObserved(DateTime observedUtc)
        {
            return observedUtc == default
                ? "Not observed yet"
                : observedUtc.ToLocalTime().ToString("dd MMM yyyy • HH:mm");
        }


        private static bool ContainsIdentifier(
            string? displayValue,
            string? identifier)
        {
            if (string.IsNullOrWhiteSpace(displayValue) ||
                string.IsNullOrWhiteSpace(identifier) ||
                identifier == "-")
            {
                return false;
            }

            return displayValue.Contains(
                identifier.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private async void RefreshTimer_Tick(
            object? sender,
            EventArgs e)
        {
            if (_disposed) return;
            await RefreshAsync();
        }
    }
}
