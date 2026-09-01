using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class MaintenanceView : UserControl
{
    private readonly Func<Task> _refreshAll;
    private bool _backupPrivacyWarningAcknowledged;
    private bool _navigateToFirmwareWhenLoaded;

    public MaintenanceView(MaintenanceViewModel viewModel, DashboardViewModel dashboard, Func<Task> refreshAll)
    {
        InitializeComponent();
        _refreshAll = refreshAll;
        viewModel.AttachDashboard(dashboard);
        DataContext = viewModel;
        Loaded += MaintenanceView_Loaded;
    }

    public void NavigateToFirmware()
    {
        if (IsLoaded)
        {
            FirmwareSection.BringIntoView();
            return;
        }

        _navigateToFirmwareWhenLoaded = true;
    }

    private void MaintenanceView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_navigateToFirmwareWhenLoaded)
            return;

        _navigateToFirmwareWhenLoaded = false;
        FirmwareSection.BringIntoView();
    }

    private async void RunAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MaintenanceActionItem action } ||
            DataContext is not MaintenanceViewModel viewModel ||
            !ConfirmAction(action))
        {
            return;
        }

        await viewModel.ExecuteAsync(action, _refreshAll);
    }

    private void ActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void RunActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: MaintenanceActionItem action } ||
            DataContext is not MaintenanceViewModel viewModel || !ConfirmAction(action))
            return;
        _ = RunActionAsync(viewModel, action);
    }

    private async Task RunActionAsync(MaintenanceViewModel viewModel, MaintenanceActionItem action) =>
        await viewModel.ExecuteAsync(action, _refreshAll);

    private void ViewActionHistory_Click(object sender, RoutedEventArgs e) =>
        MaintenanceHistoryHeading.BringIntoView();

    private void CopyLastResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MaintenanceActionItem action } &&
            !string.IsNullOrWhiteSpace(action.LastResult))
        {
            Clipboard.SetText(action.LastResult);
        }
    }

    internal static bool ConfirmAction(MaintenanceActionItem action)
    {
        (string message, MessageBoxImage icon)? confirmation = action.Action switch
        {
            MaintenanceAction.RestartWifi =>
                ("Restart Wi-Fi now? Connected wireless devices will disconnect temporarily.", MessageBoxImage.Warning),
            MaintenanceAction.RestartAdGuard =>
                ("Restart AdGuard Home now? DNS filtering may be briefly unavailable.", MessageBoxImage.Warning),
            MaintenanceAction.ReconnectWan =>
                ("Reconnect WAN now? Internet access may pause briefly.", MessageBoxImage.Warning),
            MaintenanceAction.RebootRouter =>
                ("Reboot the router now? Internet and local connectivity will be interrupted while it restarts.", MessageBoxImage.Error),
            _ => null
        };

        return confirmation is null || MessageBox.Show(
            confirmation.Value.message,
            action.Title,
            MessageBoxButton.YesNo,
            confirmation.Value.icon) == MessageBoxResult.Yes;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel)
            return;

        SaveFileDialog dialog = new()
        {
            Title = "Create RouterPilot Backup",
            Filter = "RouterPilot backup (*.rpb)|*.rpb",
            DefaultExt = ".rpb",
            AddExtension = true,
            FileName = "RouterPilotBackup_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".rpb"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        if (!_backupPrivacyWarningAcknowledged &&
            MessageBox.Show(
                "RouterPilot backup files are not encrypted. Passwords remain protected by Windows, but the backup may contain network, device and configuration information. Store backup files securely.",
                "Backup privacy notice",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        _backupPrivacyWarningAcknowledged = true;

        MaintenanceOperationResult? result = await viewModel.CreateBackupAsync(dialog.FileName);
        if (result is not null)
        {
            MessageBox.Show(result.Message, "RouterPilot Backup", MessageBoxButton.OK,
                result.Outcome == MaintenanceOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel)
            return;

        OpenFileDialog dialog = new()
        {
            Title = "Restore RouterPilot Backup",
            Filter = "RouterPilot backup (*.rpb)|*.rpb",
            DefaultExt = ".rpb",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        BackupInspection inspection;
        try
        {
            inspection = await viewModel.InspectBackupAsync(dialog.FileName);
        }
        catch (Exception)
        {
            MessageBox.Show("This backup could not be validated. No files were changed.", "Restore backup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BackupRestorePreviewWindow preview = new(inspection) { Owner = Window.GetWindow(this) };
        if (preview.ShowDialog() != true ||
            MessageBox.Show(
                "Restore the selected RouterPilot data? A pre-restore backup will be created first.",
                "Confirm restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        MaintenanceOperationResult? result = await viewModel.RestoreBackupAsync(
            inspection,
            preview.ViewModel.SelectedFiles);
        if (result is not null)
        {
            MessageBox.Show(result.Message, "Restore backup", MessageBoxButton.OK,
                result.Outcome == MaintenanceOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (result.Outcome == MaintenanceOutcome.Success &&
                preview.ViewModel.SelectedFiles.Contains("client-profiles.json") &&
                MessageBox.Show(
                    "Restart RouterPilot now to apply restored client profiles?",
                    "Restart RouterPilot",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes &&
                Application.Current is App app)
            {
                await app.RestartAsync();
            }
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel)
            return;

        Directory.CreateDirectory(viewModel.BackupFolder);
        Process.Start(new ProcessStartInfo
        {
            FileName = viewModel.BackupFolder,
            UseShellExecute = true
        });
    }

    private void CopySupportSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel) return;
        try
        {
            Clipboard.SetText(viewModel.BuildSupportSnapshot());
            MessageBox.Show("Support snapshot copied.", "RouterPilot Support", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("Support snapshot could not be copied.", "RouterPilot Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportSupportSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel) return;
        SaveFileDialog dialog = new() { Title = "Export support snapshot", Filter = "Text file (*.txt)|*.txt|Markdown file (*.md)|*.md", DefaultExt = ".txt", FileName = "RouterPilot-Support-Snapshot.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, viewModel.BuildSupportSnapshot());
            MessageBox.Show("Support snapshot exported.", "RouterPilot Support", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("Support snapshot could not be exported.", "RouterPilot Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CheckFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel)
            return;

        await viewModel.CheckFirmwareAsync();
    }

    private void OpenFirmwarePage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceViewModel viewModel)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(viewModel.FirmwareLink))
        {
            MessageBox.Show(viewModel.FirmwareUpdate.ReleaseNotes,
                "Firmware release notes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string link = viewModel.FirmwareLink!;
        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("gl-inet.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".gl-inet.com", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
