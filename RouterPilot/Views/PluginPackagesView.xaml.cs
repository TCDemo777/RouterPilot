using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class PluginPackagesView : System.Windows.Controls.UserControl
{
    private readonly PluginPackagesViewModel _viewModel;

    public PluginPackagesView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<PluginPackagesViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }
}
