using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
using RouterPilot.ViewModels;
using RouterPilot.Services;
using RouterPilot.Models;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class ProtectionView : UserControl
    {
        private readonly ProtectionViewModel _viewModel;
        private bool _isActive;
        public ProtectionView()
        {
            InitializeComponent();
            _viewModel = ((App)Application.Current).Services
                .GetRequiredService<ProtectionViewModel>();
            DataContext = _viewModel;
            InsightsTab.DataContext = Application.Current.MainWindow?.DataContext as DashboardViewModel;
            Loaded += ProtectionView_Loaded;
            Unloaded += ProtectionView_Unloaded;
            IsVisibleChanged += ProtectionView_IsVisibleChanged;
        }
        private async void ProtectionView_Loaded(object sender, RoutedEventArgs e) => await ActivateAsync();
        private void ProtectionView_Unloaded(object sender, RoutedEventArgs e) => Deactivate();

        private async void ProtectionView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;

            if (IsVisible)
                await ActivateAsync();
            else
                Deactivate();
        }

        private async Task ActivateAsync()
        {
            if (_isActive || !IsVisible) return;

            _isActive = true;
            await _viewModel.StartAsync();

            if (!_isActive)
                _viewModel.Stop();
        }

        private void Deactivate()
        {
            _isActive = false;
            _viewModel.Stop();
        }

        private void ProtectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, ProtectionTabs)) return;

            ScrollViewer? selected = ProtectionTabs.SelectedIndex switch
            {
                0 => ProtectionScrollViewer,
                1 => InsightsScrollViewer,
                2 => FiltersScrollViewer,
                3 => BlocklistsScrollViewer,
                4 => BlockedServicesScrollViewer,
                5 => SchedulesScrollViewer,
                _ => null
            };

            if (ProtectionTabs.SelectedIndex == 3)
                _ = _viewModel.LoadBlocklistsAsync();

            // Let the TabControl activate and lay out its new content first;
            // this runs only for a direct tab selection, never for data updates.
            if (selected is not null)
                _ = Dispatcher.BeginInvoke(selected.ScrollToTop, DispatcherPriority.Loaded);
        }

        private void ViewTopBlockedDomainActivity_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.HasTopBlockedDomain &&
                Window.GetWindow(this) is DashboardWindow dashboard)
            {
                dashboard.NavigateToDnsActivityForDomain(_viewModel.TopBlockedDomain);
            }
        }

        private void ViewInsightsDomainActivity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string domain } &&
                Window.GetWindow(this) is DashboardWindow dashboard)
            {
                dashboard.NavigateToDnsActivityForDomain(domain);
            }
        }

        private void CopyProtectionSummary_Click(object sender, RoutedEventArgs e)
        {
            if (InsightsTab.DataContext is not DashboardViewModel dashboard) return;
            try
            {
                Clipboard.SetText(dashboard.BuildProtectionSummary());
                _viewModel.SetMessage("Protection summary copied.");
            }
            catch
            {
                _viewModel.SetMessage("Protection summary could not be copied.");
            }
        }

        private void AddBlocklist_Click(object sender, RoutedEventArgs e) =>
            BlocklistEditorDialog.Show(Window.GetWindow(this), "Add blocklist", null, _viewModel.AddBlocklistAsync);

        private void EditBlocklist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: AdGuardBlocklist blocklist }) return;
            BlocklistEditorDialog.Show(Window.GetWindow(this), "Edit blocklist", blocklist,
                draft => _viewModel.EditBlocklistAsync(blocklist, draft));
        }

        private async void RemoveBlocklist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: AdGuardBlocklist blocklist }) return;
            string name = string.IsNullOrWhiteSpace(blocklist.Name) ? blocklist.Url : blocklist.Name;
            if (MessageBox.Show($"Remove blocklist?\n\n{name} will be removed from AdGuard Home.", "Remove blocklist", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            string? failure = await _viewModel.RemoveBlocklistAsync(blocklist);
            if (failure is not null)
                MessageBox.Show(failure, "Remove blocklist", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private Expander? ScheduleEditor => FindDescendant<Expander>(this, "ScheduleEditor");
        private Expander? AllowedWindowEditor => FindDescendant<Expander>(this, "AllowedWindowEditor");

        private void AddSchedule_Click(object sender, RoutedEventArgs e)
        {
            ShowEditor(showAllowedWindow: false);
        }

        private void OpenAllowedWindow_Click(object sender, RoutedEventArgs e)
        {
            ShowEditor(showAllowedWindow: true);
        }

        private void EditWindow_Click(object sender, RoutedEventArgs e)
        {
            ShowEditor(showAllowedWindow: true);
        }

        private void EditSchedule_Click(object sender, RoutedEventArgs e)
        {
            ShowEditor(showAllowedWindow: false);
        }

        private void CancelSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (ScheduleEditor is { } editor) editor.IsExpanded = false;
        }

        private void CancelWindow_Click(object sender, RoutedEventArgs e)
        {
            if (AllowedWindowEditor is { } editor) editor.IsExpanded = false;
        }

        private void ShowEditor(bool showAllowedWindow)
        {
            if (ScheduleEditor is { } scheduleEditor)
                scheduleEditor.IsExpanded = !showAllowedWindow;
            if (AllowedWindowEditor is { } windowEditor)
                windowEditor.IsExpanded = showAllowedWindow;

            (showAllowedWindow ? AllowedWindowEditor : ScheduleEditor)?.Focus();
        }

        private static T? FindDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match && match.Name == name) return match;
                if (FindDescendant<T>(child, name) is { } descendant) return descendant;
            }

            return null;
        }

        private void RunWindowAllowNow_Click(object sender, RoutedEventArgs e) =>
            ConfirmWindowAction(sender, Models.AdGuardServiceScheduleAction.Allow);

        private void RunWindowBlockNow_Click(object sender, RoutedEventArgs e) =>
            ConfirmWindowAction(sender, Models.AdGuardServiceScheduleAction.Block);

        private void WindowMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu is null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }

        private void DuplicateWindow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Models.AdGuardServiceWindow window)
                _viewModel.Schedules.DuplicateWindowCommand.Execute(window);
        }

        private void ConfirmWindowAction(object sender, Models.AdGuardServiceScheduleAction action)
        {
            if ((sender as FrameworkElement)?.DataContext is not Models.AdGuardServiceWindow window) return;
            if (MessageBox.Show($"{action} services in '{window.Name}' now?", "RouterPilot", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (action == Models.AdGuardServiceScheduleAction.Allow)
                _viewModel.Schedules.RunAllowNowCommand.Execute(window);
            else
                _viewModel.Schedules.RunBlockNowCommand.Execute(window);
        }

        private void DeleteWindow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Models.AdGuardServiceWindow window) return;
            if (MessageBox.Show($"Delete the complete allowed-time window '{window.Name}'?", "RouterPilot", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _viewModel.Schedules.DeleteWindowCommand.Execute(window);
        }

        private void RunScheduleNow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Models.AdGuardServiceSchedule schedule) return;
            if (MessageBox.Show($"Run '{schedule.Name}' now?", "RouterPilot", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _viewModel.Schedules.RunNowCommand.Execute(schedule);
        }

        private void DeleteSchedule_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Models.AdGuardServiceSchedule schedule) return;
            if (MessageBox.Show($"Delete '{schedule.Name}'?", "RouterPilot", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _viewModel.Schedules.DeleteCommand.Execute(schedule);
        }
    }
}
