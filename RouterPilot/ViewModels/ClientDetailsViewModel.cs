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
    public partial class ClientDetailsViewModel : ObservableObject
    {
        private const int RecentDnsHistoryLimit = 50;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly AdGuardAvailabilityService _adGuardAvailabilityService;
        private readonly ClientProfileService _clientProfileService;
        private readonly IClientPresenceHistoryService _presenceHistory;
        private readonly Dictionary<string, ClientProfile> _clientProfiles;
        private readonly DispatcherTimer _refreshTimer;
        private readonly ClientInfo _client;
        private readonly DhcpLeaseInfo? _dhcpLease;
        private readonly DhcpReservationInfo? _dhcpReservation;

        public ObservableCollection<QueryLogEntry> RecentQueries { get; } =
            new();

        public ObservableCollection<DomainStat> TopDomains { get; } =
            new();

        public ObservableCollection<DomainStat> TopBlockedDomains { get; } =
            new();
        public ObservableCollection<PresenceTimelineItem> RecentPresence { get; } = new();

        public string ClientName =>
            string.IsNullOrWhiteSpace(ProfileNickname)
                ? _client.Name
                : ProfileNickname;
        public string IpAddress => _client.IpAddress;
        public string MacAddress => _client.MacAddress;
        public string LastSeen => _client.LastSeen;
        public string TotalQueriesDisplay => _client.TotalQueriesDisplay;
        public string BlockedQueriesDisplay => _client.BlockedQueriesDisplay;
        public string BlockRateDisplay => _client.BlockRateDisplay;
        public bool IsEthernetConnection => _client.IsEthernetConnection;
        public bool IsWifiConnection => _client.IsWifiConnection;
        public string ConnectionType => IsEthernetConnection ? "Ethernet" : "Wi-Fi";
        public string ConnectionSummary => _client.ConnectionSummary;
        public string WifiNetwork => _client.WifiNetwork;
        public string WifiBand => _client.ConnectionType;
        public string WifiInterface => _client.LiveInterface;
        public bool HasSignal => _client.HasSignalSummary;
        public string SignalQuality => _client.SignalQuality;
        public string SignalStrength => _client.SignalStrength;
        public string SignalSummary => _client.SignalSummary;
        public string HealthText => _client.HealthText;
        public string HealthColour => _client.HealthColour;
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

        public bool HasDhcpDetails => _dhcpLease is not null || _dhcpReservation is not null;
        public string DhcpIpAddress => _dhcpReservation?.IpAddress ?? _dhcpLease?.IpAddress ?? _client.IpAddress;
        public string DhcpLeaseType => _dhcpReservation is not null || _dhcpLease?.IsStatic == true ? "Reserved" : "Dynamic";
        public string DhcpLeaseRemaining => _dhcpLease?.RemainingLease ?? "Not reported";
        public string DhcpReservation => _dhcpReservation is null ? "No" : _dhcpReservation.Enabled ? "Yes" : "Disabled";
        public string DhcpScope => _dhcpLease?.ScopeDisplay ?? _dhcpReservation?.ScopeDisplay ?? "Not reported";
        public string DhcpSummary => $"{DhcpLeaseType} • {DhcpScope}";
        public bool HasDhcpLeaseRemaining =>
            !string.IsNullOrWhiteSpace(DhcpLeaseRemaining) &&
            !string.Equals(DhcpLeaseRemaining, "N/A", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DhcpLeaseRemaining, "Not reported", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DhcpLeaseRemaining, "Static", StringComparison.OrdinalIgnoreCase);

        public bool HasRecentQueries => RecentQueries.Count > 0;
        public bool HasTopDomains => TopDomains.Count > 0;
        public bool HasTopBlockedDomains => TopBlockedDomains.Count > 0;

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
        private bool loadingProfile;

        public string AvailabilityMonitoringStatus => MonitorAvailability
            ? "On — based on RouterPilot observations while the app is running."
            : "Off";

        public string PauseButtonText =>
            IsPaused ? "Resume" : "Pause";

        public ClientDetailsViewModel(
            ClientInfo client,
            IRouterManagerProvider routerManagerProvider,
            AdGuardAvailabilityService adGuardAvailabilityService,
            IClientPresenceHistoryService presenceHistory,
            IEnumerable<DhcpLeaseInfo>? dhcpLeases = null,
            IEnumerable<DhcpReservationInfo>? dhcpReservations = null)
        {
            _client = client;
            _routerManagerProvider = routerManagerProvider;
            _adGuardAvailabilityService = adGuardAvailabilityService;
            _presenceHistory = presenceHistory;
            _clientProfileService = new ClientProfileService();
            _clientProfiles = _clientProfileService.Load();

            IEnumerable<DhcpLeaseInfo> availableLeases = dhcpLeases ?? Enumerable.Empty<DhcpLeaseInfo>();
            IEnumerable<DhcpReservationInfo> availableReservations = dhcpReservations ?? Enumerable.Empty<DhcpReservationInfo>();
            _dhcpLease = availableLeases.FirstOrDefault(lease => SameMac(lease.MacAddress, client.MacAddress))
                ?? availableLeases.FirstOrDefault(lease => SameText(lease.IpAddress, client.IpAddress));
            _dhcpReservation = availableReservations.FirstOrDefault(reservation => SameMac(reservation.MacAddress, client.MacAddress))
                ?? availableReservations.FirstOrDefault(reservation => SameText(reservation.IpAddress, client.IpAddress));

            LoadProfile();
            IsFavorite = _client.IsFavorite;
            RefreshPresencePresentation();

            _refreshTimer =
                new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };

            _refreshTimer.Tick += RefreshTimer_Tick;
        }

        public async Task StartAsync()
        {
            await RefreshAsync();

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
        public async Task RefreshAsync()
        {
            if (IsLoading ||
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
                    ApplyEntries(new List<QueryLogEntry>());
                    StatusMessage =
                        "DNS activity is unavailable. Router client information remains available.";
                    return;
                }

                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                List<QueryLogEntry> entries =
                    await routerManager.GetQueryLogAsync();

                ApplyEntries(
                    entries
                        .Where(MatchesClient)
                        .ToList());
            }
            catch (Exception ex)
            {
                StatusMessage =
                    "Unable to load client activity: " +
                    ex.Message;
            }
            finally
            {
                IsLoading = false;
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

            _clientProfileService.Save(_clientProfiles.Values);

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
            ClientRefreshNotifier.RequestRefresh();
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

            _clientProfileService.Save(_clientProfiles.Values);
            OnPropertyChanged(nameof(ClientName));
            ClientRefreshNotifier.RequestRefresh();
            StatusMessage = "Custom client profile cleared.";
        }

        [RelayCommand]
        private void ClearPresenceHistory()
        {
            if (MessageBox.Show("Clear presence history for this device? This does not affect its profile, monitoring, DNS activity, or Timeline.", "Clear presence history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _presenceHistory.Clear(_client.MacAddress);
            RefreshPresencePresentation();
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
            OnPropertyChanged(nameof(CurrentPresenceStatus));
            OnPropertyChanged(nameof(CurrentPresenceStatusColour));
            OnPropertyChanged(nameof(ObservedOnlineToday));
            OnPropertyChanged(nameof(CurrentObservedOnline));
            OnPropertyChanged(nameof(LastOfflinePeriod));
        }

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
            _clientProfileService.Save(_clientProfiles.Values);
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
                return new string(
                    client.MacAddress
                        .Where(char.IsLetterOrDigit)
                        .Select(char.ToUpperInvariant)
                        .ToArray());
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

        private static string FormatDuration(TimeSpan duration) => duration < TimeSpan.FromMinutes(1) ? "< 1 min" : duration < TimeSpan.FromHours(1) ? $"{(int)duration.TotalMinutes} min" : $"{(int)duration.TotalHours}h {duration.Minutes}m";

        public sealed record PresenceTimelineItem(string State, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
        {
            public double Width => Math.Max(4, (EndedAt - StartedAt).TotalHours / 24 * 360);
            public string ToolTip => $"{State}\n{StartedAt.LocalDateTime:t}–{EndedAt.LocalDateTime:t}\n{FormatDuration(EndedAt - StartedAt)}";
        }

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
            _clientProfileService.Save(_clientProfiles.Values);
            ClientRefreshNotifier.RequestRefresh();
        }

        private static bool SameMac(string? first, string? second)
        {
            string normalisedFirst = new string((first ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            string normalisedSecond = new string((second ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

            return normalisedFirst.Length == 12 &&
                string.Equals(normalisedFirst, normalisedSecond, StringComparison.Ordinal);
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
            await RefreshAsync();
        }
    }
}
