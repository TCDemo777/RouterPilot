using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

namespace RouterPilot.Views
{
    public partial class OverviewView : UserControl
    {
        private readonly MaintenanceViewModel _maintenance;
        private readonly Func<Task> _refreshAll;
        private bool _backupPrivacyWarningAcknowledged;

        public OverviewView(MaintenanceViewModel maintenance,
            DashboardViewModel dashboard, Func<Task> refreshAll)
        {
            InitializeComponent();
            _maintenance = maintenance;
            _maintenance.AttachDashboard(dashboard);
            _refreshAll = refreshAll;
            DataContext = dashboard;
            _maintenance.PropertyChanged += Maintenance_PropertyChanged;
            Loaded += (_, _) => RefreshQuickActionAvailability();
            Unloaded += (_, _) => _maintenance.PropertyChanged -= Maintenance_PropertyChanged;
        }

        private void Maintenance_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
            Dispatcher.InvokeAsync(RefreshQuickActionAvailability);

        private void RefreshQuickActionAvailability()
        {
            bool free = !_maintenance.IsBusy;
            RefreshQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RefreshAll);
            DiagnosticsQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RunDiagnostics);
            RestartAdGuardQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RestartAdGuard);
            RestartWifiQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RestartWifi);
            BackupQuickAction.IsEnabled = free;
            FirmwareQuickAction.IsEnabled = free && _maintenance.CanCheckFirmware;
            RouterUiQuickAction.IsEnabled = !_maintenance.IsBusy && _maintenance.Dashboard.RouterConnected;
        }

        private bool ActionAvailable(MaintenanceAction action) =>
            _maintenance.Actions.FirstOrDefault(item => item.Action == action)?.IsAvailable == true;

        private async void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string actionName } || _maintenance.IsBusy)
                return;

            if (actionName == "CreateBackup")
            {
                await CreateBackupAsync();
                return;
            }

            if (actionName == "CheckFirmware")
            {
                await _maintenance.CheckFirmwareAsync();
                return;
            }

            if (actionName == "OpenRouter")
            {
                OpenRouterUi();
                return;
            }

            if (!Enum.TryParse(actionName, out MaintenanceAction action))
                return;

            MaintenanceActionItem? item = _maintenance.Actions.FirstOrDefault(x => x.Action == action);
            if (item is null || !item.IsAvailable || !MaintenanceView.ConfirmAction(item))
                return;

            await _maintenance.ExecuteAsync(item, _refreshAll);
        }

        private async Task CreateBackupAsync()
        {
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
                MessageBox.Show("RouterPilot backup files are not encrypted. Passwords remain protected by Windows, but the backup may contain network, device and configuration information. Store backup files securely.",
                    "Backup privacy notice", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            _backupPrivacyWarningAcknowledged = true;
            await _maintenance.CreateBackupAsync(dialog.FileName);
        }

        private void OpenRouterUi()
        {
            try
            {
                AppSettings settings = new SettingsService().Load();
                RouterConnectionOptions options = new SettingsService().CreateConnectionOptions(settings);
                Uri uri = new RouterEndpointProvider(options).RouterBaseUri;
                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    return;
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("The configured router address is not valid.", "Open Router UI",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
