using System.Windows;

namespace RouterPilot.Views;

public partial class SpeedTestBandwidthWarningDialog : Window
{
    public SpeedTestBandwidthWarningDialog()
    {
        InitializeComponent();
    }

    public bool SuppressFutureWarnings => SuppressWarningCheckBox.IsChecked == true;

    private void Run_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
