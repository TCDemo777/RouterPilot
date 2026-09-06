using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
}
