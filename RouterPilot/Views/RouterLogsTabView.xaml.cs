using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

/// <summary>
/// Passive Router-tab host for the canonical Router Logs view model. Loading the
/// tab deliberately does not refresh logs; Refresh remains the explicit action.
/// </summary>
public partial class RouterLogsTabView : UserControl
{
    public RouterLogsTabView()
    {
        InitializeComponent();
        DataContext = ((App)Application.Current).Services.GetRequiredService<RouterLogsViewModel>();
    }
}
