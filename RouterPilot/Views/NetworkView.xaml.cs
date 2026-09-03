using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class NetworkView : UserControl
    {
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly IDhcpReservationService _dhcpReservationService;
        private readonly DhcpReservationValidator _dhcpReservationValidator;
        private readonly IPortForwardService _portForwardService;
        private readonly IPublicIpService _publicIpService;
        private NetworkHealthView? _networkHealthView;
        private DashboardViewModel? _portForwardRulesOwner;
        private bool _maintenanceInProgress;
        private bool _showPortForwardAttentionOnly;
        private CancellationTokenSource? _diagnosticsCancellation;
        private bool _diagnosticsRunning;
        private CancellationTokenSource? _qualityCancellation;
        private bool _qualityRunning;
        private bool _advancedRefreshing;
        private bool _mapSelectionFromInput;
        private readonly List<InternetQualityRun> _qualityHistory = new();
        private InternetQualityRun? _latestQualityRun;

        private sealed record InternetQualityRun(DateTimeOffset Timestamp, PingSummary? Gateway, PingSummary? Internet, long? DnsMs, bool DnsSucceeded);
        private sealed record PingSummary(int Responses, int Attempts, double? MinMs, double? AverageMs, double? MaxMs, double? VariationMs);

        public NetworkView()
        {
            InitializeComponent();
            _routerManagerProvider = ((App)Application.Current).Services
                .GetRequiredService<IRouterManagerProvider>();
            _dhcpReservationService = ((App)Application.Current).Services
                .GetRequiredService<IDhcpReservationService>();
            _dhcpReservationValidator = ((App)Application.Current).Services
                .GetRequiredService<DhcpReservationValidator>();
            _portForwardService = ((App)Application.Current).Services.GetRequiredService<IPortForwardService>();
            _publicIpService = ((App)Application.Current).Services.GetRequiredService<IPublicIpService>();
            Loaded += NetworkView_Loaded;
            Unloaded += NetworkView_Unloaded;
            DataContextChanged += NetworkView_DataContextChanged;
            UpdateNetworkTabVisibility();
        }

        public void NavigateToSection(string section)
        {
            NetworkTabs.SelectedIndex = section switch
            {
                "map" => 1,
                "wifi" => 2,
                "dhcp" => 3,
                "port-forward" => 4,
                "health" => 5,
                "internet-quality" => 6,
                _ => 0
            };
            UpdateNetworkTabVisibility();
        }

        public void NavigateToDhcpReservation(string? deviceIdentity)
        {
            NavigateToSection("dhcp");
            string macKey = ClientIdentity.NormalizeMac(deviceIdentity);
            if (!ClientIdentity.IsMacKey(macKey)) return;

            SelectAndBringIntoView(
                DhcpReservationsList,
                () => (DataContext as DashboardViewModel)?.DhcpReservations
                    .FirstOrDefault(item => ClientIdentity.MacEquals(item.MacAddress, macKey)));
        }

        public void NavigateToPortForwardRule(string? ruleId)
        {
            NavigateToSection("port-forward");
            ShowAllPortForwardRules();
            if (string.IsNullOrWhiteSpace(ruleId)) return;

            SelectAndBringIntoView(
                PortForwardRulesList,
                () => (DataContext as DashboardViewModel)?.PortForwardRules
                    .FirstOrDefault(item => string.Equals(item.Id, ruleId, StringComparison.Ordinal)));
        }

        private async void NetworkView_Loaded(object sender, RoutedEventArgs e)
        {
            AttachPortForwardRuleViewUpdates();
            RefreshPortForwardRuleFilter();
            UpdateMapSummary();
            await RefreshAdvancedTelemetryAsync();
        }

        private async Task RefreshAdvancedTelemetryAsync()
        {
            if (_advancedRefreshing || DataContext is not DashboardViewModel dashboard) return;
            _advancedRefreshing = true;
            try
            {
                RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync();
                dashboard.AdvancedRouterSnapshot = await manager.GetRouterAdvancedTelemetryAsync();
            }
            catch { dashboard.AdvancedRouterSnapshot = RouterAdvancedSnapshot.Unknown; }
            finally { _advancedRefreshing = false; }
        }

        private void NetworkView_Unloaded(object sender, RoutedEventArgs e)
        {
            _diagnosticsCancellation?.Cancel();
            _qualityCancellation?.Cancel();
            DetachPortForwardRuleViewUpdates();
        }

        private async void RunInternetQuality_Click(object sender, RoutedEventArgs e)
        {
            if (_qualityRunning) return;
            _qualityRunning = true;
            RunInternetQualityButton.IsEnabled = false;
            CancelInternetQualityButton.IsEnabled = true;
            _qualityCancellation?.Cancel();
            _qualityCancellation?.Dispose();
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(25));
            _qualityCancellation = cts;
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(cts.Token);
                string gateway = (DataContext as DashboardViewModel)?.Gateway ?? string.Empty;
                PingSummary? gatewaySummary = string.IsNullOrWhiteSpace(gateway) || gateway == "-" ? null : await MeasurePingAsync(router, gateway, cts.Token);
                PingSummary? internetSummary = await MeasurePingAsync(router, "1.1.1.1", cts.Token);
                Stopwatch dnsTimer = Stopwatch.StartNew();
                string dns = await router.DnsLookupAsync("example.com", cts.Token);
                dnsTimer.Stop();
                cts.Token.ThrowIfCancellationRequested();
                bool dnsOk = dns.Contains("Name:", StringComparison.OrdinalIgnoreCase) && dns.Contains("Address:", StringComparison.OrdinalIgnoreCase);
                InternetQualityRun run = new(DateTimeOffset.Now, gatewaySummary, internetSummary, dnsOk ? dnsTimer.ElapsedMilliseconds : null, dnsOk);
                _latestQualityRun = run;
                _qualityHistory.Add(run);
                if (_qualityHistory.Count > 10) _qualityHistory.RemoveAt(0);
                InternetQualityStatusText.Text = "Internet quality test completed.";
                InternetQualityDetailsText.Text = FormatQuality(run);
                CopyInternetQualityButton.IsEnabled = true;
                InternetQualityHistoryList.ItemsSource = null;
                InternetQualityHistoryList.ItemsSource = _qualityHistory.AsEnumerable().Reverse().Select(item => $"{item.Timestamp.LocalDateTime:g} • {FormatMetric(item.Internet?.AverageMs)} Internet • DNS {FormatMetric(item.DnsMs)}").ToList();
            }
            catch (OperationCanceledException)
            {
                InternetQualityStatusText.Text = "Internet quality test timed out or was cancelled.";
            }
            catch (Exception ex)
            {
                InternetQualityStatusText.Text = OperationFailurePolicy.UserMessage(ex, "Internet quality", "Internet quality diagnostics are currently unavailable.");
            }
            finally
            {
                if (ReferenceEquals(_qualityCancellation, cts)) _qualityCancellation = null;
                _qualityRunning = false;
                RunInternetQualityButton.IsEnabled = true;
                CancelInternetQualityButton.IsEnabled = false;
            }
        }

        private async void CancelInternetQuality_Click(object sender, RoutedEventArgs e) => _qualityCancellation?.Cancel();

        private void CopyInternetQuality_Click(object sender, RoutedEventArgs e)
        {
            if (_latestQualityRun is null) return;
            Clipboard.SetText("RouterPilot Internet Quality Report\n\nGenerated: " + _latestQualityRun.Timestamp.LocalDateTime.ToString("g") + "\n" + FormatQuality(_latestQualityRun) + "\n\nThis is a short manual diagnostic sample, not continuous monitoring. Addresses, client identities, SSIDs, endpoints and credentials are omitted.");
        }

        private static async Task<PingSummary?> MeasurePingAsync(RouterManager router, string target, CancellationToken token)
        {
            List<double> values = new();
            for (int i = 0; i < 3; i++)
            {
                string output = await router.PingAsync(target, token);
                Match match = Regex.Match(output, @"=\s*[^/]+/(?<avg>[\d.]+)/(?<max>[\d.]+)", RegexOptions.IgnoreCase);
                Match minMatch = Regex.Match(output, @"=\s*(?<min>[\d.]+)/", RegexOptions.IgnoreCase);
                if (match.Success && double.TryParse(match.Groups["avg"].Value, out double average)) values.Add(average);
            }
            if (values.Count == 0) return null;
            double variation = values.Count > 1 ? values.Skip(1).Select((value, index) => Math.Abs(value - values[index])).Average() : double.NaN;
            return new PingSummary(values.Count * 4, 12, values.Min(), values.Average(), values.Max(), double.IsNaN(variation) ? null : variation);
        }

        private static string FormatQuality(InternetQualityRun run) =>
            $"Gateway: {(run.Gateway is null ? "—" : FormatMetric(run.Gateway.AverageMs) + " average")}\n" +
            $"Internet: {(run.Internet is null ? "Unavailable" : $"{run.Internet.Responses}/{run.Internet.Attempts} responses • {FormatMetric(run.Internet.MinMs)} min • {FormatMetric(run.Internet.AverageMs)} average • {FormatMetric(run.Internet.MaxMs)} max • variation {FormatMetric(run.Internet.VariationMs)}")}\n" +
            $"DNS resolution: {(run.DnsSucceeded ? $"Pass • {FormatMetric(run.DnsMs)}" : "Unavailable")}";

        private static string FormatMetric(double? value) => value is null ? "—" : $"{value.Value:0.#} ms";
        private static string FormatMetric(long? value) => value is null ? "—" : $"{value.Value} ms";

        private async void RunNetworkDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            if (_diagnosticsRunning) return;
            _diagnosticsRunning = true;
            RunNetworkDiagnosticsButton.IsEnabled = false;
            NetworkDiagnosticsStatusText.Text = "Running bounded connectivity checks...";
            NetworkDiagnosticsDetailsText.Text = string.Empty;
            _diagnosticsCancellation?.Cancel();
            _diagnosticsCancellation?.Dispose();
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            _diagnosticsCancellation = cts;
            string profileId = ((App)Application.Current).Services.GetRequiredService<IActiveRouterContext>().CurrentProfileId;
            long contextVersion = ((App)Application.Current).Services.GetRequiredService<IActiveRouterContext>().Version;
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(cts.Token);
                Stopwatch dnsTimer = Stopwatch.StartNew();
                string dns = await router.DnsLookupAsync("example.com", cts.Token);
                dnsTimer.Stop();
                string ping = await router.PingAsync("1.1.1.1", cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                IActiveRouterContext active = ((App)Application.Current).Services.GetRequiredService<IActiveRouterContext>();
                if (profileId != active.CurrentProfileId || contextVersion != active.Version) return;
                bool dnsOk = dns.Contains("Name:", StringComparison.OrdinalIgnoreCase) &&
                             dns.Contains("Address:", StringComparison.OrdinalIgnoreCase);
                Match loss = Regex.Match(ping, @"(?<loss>\d+(?:\.\d+)?)%\s*packet loss", RegexOptions.IgnoreCase);
                Match avg = Regex.Match(ping, @"=\s*[^/]+/(?<avg>[\d.]+)/", RegexOptions.IgnoreCase);
                string lossText = loss.Success ? $"Observed loss: {loss.Groups["loss"].Value}%" : "Observed loss: —";
                string latencyText = avg.Success ? $"Latency average: {avg.Groups["avg"].Value} ms" : "Latency average: —";
                NetworkDiagnosticsStatusText.Text = dnsOk ? "Internet/DNS checks completed." : "Internet check completed; DNS resolution was not confirmed.";
                NetworkDiagnosticsDetailsText.Text = $"DNS resolution: {(dnsOk ? "Working" : "Unavailable")} ({dnsTimer.ElapsedMilliseconds} ms)  •  {latencyText}  •  {lossText}";
            }
            catch (OperationCanceledException)
            {
                NetworkDiagnosticsStatusText.Text = "Network diagnostics timed out or were cancelled.";
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsStatusText.Text = OperationFailurePolicy.UserMessage(ex, "Network diagnostics", "Network diagnostics are currently unavailable.");
            }
            finally
            {
                if (ReferenceEquals(_diagnosticsCancellation, cts)) _diagnosticsCancellation = null;
                _diagnosticsRunning = false;
                RunNetworkDiagnosticsButton.IsEnabled = true;
            }
        }

        private void NetworkView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            DetachPortForwardRuleViewUpdates();
            AttachPortForwardRuleViewUpdates();
            RefreshPortForwardRuleFilter();
            UpdateMapSummary();
        }

        private void AttachPortForwardRuleViewUpdates()
        {
            if (_portForwardRulesOwner is not null || DataContext is not DashboardViewModel viewModel) return;

            _portForwardRulesOwner = viewModel;
            viewModel.PropertyChanged += PortForwardRulesOwner_PropertyChanged;
        }

        private void DetachPortForwardRuleViewUpdates()
        {
            if (_portForwardRulesOwner is not null)
                _portForwardRulesOwner.PropertyChanged -= PortForwardRulesOwner_PropertyChanged;
            _portForwardRulesOwner = null;
        }

        private void PortForwardRulesOwner_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DashboardViewModel.PortForwardRules))
                RefreshPortForwardRuleFilter();
        }

        private void PortForwardFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _showPortForwardAttentionOnly = PortForwardFilterComboBox.SelectedIndex == 1;
            RefreshPortForwardRuleFilter();
        }

        private void ShowAllPortForwardRules()
        {
            if (!_showPortForwardAttentionOnly) return;

            PortForwardFilterComboBox.SelectedIndex = 0;
        }

        private void RefreshPortForwardRuleFilter()
        {
            if (PortForwardRulesList is null) return;

            object? selectedRule = PortForwardRulesList.SelectedItem;
            PortForwardRulesList.Items.Filter = item => item is PortForwardRuleInfo rule &&
                (!_showPortForwardAttentionOnly || rule.TargetStatusSeverity is "Warning" or "Critical");
            PortForwardRulesList.Items.Refresh();

            if (selectedRule is not null && !PortForwardRulesList.Items.Contains(selectedRule))
                PortForwardRulesList.SelectedItem = null;

            bool hasRules = DataContext is DashboardViewModel { PortForwardRules.Count: > 0 };
            NoPortForwardAttentionRulesState.Visibility = _showPortForwardAttentionOnly && hasRules && PortForwardRulesList.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SelectAndBringIntoView(ListBox list, Func<object?> resolveTarget)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                object? target = resolveTarget();
                if (target is null) return;

                list.SelectedItem = target;
                if (list.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement container)
                    container.BringIntoView();
            }, DispatcherPriority.Loaded);
        }

        private void AddDhcpReservation_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageDhcpReservations()) return;
            ShowDhcpReservationDialog(null);
        }

        private void EditDhcpReservation_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageDhcpReservations() || sender is not FrameworkElement { Tag: DhcpReservationInfo reservation }) return;
            ShowDhcpReservationDialog(reservation);
        }

        private async void ViewDhcpClient_Click(object sender, RoutedEventArgs e)
        {
            string? macAddress = sender is FrameworkElement
            {
                Tag: DhcpLeaseInfo lease
            }
                ? lease.MacAddress
                : sender is FrameworkElement
                {
                    Tag: DhcpReservationInfo reservation
                }
                    ? reservation.MacAddress
                    : null;

            if (!ClientIdentity.IsMacKey(macAddress)) return;

            if (Window.GetWindow(this) is DashboardWindow dashboard)
                await dashboard.OpenClientDetailsForDeviceIdentityAsync(macAddress);
        }

        private async void DeleteDhcpReservation_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageDhcpReservations() || sender is not FrameworkElement { Tag: DhcpReservationInfo reservation }) return;
            string device = string.IsNullOrWhiteSpace(reservation.Hostname) ? "Unknown device" : reservation.Hostname;
            if (MessageBox.Show($"Delete DHCP reservation?\n\nDevice: {device}\nReserved IP: {reservation.IpAddress}\n\nThis removes the fixed DHCP reservation from the router.", "Delete DHCP Reservation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            DashboardViewModel? viewModel = DataContext as DashboardViewModel;
            if (viewModel is null) return;
            viewModel.DhcpReservationMutationInProgress = true;
            try
            {
                DhcpReservationOperationResult result = await _dhcpReservationService.DeleteReservationAsync(new DhcpReservationIdentity(reservation.MacAddress, reservation.IpAddress, reservation.Hostname), CancellationToken.None);
                if (!result.Success)
                {
                    MessageBox.Show(ReservationFailureMessage(result.FailureCategory), "DHCP Reservation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                await RefreshDashboardAsync();
                ClientRefreshNotifier.RequestRefresh();
            }
            catch
            {
                MessageBox.Show("RouterPilot could not apply the DHCP reservation.", "DHCP Reservation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                viewModel.DhcpReservationMutationInProgress = false;
            }
        }

        private void ShowDhcpReservationDialog(DhcpReservationInfo? existing)
        {
            bool isEdit = existing is not null;
            DhcpReservationEditorDialog.Show(Window.GetWindow(this), isEdit ? "Edit DHCP Reservation" : "Add DHCP Reservation", new DhcpReservationRequest { Hostname = existing?.Hostname, MacAddress = existing?.MacAddress ?? string.Empty, IpAddress = existing?.IpAddress ?? string.Empty }, request => ExecuteReservationRequestAsync(existing, request));
        }

        private async Task<string?> ExecuteReservationRequestAsync(DhcpReservationInfo? existing, DhcpReservationRequest request)
        {
            if (!CanManageDhcpReservations()) return "DHCP reservation changes are unavailable while the router connection is not ready.";
            DashboardViewModel? viewModel = DataContext as DashboardViewModel;
            if (viewModel is null) return "RouterPilot could not access the DHCP view.";
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
                DhcpSnapshot snapshot = await router.GetDhcpSnapshotAsync();
                var reservations = existing is null ? snapshot.Reservations : snapshot.Reservations.Where(item => !(string.Equals(item.MacAddress, existing.MacAddress, StringComparison.OrdinalIgnoreCase) && string.Equals(item.IpAddress, existing.IpAddress, StringComparison.OrdinalIgnoreCase))).ToList();
                DhcpReservationValidationResult validation = _dhcpReservationValidator.Validate(request.MacAddress, request.IpAddress, snapshot.Scopes, reservations, snapshot.Leases);
                if (!validation.IsValid) return ReservationFailureMessage(validation.Code.ToString());

                viewModel.DhcpReservationMutationInProgress = true;
                DhcpReservationOperationResult result = existing is null
                    ? await _dhcpReservationService.AddReservationAsync(request, CancellationToken.None)
                    : await _dhcpReservationService.UpdateReservationAsync(new DhcpReservationIdentity(existing.MacAddress, existing.IpAddress), request, CancellationToken.None);
                if (!result.Success) return ReservationFailureMessage(result.FailureCategory);
                await RefreshDashboardAsync();
                ClientRefreshNotifier.RequestRefresh();
                return null;
            }
            catch
            {
                return "RouterPilot could not apply the DHCP reservation.";
            }
            finally
            {
                viewModel.DhcpReservationMutationInProgress = false;
            }
        }

        private bool CanManageDhcpReservations() => DataContext is DashboardViewModel { CanManageDhcpReservations: true };

        private static string ReservationFailureMessage(string category) => category switch
        {
            nameof(DhcpReservationValidationCode.DuplicateExactReservation) => "That reservation already exists.",
            nameof(DhcpReservationValidationCode.ConflictingReservedIp) => "That IP address is reserved for another device.",
            nameof(DhcpReservationValidationCode.ConflictingMacReservation) => "This device already has a reservation for a different IP address.",
            nameof(DhcpReservationValidationCode.OutsideKnownDhcpSubnet) => "The requested IP address is outside a known DHCP network.",
            nameof(DhcpReservationValidationCode.InvalidMac) => "Enter a valid MAC address.",
            nameof(DhcpReservationValidationCode.BroadcastMac) => "The broadcast MAC address cannot be reserved.",
            nameof(DhcpReservationValidationCode.MulticastMac) => "A multicast MAC address cannot be reserved.",
            nameof(DhcpReservationValidationCode.InvalidIp) => "Enter a valid IPv4 address.",
            nameof(DhcpReservationValidationCode.NetworkAddress) => "The network address cannot be reserved.",
            nameof(DhcpReservationValidationCode.BroadcastAddress) => "The broadcast address cannot be reserved.",
            nameof(DhcpReservationValidationCode.RouterAddress) => "The router address cannot be reserved.",
            nameof(DhcpReservationValidationCode.AmbiguousScope) => "The requested IP address maps to more than one DHCP network.",
            "RollbackVerificationFailed" => "RouterPilot could not confirm the DHCP reservation state. Review Network → DHCP before making further changes.",
            "UpdateVerificationFailed" or "DeleteVerificationFailed" or "VerificationFailed" => "RouterPilot could not verify the DHCP reservation after applying it.",
            _ => "RouterPilot could not apply the DHCP reservation."
        };

        private async Task RefreshDashboardAsync()
        {
            if (Application.Current.MainWindow is DashboardWindow dashboard) await dashboard.RefreshNowAsync();
        }

        private void NetworkTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, NetworkTabs)) return;
            UpdateNetworkTabVisibility();
        }

        private void UpdateNetworkTabVisibility()
        {
            // Selection can change while XAML is constructing the tab headers.
            // Apply visibility only after all named content containers exist.
            if (OverviewSummaryContent is null || OverviewMaintenanceContent is null ||
                OverviewDetailsContent is null || MapContent is null || WifiContent is null || DhcpContent is null || PortForwardContent is null || HealthContent is null || InternetQualityContent is null)
            {
                return;
            }

            bool showMap = NetworkTabs.SelectedIndex == 1;
            bool showWifi = NetworkTabs.SelectedIndex == 2;
            bool showDhcp = NetworkTabs.SelectedIndex == 3;
            bool showPortForward = NetworkTabs.SelectedIndex == 4;
            bool showHealth = NetworkTabs.SelectedIndex == 5;
            bool showQuality = NetworkTabs.SelectedIndex == 6;
            if (showHealth && _networkHealthView is null)
            {
                _networkHealthView = new NetworkHealthView();
                HealthContent.Content = _networkHealthView;
            }
            OverviewSummaryContent.Visibility = showMap || showWifi || showDhcp || showPortForward || showHealth || showQuality ? Visibility.Collapsed : Visibility.Visible;
            OverviewMaintenanceContent.Visibility = showMap || showWifi || showDhcp || showPortForward || showHealth || showQuality ? Visibility.Collapsed : Visibility.Visible;
            OverviewDetailsContent.Visibility = showMap || showWifi || showDhcp || showPortForward || showHealth || showQuality ? Visibility.Collapsed : Visibility.Visible;
            MapContent.Visibility = showMap ? Visibility.Visible : Visibility.Collapsed;
            WifiContent.Visibility = showWifi ? Visibility.Visible : Visibility.Collapsed;
            DhcpContent.Visibility = showDhcp ? Visibility.Visible : Visibility.Collapsed;
            PortForwardContent.Visibility = showPortForward ? Visibility.Visible : Visibility.Collapsed;
            HealthContent.Visibility = showHealth ? Visibility.Visible : Visibility.Collapsed;
            InternetQualityContent.Visibility = showQuality ? Visibility.Visible : Visibility.Collapsed;
            if (showPortForward) _ = RefreshPortForwardAsync();
            if (showMap) UpdateMapSummary();
        }

        private void UpdateMapSummary()
        {
            if (DataContext is not DashboardViewModel vm || MapCurrentCountText is null) return;
            int current = vm.LanClients.Count(client => client.IsOnline);
            int wired = vm.LanClients.Count(client => client.IsOnline && client.ConnectionType.Contains("Wired", StringComparison.OrdinalIgnoreCase));
            int wifi = vm.LanClients.Count(client => client.IsOnline && client.ConnectionType.Contains("Wi", StringComparison.OrdinalIgnoreCase));
            int unknown = Math.Max(0, current - wired - wifi);
            MapCurrentCountText.Text = current.ToString();
            MapWiredCountText.Text = wired.ToString();
            MapWirelessCountText.Text = $"{wifi} / {unknown}";
            MapStatusText.Text = vm.LanIsLoading ? "Loading current clients…" : "Current clients grouped by authoritative connection evidence.";
        }

        private void CopyNetworkMapSummary_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DashboardViewModel vm) return;
            int current = vm.LanClients.Count(client => client.IsOnline);
            int wired = vm.LanClients.Count(client => client.IsOnline && client.ConnectionType.Contains("Wired", StringComparison.OrdinalIgnoreCase));
            int wifi = vm.LanClients.Count(client => client.IsOnline && client.ConnectionType.Contains("Wi", StringComparison.OrdinalIgnoreCase));
            int unknown = Math.Max(0, current - wired - wifi);
            string summary = $"RouterPilot Network Map Summary\nCurrent devices: {current}\nWired: {wired}\nWi-Fi: {wifi}\nUnknown attachment: {unknown}\nGenerated: {DateTime.Now:g}";
            try { Clipboard.SetText(summary); MapStatusText.Text = "Network map summary copied."; }
            catch { MapStatusText.Text = "Network map summary could not be copied."; }
        }

        private void OpenMapClientDetails_Click(object sender, RoutedEventArgs e)
        {
            if ((DataContext as DashboardViewModel)?.SelectedMapClient is not LanClientInfo client) return;
            if (Application.Current.MainWindow is DashboardWindow dashboard)
                dashboard.OpenClientDetailsForDeviceIdentity(client.MacAddress);
        }

        private void OpenMapRouterDetails_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is DashboardWindow dashboard)
                dashboard.NavigateToRouterOverview();
        }

        private void BackToMap_Click(object sender, RoutedEventArgs e)
        {
            MapClientsList?.BringIntoView();
            MapClientsList?.Focus();
        }

        private void MapClientsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _mapSelectionFromInput = true;

        private void MapClientsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space)
                _mapSelectionFromInput = true;
        }

        private void MapClientsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_mapSelectionFromInput) return;
            _mapSelectionFromInput = false;
            if ((DataContext as DashboardViewModel)?.SelectedMapClient is null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => MapDetailsCard?.BringIntoView()));
        }
        private async Task RefreshPortForwardAsync()
        {
            if (DataContext is not DashboardViewModel viewModel || viewModel.PortForwardIsLoading) return;
            viewModel.PortForwardIsLoading = true;
            try { var rules = await _portForwardService.GetRulesAsync(CancellationToken.None); viewModel.PortForwardRules.Clear(); foreach (var rule in rules) viewModel.PortForwardRules.Add(rule); viewModel.ReevaluatePortForwardIntelligence(); viewModel.SetPortForwardingCapabilities(true, true); viewModel.PortForwardStatus = string.Empty; }
            catch { viewModel.SetPortForwardingCapabilities(false, false); viewModel.PortForwardStatus = "Port forwarding is unavailable for this router session."; }
            finally { viewModel.PortForwardIsLoading = false; }
        }
        private async void RefreshPortForward_Click(object sender, RoutedEventArgs e) => await RefreshPortForwardAsync();
        private void AddPortForward_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DashboardViewModel { PortForwardingWriteSupported: true }) return;
            ShowPortForwardDialog(null);
        }
        private void EditPortForward_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DashboardViewModel { PortForwardingWriteSupported: true } || sender is not FrameworkElement { Tag: PortForwardRuleInfo rule }) return;
            ShowPortForwardDialog(rule);
        }
        private async void DeletePortForward_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DashboardViewModel { PortForwardingWriteSupported: true } || sender is not FrameworkElement { Tag: PortForwardRuleInfo rule }) return;
            if (MessageBox.Show($"Delete port forward '{rule.Name}'?", "Port Forwarding", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            PortForwardOperationResult result = await _portForwardService.DeleteAsync(rule.Id, CancellationToken.None);
            if (!result.Success) { MessageBox.Show(PortForwardFailureMessage(result.FailureCategory), "Port Forwarding", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            await RefreshPortForwardAsync();
        }

        private void ShowPortForwardDialog(PortForwardRuleInfo? existing)
        {
            if (DataContext is not DashboardViewModel viewModel) return;
            PortForwardEditorDialog.Show(Window.GetWindow(this), existing is null ? "Add Port Forward" : "Edit Port Forward", existing, viewModel, request => ExecutePortForwardAsync(existing, request), CreatePortForwardReservationAsync, RefreshPortForwardDhcpStateAsync);
        }

        private async Task<bool> RefreshPortForwardDhcpStateAsync(bool forceConfigurationRefresh)
        {
            if (Application.Current.MainWindow is not DashboardWindow dashboard) return false;
            return await dashboard.RefreshDhcpStateAsync(forceConfigurationRefresh);
        }

        // This is deliberately a narrow adapter over the established DHCP reservation
        // workflow. It confirms that the editor's selected device still owns the
        // draft IP before handing the write to IDhcpReservationService.
        private async Task<string?> CreatePortForwardReservationAsync(PortForwardReservationTarget target)
        {
            if (!CanManageDhcpReservations()) return "DHCP reservation changes are unavailable while the router connection is not ready.";
            if (DataContext is not DashboardViewModel viewModel) return "RouterPilot could not access the DHCP view.";
            bool reservationMutationStarted = false;
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
                DhcpSnapshot snapshot = await router.GetDhcpSnapshotAsync();
                DhcpReservationValidationResult validation = _dhcpReservationValidator.Validate(target.MacAddress, target.IpAddress, snapshot.Scopes, snapshot.Reservations, snapshot.Leases);
                if (string.IsNullOrWhiteSpace(validation.NormalizedMac)) return ReservationFailureMessage(validation.Code.ToString());

                var deviceLeases = snapshot.Leases.Where(lease => SameMac(lease.MacAddress, validation.NormalizedMac)).ToList();
                if (deviceLeases.Count == 0) return "Cannot create reservation\n\nThe selected device is no longer correlated to this target IP.";
                if (!deviceLeases.Any(lease => string.Equals(lease.IpAddress, validation.IpAddress, StringComparison.OrdinalIgnoreCase)))
                    return $"Target IP changed\n\nRule: {target.IpAddress} â€¢ Current device IP: {deviceLeases[0].IpAddress}";

                bool exactReservation = snapshot.Reservations.Any(reservation => SameMac(reservation.MacAddress, validation.NormalizedMac) && string.Equals(reservation.IpAddress, validation.IpAddress, StringComparison.OrdinalIgnoreCase));
                if (exactReservation)
                {
                    await RefreshDashboardAsync();
                    ClientRefreshNotifier.RequestRefresh();
                    return null;
                }
                if (snapshot.Reservations.Any(reservation => SameMac(reservation.MacAddress, validation.NormalizedMac)))
                {
                    await RefreshDashboardAsync();
                    ClientRefreshNotifier.RequestRefresh();
                    return "This device already has a reservation for a different IP address.";
                }
                if (!validation.IsValid) return PortForwardReservationFailureMessage(validation, target.IpAddress);

                viewModel.DhcpReservationMutationInProgress = true;
                reservationMutationStarted = true;
                DhcpReservationOperationResult result = await _dhcpReservationService.AddReservationAsync(new DhcpReservationRequest
                {
                    Hostname = target.DeviceName,
                    MacAddress = validation.NormalizedMac,
                    IpAddress = validation.IpAddress!
                }, CancellationToken.None);
                if (!result.Success)
                {
                    if (result.FailureCategory == nameof(DhcpReservationValidationCode.DuplicateExactReservation))
                    {
                        await RefreshDashboardAsync();
                        ClientRefreshNotifier.RequestRefresh();
                        return null;
                    }
                    return PortForwardReservationFailureMessage(result.FailureCategory, target.IpAddress);
                }
                await RefreshDashboardAsync();
                ClientRefreshNotifier.RequestRefresh();
                return null;
            }
            catch
            {
                return "RouterPilot could not apply the DHCP reservation.";
            }
            finally
            {
                if (reservationMutationStarted) viewModel.DhcpReservationMutationInProgress = false;
            }
        }

        private static bool SameMac(string first, string second) => ClientIdentity.MacEquals(first, second);

        private static string PortForwardReservationFailureMessage(DhcpReservationValidationResult validation, string requestedIp) =>
            PortForwardReservationFailureMessage(validation.Code.ToString(), requestedIp);

        private static string PortForwardReservationFailureMessage(string category, string requestedIp) => category switch
        {
            nameof(DhcpReservationValidationCode.ConflictingReservedIp) => $"Cannot create reservation\n\n{requestedIp} is already reserved for another device.",
            nameof(DhcpReservationValidationCode.DuplicateExactReservation) => "That reservation already exists.",
            _ => ReservationFailureMessage(category)
        };

        private async Task<string?> ExecutePortForwardAsync(PortForwardRuleInfo? existing, PortForwardRuleRequest request)
        {
            if (DataContext is not DashboardViewModel { PortForwardingWriteSupported: true } viewModel) return "Port forwarding changes are unavailable while the router connection is not ready.";
            viewModel.PortForwardIsLoading = true;
            try
            {
                PortForwardOperationResult result = existing is null
                    ? await _portForwardService.AddAsync(request, CancellationToken.None)
                    : await _portForwardService.UpdateAsync(existing.Id, request, CancellationToken.None);
                if (!result.Success) return PortForwardFailureMessage(result.FailureCategory);
                viewModel.PortForwardIsLoading = false;
                await RefreshPortForwardAsync();
                return null;
            }
            catch { return "RouterPilot could not apply the port forward."; }
            finally { viewModel.PortForwardIsLoading = false; }
        }

        private async void TogglePortForward_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DashboardViewModel { PortForwardingWriteSupported: true } viewModel || sender is not FrameworkElement { Tag: PortForwardRuleInfo rule }) return;
            viewModel.PortForwardIsLoading = true;
            try
            {
                PortForwardRuleRequest request = new() { Name = rule.Name, Protocol = rule.Protocol, SourceZone = rule.SourceZone, ExternalPort = rule.ExternalPort, DestinationZone = rule.DestinationZone, DestinationIp = rule.DestinationIp, InternalPort = rule.InternalPort, Enabled = !rule.Enabled };
                PortForwardOperationResult result = await _portForwardService.UpdateAsync(rule.Id, request, CancellationToken.None);
                if (!result.Success) { MessageBox.Show(PortForwardFailureMessage(result.FailureCategory), "Port Forwarding", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                viewModel.PortForwardIsLoading = false;
                await RefreshPortForwardAsync();
            }
            finally { viewModel.PortForwardIsLoading = false; }
        }

        private static string PortForwardFailureMessage(string category) => category switch
        {
            "InvalidName" => "Enter a name of up to 64 characters.",
            "InvalidProtocol" => "Select TCP, UDP, or TCP + UDP.",
            "InvalidPort" => "Enter a single port number from 1 to 65535.",
            "InvalidDestinationIp" => "Enter a valid IPv4 address.",
            "OutsideKnownLanScope" => "The internal IP address is outside a known LAN scope.",
            "PortConflict" => "A port forward with an overlapping protocol already uses that external port.",
            "RuleNotFound" => "This port forward no longer exists. Refresh the list and try again.",
            "VerificationFailed" => "RouterPilot could not verify the router state after this change. Refresh the list before trying again.",
            _ => "RouterPilot could not apply the port forward."
        };

        private async void RestartWifi_Click(object sender, RoutedEventArgs e)
        {
            if (_maintenanceInProgress) return;
            if (MessageBox.Show(
                    "Restart Wi-Fi now? Wireless clients will disconnect briefly.",
                    "Restart Wi-Fi",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await RunMaintenanceAsync(async router => await router.RestartWifiAsync());
        }

        private async void ReconnectWan_Click(object sender, RoutedEventArgs e)
        {
            if (_maintenanceInProgress) return;
            if (MessageBox.Show(
                    "Reconnect the WAN interface now? Internet access may pause briefly.",
                    "Reconnect WAN",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (await RunMaintenanceAsync(async router => await router.RestartWanAsync()))
            {
                await _publicIpService.RefreshAsync(forceRefresh: true);
            }
        }


        private async System.Threading.Tasks.Task<bool> RunMaintenanceAsync(
            Func<RouterManager, System.Threading.Tasks.Task<string>> operation)
        {
            _maintenanceInProgress = true;
            MaintenanceStatusText.Text = "Working…";
            IsEnabled = false;

            try
            {
                RouterManager routerManager =
                    await _routerManagerProvider.GetRouterManagerAsync();
                MaintenanceStatusText.Text = await operation(routerManager);
                return true;
            }
            catch (Exception ex)
            {
                MaintenanceStatusText.Text = OperationFailurePolicy.UserMessage(
                    ex,
                    "Network maintenance operation",
                    "Operation could not be completed. Check the router connection and try again.");
                return false;
            }
            finally
            {
                IsEnabled = true;
                _maintenanceInProgress = false;
            }
        }
    }
}
