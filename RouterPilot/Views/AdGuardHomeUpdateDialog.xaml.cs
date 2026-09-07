using System.Windows;
using RouterPilot.Services;

namespace RouterPilot.Views;

public partial class AdGuardHomeUpdateDialog : Window, System.ComponentModel.INotifyPropertyChanged
{
    public AdGuardHomeUpdateDialog(string installedVersion, string latestVersion)
    {
        InstalledVersion = installedVersion;
        LatestVersion = latestVersion;
        InitializeComponent();
        DataContext = this;
        OptionsChanged(this, new RoutedEventArgs());
    }

    public string InstalledVersion { get; }
    public string LatestVersion { get; }
    public bool SelectSpecificRelease => SelectRelease.IsChecked == true;
    public bool IgnoreFreeSpaceCheck => IgnoreFreeSpace.IsChecked == true;
    public string CommandPreview => AdGuardHomeUpdaterCommand.Build(SelectSpecificRelease, IgnoreFreeSpaceCheck);

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (RiskWarning is null) return;
        RiskWarning.Visibility = IgnoreFreeSpaceCheck ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = AdGuardHomeUpdaterCommand.CanOpenTerminal(IgnoreFreeSpaceCheck, RiskAcknowledgement.IsChecked == true);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CommandPreview)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, e);
    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
