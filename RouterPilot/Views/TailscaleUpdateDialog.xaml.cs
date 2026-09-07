using System.Windows;
using RouterPilot.Services;

namespace RouterPilot.Views;

public partial class TailscaleUpdateDialog : Window, System.ComponentModel.INotifyPropertyChanged
{
    public TailscaleUpdateDialog(string installedVersion, string latestVersion)
    {
        InstalledVersion = installedVersion;
        LatestVersion = latestVersion;
        InitializeComponent();
        DataContext = this;
        OptionsChanged(this, new RoutedEventArgs());
    }

    public string InstalledVersion { get; }
    public string LatestVersion { get; }
    public TailscaleUpdaterOptions Options => new(SelectRelease.IsChecked == true, EnableSsh.IsChecked == true,
        UseFullBinaries.IsChecked == true, DisableUpx.IsChecked == true, SkipGlInetConfiguration.IsChecked == true, IgnoreFreeSpace.IsChecked == true);
    public string CommandPreview => TailscaleUpdaterCommand.Build(Options);

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (RiskWarning is null) return;
        RiskWarning.Visibility = Options.IgnoreFreeSpace ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = TailscaleUpdaterCommand.CanOpenTerminal(Options, RiskAcknowledgement.IsChecked == true);
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CommandPreview)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
