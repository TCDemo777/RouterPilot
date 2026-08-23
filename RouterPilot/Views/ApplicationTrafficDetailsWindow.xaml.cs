using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class ApplicationTrafficDetailsWindow : Window
{
    private readonly ApplicationTrafficDetailsViewModel _viewModel;

    public ApplicationTrafficDetailsWindow(string applicationId, string applicationName)
    {
        InitializeComponent();
        _viewModel = ActivatorUtilities.CreateInstance<ApplicationTrafficDetailsViewModel>(
            ((App)Application.Current).Services, applicationId, applicationName);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void ViewClient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string mac } && Owner is DashboardWindow dashboard)
            await dashboard.OpenClientDetailsForDeviceIdentityAsync(mac);
    }

    private async void ChangeProtection_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanChangeProtection) return;

        bool block = !_viewModel.IsProtectionBlocked;
        string action = block ? "Block" : "Unblock";
        string message = block
            ? $"Block {_viewModel.Label}?\n\nThis will prevent traffic classified by the router as {_viewModel.Label}."
            : $"Unblock {_viewModel.Label}?\n\nThis will allow traffic previously blocked by this application rule.";
        if (MessageBox.Show(message, $"{action} application", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await _viewModel.ChangeProtectionAsync(block);
    }
}
