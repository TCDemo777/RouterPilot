using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class KnownDevicesView : UserControl
{
    private readonly KnownDevicesViewModel _viewModel;
    public KnownDevicesView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<KnownDevicesViewModel>();
        DataContext = _viewModel;
    }
    private void CurrentClients_Click(object sender, System.Windows.RoutedEventArgs e) =>
        (Application.Current.MainWindow as DashboardWindow)?.ShowClients();
    private void Devices_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedDevice is null) return;
        new ClientDetailsWindow(_viewModel.SelectedDevice.ToClientInfo(), allowLiveRefresh: _viewModel.SelectedDevice.IsOnline)
        { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();
        _viewModel.ReloadProfiles();
    }
}
