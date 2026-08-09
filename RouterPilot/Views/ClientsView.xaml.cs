using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        private bool _isRefreshNotifierSubscribed;

        public ClientsView()
        {
            InitializeComponent();

            _viewModel = ((App)Application.Current).Services
                .GetRequiredService<ClientsViewModel>();
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
        }

        private void SubscribeToRefreshNotifier()
        {
            if (_isRefreshNotifierSubscribed)
            {
                return;
            }

            ClientRefreshNotifier.RefreshRequested +=
                ClientRefreshNotifier_RefreshRequested;
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
            _isRefreshNotifierSubscribed = false;
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

        private async System.Threading.Tasks.Task RefreshClientsAsync()
        {
            try
            {
                await _viewModel.LoadClientsAsync();
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage =
                    "Unable to load clients: " +
                    ex.Message;
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

        private void ClientCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ClientInfo } card ||
                card.ContextMenu is null)
                return;

            // Context actions are scoped to the card under the pointer. Do
            // not alter SelectedClient or trigger the left-click auto-scroll.
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
            string selectedKey = GetClientSelectionKey(client);

            // Use the item from the activated card rather than whichever
            // container happens to hold selection during virtualised layout.
            _viewModel.SelectedClient = client;
            ClientsGrid.SelectedItem = client;

            if (AutoScrollToTopCheckBox.IsChecked != true)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_viewModel.SelectedClient is not null &&
                        string.Equals(
                            GetClientSelectionKey(_viewModel.SelectedClient),
                            selectedKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ClientsPageScrollViewer.ScrollToTop();
                    }
                }),
                DispatcherPriority.ContextIdle);
        }

        private void ClientsGrid_RequestBringIntoView(
            object sender,
            RequestBringIntoViewEventArgs e)
        {
            // Reapplying SelectedItem after a refresh must not pull the outer
            // page back down to the selected card.
            if (ItemsControl.ContainerFromElement(
                    ClientsGrid,
                    e.OriginalSource as DependencyObject) is ListBoxItem)
            {
                e.Handled = true;
            }
        }

        private static string GetClientSelectionKey(ClientInfo client)
        {
            string macAddress = string.Concat(
                (client.MacAddress ?? string.Empty)
                .Where(char.IsLetterOrDigit));

            return !string.IsNullOrEmpty(macAddress)
                ? macAddress.ToUpperInvariant()
                : client.IpAddress ?? string.Empty;
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
            var window =
                new ClientDetailsWindow(client)
                {
                    Owner = Window.GetWindow(this)
                };

            // Keep the card list stable while the user is viewing or editing
            // the selected client. Automatic refresh resumes when the details
            // window closes.
            bool resumeRefresh = _refreshTimer.IsEnabled;
            _refreshTimer.Stop();

            try
            {
                window.ShowDialog();
            }
            finally
            {
                if (resumeRefresh && IsLoaded)
                {
                    _refreshTimer.Start();
                }
            }
        }
    }
}
