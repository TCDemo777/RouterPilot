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
            UpdateNetworkTabVisibility();
#if DEBUG
            AddDhcpContractProbeButton();
#endif
        }

#if DEBUG
        private void AddDhcpContractProbeButton()
        {
            var probeButton = new Button
            {
                Content = "Run DHCP Contract Probe",
                Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            probeButton.Click += RunDhcpContractProbe_Click;
            DhcpDebugToolsHost.Children.Add(probeButton);

            var writeVerificationButton = new Button
            {
                Content = "Run Controlled DHCP Write Verification",
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            writeVerificationButton.Click += RunDhcpWriteVerification_Click;
            DhcpDebugToolsHost.Children.Add(writeVerificationButton);
        }

        private async void RunDhcpWriteVerification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            if (MessageBox.Show(
                    "This Debug-only verification will create, edit, and delete one generated temporary DHCP reservation, then reload dnsmasq. Existing reservations will not be targeted. Continue?",
                    "Controlled DHCP Write Verification",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
                string report = await router.RunDhcpReservationWriteVerificationAsync();
                ShowDhcpContractProbeDialog(report);
            }
            catch
            {
                MessageBox.Show(
                    "RouterPilot could not complete the controlled DHCP write verification.",
                    "DHCP Write Verification",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async void RunDhcpContractProbe_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            button.IsEnabled = false;
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
                string report = await router.GetDhcpContractProbeReportAsync();
                ShowDhcpContractProbeDialog(report);
            }
            catch
            {
                MessageBox.Show(
                    "RouterPilot could not complete the read-only DHCP contract probe.",
                    "DHCP Contract Probe",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private static void ShowDhcpContractProbeDialog(string report)
        {
            var output = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Margin = new Thickness(16)
            };
            var copyButton = new Button
            {
                Content = "Copy Report",
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 16, 16),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            copyButton.Click += (_, _) => Clipboard.SetText(report);

            var panel = new DockPanel();
            DockPanel.SetDock(copyButton, Dock.Bottom);
            panel.Children.Add(copyButton);
            panel.Children.Add(output);

            new Window
            {
                Title = "DHCP Contract Probe — Local Debug Output",
                Content = panel,
                Width = 760,
                Height = 560,
                MinWidth = 560,
                MinHeight = 360,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            }.ShowDialog();
        }
#endif

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
                OverviewDetailsContent is null || WifiContent is null || DhcpContent is null)
            {
                return;
            }

            bool showWifi = NetworkTabs.SelectedIndex == 1;
            bool showDhcp = NetworkTabs.SelectedIndex == 2;
            OverviewSummaryContent.Visibility = showWifi || showDhcp ? Visibility.Collapsed : Visibility.Visible;
            OverviewMaintenanceContent.Visibility = showWifi || showDhcp ? Visibility.Collapsed : Visibility.Visible;
            OverviewDetailsContent.Visibility = showWifi || showDhcp ? Visibility.Collapsed : Visibility.Visible;
            WifiContent.Visibility = showWifi ? Visibility.Visible : Visibility.Collapsed;
            DhcpContent.Visibility = showDhcp ? Visibility.Visible : Visibility.Collapsed;
        }

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

            await RunMaintenanceAsync(async router => await router.RestartWanAsync());
        }

        private async System.Threading.Tasks.Task RunMaintenanceAsync(
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
            }
            catch (Exception ex)
            {
                MaintenanceStatusText.Text = "Operation failed: " + ex.Message;
            }
            finally
            {
                IsEnabled = true;
                _maintenanceInProgress = false;
            }
        }
    }
}
