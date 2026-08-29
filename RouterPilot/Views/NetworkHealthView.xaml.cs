using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;
public partial class NetworkHealthView : UserControl
{
    private readonly NetworkHealthViewModel _viewModel;
    public NetworkHealthView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<NetworkHealthViewModel>();
        DataContext = _viewModel;
    }
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target } || Window.GetWindow(this) is not DashboardWindow dashboard) return;
        if (target is "wifi" or "dhcp" or "vpn") dashboard.NavigateToNetworkSection(target);
        else dashboard.NavigateToHealthTarget(target);
    }
}
