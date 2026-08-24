using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using RouterPilot.Models;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class ClientDetailsWindow : Window
    {
        private readonly ClientDetailsViewModel _viewModel;
        private bool _isRefreshActive;

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
            IEnumerable<PortForwardRuleInfo> portForwardRules =
                dashboard?.PortForwardRules.ToArray() ?? Array.Empty<PortForwardRuleInfo>();

            _viewModel =
                ActivatorUtilities.CreateInstance<ClientDetailsViewModel>(
                    ((App)Application.Current).Services,
                    client,
                    dhcpLeases,
                    dhcpReservations,
                    portForwardRules);

            DataContext = _viewModel;
            _viewModel.DeviceForgotten += ViewModel_DeviceForgotten;

            AllowLiveRefresh = allowLiveRefresh;

            Loaded += ClientDetailsWindow_Loaded;
            Closed += ClientDetailsWindow_Closed;
            IsVisibleChanged += ClientDetailsWindow_IsVisibleChanged;
            StateChanged += ClientDetailsWindow_StateChanged;
        }

        private bool AllowLiveRefresh { get; }

        private async void ClientDetailsWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            await ActivateRefreshAsync();
        }

        private async void ClientDetailsWindow_IsVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;

            if (IsVisible && WindowState != WindowState.Minimized)
                await ActivateRefreshAsync();
            else
                DeactivateRefresh();
        }

        private async void ClientDetailsWindow_StateChanged(object? sender, EventArgs e)
        {
            if (!IsLoaded) return;

            if (WindowState == WindowState.Minimized)
                DeactivateRefresh();
            else if (IsVisible)
                await ActivateRefreshAsync();
        }

        private async Task ActivateRefreshAsync()
        {
            if (!AllowLiveRefresh || _isRefreshActive || !IsVisible ||
                WindowState == WindowState.Minimized)
            {
                return;
            }

            _isRefreshActive = true;
            await _viewModel.StartAsync();

            if (!_isRefreshActive)
                _viewModel.Stop();
        }

        private void DeactivateRefresh()
        {
            _isRefreshActive = false;
            _viewModel.Stop();
        }

        private void ClientDetailsWindow_Closed(
            object? sender,
            EventArgs e)
        {
            DeactivateRefresh();
            _viewModel.DeviceForgotten -= ViewModel_DeviceForgotten;
            _viewModel.Dispose();
        }

        private void ViewModel_DeviceForgotten(object? sender, EventArgs e) => Close();
    }
}
