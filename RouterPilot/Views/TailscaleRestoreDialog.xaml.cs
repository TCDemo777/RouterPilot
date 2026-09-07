using System.Windows;
using RouterPilot.Services;
namespace RouterPilot.Views;
public partial class TailscaleRestoreDialog : Window
{
    public TailscaleRestoreDialog() => InitializeComponent();
    private void AcknowledgementChanged(object sender, RoutedEventArgs e) => OpenButton.IsEnabled = TailscaleUpdaterCommand.CanOpenRestoreTerminal(Acknowledgement.IsChecked == true);
    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
