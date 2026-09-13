using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class RouterFirmwareTabView : UserControl
{
    private readonly MaintenanceViewModel _viewModel;

    public RouterFirmwareTabView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<MaintenanceViewModel>();
        _viewModel.AttachDashboard(((App)Application.Current).Services.GetRequiredService<DashboardViewModel>());
        DataContext = _viewModel;
    }

    private async void CheckFirmware_Click(object sender, RoutedEventArgs e) => await _viewModel.CheckFirmwareAsync();

    private async void PrepareFirmwareUpgrade_Click(object sender, RoutedEventArgs e) => await _viewModel.PrepareForFirmwareUpgradeAsync(RefreshAllAsync);

    private async void RunPostUpgradeCheck_Click(object sender, RoutedEventArgs e) => await _viewModel.RunPostUpgradeCheckAsync(RefreshAllAsync);

    private async void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        string version = _viewModel.FirmwareLatestVersion;
        if (string.IsNullOrWhiteSpace(version) || version is "—" or "Unavailable" or "No newer version available")
            return;

        var window = new FirmwareReleaseNotesWindow(version, installed: false) { Owner = Window.GetWindow(this) };
        window.Show();
        await window.LoadAsync(ct => _viewModel.GetFirmwareReleaseNotesAsync(_viewModel.Dashboard.RouterModel, version, ct));
    }

    private Task RefreshAllAsync() => Window.GetWindow(this) is DashboardWindow window
        ? window.RefreshForFirmwareLifecycleAsync()
        : Task.CompletedTask;
}
