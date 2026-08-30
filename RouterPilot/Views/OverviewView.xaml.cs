using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly DashboardPreferencesService _dashboardPreferences;
        private bool _backupPrivacyWarningAcknowledged;

        public OverviewView(MaintenanceViewModel maintenance,
            DashboardViewModel dashboard, Func<Task> refreshAll)
        {
            InitializeComponent();
            _maintenance = maintenance;
            _maintenance.AttachDashboard(dashboard);
            _refreshAll = refreshAll;
            _dashboardPreferences = ((App)Application.Current).Services
                .GetRequiredService<DashboardPreferencesService>();
            DataContext = dashboard;
            _maintenance.PropertyChanged += Maintenance_PropertyChanged;
            _dashboardPreferences.Changed += DashboardPreferences_Changed;
            Loaded += (_, _) =>
            {
                RefreshQuickActionAvailability();
                ApplyDashboardPreferences();
            };
            Unloaded += (_, _) =>
            {
                _maintenance.PropertyChanged -= Maintenance_PropertyChanged;
                _dashboardPreferences.Changed -= DashboardPreferences_Changed;
            };
        }

        private void DashboardPreferences_Changed(object? sender, EventArgs e) =>
            Dispatcher.InvokeAsync(ApplyDashboardPreferences);

        private void OverviewView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Six cards remain balanced as two rows of three at the available
            // Overview width, including narrower Settings-window layouts.
            SystemHealthCards.Columns = 3;
        }

        private void ApplyDashboardPreferences()
        {
            Dictionary<string, Border> controls = new(StringComparer.OrdinalIgnoreCase)
            {
                ["router"] = RouterDashboardCard,
                ["adguard-home"] = AdGuardDashboardCard,
                ["internet"] = InternetDashboardCard,
                ["network-health"] = NetworkHealthDashboardCard,
                ["vpn-status"] = VpnDashboardCard
            };

            List<Border> visibleCards = _dashboardPreferences.Cards
                .OrderBy(card => card.DisplayOrder)
                .Where(card => card.IsVisible && controls.ContainsKey(card.Key))
                .Select(card => controls[card.Key])
                .ToList();

            // Network Health is a peer health section, not a member of the
            // two-column System details grid.
            List<Border> layoutCards = visibleCards
                .Where(card => !ReferenceEquals(card, NetworkHealthDashboardCard))
                .ToList();

            foreach (Border card in controls.Values)
                card.Visibility = visibleCards.Contains(card) ? Visibility.Visible : Visibility.Collapsed;

            for (int index = 0; index < layoutCards.Count; index++)
            {
                Border card = layoutCards[index];
                bool finalOddCard = layoutCards.Count % 2 == 1 && index == layoutCards.Count - 1;
                Grid.SetRow(card, (index / 2) * 2);
                Grid.SetColumn(card, finalOddCard ? 0 : (index % 2) * 2);
                Grid.SetColumnSpan(card, finalOddCard ? 3 : 1);
            }

            bool hasVisibleCards = visibleCards.Count > 0;
            DashboardDetailsGrid.Visibility = hasVisibleCards ? Visibility.Visible : Visibility.Collapsed;
            SystemDetailsHeading.Visibility = hasVisibleCards ? Visibility.Visible : Visibility.Collapsed;
            SystemDetailsSection.Visibility = hasVisibleCards ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HealthIssueNavigate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string target } && Application.Current.MainWindow is DashboardWindow dashboard)
                dashboard.NavigateToHealthTarget(target);
        }

        private void Maintenance_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
            Dispatcher.InvokeAsync(RefreshQuickActionAvailability);

        private void RefreshQuickActionAvailability()
        {
            bool free = !_maintenance.IsBusy;
            RefreshQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RefreshAll);
            RestartAdGuardQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RestartAdGuard);
            RestartWifiQuickAction.IsEnabled = free && ActionAvailable(MaintenanceAction.RestartWifi);
            BackupQuickAction.IsEnabled = free;
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
