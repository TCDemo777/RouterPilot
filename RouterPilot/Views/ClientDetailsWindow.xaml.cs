using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RouterPilot.Models;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class ClientDetailsWindow : Window
    {
        private readonly ClientDetailsViewModel _viewModel;

        public ClientDetailsWindow(
            ClientInfo client,
            bool allowLiveRefresh = true)
        {
            InitializeComponent();

            DashboardViewModel? dashboard =
                Application.Current.MainWindow?.DataContext as DashboardViewModel;
            IEnumerable<DhcpLeaseInfo> dhcpLeases =
                dashboard?.DhcpLeases.ToArray() ?? Array.Empty<DhcpLeaseInfo>();
            IEnumerable<DhcpReservationInfo> dhcpReservations =
                dashboard?.DhcpReservations.ToArray() ?? Array.Empty<DhcpReservationInfo>();

            _viewModel =
                ActivatorUtilities.CreateInstance<ClientDetailsViewModel>(
                    ((App)Application.Current).Services,
                    client,
                    dhcpLeases,
                    dhcpReservations);

            DataContext = _viewModel;

            AllowLiveRefresh = allowLiveRefresh;

            Loaded += ClientDetailsWindow_Loaded;
            Closed += ClientDetailsWindow_Closed;
        }

        private bool AllowLiveRefresh { get; }

        private async void ClientDetailsWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            if (AllowLiveRefresh) await _viewModel.StartAsync();
        }

        private void ClientDetailsWindow_Closed(
            object? sender,
            EventArgs e)
        {
            _viewModel.Stop();
        }
    }
}
