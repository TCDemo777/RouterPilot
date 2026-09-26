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
        dashboard.NavigateToHealthTarget(target);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is DashboardWindow dashboard)
            await dashboard.RefreshNowAsync();
    }
}
