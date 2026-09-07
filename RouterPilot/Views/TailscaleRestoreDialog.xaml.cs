using System.Windows;
namespace RouterPilot.Views;
public partial class TailscaleRestoreDialog : Window
{
    public TailscaleRestoreDialog() => InitializeComponent();
    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
