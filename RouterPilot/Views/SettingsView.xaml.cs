using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using RouterPilot.Services;

namespace RouterPilot.Views
{
    public partial class SettingsView : UserControl
    {
        private readonly SettingsViewModel _viewModel;
        private bool _isUpdatingPassword;
        private bool _isUpdatingPrivateKeyPassphrase;

        public SettingsView()
        {
            InitializeComponent();

            _viewModel =
                ((App)Application.Current).Services
                    .GetRequiredService<SettingsViewModel>();

            DataContext =
                _viewModel;

            Loaded +=
                SettingsView_Loaded;

            _viewModel.SettingsSaved +=
                ViewModel_SettingsSaved;
        }

        private void SettingsView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            UpdatePasswordBox();
            UpdatePrivateKeyPassphraseBox();
        }

        private void PasswordInput_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (_isUpdatingPassword)
            {
                return;
            }

            _viewModel.Password =
                PasswordInput.Password;
        }

        private void PrivateKeyPassphraseInput_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (_isUpdatingPrivateKeyPassphrase)
            {
                return;
            }

            _viewModel.PrivateKeyPassphrase = PrivateKeyPassphraseInput.Password;
        }

        private void BrowsePrivateKey_Click(
            object sender,
            RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select SSH private key",
                Filter = "All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                _viewModel.PrivateKeyPath = dialog.FileName;
            }
        }

        private void ShowPasswordCheckBox_Changed(
            object sender,
            RoutedEventArgs e)
        {
            bool show = ShowPasswordCheckBox.IsChecked == true;

            if (show)
            {
                PasswordTextInput.Text = PasswordInput.Password;
                PasswordInput.Visibility = Visibility.Collapsed;
                PasswordTextInput.Visibility = Visibility.Visible;
                PasswordTextInput.Focus();
                PasswordTextInput.CaretIndex = PasswordTextInput.Text.Length;
            }
            else
            {
                PasswordInput.Password = PasswordTextInput.Text;
                PasswordTextInput.Visibility = Visibility.Collapsed;
                PasswordInput.Visibility = Visibility.Visible;
                PasswordInput.Focus();
            }
        }

        private void ViewModel_SettingsSaved(
            object? sender,
            System.EventArgs e)
        {
            Window? host = Window.GetWindow(this);

            // On first-run or standalone settings windows, saving should
            // continue directly to the dashboard.
            if (host is not null &&
                host is not DashboardWindow &&
                Application.Current is App app)
            {
                app.CompleteFirstRun(host);
            }
        }

        private void UpdatePasswordBox()
        {
            _isUpdatingPassword =
                true;

            PasswordInput.Password =
                _viewModel.Password;

            _isUpdatingPassword =
                false;
        }

        private void UpdatePrivateKeyPassphraseBox()
        {
            _isUpdatingPrivateKeyPassphrase = true;
            PrivateKeyPassphraseInput.Password = _viewModel.PrivateKeyPassphrase;
            _isUpdatingPrivateKeyPassphrase = false;
        }

        private void ResetDashboard_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "Restore all Overview cards and the default order?",
                    "Reset Dashboard",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _viewModel.ResetDashboard();
            }
        }

        private void ManageRouters_Click(object sender, RoutedEventArgs e)
        {
            new RouterProfilesWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void OpenFirmwareReleaseNotes_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_viewModel.FirmwareUpdate.ReleaseNotesUrl))
            {
                MessageBox.Show(
                    _viewModel.FirmwareUpdate.ReleaseNotes,
                    "Firmware release notes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (Uri.TryCreate(_viewModel.FirmwareUpdate.ReleaseNotesUrl,
                    UriKind.Absolute,
                    out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                (uri.Host.Equals("gl-inet.com", System.StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".gl-inet.com", System.StringComparison.OrdinalIgnoreCase)))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
        }
    }
}
