using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Specialized;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class ClientsView : UserControl
    {
        private readonly ClientsViewModel _viewModel;
        private readonly DispatcherTimer _refreshTimer;
        private readonly IDhcpReservationService _dhcpReservationService;
        private readonly DhcpReservationValidator _dhcpReservationValidator;
        private bool _isRefreshNotifierSubscribed;
        private bool _isActivityFeedSubscribed;

        public ClientsView()
        {
            InitializeComponent();

            _viewModel = ((App)Application.Current).Services
                .GetRequiredService<ClientsViewModel>();
            _dhcpReservationService = ((App)Application.Current).Services.GetRequiredService<IDhcpReservationService>();
            _dhcpReservationValidator = ((App)Application.Current).Services.GetRequiredService<DhcpReservationValidator>();
            DataContext = _viewModel;

            _refreshTimer =
                new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(10)
                };

            _refreshTimer.Tick +=
                ClientsRefreshTimer_Tick;

            Loaded += ClientsView_Loaded;
            Unloaded += ClientsView_Unloaded;
        }

        private async void ClientsView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            SubscribeToRefreshNotifier();
            SubscribeToActivityFeed();

            await RefreshClientsAsync();

            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }

        private async void ClientsRefreshTimer_Tick(
            object? sender,
            EventArgs e)
        {
            if (!IsVisible ||
                _viewModel.IsLoading)
            {
                return;
            }

            await RefreshClientsAsync();
        }

        private void ClientsView_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            UnsubscribeFromRefreshNotifier();
            UnsubscribeFromActivityFeed();
        }

        private void SubscribeToRefreshNotifier()
        {
            if (_isRefreshNotifierSubscribed)
            {
                return;
            }

            ClientRefreshNotifier.RefreshRequested +=
                ClientRefreshNotifier_RefreshRequested;
            ClientRefreshNotifier.ProfileStateChanged +=
                ClientRefreshNotifier_ProfileStateChanged;
            _isRefreshNotifierSubscribed = true;
        }

        private void UnsubscribeFromRefreshNotifier()
        {
            if (!_isRefreshNotifierSubscribed)
            {
                return;
            }

            ClientRefreshNotifier.RefreshRequested -=
                ClientRefreshNotifier_RefreshRequested;
            ClientRefreshNotifier.ProfileStateChanged -=
                ClientRefreshNotifier_ProfileStateChanged;
            _isRefreshNotifierSubscribed = false;
        }

        private void SubscribeToActivityFeed()
        {
            if (_isActivityFeedSubscribed) return;
            _viewModel.SelectedClientActivity.CollectionChanged += SelectedClientActivity_CollectionChanged;
            _isActivityFeedSubscribed = true;
        }

        private void UnsubscribeFromActivityFeed()
        {
            if (!_isActivityFeedSubscribed) return;
            _viewModel.SelectedClientActivity.CollectionChanged -= SelectedClientActivity_CollectionChanged;
            _isActivityFeedSubscribed = false;
        }

        private void SelectedClientActivity_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (AutoScrollToTopCheckBox.IsChecked != true || _viewModel.SelectedClientActivity.Count == 0) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AutoScrollToTopCheckBox.IsChecked == true && _viewModel.SelectedClientActivity.Count > 0)
                    RecentActivityList.ScrollIntoView(_viewModel.SelectedClientActivity[0]);
            }), DispatcherPriority.ContextIdle);
        }

        private async void ClientRefreshNotifier_RefreshRequested(
            object? sender,
            EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(
                    async () => await RefreshClientsAsync());

                return;
            }

            await RefreshClientsAsync();
        }

        private void ClientRefreshNotifier_ProfileStateChanged(
            object? sender,
            EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(_viewModel.ReloadProfileState));
                return;
            }

            _viewModel.ReloadProfileState();
        }

        private async System.Threading.Tasks.Task RefreshClientsAsync()
        {
            double outerScrollOffset = ClientsPageScrollViewer.VerticalOffset;
            try
            {
                await _viewModel.LoadClientsAsync();
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Client view refresh",
                    "Unable to load clients. Check the router connection and try again.");
            }
            finally
            {
                // Rebuilding the selected ListBox item can cause WPF to
                // request that it be brought into view. Refreshing client
                // data must not take ownership of the user's page position.
                _ = Dispatcher.BeginInvoke(new Action(() =>
                    ClientsPageScrollViewer.ScrollToVerticalOffset(outerScrollOffset)),
                    DispatcherPriority.ContextIdle);
            }
        }

        private void SortOptionComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 ||
                e.AddedItems[0] is not string selectedSort)
            {
                return;
            }

            // Run after WPF finishes changing the selection. This removes
            // the previous requirement to press Ascending/Descending.
            Dispatcher.BeginInvoke(
                new Action(() =>
                    _viewModel.SelectSortOption(selectedSort)),
                DispatcherPriority.DataBind);
        }

        private void FavoriteButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is Button button &&
                button.Tag is ClientInfo client)
            {
                _viewModel.ToggleFavorite(client);
                e.Handled = true;
            }
        }

        private void ViewDetailsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenSelectedClient();
        }

        private void ReviewNewDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ClientInfo client })
            {
                OpenClientDetails(client);
            }
        }

        private void ClientsGrid_MouseDoubleClick(
            object sender,
            MouseButtonEventArgs e)
        {
            OpenSelectedClient();
        }

        private void ClientsGrid_PreviewMouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(
                    ClientsGrid,
                    e.OriginalSource as DependencyObject) is ListBoxItem item &&
                item.DataContext is ClientInfo client)
            {
                ActivateClient(client);
            }
        }

        private void ClientsGrid_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            if ((e.Key is Key.Enter or Key.Space) &&
                ClientsGrid.SelectedItem is ClientInfo client)
            {
                ActivateClient(client);
                e.Handled = true;
            }
        }

        private async void ClientCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ClientInfo } card ||
                card.ContextMenu is null)
                return;

            // Context actions are scoped to the card under the pointer. Do
            // not alter SelectedClient or trigger the left-click auto-scroll.
            await ConfigureDhcpReservationActionAsync(card.ContextMenu, card.DataContext as ClientInfo);
            card.ContextMenu.PlacementTarget = card;
            card.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        private static ClientInfo? ContextClient(object sender) =>
            sender is MenuItem { DataContext: ClientInfo client } ? client : null;

        private void ContextViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is ClientInfo client)
                OpenClientDetails(client);
        }

        private void ContextCopyIp_Click(object sender, RoutedEventArgs e) =>
            CopyClientValue(ContextClient(sender)?.IpAddress, "IP address copied.");

        private void ContextCopyMac_Click(object sender, RoutedEventArgs e) =>
            CopyClientValue(ContextClient(sender)?.MacAddress, "MAC address copied.");

        private async void ContextPing_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is ClientInfo client)
                await RunClientDiagnosticAsync(client, ping: true);
        }

        private async void ContextTraceroute_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is ClientInfo client)
                await RunClientDiagnosticAsync(client, ping: false);
        }

        private void ContextDnsActivity_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is not null && Window.GetWindow(this) is DashboardWindow dashboard)
                dashboard.NavigateToDnsActivity();
        }

        private void ContextFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is ClientInfo client)
                _viewModel.ToggleFavorite(client);
        }

        private void ContextEditProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is ClientInfo client)
                OpenClientDetails(client);
        }

        private async void ContextDhcpReservation_Click(object sender, RoutedEventArgs e)
        {
            if (ContextClient(sender) is not ClientInfo client || sender is not MenuItem { Tag: string action }) return;
            DhcpClientReservationAction? state = await GetDhcpReservationActionAsync(client);
            if (state is null) return;
            if (!state.CanWrite)
            {
                MessageBox.Show($"DHCP reservation\n\nReserved IP: {state.Reservation?.IpAddress ?? client.IpAddress}", "DHCP Reservation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DhcpReservationInfo? existing = state.Reservation;
            DhcpReservationEditorDialog.Show(Window.GetWindow(this), action, new DhcpReservationRequest { Hostname = client.Name, MacAddress = existing?.MacAddress ?? client.MacAddress, IpAddress = existing?.IpAddress ?? client.IpAddress }, request => ExecuteClientReservationAsync(existing, request));
        }

        private async Task ConfigureDhcpReservationActionAsync(ContextMenu menu, ClientInfo? client)
        {
            MenuItem? item = menu.Items.OfType<MenuItem>().FirstOrDefault(candidate => string.Equals(candidate.Tag as string, "DhcpReservationAction", StringComparison.Ordinal));
            if (item is null) return;
            DhcpClientReservationAction? state = client is null ? null : await GetDhcpReservationActionAsync(client);
            item.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
            if (state is null) return;
            item.Header = state.Reservation is null ? "Reserve Current IP" : state.CanWrite ? "Edit DHCP Reservation" : "View DHCP Reservation";
        }

        private async Task<DhcpClientReservationAction?> GetDhcpReservationActionAsync(ClientInfo client)
        {
            try
            {
                RouterManager router = await ((App)Application.Current).Services.GetRequiredService<IRouterManagerProvider>().GetRouterManagerAsync();
                DhcpSnapshot snapshot = await router.GetDhcpSnapshotAsync();
                DhcpReservationEligibility eligibility = _dhcpReservationValidator.GetEligibility(client.MacAddress, client.IpAddress, snapshot.Scopes, snapshot.Reservations, snapshot.Leases);
                DhcpReservationInfo? exact = snapshot.Reservations.FirstOrDefault(item => SameMac(item.MacAddress, client.MacAddress) && string.Equals(item.IpAddress, client.IpAddress, StringComparison.OrdinalIgnoreCase));
                DhcpReservationInfo? sameMac = snapshot.Reservations.FirstOrDefault(item => SameMac(item.MacAddress, client.MacAddress));
                bool canWrite = Application.Current.MainWindow?.DataContext is DashboardViewModel { CanManageDhcpReservations: true };
                if (exact is not null || sameMac is not null) return new DhcpClientReservationAction(exact ?? sameMac, canWrite);
                return eligibility.Eligible && canWrite ? new DhcpClientReservationAction(null, true) : null;
            }
            catch { return null; }
        }

        private async Task<string?> ExecuteClientReservationAsync(DhcpReservationInfo? existing, DhcpReservationRequest request)
        {
            try
            {
                _viewModel.StatusMessage = "Applying DHCP reservation…";
                DhcpReservationOperationResult result = existing is null
                    ? await _dhcpReservationService.AddReservationAsync(request, CancellationToken.None)
                    : await _dhcpReservationService.UpdateReservationAsync(new DhcpReservationIdentity(existing.MacAddress, existing.IpAddress, existing.Hostname), request, CancellationToken.None);
                if (!result.Success) return "RouterPilot could not apply or verify the DHCP reservation.";
                if (Application.Current.MainWindow is DashboardWindow dashboard) await dashboard.RefreshNowAsync();
                ClientRefreshNotifier.RequestRefresh();
                _viewModel.StatusMessage = "DHCP reservation updated.";
                return null;
            }
            catch { _viewModel.StatusMessage = "DHCP reservation could not be applied."; return "RouterPilot could not apply the DHCP reservation."; }
        }

        private static bool SameMac(string first, string second) => ClientIdentity.MacEquals(first, second);
        private sealed record DhcpClientReservationAction(DhcpReservationInfo? Reservation, bool CanWrite);

        private void CopyClientValue(string? value, string confirmation)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-")
                return;
            Clipboard.SetText(value);
            _viewModel.StatusMessage = confirmation;
        }

        private async Task RunClientDiagnosticAsync(ClientInfo client, bool ping)
        {
            try
            {
                RouterManager router = await ((App)Application.Current).Services
                    .GetRequiredService<IRouterManagerProvider>().GetRouterManagerAsync();
                string result = ping
                    ? await router.PingClientAsync(client.IpAddress)
                    : await router.TracerouteAsync(client.IpAddress);
                _viewModel.PingResult = result;
                _viewModel.StatusMessage = (ping ? "Ping" : "Traceroute") + " completed for " + client.Name + ".";
            }
            catch
            {
                _viewModel.StatusMessage = (ping ? "Ping" : "Traceroute") + " could not be completed.";
            }
        }

        private void ActivateClient(ClientInfo client)
        {
            // Use the item from the activated card rather than whichever
            // container happens to hold selection during virtualised layout.
            _viewModel.SelectedClient = client;
            ClientsGrid.SelectedItem = client;
        }

        private void ClientsGrid_RequestBringIntoView(
            object sender,
            RequestBringIntoViewEventArgs e)
        {
            // The outer page owns page scrolling. Selection restoration and
            // keyboard/listbox focus must never implicitly reposition it.
            e.Handled = true;
        }

        private void OpenSelectedClient()
        {
            ClientInfo? client =
                _viewModel.SelectedClient;

            if (client is null)
            {
                _viewModel.StatusMessage =
                    "Select a client first.";
                return;
            }

            OpenClientDetails(client);
        }

        private void OpenClientDetails(ClientInfo client)
        {
            // Keep the card list stable while the user is viewing or editing
            // the selected client. Automatic refresh resumes when the details
            // window closes.
            bool resumeRefresh = _refreshTimer.IsEnabled;
            _refreshTimer.Stop();

            try
            {
                if (Window.GetWindow(this) is DashboardWindow dashboard)
                    dashboard.OpenClientDetailsForResolvedClient(client);
                else
                    new ClientDetailsWindow(client) { Owner = Window.GetWindow(this) }.ShowDialog();
            }
            finally
            {
                if (resumeRefresh && IsLoaded)
                {
                    _refreshTimer.Start();
                }
            }
        }

        private void KnownDevices_Click(object sender, RoutedEventArgs e) =>
            (Application.Current.MainWindow as DashboardWindow)?.ShowKnownDevices();
    }
}
