using System;
using System.Collections.ObjectModel;
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
        private bool _quietHoursEnabled;
        private string _quietHoursStart = "22:00";
        private string _quietHoursEnd = "07:00";
        private bool _useAdGuardHttps;
        private string _searchText = string.Empty;

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
        public bool QuietHoursEnabled { get => _quietHoursEnabled; set { if (SetProperty(ref _quietHoursEnabled, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public string QuietHoursStart { get => _quietHoursStart; set { if (SetProperty(ref _quietHoursStart, value)) { MarkChanged(); RefreshNotificationSummary(); } } }
        public string QuietHoursEnd { get => _quietHoursEnd; set { if (SetProperty(ref _quietHoursEnd, value)) { MarkChanged(); RefreshNotificationSummary(); } } }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    OnPropertyChanged(nameof(GeneralSectionVisibility));
                    OnPropertyChanged(nameof(RouterSectionVisibility));
                    OnPropertyChanged(nameof(NotificationsSectionVisibility));
                    OnPropertyChanged(nameof(FirmwareSectionVisibility));
                }
            }
        }

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

        public Visibility GeneralSectionVisibility => Matches("General Startup Appearance Theme Application dashboard refresh") ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RouterSectionVisibility => Matches("Router Connection Authentication SSH password AdGuard Home router communication protection") ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NotificationsSectionVisibility => Matches("Notifications Windows Notification Centre Quiet Hours categories test") ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FirmwareSectionVisibility => Matches("Firmware Current Latest Update Release Notes Check") ? Visibility.Visible : Visibility.Collapsed;

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
            DashboardPreferencesService dashboardPreferences)
        {
            _settingsService = settingsService;
            _routerManagerProvider = routerManagerProvider;
            AdGuardAvailability = adGuardAvailability;
            AdGuardTransportSecurity = adGuardTransportSecurity;
            _notificationService = notificationService;
            _firmwareUpdateService = firmwareUpdateService;
            _dashboardPreferences = dashboardPreferences;
            _notificationService.PropertyChanged += (_, _) => RefreshNotificationSummary();
            AdGuardTransportSecurity.PropertyChanged +=
                (_, _) => RefreshAdGuardTransportStatus();
            _firmwareUpdateService.PropertyChanged += (_, _) => RefreshFirmwareStatus();

            SaveCommand =
                new RelayCommand(Save);

            ReloadCommand =
                new RelayCommand(Load);

            CheckFirmwareUpdateCommand = new AsyncRelayCommand(CheckFirmwareUpdateAsync);
            MoveDashboardCardUpCommand = new RelayCommand<DashboardCardPreference>(_dashboardPreferences.MoveUp);
            MoveDashboardCardDownCommand = new RelayCommand<DashboardCardPreference>(_dashboardPreferences.MoveDown);

            Load();
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
                OnPropertyChanged(nameof(IsAdGuardHttpConfigured));
                RefreshAdGuardTransportStatus();
                NotificationPreferences preferences = settings.NotificationPreferences ?? new NotificationPreferences();
                NotificationsEnabled = preferences.Enabled;
                NotificationCentreEnabled = preferences.NotificationCentreEnabled;
                WindowsToastsEnabled = preferences.WindowsToastsEnabled;
                MonitoredDeviceAvailabilityEnabled = preferences.MonitoredDeviceAvailabilityEnabled;
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
                StatusMessage =
                    "Unable to load settings: " +
                    ex.Message;
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

                        StartWithWindows =
                            StartWithWindows,

                        Theme =
                            Theme,

                        RefreshIntervalSeconds =
                            RefreshIntervalSeconds,

                        DefaultPauseMinutes =
                            DefaultPauseMinutes,
                        NotificationPreferences = new NotificationPreferences
                        {
                            Enabled = NotificationsEnabled,
                            NotificationCentreEnabled = NotificationCentreEnabled,
                            WindowsToastsEnabled = WindowsToastsEnabled,
                            MonitoredDeviceAvailabilityEnabled = MonitoredDeviceAvailabilityEnabled,
                            QuietHoursEnabled = QuietHoursEnabled,
                            QuietHoursStart = TimeOnly.TryParse(QuietHoursStart, out TimeOnly start) ? start : new TimeOnly(22, 0),
                            QuietHoursEnd = TimeOnly.TryParse(QuietHoursEnd, out TimeOnly end) ? end : new TimeOnly(7, 0)
                        },
                        DashboardCards = existing.DashboardCards ?? new List<DashboardCardPreference>()
                    };

                _settingsService.Save(
                    settings);
                _routerManagerProvider.Invalidate();

                HasUnsavedChanges =
                    false;

                StatusMessage =
                    "Settings saved successfully.";

                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                StatusMessage =
                    "Unable to save settings: " +
                    ex.Message;
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

        private bool Matches(string terms) => string.IsNullOrWhiteSpace(SearchText) ||
            terms.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

        private bool IsQuietHoursActive() =>
            new NotificationPreferences
            {
                QuietHoursEnabled = QuietHoursEnabled,
                QuietHoursStart = TimeOnly.TryParse(QuietHoursStart, out TimeOnly start) ? start : new TimeOnly(22, 0),
                QuietHoursEnd = TimeOnly.TryParse(QuietHoursEnd, out TimeOnly end) ? end : new TimeOnly(7, 0)
            }.IsQuietHours(DateTimeOffset.Now);

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
