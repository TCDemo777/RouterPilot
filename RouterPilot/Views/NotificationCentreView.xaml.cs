using System.Windows;
using System.Windows.Controls;
using RouterPilot.Models;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class NotificationCentreView : UserControl
{
    public NotificationCentreView(NotificationCentreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ViewDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppNotification notification })
            return;

        if (Window.GetWindow(this) is DashboardWindow dashboard)
            dashboard.OpenClientDetailsForDeviceIdentity(notification.ActionTarget);
    }
}
