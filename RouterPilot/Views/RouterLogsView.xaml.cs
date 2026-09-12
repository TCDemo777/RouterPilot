using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;
namespace RouterPilot.Views;
public partial class RouterLogsView : System.Windows.Controls.UserControl
{
    private readonly RouterLogsViewModel _viewModel;
    private readonly bool _refreshOnLoad;

    public RouterLogsView(bool refreshOnLoad = true)
    {
        InitializeComponent();
        _refreshOnLoad = refreshOnLoad;
        _viewModel = ((App)Application.Current).Services.GetRequiredService<RouterLogsViewModel>();
        DataContext = _viewModel;
        ConfigureSearchVisuals();
        Loaded += RouterLogsView_Loaded;
    }

    private async void RouterLogsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_refreshOnLoad)
            await _viewModel.RefreshAsync();
    }

    private void ConfigureSearchVisuals()
    {
        SearchBox.Padding = new Thickness(36, 6, 10, 6);
        if (SearchBox.Parent is not Grid searchGrid)
            return;

        var icon = new TextBlock { Style = (Style)FindResource("Search.Icon") };
        searchGrid.Children.Add(icon);

        var placeholder = new TextBlock
        {
            Margin = new Thickness(36, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Text = "Search"
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        searchGrid.Children.Add(placeholder);

        void UpdatePlaceholder(object? sender, RoutedEventArgs args) =>
            placeholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;

        SearchBox.TextChanged += UpdatePlaceholder;
        SearchBox.GotKeyboardFocus += UpdatePlaceholder;
        SearchBox.LostKeyboardFocus += UpdatePlaceholder;
        UpdatePlaceholder(null, new RoutedEventArgs());
    }
}
