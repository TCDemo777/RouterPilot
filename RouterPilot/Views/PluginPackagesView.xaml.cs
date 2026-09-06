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

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPackage is null || MessageBox.Show($"Install {_viewModel.SelectedPackage.Name}?\n\nThis changes software installed on the router.", "Install plug-in", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _viewModel.InstallSelectedAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPackage is null || MessageBox.Show($"Remove {_viewModel.SelectedPackage.Name}?\n\nThis changes software installed on the router.", "Remove plug-in", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _viewModel.RemoveSelectedAsync();
    }

    private async void RefreshIndexes_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshIndexesAsync();
}
