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

            Migrate(settings);
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

    private static void Migrate(AppSettings settings)
    {
        // Migrate settings written by older releases, which used RouterIp.
        if (string.IsNullOrWhiteSpace(settings.RouterHost) &&
            !string.IsNullOrWhiteSpace(settings.RouterIp))
        {
            settings.RouterHost =
                RouterConnectionOptions.NormaliseHost(
                    settings.RouterIp);
        }
        else
        {
            settings.RouterHost =
                RouterConnectionOptions.NormaliseHost(
                    settings.RouterHost);
        }

        // RouterIp remains only as a deserialization migration bridge.
        settings.RouterIp = null;
        settings.RouterPort =
            settings.RouterPort is >= 1 and <= 65535
                ? settings.RouterPort
                : 80;
        settings.AdGuardPort =
            settings.AdGuardPort is >= 1 and <= 65535
                ? settings.AdGuardPort
                : 3000;
        settings.RefreshIntervalSeconds =
            Math.Clamp(
                settings.RefreshIntervalSeconds,
                5,
                3600);
    }
}
