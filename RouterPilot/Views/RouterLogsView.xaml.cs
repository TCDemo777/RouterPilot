using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;
namespace RouterPilot.Views;
public partial class RouterLogsView : System.Windows.Controls.UserControl
{
    private readonly RouterLogsViewModel _viewModel;
    public RouterLogsView() { InitializeComponent(); _viewModel = ((App)Application.Current).Services.GetRequiredService<RouterLogsViewModel>(); DataContext = _viewModel; Loaded += async (_, _) => await _viewModel.RefreshAsync(); }
}
