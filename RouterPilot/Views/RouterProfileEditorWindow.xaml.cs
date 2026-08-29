using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.Views;
public partial class RouterProfileEditorWindow : Window
{
    private readonly SettingsService _settings = ((App)Application.Current).Services.GetRequiredService<SettingsService>();
    private readonly RouterProfile _original;
    private readonly TextBox _passwordTextBox = new() { Visibility = Visibility.Collapsed };
    private readonly CheckBox _showPasswordCheckBox = new() { Content = "Show password", Margin = new Thickness(0, 8, 0, 12) };
    public RouterProfile? Profile { get; private set; }
    public RouterProfileEditorWindow(RouterProfile? profile = null)
    { InitializeComponent(); _original = profile?.Clone() ?? new RouterProfile(); NameBox.Text = _original.DisplayName; HostBox.Text = _original.RouterHost; UserBox.Text = _original.Username; PortBox.Text = _original.SshPort.ToString(); AuthBox.SelectedValue = _original.SshAuthenticationMethod; KeyBox.Text = _original.PrivateKeyPath; AdGuardHttps.IsChecked = _original.UseAdGuardHttps; PasswordBox.Password = _settings.DecryptPassword(_original.EncryptedPassword); PassphraseBox.Password = _settings.DecryptPassword(_original.EncryptedPrivateKeyPassphrase); PasswordPanel.Children.Insert(1, _passwordTextBox); PasswordPanel.Children.Add(_showPasswordCheckBox); _showPasswordCheckBox.Checked += ShowPasswordCheckBox_Changed; _showPasswordCheckBox.Unchecked += ShowPasswordCheckBox_Changed; _passwordTextBox.TextChanged += PasswordTextBox_TextChanged; AuthBox.SelectionChanged += (_, _) => UpdateAuth(); UpdateAuth(); }
    private void UpdateAuth() { bool key = AuthBox.SelectedValue is SshAuthenticationMethod method && method == SshAuthenticationMethod.PrivateKey; PasswordPanel.Visibility = key ? Visibility.Collapsed : Visibility.Visible; KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed; }
    private void ShowPasswordCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool show = _showPasswordCheckBox.IsChecked == true;
        if (show)
        {
            _passwordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            _passwordTextBox.Visibility = Visibility.Visible;
            _passwordTextBox.Focus();
            _passwordTextBox.CaretIndex = _passwordTextBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = _passwordTextBox.Text;
            _passwordTextBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
        }
    }
    private void PasswordTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_showPasswordCheckBox.IsChecked == true) PasswordBox.Password = _passwordTextBox.Text;
    }
    private void Browse_Click(object s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "All files (*.*)|*.*", CheckFileExists = true }; if (d.ShowDialog(this) == true) KeyBox.Text = d.FileName; }
    private void Save_Click(object s, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text) || !int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) { MessageBox.Show("Enter a router host, username, and SSH port from 1 to 65,535.", "Router configuration"); return; } var auth = AuthBox.SelectedValue is SshAuthenticationMethod a ? a : SshAuthenticationMethod.Password; if (auth == SshAuthenticationMethod.PrivateKey && string.IsNullOrWhiteSpace(KeyBox.Text)) { MessageBox.Show("Select an SSH private key file.", "Router configuration"); return; } Profile = _original.Clone(); Profile.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? "My Router" : NameBox.Text.Trim(); Profile.RouterHost = RouterConnectionOptions.NormaliseHost(HostBox.Text); Profile.Username = UserBox.Text.Trim(); Profile.SshPort = port; Profile.SshAuthenticationMethod = auth; Profile.PrivateKeyPath = auth == SshAuthenticationMethod.PrivateKey ? KeyBox.Text.Trim() : string.Empty; Profile.EncryptedPassword = auth == SshAuthenticationMethod.Password ? _settings.EncryptPassword(PasswordBox.Password) : _original.EncryptedPassword; Profile.EncryptedPrivateKeyPassphrase = auth == SshAuthenticationMethod.PrivateKey ? _settings.EncryptPassword(PassphraseBox.Password) : string.Empty; Profile.UseAdGuardHttps = AdGuardHttps.IsChecked == true; DialogResult = true; }
    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
