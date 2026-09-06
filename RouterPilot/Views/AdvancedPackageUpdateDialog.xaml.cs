using System.Windows;
using RouterPilot.Models;

namespace RouterPilot.Views;

public partial class AdvancedPackageUpdateDialog : Window
{
    private readonly PluginPackage _package;
    public AdvancedPackageUpdateDialog(PluginPackage package)
    {
        _package = package;
        InitializeComponent();
        DataContext = package;
        Owner = Application.Current?.MainWindow;
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => ConfirmButton.IsEnabled = Acknowledgement.IsChecked == true && string.Equals(PackageConfirmation.Text, _package.Name, StringComparison.Ordinal);
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
