using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels
{
    public class SettingsViewModel : ObservableObject
    {
        public event EventHandler? SettingsSaved;
        private readonly SettingsService _settingsService;
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly NotificationService _notificationService;
        private readonly FirmwareUpdateService _firmwareUpdateService;
        private readonly DashboardPreferencesService _dashboardPreferences;
        private readonly DashboardViewModel _dashboard;
        private readonly IRouterProfileService _profiles;
        public AdGuardAvailabilityService AdGuardAvailability { get; }
        public AdGuardTransportSecurityService AdGuardTransportSecurity { get; }
        public ObservableCollection<DashboardCardPreference> DashboardCards => _dashboardPreferences.Cards;

        private string _routerIp = "";
        private string _username = "";
        private string _password = "";
        private bool _rememberPassword;
        private bool _startWithWindows;
        private string _theme = ThemeService.SystemTheme;
        private int _refreshIntervalSeconds = 30;
        private int _defaultPauseMinutes = 30;
        private string _statusMessage = "Settings loaded.";
        private bool _hasUnsavedChanges;
        private bool _isLoading;
        private bool _notificationsEnabled = true;
        private bool _notificationCentreEnabled = true;
        private bool _windowsToastsEnabled = true;
        private bool _monitoredDeviceAvailabilityEnabled;
        private bool _routerWanNotificationsEnabled = true;
        private bool _vpnNotificationsEnabled = true;
        private bool _networkHealthNotificationsEnabled = true;
        private bool _firmwareNotificationsEnabled = true;
        private bool _adGuardNotificationsEnabled = true;
        private bool _clientNotificationsEnabled = true;
        private bool _applicationUpdateNotificationsEnabled = true;
        private bool _quietHoursEnabled;
        private string _quietHoursStart = "22:00";
        private string _quietHoursEnd = "07:00";
        private bool _useAdGuardHttps;
        private bool _includeAdGuardHomeInRouterHealth;
        private int _sshPort = 22;
        private SshAuthenticationMethod _sshAuthenticationMethod = RouterPilot.Models.SshAuthenticationMethod.Password;
        private string _privateKeyPath = "";
        private string _privateKeyPassphrase = "";

        public bool IncludeAdGuardHomeInRouterHealth
        {
            get => _includeAdGuardHomeInRouterHealth;
            set
            {
                if (SetProperty(ref _includeAdGuardHomeInRouterHealth, value))
                {
                    // Health consumes the shared Dashboard state, so a user
                    // toggle takes effect immediately without a refresh.
                    _dashboard.IncludeAdGuardHomeInRouterHealth = value;
                    MarkChanged();
                }
            }
        }

        public string RouterIp
        {
            get => _routerIp;
            set
            {
                if (SetProperty(ref _routerIp, value))
                {
                    MarkChanged();
                }
            }
        }

        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value))
                {
                    MarkChanged();
                }
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                if (SetProperty(ref _password, value))
                {
                    MarkChanged();
                }
            }
        }

        public bool RememberPassword
        {
            get => _rememberPassword;
            set
            {
                if (SetProperty(ref _rememberPassword, value))
                {
                    MarkChanged();
                }
            }
        }

        public int SshPort
        {
            get => _sshPort;
            set
            {
                if (SetProperty(ref _sshPort, value))
                {
                    MarkChanged();
                }
            }
        }

        public SshAuthenticationMethod SshAuthenticationMethod
        {
            get => _sshAuthenticationMethod;
            set
            {
                if (SetProperty(ref _sshAuthenticationMethod, value))
                {
                    OnPropertyChanged(nameof(IsPasswordAuthentication));
                    OnPropertyChanged(nameof(IsPrivateKeyAuthentication));
                    MarkChanged();
                }
            }
        }

        public bool IsPasswordAuthentication =>
            SshAuthenticationMethod == RouterPilot.Models.SshAuthenticationMethod.Password;

        public bool IsPrivateKeyAuthentication =>
            SshAuthenticationMethod == RouterPilot.Models.SshAuthenticationMethod.PrivateKey;

        public string PrivateKeyPath
        {
            get => _privateKeyPath;
            set
            {
                if (SetProperty(ref _privateKeyPath, value))
                {
                    MarkChanged();
                }
            }
        }

        public string PrivateKeyPassphrase
        {
            get => _privateKeyPassphrase;
            set
            {
                if (SetProperty(ref _privateKeyPassphrase, value))
                {
                    MarkChanged();
                }
            }
        }

        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (SetProperty(ref _startWithWindows, value))
                {
                    MarkChanged();
                }
            }
        }


        public string Theme
        {
            get => _theme;
            set
            {
                string normalizedTheme = ThemeService.Normalize(value);

                if (SetProperty(ref _theme, normalizedTheme))
                {
                    ThemeService.Apply(normalizedTheme);
                    MarkChanged();
                }
            }
        }

        public int RefreshIntervalSeconds
        {
            get => _refreshIntervalSeconds;
            set
            {
                if (SetProperty(ref _refreshIntervalSeconds, value))
                {
                    MarkChanged();
                }
            }
        }

        public int DefaultPauseMinutes
        {
            get => _defaultPauseMinutes;
            set
            {
                if (SetProperty(ref _defaultPauseMinutes, value))
                {
                    MarkChanged();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public bool NotificationsEnabled { get => _notificationsEnabled; set { if (SetProperty(ref _notificationsEnabled, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public bool NotificationCentreEnabled { get => _notificationCentreEnabled; set { if (SetProperty(ref _notificationCentreEnabled, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public bool WindowsToastsEnabled { get => _windowsToastsEnabled; set { if (SetProperty(ref _windowsToastsEnabled, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public bool MonitoredDeviceAvailabilityEnabled { get => _monitoredDeviceAvailabilityEnabled; set { if (SetProperty(ref _monitoredDeviceAvailabilityEnabled, value)) MarkChanged(); } }
        public bool RouterWanNotificationsEnabled { get => _routerWanNotificationsEnabled; set { if (SetProperty(ref _routerWanNotificationsEnabled, value)) MarkChanged(); } }
        public bool VpnNotificationsEnabled { get => _vpnNotificationsEnabled; set { if (SetProperty(ref _vpnNotificationsEnabled, value)) MarkChanged(); } }
        public bool NetworkHealthNotificationsEnabled { get => _networkHealthNotificationsEnabled; set { if (SetProperty(ref _networkHealthNotificationsEnabled, value)) MarkChanged(); } }
        public bool FirmwareNotificationsEnabled { get => _firmwareNotificationsEnabled; set { if (SetProperty(ref _firmwareNotificationsEnabled, value)) MarkChanged(); } }
        public bool AdGuardNotificationsEnabled { get => _adGuardNotificationsEnabled; set { if (SetProperty(ref _adGuardNotificationsEnabled, value)) MarkChanged(); } }
        public bool ClientNotificationsEnabled { get => _clientNotificationsEnabled; set { if (SetProperty(ref _clientNotificationsEnabled, value)) MarkChanged(); } }
        public bool ApplicationUpdateNotificationsEnabled { get => _applicationUpdateNotificationsEnabled; set { if (SetProperty(ref _applicationUpdateNotificationsEnabled, value)) MarkChanged(); } }
        public bool QuietHoursEnabled { get => _quietHoursEnabled; set { if (SetProperty(ref _quietHoursEnabled, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public string QuietHoursStart { get => _quietHoursStart; set { if (SetProperty(ref _quietHoursStart, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public string QuietHoursEnd { get => _quietHoursEnd; set { if (SetProperty(ref _quietHoursEnd, value)) { MarkChanged(); RefreshNotificationSummary(); } } }

        public bool IsAdGuardHttpConfigured => !_useAdGuardHttps;

        public string AdGuardTransportStatus => AdGuardTransportSecurity.Status switch
        {
            AdGuardTransportSecurityStatus.Secure => "Secure",
            AdGuardTransportSecurityStatus.Unencrypted => "Unencrypted",
            _ => "Unavailable"
        };

        public string AdGuardTransportDetail => AdGuardTransportSecurity.Detail;

        public int StoredNotificationCount => _notificationService.Notifications.Count;
        public string MostRecentNotificationTime => _notificationService.Notifications.FirstOrDefault()?.Timestamp.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? RouterPilotStatusPresentation.NotAvailable;
        public string ActiveDeliveryChannels => !NotificationsEnabled
            ? "Neither"
            : NotificationCentreEnabled && WindowsToastsEnabled
                ? "Windows notifications and Notification Centre"
                : NotificationCentreEnabled
                    ? "Notification Centre"
                    : WindowsToastsEnabled ? "Windows notifications" : "Neither";
        public string QuietHoursStatus => IsQuietHoursActive() ? "Active" : "Disabled";

        public FirmwareUpdateCheck FirmwareUpdate => _firmwareUpdateService.Current;
        public bool IsFirmwareChecking => _firmwareUpdateService.IsChecking;
        public string FirmwareCurrentVersion => string.IsNullOrWhiteSpace(FirmwareUpdate.CurrentVersion) ? RouterPilotStatusPresentation.NotAvailable : FirmwareUpdate.CurrentVersion;
        public string FirmwareLatestVersion => string.IsNullOrWhiteSpace(FirmwareUpdate.LatestVersion) ? RouterPilotStatusPresentation.NotAvailable : FirmwareUpdate.LatestVersion;
        public string FirmwareStatus => IsFirmwareChecking ? "Pending" : FirmwareUpdate.Status switch
        {
            FirmwareUpdateCheckStatus.UpToDate => "Up to date",
            FirmwareUpdateCheckStatus.UpdateAvailable => "Update available",
            FirmwareUpdateCheckStatus.Error => "Error",
            _ => RouterPilotStatusPresentation.NotAvailable
        };
        public string FirmwareLastChecked => FirmwareUpdate.LastChecked is { } checkedAt
            ? checkedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm")
            : RouterPilotStatusPresentation.NotAvailable;
        public bool HasFirmwareReleaseNotes => !string.IsNullOrWhiteSpace(FirmwareUpdate.ReleaseNotes) ||
                                                !string.IsNullOrWhiteSpace(FirmwareUpdate.ReleaseNotesUrl) ||
                                                !string.IsNullOrWhiteSpace(FirmwareUpdate.DownloadUrl);


        public IRelayCommand SaveCommand { get; }

        public IRelayCommand ReloadCommand { get; }

        public IAsyncRelayCommand CheckFirmwareUpdateCommand { get; }
        public IRelayCommand<DashboardCardPreference> MoveDashboardCardUpCommand { get; }
        public IRelayCommand<DashboardCardPreference> MoveDashboardCardDownCommand { get; }

        public void ResetDashboard()
        {
            _dashboardPreferences.Reset();
            StatusMessage = "Dashboard layout reset to the default cards and order.";
        }

        public SettingsViewModel(
            SettingsService settingsService,
            IRouterManagerProvider routerManagerProvider,
            AdGuardAvailabilityService adGuardAvailability,
            AdGuardTransportSecurityService adGuardTransportSecurity,
            NotificationService notificationService,
            FirmwareUpdateService firmwareUpdateService,
            DashboardPreferencesService dashboardPreferences,
            DashboardViewModel dashboard,
            IRouterProfileService profiles)
        {
            _settingsService = settingsService;
            _routerManagerProvider = routerManagerProvider;
            AdGuardAvailability = adGuardAvailability;
            AdGuardTransportSecurity = adGuardTransportSecurity;
            _notificationService = notificationService;
            _firmwareUpdateService = firmwareUpdateService;
            _dashboardPreferences = dashboardPreferences;
            _dashboard = dashboard;
            _profiles = profiles;
            _notificationService.PropertyChanged += (_, _) => RefreshNotificationSummary();
            AdGuardTransportSecurity.PropertyChanged +=
                (_, _) => RefreshAdGuardTransportStatus();
            _firmwareUpdateService.PropertyChanged += (_, _) => RefreshFirmwareStatus();
            _profiles.ActiveProfileChanged += (_, _) => ReloadActiveRouterSettingsOnUiThread();

            SaveCommand =
                new RelayCommand(Save);

            ReloadCommand =
                new RelayCommand(Load);

            CheckFirmwareUpdateCommand = new AsyncRelayCommand(CheckFirmwareUpdateAsync);
            MoveDashboardCardUpCommand = new RelayCommand<DashboardCardPreference>(_dashboardPreferences.MoveUp);
            MoveDashboardCardDownCommand = new RelayCommand<DashboardCardPreference>(_dashboardPreferences.MoveDown);

            Load();
        }

        // The settings view model is long-lived. A profile switch discards any
        // unsaved router edits and reloads only the active router projection.
        private void LoadActiveRouterSettings()
        {
            _isLoading = true;
            try
            {
                AppSettings settings = _settingsService.Load();
                RouterIp = settings.RouterHost;
                Username = settings.Username;
                RememberPassword = settings.RememberPassword;
                Password = settings.RememberPassword ? _settingsService.DecryptPassword(settings.EncryptedPassword) : "";
                SshPort = settings.SshPort;
                SshAuthenticationMethod = settings.SshAuthenticationMethod;
                PrivateKeyPath = settings.PrivateKeyPath;
                PrivateKeyPassphrase = string.IsNullOrWhiteSpace(settings.EncryptedPrivateKeyPassphrase) ? "" : _settingsService.DecryptPassword(settings.EncryptedPrivateKeyPassphrase);
                _useAdGuardHttps = settings.UseAdGuardHttps;
                OnPropertyChanged(nameof(IsAdGuardHttpConfigured));
                HasUnsavedChanges = false;
                StatusMessage = "Router settings reloaded for the active router.";
            }
            finally { _isLoading = false; }
        }

        private void ReloadActiveRouterSettingsOnUiThread()
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(LoadActiveRouterSettings);
                return;
            }

            LoadActiveRouterSettings();
        }

        public void Load()
        {
            _isLoading = true;

            try
            {
                AppSettings settings =
                    _settingsService.Load();

                RouterIp =
                    settings.RouterHost;

                Username =
                    settings.Username;

                RememberPassword =
                    settings.RememberPassword;

                Password =
                    settings.RememberPassword
                        ? _settingsService.DecryptPassword(
                            settings.EncryptedPassword)
                        : "";

                SshPort = settings.SshPort;
                SshAuthenticationMethod = settings.SshAuthenticationMethod;
                PrivateKeyPath = settings.PrivateKeyPath;
                PrivateKeyPassphrase = string.IsNullOrWhiteSpace(settings.EncryptedPrivateKeyPassphrase)
                    ? ""
                    : _settingsService.DecryptPassword(settings.EncryptedPrivateKeyPassphrase);

                StartWithWindows =
                    settings.StartWithWindows;

                Theme =
                    ThemeService.Normalize(settings.Theme);

                RefreshIntervalSeconds =
                    settings.RefreshIntervalSeconds <= 0
                        ? 30
                        : settings.RefreshIntervalSeconds;

                DefaultPauseMinutes =
                    settings.DefaultPauseMinutes <= 0
                        ? 30
                        : settings.DefaultPauseMinutes;
                _useAdGuardHttps = settings.UseAdGuardHttps;
                IncludeAdGuardHomeInRouterHealth = settings.IncludeAdGuardHomeInRouterHealth ?? false;
                _dashboard.IncludeAdGuardHomeInRouterHealth = IncludeAdGuardHomeInRouterHealth;
                OnPropertyChanged(nameof(IsAdGuardHttpConfigured));
                RefreshAdGuardTransportStatus();
                NotificationPreferences preferences = settings.NotificationPreferences ?? new NotificationPreferences();
                NotificationsEnabled = preferences.Enabled;
                NotificationCentreEnabled = preferences.NotificationCentreEnabled;
                WindowsToastsEnabled = preferences.WindowsToastsEnabled;
                MonitoredDeviceAvailabilityEnabled = preferences.MonitoredDeviceAvailabilityEnabled;
                RouterWanNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.Router);
                VpnNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.Vpn);
                NetworkHealthNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.NetworkHealth);
                FirmwareNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.Firmware);
                AdGuardNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.AdGuard);
                ClientNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.Device);
                ApplicationUpdateNotificationsEnabled = preferences.IsCategoryEnabled(NotificationCategory.ApplicationUpdates);
                QuietHoursEnabled = preferences.QuietHoursEnabled;
                QuietHoursStart = preferences.QuietHoursStart.ToString("HH:mm");
                QuietHoursEnd = preferences.QuietHoursEnd.ToString("HH:mm");

                HasUnsavedChanges =
                    false;

                StatusMessage =
                    "Settings loaded.";
            }
            catch (Exception ex)
            {
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Settings load",
                    "Unable to load settings. RouterPilot will use safe defaults where available.");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void Save()
        {
            string? validationError =
                Validate();

            if (validationError is not null)
            {
                StatusMessage =
                    validationError;

                return;
            }

            try
            {
                AppSettings existing = _settingsService.Load();

                var settings =
                    new AppSettings
                    {
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
                        LastSuccessfulUpdateCheckUtc = existing.LastSuccessfulUpdateCheckUtc,
                        LatestVersionSeen = existing.LatestVersionSeen,
                        LastNotifiedUpdateVersion = existing.LastNotifiedUpdateVersion,
                        RouterHost =
                            RouterConnectionOptions.NormaliseHost(RouterIp),

                        Username =
                            Username.Trim(),

                        RememberPassword =
                            RememberPassword,

                        EncryptedPassword =
                            RememberPassword
                                ? _settingsService.EncryptPassword(
                                    Password)
                                : "",

                        SshPort = SshPort,
                        SshAuthenticationMethod = SshAuthenticationMethod,
                        PrivateKeyPath = PrivateKeyPath.Trim(),
                        EncryptedPrivateKeyPassphrase = SshAuthenticationMethod == RouterPilot.Models.SshAuthenticationMethod.PrivateKey
                            ? _settingsService.EncryptPassword(PrivateKeyPassphrase)
                            : existing.EncryptedPrivateKeyPassphrase,

                        StartWithWindows =
                            StartWithWindows,

                        Theme =
                            Theme,

                        RefreshIntervalSeconds =
                            RefreshIntervalSeconds,

                        DefaultPauseMinutes =
                            DefaultPauseMinutes,
                        IncludeAdGuardHomeInRouterHealth = IncludeAdGuardHomeInRouterHealth,
                        NotificationPreferences = new NotificationPreferences
                        {
                            Enabled = NotificationsEnabled,
                            NotificationCentreEnabled = NotificationCentreEnabled,
                            WindowsToastsEnabled = WindowsToastsEnabled,
                            MonitoredDeviceAvailabilityEnabled = MonitoredDeviceAvailabilityEnabled,
                            Events = existing.NotificationPreferences?.Events ?? new Dictionary<NotificationEventType, bool>(),
                            Categories = BuildNotificationCategories(existing.NotificationPreferences),
                            QuietHoursEnabled = QuietHoursEnabled,
                            QuietHoursStart = TimeOnly.TryParse(QuietHoursStart, out TimeOnly start) ? start : new TimeOnly(22, 0),
                            QuietHoursEnd = TimeOnly.TryParse(QuietHoursEnd, out TimeOnly end) ? end : new TimeOnly(7, 0)
                        },
                        DashboardCards = existing.DashboardCards ?? new List<DashboardCardPreference>()
                    };

                settings.RouterProfiles = existing.RouterProfiles.Select(profile => profile.Clone()).ToList();
                settings.ActiveRouterProfileId = existing.ActiveRouterProfileId;
                SettingsService.UpdateActiveProfileFromLegacy(settings);

                _settingsService.Save(
                    settings);
                _dashboard.IncludeAdGuardHomeInRouterHealth = IncludeAdGuardHomeInRouterHealth;
                _routerManagerProvider.Invalidate();

                HasUnsavedChanges =
                    false;

                StatusMessage =
                    "Settings saved successfully.";

                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                StatusMessage = OperationFailurePolicy.UserMessage(
                    ex,
                    "Settings save",
                    "Unable to save settings. Try again after checking local file access.");
            }
        }

        private void RefreshNotificationSummary()
        {
            OnPropertyChanged(nameof(StoredNotificationCount));
            OnPropertyChanged(nameof(MostRecentNotificationTime));
            OnPropertyChanged(nameof(ActiveDeliveryChannels));
            OnPropertyChanged(nameof(QuietHoursStatus));
        }

        private void RefreshAdGuardTransportStatus()
        {
            OnPropertyChanged(nameof(AdGuardTransportStatus));
            OnPropertyChanged(nameof(AdGuardTransportDetail));
        }

        private void RefreshFirmwareStatus()
        {
            OnPropertyChanged(nameof(FirmwareUpdate));
            OnPropertyChanged(nameof(IsFirmwareChecking));
            OnPropertyChanged(nameof(FirmwareCurrentVersion));
            OnPropertyChanged(nameof(FirmwareLatestVersion));
            OnPropertyChanged(nameof(FirmwareStatus));
            OnPropertyChanged(nameof(FirmwareLastChecked));
            OnPropertyChanged(nameof(HasFirmwareReleaseNotes));
        }

        private async Task CheckFirmwareUpdateAsync() =>
            await _firmwareUpdateService.CheckManuallyAsync();

        private bool IsQuietHoursActive() =>
            new NotificationPreferences
            {
                QuietHoursEnabled = QuietHoursEnabled,
                QuietHoursStart = TimeOnly.TryParse(QuietHoursStart, out TimeOnly start) ? start : new TimeOnly(22, 0),
                QuietHoursEnd = TimeOnly.TryParse(QuietHoursEnd, out TimeOnly end) ? end : new TimeOnly(7, 0)
            }.IsQuietHours(DateTimeOffset.Now);

        private Dictionary<NotificationCategory, bool> BuildNotificationCategories(
            NotificationPreferences? existing)
        {
            var categories = existing?.Categories is { } saved
                ? new Dictionary<NotificationCategory, bool>(saved)
                : new Dictionary<NotificationCategory, bool>();
            categories[NotificationCategory.Router] = RouterWanNotificationsEnabled;
            categories[NotificationCategory.Vpn] = VpnNotificationsEnabled;
            categories[NotificationCategory.NetworkHealth] = NetworkHealthNotificationsEnabled;
            categories[NotificationCategory.Firmware] = FirmwareNotificationsEnabled;
            categories[NotificationCategory.AdGuard] = AdGuardNotificationsEnabled;
            categories[NotificationCategory.Device] = ClientNotificationsEnabled;
            categories[NotificationCategory.ApplicationUpdates] = ApplicationUpdateNotificationsEnabled;
            return categories;
        }

        private string? Validate()
        {
            if (string.IsNullOrWhiteSpace(
                    RouterIp))
            {
                return "Enter the router IP address or hostname.";
            }

            if (string.IsNullOrWhiteSpace(
                    Username))
            {
                return "Enter the SSH username.";
            }

            if (RememberPassword &&
                string.IsNullOrWhiteSpace(
                    Password))
            {
                return "Enter a password, or turn off Remember password.";
            }

            if (SshPort is < 1 or > 65535)
            {
                return "SSH port must be between 1 and 65,535.";
            }

            if (SshAuthenticationMethod == RouterPilot.Models.SshAuthenticationMethod.PrivateKey &&
                (string.IsNullOrWhiteSpace(PrivateKeyPath) || !File.Exists(PrivateKeyPath)))
            {
                return "SSH private key could not be found or opened.";
            }

            if (RefreshIntervalSeconds < 5 ||
                RefreshIntervalSeconds > 3600)
            {
                return "Refresh interval must be between 5 and 3,600 seconds.";
            }

            if (DefaultPauseMinutes < 1 ||
                DefaultPauseMinutes > 1440)
            {
                return "Default pause must be between 1 and 1,440 minutes.";
            }

            return null;
        }

        private void MarkChanged()
        {
            if (_isLoading)
            {
                return;
            }

            HasUnsavedChanges =
                true;

            StatusMessage =
                "You have unsaved changes.";
        }
    }
}
