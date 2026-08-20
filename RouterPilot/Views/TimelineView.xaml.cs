using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using RouterPilot.Models;

namespace RouterPilot.Views;

public partial class TimelineView : UserControl
{
    private readonly TimelineViewModel _viewModel;

    public TimelineView()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<TimelineViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.MarkReadAsync();
    }

    private void ToggleIncident_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NetworkIncident incident }) incident.IsExpanded = !incident.IsExpanded;
    }
}
