using System;
using System.Windows;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly IRouterManagerProvider _routerManagerProvider;


        public SettingsWindow()
        {
            InitializeComponent();

            _settingsService =
                ((App)Application.Current).Services
                    .GetRequiredService<SettingsService>();
            _routerManagerProvider = ((App)Application.Current).Services
                .GetRequiredService<IRouterManagerProvider>();

            LoadSettings();

            SaveButton.Click += SaveButton_Click;
        }



        private void LoadSettings()
        {
            AppSettings settings =
                _settingsService.Load();


            RouterIpBox.Text =
                settings.RouterHost;


            UsernameBox.Text =
                settings.Username;


            PasswordBox.Password =
                _settingsService.DecryptPassword(
                    settings.EncryptedPassword);


            RememberPasswordCheck.IsChecked =
                settings.RememberPassword;


            StartWithWindowsCheck.IsChecked =
                settings.StartWithWindows;
        }





        private void SaveButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                AppSettings existing = _settingsService.Load();

                var settings =
                    new AppSettings
                    {
                        RouterHost =
                            RouterConnectionOptions.NormaliseHost(RouterIpBox.Text),

                        RouterPort = existing.RouterPort,
                        AdGuardPort = existing.AdGuardPort,
                        UseRouterHttps = existing.UseRouterHttps,
                        UseAdGuardHttps = existing.UseAdGuardHttps,
                        TrustedSshHostFingerprints =
                            existing.TrustedSshHostFingerprints ??
                            new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase),
                        TrustedRouterCertificateFingerprints =
                            existing.TrustedRouterCertificateFingerprints ??
                            new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase),
                        FirmwareUpdateCheck = existing.FirmwareUpdateCheck ?? new FirmwareUpdateCheck(),
                        LastNotifiedFirmwareVersion = existing.LastNotifiedFirmwareVersion,
                        Theme = existing.Theme,
                        RefreshIntervalSeconds = existing.RefreshIntervalSeconds,
                        DefaultPauseMinutes = existing.DefaultPauseMinutes,
                        SshPort = existing.SshPort,
                        SshAuthenticationMethod = existing.SshAuthenticationMethod,
                        PrivateKeyPath = existing.PrivateKeyPath,
                        EncryptedPrivateKeyPassphrase = existing.EncryptedPrivateKeyPassphrase,

                        Username =
                            UsernameBox.Text.Trim(),

                        RememberPassword =
                            RememberPasswordCheck.IsChecked == true,

                        StartWithWindows =
                            StartWithWindowsCheck.IsChecked == true
                    };



                if (settings.RememberPassword)
                {
                    settings.EncryptedPassword =
                        _settingsService.EncryptPassword(
                            PasswordBox.Password);
                }
                else
                {
                    settings.EncryptedPassword = "";
                }

                settings.RouterProfiles = existing.RouterProfiles.Select(profile => profile.Clone()).ToList();
                settings.ActiveRouterProfileId = existing.ActiveRouterProfileId;
                SettingsService.UpdateActiveProfileFromLegacy(settings);


                _settingsService.Save(settings);
                _routerManagerProvider.Invalidate();



                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    OperationFailurePolicy.UserMessage(
                        ex,
                        "Settings save",
                        "RouterPilot could not save the settings. Check the values and local file access, then try again."),
                    "Settings Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }





        private void Cancel_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }
    }
}
