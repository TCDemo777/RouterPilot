using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;
using System.Threading.Tasks;

namespace RouterPilot.Views;

public partial class KnownDevicesView : UserControl
{
    private readonly KnownDevicesViewModel _viewModel;
    public KnownDevicesView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<KnownDevicesViewModel>();
        DataContext = _viewModel;
        Loaded += KnownDevicesView_Loaded;
        Unloaded += KnownDevicesView_Unloaded;
        IsVisibleChanged += KnownDevicesView_IsVisibleChanged;
    }

    private async void KnownDevicesView_Loaded(object sender, RoutedEventArgs e) => await ActivateAsync();

    private void KnownDevicesView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;

        if (IsVisible)
            _ = ActivateAsync();
        else
            _viewModel.Stop();
    }

    private async Task ActivateAsync()
    {
        if (IsVisible)
            await _viewModel.StartAsync();
    }

    private void KnownDevicesView_Unloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= KnownDevicesView_Unloaded;
        _viewModel.Stop();
        _viewModel.Dispose();
    }

    private void CurrentClients_Click(object sender, System.Windows.RoutedEventArgs e) =>
        (Application.Current.MainWindow as DashboardWindow)?.ShowClients();
    private void Devices_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedDevice is null) return;
        DashboardWindow? dashboard = Application.Current.MainWindow as DashboardWindow;
        if (dashboard is not null && dashboard.OpenClientDetailsForDeviceIdentity(_viewModel.SelectedDevice.MacKey))
            return;

        // If the dashboard is not available (for example, during a standalone
        // view test), retain the same standard detail layout with the persisted
        // Known Device projection.
        new ClientDetailsWindow(_viewModel.SelectedDevice.ToClientInfo(), allowLiveRefresh: _viewModel.SelectedDevice.IsOnline)
        { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();
        _viewModel.ReloadProfiles();
    }

    private void AutoScrollToTopCheckBox_Checked(object sender, RoutedEventArgs e) { }
}
