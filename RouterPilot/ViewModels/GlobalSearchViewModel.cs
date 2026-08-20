using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels
{
    public partial class GlobalSearchViewModel : ObservableObject
    {
        private const int ResultLimit = 12;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly List<ClientInfo> _clients = new();
        private readonly List<QueryLogEntry> _logs = new();
        private DashboardViewModel? _dashboard;

        public ObservableCollection<GlobalSearchResult> Results { get; } = new();

        [ObservableProperty]
        private string searchText = string.Empty;

        [ObservableProperty]
        private string statusMessage =
            "Enter at least two characters to search clients and recent DNS activity.";

        [ObservableProperty]
        private bool isLoading;

        public GlobalSearchViewModel(
            IRouterManagerProvider routerManagerProvider)
        {
            _routerManagerProvider = routerManagerProvider;
        }

        public void Attach(DashboardViewModel dashboard)
        {
            _dashboard = dashboard;
            ApplyUnifiedSearch();
        }

        [RelayCommand]
        public async Task RefreshIndexAsync()
        {
            if (_dashboard is not null)
            {
                ApplyUnifiedSearch();
                return;
            }
            if (IsLoading)
            {
                return;
            }

            IsLoading = true;
            StatusMessage = "Refreshing search index...";

            try
            {
                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                List<ClientInfo> clients =
                    await routerManager.GetAdGuardClientsAsync();

                List<QueryLogEntry> logs =
                    await routerManager.GetQueryLogAsync();

                _clients.Clear();
                _clients.AddRange(clients);

                _logs.Clear();
                _logs.AddRange(logs);

                ApplySearch();

                StatusMessage =
                    $"Search index updated: {_clients.Count} clients and " +
                    $"{_logs.Count} recent queries.";
            }
            catch (Exception ex)
            {
                StatusMessage =
                    "Unable to refresh global search: " +
                    ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        partial void OnSearchTextChanged(string value)
        {
            if (_dashboard is not null) ApplyUnifiedSearch();
            else ApplySearch();
        }

        private void ApplyUnifiedSearch()
        {
            Results.Clear();
            string search = SearchText.Trim();
            if (search.Length < 2) { StatusMessage = "Enter at least two characters to search RouterPilot."; return; }
            if (_dashboard is null) { StatusMessage = "Search is not ready."; return; }
            string mac = LanClientClassifier.NormalizeMac(search);
            var candidates = new List<GlobalSearchResult>();
            candidates.AddRange(_dashboard.DhcpLeases.Select(item => Result("Client", Name(item.ClientName, item.Hostname), $"Client • {item.IpAddress}", $"{item.ClientName} {item.Hostname} {item.IpAddress} {LanClientClassifier.NormalizeMac(item.MacAddress)} {item.DeviceType}", "clients", LanClientClassifier.NormalizeMac(item.MacAddress), "#3367D6")));
            foreach (PortForwardRuleInfo rule in _dashboard.PortForwardRules)
            {
                string terms = PortForwardTerms(rule);
                GlobalSearchResult candidate = Result("Port Forward", string.IsNullOrWhiteSpace(rule.Name) ? "Port forward" : rule.Name, $"Port Forward • {rule.Protocol.ToUpperInvariant()} • {rule.ExternalPort}", terms, "port-forward", rule.Id, "#B26A00");
                candidates.Add(candidate);
            }
            candidates.AddRange(_dashboard.DhcpReservations.Select(item => Result("DHCP Reservation", Name(item.Hostname), $"DHCP Reservation • {item.IpAddress}", $"{item.Hostname} {item.IpAddress} {LanClientClassifier.NormalizeMac(item.MacAddress)}", "dhcp", LanClientClassifier.NormalizeMac(item.MacAddress), "#16803C")));
            candidates.AddRange(_dashboard.WifiNetworks.Select(item => Result("Wi-Fi Network", item.Ssid, $"Wi-Fi Network • {item.Band}", $"{item.Ssid} {item.Band} {item.Interface} {item.GuestClassificationDisplay}", "wifi", $"{item.Radio}:{item.Interface}:{item.Ssid}", "#6A4FB3")));
            candidates.AddRange(Pages.Select(item => Result("Page", item.Title, item.Subtitle, item.Terms, item.Target, item.Target, "#687386")));
            List<GlobalSearchResult> matches = candidates.Where(item => Match(item.SearchTerms, search, mac)).GroupBy(item => item.Category + ":" + item.EntityId, StringComparer.OrdinalIgnoreCase).Select(item => item.First()).OrderByDescending(item => Rank(item, search, mac)).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (GlobalSearchResult result in matches.Take(ResultLimit)) Results.Add(result);
            StatusMessage = Results.Count == 0 ? "No results." : $"{Results.Count} results found.";
        }

        private static GlobalSearchResult Result(string type, string title, string subtitle, string terms, string target, string id, string colour) => new() { Category = type, Title = title, Subtitle = subtitle, Detail = string.Empty, BadgeText = type, BadgeColour = colour, SearchTerms = terms, NavigationTarget = target, EntityId = id };
        private static bool Match(string terms, string search, string mac) => terms.Contains(search, StringComparison.OrdinalIgnoreCase) || terms.Contains(NormalisePortToken(search), StringComparison.OrdinalIgnoreCase) || (mac.Length >= 2 && terms.Contains(mac, StringComparison.OrdinalIgnoreCase));
        private static int Rank(GlobalSearchResult item, string search, string mac) => item.Category == "Port Forward" && item.SearchTerms.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("port:" + NormalisePortToken(search), StringComparer.OrdinalIgnoreCase) ? 6 : string.Equals(item.Title, search, StringComparison.OrdinalIgnoreCase) ? 5 : item.Title.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 4 : item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ? 3 : item.SearchTerms.Contains(mac, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        private static string Name(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "-" && !string.Equals(value, "Unknown device", StringComparison.OrdinalIgnoreCase)) ?? "Device";
        private static string PortForwardTerms(PortForwardRuleInfo rule) => string.Join(' ', new[] { rule.Name, rule.Protocol, rule.DestinationIp }.Concat(PortTokens(rule.ExternalPort)).Concat(PortTokens(rule.InternalPort)));
        private static IEnumerable<string> PortTokens(string? value)
        {
            string full = NormalisePortToken(value);
            if (full.Length == 0) yield break;
            yield return full;
            yield return "port:" + full;
            foreach (string endpoint in full.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return endpoint;
                yield return "port:" + endpoint;
            }
        }
        private static string NormalisePortToken(string? value) => (value ?? string.Empty).Trim().Replace('–', '-').Replace('—', '-').Replace(':', '-');
        private static readonly (string Title, string Subtitle, string Terms, string Target)[] Pages = [("Overview", "Page", "overview home dashboard", "overview"), ("Clients", "Page", "clients devices", "clients"), ("Wi-Fi", "Network page", "wifi wi-fi wireless", "wifi"), ("Port Forwarding", "Network page", "port forwarding port forward nat", "port-forward"), ("DHCP", "Network page", "dhcp reservations leases", "dhcp"), ("VPN", "Network page", "vpn", "vpn"), ("DNS Filtering", "Page", "dns adguard protection", "protection"), ("Timeline", "Page", "timeline logs history", "timeline"), ("System Health", "Page", "health system health", "health"), ("Analytics", "Page", "analytics diagnostics", "analytics"), ("Settings", "Page", "settings", "settings")];

        private void ApplySearch()
        {
            Results.Clear();

            string search =
                SearchText.Trim();

            if (search.Length < 2)
            {
                StatusMessage =
                    "Enter at least two characters to search clients and recent DNS activity.";
                return;
            }

            IEnumerable<GlobalSearchResult> clients =
                _clients
                    .Where(client =>
                        Contains(client.Name, search) ||
                        Contains(client.IpAddress, search) ||
                        Contains(client.MacAddress, search))
                    .Take(25)
                    .Select(client =>
                        new GlobalSearchResult
                        {
                            Category = "Client",
                            Title = client.Name,
                            Subtitle =
                                $"{client.IpAddress} · {client.MacAddress}",
                            Detail =
                                $"{client.TotalQueries:N0} queries · " +
                                $"{client.BlockedQueries:N0} blocked · " +
                                $"{client.BlockRate:F1}% block rate",
                            BadgeText = "Client",
                            BadgeColour = "#3367D6"
                        });

            IEnumerable<GlobalSearchResult> domains =
                _logs
                    .Where(entry =>
                        Contains(entry.Domain, search) ||
                        Contains(entry.Client, search))
                    .GroupBy(
                        entry => entry.Domain,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        int total = group.Count();
                        int blocked =
                            group.Count(entry => entry.IsBlocked);

                        return new GlobalSearchResult
                        {
                            Category = "Domain",
                            Title = group.Key,
                            Subtitle =
                                $"{total:N0} recent queries · " +
                                $"{blocked:N0} blocked",
                            Detail =
                                string.Join(
                                    ", ",
                                    group
                                        .Select(entry => entry.Client)
                                        .Where(value =>
                                            !string.IsNullOrWhiteSpace(value))
                                        .Distinct(
                                            StringComparer.OrdinalIgnoreCase)
                                        .Take(5)),
                            BadgeText =
                                blocked == total && total > 0
                                    ? "Blocked"
                                    : blocked == 0
                                        ? "Allowed"
                                        : "Mixed",
                            BadgeColour =
                                blocked == total && total > 0
                                    ? "#C62828"
                                    : blocked == 0
                                        ? "#16803C"
                                        : "#B26A00"
                        };
                    })
                    .Take(50);

            foreach (GlobalSearchResult result in
                     clients.Concat(domains)
                            .OrderBy(result => result.Category)
                            .ThenBy(result => result.Title))
            {
                Results.Add(result);
            }

            StatusMessage =
                Results.Count == 0
                    ? "No matching clients or domains found."
                    : $"{Results.Count} results found.";
        }

        private static bool Contains(
            string? value,
            string search)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Contains(
                       search,
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
