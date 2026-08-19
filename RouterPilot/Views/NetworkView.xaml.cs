using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private readonly VpnView _vpnView;
        private bool _maintenanceInProgress;

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
            _vpnView = new VpnView(embedded: true);
            VpnContent.Content = _vpnView;
            UpdateNetworkTabVisibility();
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
                OverviewDetailsContent is null || WifiContent is null || DhcpContent is null || PortForwardContent is null || VpnContent is null)
            {
                return;
            }

            bool showWifi = NetworkTabs.SelectedIndex == 1;
            bool showDhcp = NetworkTabs.SelectedIndex == 2;
            bool showPortForward = NetworkTabs.SelectedIndex == 3;
            bool showVpn = NetworkTabs.SelectedIndex == 4;
            OverviewSummaryContent.Visibility = showWifi || showDhcp || showPortForward || showVpn ? Visibility.Collapsed : Visibility.Visible;
            OverviewMaintenanceContent.Visibility = showWifi || showDhcp || showPortForward || showVpn ? Visibility.Collapsed : Visibility.Visible;
            OverviewDetailsContent.Visibility = showWifi || showDhcp || showPortForward || showVpn ? Visibility.Collapsed : Visibility.Visible;
            WifiContent.Visibility = showWifi ? Visibility.Visible : Visibility.Collapsed;
            DhcpContent.Visibility = showDhcp ? Visibility.Visible : Visibility.Collapsed;
            PortForwardContent.Visibility = showPortForward ? Visibility.Visible : Visibility.Collapsed;
            VpnContent.Visibility = showVpn ? Visibility.Visible : Visibility.Collapsed;
            if (showPortForward) _ = RefreshPortForwardAsync();
            if (showVpn) _ = _vpnView.RefreshForHostAsync();
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
            PortForwardEditorDialog.Show(Window.GetWindow(this), existing is null ? "Add Port Forward" : "Edit Port Forward", existing, viewModel, request => ExecutePortForwardAsync(existing, request));
        }

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
                MaintenanceStatusText.Text = "Operation failed: " + ex.Message;
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
