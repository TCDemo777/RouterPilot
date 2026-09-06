using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;
namespace RouterPilot.Views;
public partial class PluginPackagesView : System.Windows.Controls.UserControl
{
    private readonly PluginPackagesViewModel _viewModel;
    private bool _layoutPrepared;

    public PluginPackagesView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<PluginPackagesViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            PrepareFullWidthLayout();
            await _viewModel.RefreshAsync();
        };
    }

    private void PrepareFullWidthLayout()
    {
        if (_layoutPrepared || Content is not Grid root)
            return;

        // The original XAML remains the source of the existing controls and
        // bindings. Re-home the two cards after loading so the virtualized
        // table gets the full page width and details occupy a separate row.
        Grid? split = root.Children.OfType<Grid>().FirstOrDefault(child => Grid.GetRow(child) == 3);
        if (split is null || split.Children.Count != 2)
            return;

        Border table = (Border)split.Children[0];
        Border details = (Border)split.Children[1];
        root.Children.Remove(split);
        root.RowDefinitions[3].Height = new GridLength(420);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.Margin = new Thickness(0, 0, 0, 14);
        details.Margin = new Thickness(0, 0, 0, 14);
        ArrangeDetails(details);
        root.Children.Add(table);
        root.Children.Add(details);
        Grid.SetRow(table, 3);
        Grid.SetRow(details, 4);

        // A page-level scroll host is needed only for the details row; the
        // DataGrid retains its bounded viewport and row virtualization.
        root.Children.Remove(table);
        root.Children.Remove(details);
        Content = null;
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        root.Children.Add(table);
        root.Children.Add(details);
        Grid.SetRow(table, 3);
        Grid.SetRow(details, 4);
        _layoutPrepared = true;
    }

    private static void ArrangeDetails(Border details)
    {
        if (details.Child is not ScrollViewer scroll || scroll.Content is not StackPanel old || old.Children.Count < 12)
            return;

        UIElement[] item = old.Children.Cast<UIElement>().ToArray();
        old.Children.Clear();
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(layout, item[0], 0);
        item[1].SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 14));
        Add(layout, item[1], 1);

        var summary = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (int i = 0; i < 4; i++) summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddPair(summary, item[2], item[3], 0);
        AddPair(summary, item[4], item[5], 1);
        AddPair(summary, item[10], item[11], 2);
        AddPair(summary, CreateLabel("Source"), CreateValue("—"), 3);
        Add(layout, summary, 2);

        var text = new Grid();
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddPair(text, item[6], item[7], 0);
        AddPair(text, item[8], item[9], 1);
        Add(layout, text, 3);
        scroll.Content = layout;
    }

    private static void Add(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private static void AddPair(Grid grid, UIElement label, UIElement value, int column)
    {
        var stack = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 12, 0, 12, 0) };
        stack.Children.Add(label);
        stack.Children.Add(value);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static TextBlock CreateLabel(string text) => new() { Text = text, Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary") };
    private static TextBlock CreateValue(string text) => new() { Text = text };
}
