using System.Windows;
using System.Windows.Controls;
using RouterPilot.ViewModels;
using RouterPilot.Models;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class GlobalSearchView : UserControl
    {
        private readonly GlobalSearchViewModel _viewModel;

        public GlobalSearchView()
        {
            InitializeComponent();

            _viewModel =
                ((App)Application.Current).Services
                    .GetRequiredService<GlobalSearchViewModel>();

            DataContext =
                _viewModel;

            Loaded += GlobalSearchView_Loaded;
        }

        private void GlobalSearchView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            Loaded -= GlobalSearchView_Loaded;
            if (Application.Current.MainWindow?.DataContext is DashboardViewModel dashboard) _viewModel.Attach(dashboard);
            SearchBox.Focus();
        }

        private void ResultsList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is GlobalSearchResult result) Navigate(result);
        }

        private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key is System.Windows.Input.Key.Down or System.Windows.Input.Key.Up)
            {
                if (ResultsList.Items.Count > 0)
                {
                    int current = ResultsList.SelectedIndex < 0 ? 0 : ResultsList.SelectedIndex;
                    ResultsList.SelectedIndex = e.Key == System.Windows.Input.Key.Down ? Math.Min(current + 1, ResultsList.Items.Count - 1) : Math.Max(current - 1, 0);
                    ResultsList.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                _viewModel.SearchText = string.Empty;
                ResultsList.SelectedItem = null;
                e.Handled = true;
            }
        }

        private void ResultsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && ResultsList.SelectedItem is GlobalSearchResult result) { Navigate(result); e.Handled = true; }
            if (e.Key == System.Windows.Input.Key.Escape) { _viewModel.SearchText = string.Empty; ResultsList.SelectedItem = null; SearchBox.Focus(); e.Handled = true; }
        }

        private void Navigate(GlobalSearchResult result)
        {
            if (Application.Current.MainWindow is DashboardWindow dashboard) dashboard.NavigateToGlobalSearchResult(result);
        }
    }
}
