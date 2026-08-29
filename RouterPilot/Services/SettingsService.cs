using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RouterPilot.Configuration;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsFolder;
    private readonly string _settingsFile;

    public SettingsService(string? settingsFolder)
        : this(applicationDataPaths: null, settingsFolder)
    {
    }

    public SettingsService(
        ApplicationDataPathProvider? applicationDataPaths = null,
        string? settingsFolder = null)
    {
        _settingsFolder = settingsFolder ??
            (applicationDataPaths ?? new ApplicationDataPathProvider()).CurrentPath;

        _settingsFile = Path.Combine(
            _settingsFolder,
            "settings.json");
    }

    public AppSettings Load()
    {
        Directory.CreateDirectory(_settingsFolder);

        if (!File.Exists(_settingsFile))
            return new AppSettings();

        try
        {
            string json = File.ReadAllText(_settingsFile);
            AppSettings settings =
                JsonSerializer.Deserialize<AppSettings>(
                    json,
                    JsonOptions)
                ?? new AppSettings();

            if (Migrate(settings))
                Save(settings);
            return settings;
        }
        catch (Exception ex)
            when (ex is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            Debug.WriteLine(
                $"Unable to load settings ({ex.GetType().Name}).");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_settingsFolder);

        string tempFile = _settingsFile + ".tmp";
        string backupFile = _settingsFile + ".bak";
        string json = JsonSerializer.Serialize(
            settings,
            JsonOptions);

        File.WriteAllText(tempFile, json, Encoding.UTF8);

        if (File.Exists(_settingsFile))
        {
            File.Replace(
                tempFile,
                _settingsFile,
                backupFile,
                ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempFile, _settingsFile);
        }
    }

    public RouterConnectionOptions CreateConnectionOptions(
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = new RouterConnectionOptions
        {
            Host = settings.RouterHost,
            RouterScheme = settings.UseRouterHttps
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp,
            RouterPort = settings.RouterPort,
            AdGuardScheme = settings.UseAdGuardHttps
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp,
            AdGuardPort = settings.AdGuardPort,
            RequestTimeoutSeconds = 10
        };

        options.Validate();
        return options;
    }

    public string EncryptPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return string.Empty;

        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(encrypted);
    }

    public string DecryptPassword(string encryptedPassword)
    {
        if (string.IsNullOrWhiteSpace(encryptedPassword))
            return string.Empty;

        try
        {
            byte[] decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(encryptedPassword),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
            when (ex is CryptographicException
                or FormatException)
        {
            Debug.WriteLine(
                $"Unable to decrypt saved password ({ex.GetType().Name}).");
            return string.Empty;
        }
    }

    internal static void ApplyActiveProfile(AppSettings settings)
    {
        RouterProfile? profile = settings.RouterProfiles.FirstOrDefault(item => item.Id == settings.ActiveRouterProfileId);
        if (profile is null)
            return;

        settings.RouterHost = profile.RouterHost;
        settings.RouterPort = profile.RouterPort;
        settings.AdGuardPort = profile.AdGuardPort;
        settings.UseRouterHttps = profile.UseRouterHttps;
        settings.UseAdGuardHttps = profile.UseAdGuardHttps;
        settings.Username = profile.Username;
        settings.EncryptedPassword = profile.EncryptedPassword;
        settings.RememberPassword = profile.RememberPassword;
        settings.SshPort = profile.SshPort;
        settings.SshAuthenticationMethod = profile.SshAuthenticationMethod;
        settings.PrivateKeyPath = profile.PrivateKeyPath;
        settings.EncryptedPrivateKeyPassphrase = profile.EncryptedPrivateKeyPassphrase;
    }

    /// <summary>
    /// Keeps the retained legacy projection and its selected profile in sync
    /// while the existing single-router Settings UI remains in use.
    /// </summary>
    internal static void UpdateActiveProfileFromLegacy(AppSettings settings)
    {
        RouterProfile? profile = settings.RouterProfiles.FirstOrDefault(item => item.Id == settings.ActiveRouterProfileId);
        if (profile is null)
            return;

        profile.RouterHost = settings.RouterHost;
        profile.RouterPort = settings.RouterPort;
        profile.AdGuardPort = settings.AdGuardPort;
        profile.UseRouterHttps = settings.UseRouterHttps;
        profile.UseAdGuardHttps = settings.UseAdGuardHttps;
        profile.Username = settings.Username;
        profile.EncryptedPassword = settings.EncryptedPassword;
        profile.RememberPassword = settings.RememberPassword;
        profile.SshPort = settings.SshPort;
        profile.SshAuthenticationMethod = settings.SshAuthenticationMethod;
        profile.PrivateKeyPath = settings.PrivateKeyPath;
        profile.EncryptedPrivateKeyPassphrase = settings.EncryptedPrivateKeyPassphrase;
    }

    private static bool Migrate(AppSettings settings)
    {
        bool changed = false;
        // Migrate settings written by older releases, which used RouterIp.
        if (string.IsNullOrWhiteSpace(settings.RouterHost) &&
            !string.IsNullOrWhiteSpace(settings.RouterIp))
        {
            settings.RouterHost =
                RouterConnectionOptions.NormaliseHost(
                    settings.RouterIp);
            changed = true;
        }
        else
        {
            settings.RouterHost =
                RouterConnectionOptions.NormaliseHost(
                    settings.RouterHost);
        }

        // RouterIp remains only as a deserialization migration bridge.
        if (settings.RouterIp is not null)
        {
            settings.RouterIp = null;
            changed = true;
        }
        settings.RouterPort =
            settings.RouterPort is >= 1 and <= 65535
                ? settings.RouterPort
                : 80;
        settings.AdGuardPort =
            settings.AdGuardPort is >= 1 and <= 65535
                ? settings.AdGuardPort
                : 3000;
        settings.SshPort = settings.SshPort is >= 1 and <= 65535 ? settings.SshPort : 22;
        if (!Enum.IsDefined(settings.SshAuthenticationMethod))
            settings.SshAuthenticationMethod = SshAuthenticationMethod.Password;

        settings.RouterProfiles ??= new List<RouterProfile>();
        if (settings.RouterProfiles.Count == 0 && !string.IsNullOrWhiteSpace(settings.RouterHost))
        {
            settings.RouterProfiles.Add(new RouterProfile
            {
                DisplayName = "My Router",
                RouterHost = settings.RouterHost,
                RouterPort = settings.RouterPort,
                AdGuardPort = settings.AdGuardPort,
                UseRouterHttps = settings.UseRouterHttps,
                UseAdGuardHttps = settings.UseAdGuardHttps,
                Username = settings.Username,
                EncryptedPassword = settings.EncryptedPassword,
                RememberPassword = settings.RememberPassword,
                SshPort = settings.SshPort,
                SshAuthenticationMethod = settings.SshAuthenticationMethod,
                PrivateKeyPath = settings.PrivateKeyPath,
                EncryptedPrivateKeyPassphrase = settings.EncryptedPrivateKeyPassphrase
            });
            changed = true;
        }
        foreach (RouterProfile profile in settings.RouterProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) { profile.Id = Guid.NewGuid().ToString("N"); changed = true; }
            if (string.IsNullOrWhiteSpace(profile.DisplayName)) { profile.DisplayName = "My Router"; changed = true; }
            string normalizedHost = RouterConnectionOptions.NormaliseHost(profile.RouterHost);
            if (!string.Equals(profile.RouterHost, normalizedHost, StringComparison.Ordinal))
            {
                profile.RouterHost = normalizedHost;
                changed = true;
            }
            if (profile.SshPort is < 1 or > 65535)
            {
                profile.SshPort = 22;
                changed = true;
            }
            if (!Enum.IsDefined(profile.SshAuthenticationMethod)) { profile.SshAuthenticationMethod = SshAuthenticationMethod.Password; changed = true; }
        }
        if (settings.RouterProfiles.Count > 0 && !settings.RouterProfiles.Any(profile => profile.Id == settings.ActiveRouterProfileId))
        {
            settings.ActiveRouterProfileId = settings.RouterProfiles[0].Id;
            changed = true;
        }
        ApplyActiveProfile(settings);
        settings.RefreshIntervalSeconds =
            Math.Clamp(
                settings.RefreshIntervalSeconds,
                5,
                3600);
        return changed;
    }
}
