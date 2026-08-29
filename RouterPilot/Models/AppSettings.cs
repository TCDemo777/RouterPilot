namespace RouterPilot.Models;

public sealed class AppSettings
{
    // RouterProfiles is authoritative. The legacy fields below remain a
    // compatibility projection of the selected active profile for services
    // not yet profile-aware.
    public List<RouterProfile> RouterProfiles { get; set; } = new();
    public string ActiveRouterProfileId { get; set; } = string.Empty;
    // Deliberately empty: first-run setup must collect this.
    public string RouterHost { get; set; } = string.Empty;

    // Kept temporarily for migration from existing settings.json files.
    public string? RouterIp { get; set; }

    public int RouterPort { get; set; } = 80;
    public int AdGuardPort { get; set; } = 3000;
    public bool UseRouterHttps { get; set; }
    public bool UseAdGuardHttps { get; set; }

    public string Username { get; set; } = "root";
    public string EncryptedPassword { get; set; } = string.Empty;
    public bool RememberPassword { get; set; } = true;
    public int SshPort { get; set; } = 22;
    public SshAuthenticationMethod SshAuthenticationMethod { get; set; } = SshAuthenticationMethod.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string EncryptedPrivateKeyPassphrase { get; set; } = string.Empty;
    // Endpoint-bound SSH host-key pins. Values use SSH.NET's SHA-256 format:
    // SHA256:<non-padded-base64-fingerprint>.
    public Dictionary<string, string> TrustedSshHostFingerprints { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    // Endpoint-bound TLS certificate pins. Values are SHA-256 fingerprints of
    // the presented certificate DER data.
    public Dictionary<string, string> TrustedRouterCertificateFingerprints { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool StartWithWindows { get; set; }
    public string Theme { get; set; } = "System";
    public int RefreshIntervalSeconds { get; set; } = 30;
    public int DefaultPauseMinutes { get; set; } = 30;
    // Null means the first established AdGuard state has not selected a default yet.
    public bool? IncludeAdGuardHomeInRouterHealth { get; set; }
    public DateTimeOffset? LastSuccessfulUpdateCheckUtc { get; set; }
    public string LatestVersionSeen { get; set; } = string.Empty;
    public string LastNotifiedUpdateVersion { get; set; } = string.Empty;
    public string LastNotifiedFirmwareVersion { get; set; } = string.Empty;
    public FirmwareUpdateCheck FirmwareUpdateCheck { get; set; } = new();
    public bool SpeedTestBandwidthWarningAcknowledged { get; set; }
    public NotificationPreferences NotificationPreferences { get; set; } = new();
    public List<DashboardCardPreference> DashboardCards { get; set; } = new();
    public bool VpnDiagnosticsExpanded { get; set; }
    public bool NewDeviceDetectionInitialized { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RouterHost);
}
