using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class PluginPackagesView : System.Windows.Controls.UserControl
{
    private readonly PluginPackagesViewModel _viewModel;

    public PluginPackagesView()
    {
        InitializeComponent();
        AddUpdateButton();
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
        if (_viewModel.SelectedPackage is null || MessageBox.Show($"Uninstall {_viewModel.SelectedPackage.Name}?\n\nThis changes software installed on the router.", "Uninstall plug-in", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _viewModel.RemoveSelectedAsync();
    }

    private async void RefreshIndexes_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshIndexesAsync();

    private void AddUpdateButton()
    {
        StackPanel? actions = FindVisualChildren<StackPanel>(this)
            .FirstOrDefault(panel => panel.Orientation == Orientation.Horizontal && Grid.GetRow(panel) == 4);
        if (actions is null || actions.Children.OfType<Button>().Any(button => string.Equals(button.Content?.ToString(), "Update", StringComparison.Ordinal))) return;
        var button = new Button { Content = "Update", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        button.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(PluginPackagesViewModel.CanUpdate)) { Converter = (IValueConverter)FindResource("BooleanToVisibilityConverter") });
        button.Click += Update_Click;
        actions.Children.Insert(Math.Min(1, actions.Children.Count), button);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPackage is null || MessageBox.Show($"Update {_viewModel.SelectedPackage.Name}?\n\nInstalled: {_viewModel.SelectedPackage.DisplayInstalledVersion}\nAvailable: {_viewModel.SelectedPackage.DisplayAvailableVersion}", "Update plug-in", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _viewModel.UpdateSelectedAsync();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
