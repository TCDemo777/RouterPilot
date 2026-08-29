using RouterPilot.Models;
using RouterPilot.Presentation;
using RouterPilot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using Renci.SshNet.Common;
using RouterPilot.ViewModels;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
NetworkHealthViewInput Input(DataFreshnessState router = DataFreshnessState.Fresh, DataFreshnessState wan = DataFreshnessState.Fresh, DataFreshnessState adGuardFreshness = DataFreshnessState.Fresh, DataFreshnessState wifi = DataFreshnessState.Fresh, DataFreshnessState dhcp = DataFreshnessState.Fresh, AdGuardAvailabilityState adGuard = AdGuardAvailabilityState.Available, bool includeAdGuard = true, string vpn = "Connected", bool vpnAvailable = true, bool vpnConfigured = true, bool statsLoaded = true, RouterPilotStatus stats = RouterPilotStatus.Active, string cpu = "10%", string temperature = "45 C", string memory = "40%", string storage = "20%", string uptime = "1d", string load = "0.1", string routerFirmwareVersion = "4.6.0", FirmwareUpdateCheckStatus firmwareStatus = FirmwareUpdateCheckStatus.UpToDate) => new(router, wan, adGuardFreshness, DataFreshnessState.Fresh, wifi, dhcp, true, true, "now", "1.2.3.4", "192.168.1.1", "1.1.1.1", adGuard, includeAdGuard, true, true, false, vpnAvailable, vpnConfigured, vpn, "WireGuard", 2, 2, 0, 0, 3, true, 3, 1, cpu, temperature, memory, storage, uptime, load, routerFirmwareVersion, firmwareStatus, statsLoaded, stats, "Existing status.");
NetworkHealthViewSnapshot healthy = NetworkHealthViewProjection.Create(Input());
Require(healthy.OverallStatus == "Healthy", "healthy state");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Unavailable)).OverallStatus == "Unavailable", "router unavailable");
NetworkHealthViewSnapshot adGuardUnavailable = NetworkHealthViewProjection.Create(Input(adGuard: AdGuardAvailabilityState.Unavailable));
Require(adGuardUnavailable.Checks.Single(x => x.Title == "DNS / AdGuard").Status == "Unavailable" && adGuardUnavailable.OverallStatus == "Attention needed", "AdGuard unavailable");
NetworkHealthViewSnapshot adGuardUnused = NetworkHealthViewProjection.Create(Input(adGuard: AdGuardAvailabilityState.Unavailable, includeAdGuard: false));
Require(adGuardUnused.Checks.Single(x => x.Title == "DNS / AdGuard").Status == "Not in use" && adGuardUnused.OverallStatus == "Healthy", "optional AdGuard is informational");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading)).OverallStatus == "Initializing", "expected AdGuard loading state");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading, includeAdGuard: false)).OverallStatus == "Initializing", "optional AdGuard checking state");
Require(DashboardHealthProjection.Create(new(true, true, false, false, 0, false, 0, 0, false, FirmwareUpdateCheckStatus.UpToDate, string.Empty, string.Empty)).Score == 100, "unused AdGuard is excluded from Dashboard health score");
Require(DashboardHealthProjection.Create(new(true, true, false, true, 0, false, 0, 0, false, FirmwareUpdateCheckStatus.UpToDate, string.Empty, string.Empty)).Score == 85, "expected AdGuard affects Dashboard health score");
NetworkHealthViewSnapshot disconnectedVpn = NetworkHealthViewProjection.Create(Input(vpn: "Disconnected"));
Require(disconnectedVpn.Checks.Single(x => x.Title == "VPN").Status == "Disconnected" && disconnectedVpn.OverallStatus == "Healthy", "VPN disconnected is informational");
Require(NetworkHealthViewProjection.Create(Input(vpnConfigured: false)).OverallStatus == "Healthy", "VPN not configured is informational");
Require(NetworkHealthViewProjection.Create(Input(vpn: "Authentication failed")).OverallStatus == "Attention needed", "VPN explicit failure affects health");
Require(NetworkHealthViewProjection.Create(Input(vpn: "Connection did not complete")).OverallStatus == "Attention needed", "VPN failed tunnel affects health");
Require(NetworkHealthViewProjection.Create(Input(stats: RouterPilotStatus.Disabled)).Checks.Single(x => x.Title == "Data Statistics").Status == "Disabled", "statistics disabled");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Stale)).Checks.Single(x => x.Title == "Router").Status == "Stale", "stale state");
Require(NetworkHealthViewProjection.Create(Input(statsLoaded: false)).Checks.Single(x => x.Title == "Data Statistics").Status == "Not loaded", "partial state");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Loading)).OverallStatus == "Initializing", "loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Loading)).OverallStatus != "Healthy", "Wi-Fi loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Stale)).OverallStatus != "Healthy", "Wi-Fi stale state");
Require(NetworkHealthViewProjection.Create(Input(dhcp: DataFreshnessState.Loading)).OverallStatus != "Healthy", "DHCP loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Unavailable)).OverallStatus != "Healthy", "Wi-Fi unavailable state");
Require(NetworkHealthViewProjection.Create(Input() with { WifiActiveRadios = 1, WifiDisabledRadios = 1 }).OverallStatus == "Healthy", "intentionally disabled Wi-Fi radio is informational");
Require(NetworkHealthViewProjection.Create(Input() with { WifiActiveRadios = 0, WifiDisabledRadios = 2 }).OverallStatus == "Healthy", "all intentionally disabled Wi-Fi radios are informational");
Require(NetworkHealthViewProjection.Create(Input(dhcp: DataFreshnessState.Unavailable)).OverallStatus != "Healthy", "DHCP unavailable state");
Require(NetworkHealthViewProjection.Create(Input(cpu: "-", temperature: "-", memory: "-", storage: "-", uptime: "-", load: "-")).Checks.Single(x => x.Title == "Router resources").Status == "Unavailable", "missing resources");
Require(NetworkHealthViewProjection.Create(Input(cpu: "-", temperature: "45 C")).Checks.Single(x => x.Title == "Router resources").Status == "Partial", "partial resources");
Require(NetworkHealthViewProjection.Create(Input(wan: DataFreshnessState.Loading)).OverallStatus != "Healthy", "WAN loading state");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading)).OverallStatus != "Healthy", "AdGuard loading state");
NetworkHealthViewCheck firmwareUpToDate = NetworkHealthViewProjection.Create(Input(routerFirmwareVersion: "4.6.0", firmwareStatus: FirmwareUpdateCheckStatus.UpToDate)).Checks.Single(x => x.Title == "Firmware");
Require(firmwareUpToDate.Status == "Up to date" && firmwareUpToDate.Detail == "Current version: 4.6.0", "GL.iNet firmware up to date");
Require(NetworkHealthViewProjection.Create(Input(firmwareStatus: FirmwareUpdateCheckStatus.UpdateAvailable)).Checks.Single(x => x.Title == "Firmware").Status == "Update available", "GL.iNet firmware update available");
Require(NetworkHealthViewProjection.Create(Input(firmwareStatus: FirmwareUpdateCheckStatus.Pending)).Checks.Single(x => x.Title == "Firmware").Status == "Checking", "GL.iNet firmware checking");
Require(NetworkHealthViewProjection.Create(Input(firmwareStatus: FirmwareUpdateCheckStatus.NotAvailable)).Checks.Single(x => x.Title == "Firmware").Status == "Unavailable", "GL.iNet firmware unavailable");
Require(firmwareUpToDate.NavigationTarget == "maintenance-firmware", "Firmware navigation targets Maintenance firmware.");
Require(nameof(NetworkHealthViewInput.RouterFirmwareVersion) == "RouterFirmwareVersion", "Network Health has no LuCI firmware input.");
using ServiceProvider services = new ServiceCollection().AddSingleton<DashboardViewModel>().BuildServiceProvider();
Require(ReferenceEquals(services.GetRequiredService<DashboardViewModel>(), services.GetRequiredService<DashboardViewModel>()), "Dashboard ViewModel DI registration must be authoritative.");

using PublicIpService publicIp = new();
List<(string? Previous, string Current)> publicIpEvents = [];
publicIp.PublicIpChanged += (previous, current) => publicIpEvents.Add((previous, current));
MethodInfo? publish = typeof(PublicIpService).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic);
Require(publish is not null, "Public-IP publisher is available for deterministic change detection coverage.");
publish!.Invoke(publicIp, [new PublicIpResult(" 1.2.3.4 ", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 0, "first confirmed public IP establishes a silent baseline");
publish.Invoke(publicIp, [new PublicIpResult("1.2.3.4", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 0, "normalized unchanged public IP does not raise an event");
publish.Invoke(publicIp, [new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.Unavailable, null)]);
publish.Invoke(publicIp, [new PublicIpResult("1.2.3.4", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 0, "unavailable then unchanged public IP does not raise an event");
publish.Invoke(publicIp, [new PublicIpResult("5.6.7.8", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 1, "a confirmed public IP transition raises one event");
Require(publicIpEvents[0] == ("1.2.3.4", "5.6.7.8"), "public IP event compares confirmed normalized values");

MethodInfo? automaticUpdateDue = typeof(UpdateService).GetMethod("IsAutomaticCheckDue", BindingFlags.Static | BindingFlags.NonPublic);
Require(automaticUpdateDue is not null, "automatic update due policy is available for deterministic coverage");
DateTimeOffset updateNow = DateTimeOffset.UtcNow;
Require((bool)automaticUpdateDue!.Invoke(null, [new AppSettings(), updateNow])!, "first automatic update check is due");
Require(!(bool)automaticUpdateDue.Invoke(null, [new AppSettings { LastSuccessfulUpdateCheckUtc = updateNow - TimeSpan.FromHours(23) }, updateNow])!, "automatic update check is skipped before 24 hours");
Require((bool)automaticUpdateDue.Invoke(null, [new AppSettings { LastSuccessfulUpdateCheckUtc = updateNow - TimeSpan.FromHours(25) }, updateNow])!, "automatic update check is due after 24 hours");

NotificationPreferences defaults = new();
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.Router }), "new category preferences preserve router notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.Firmware }), "new category preferences preserve firmware notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.AdGuard }), "new category preferences preserve AdGuard notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.Device }), "new category preferences preserve device notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }), "new category preferences preserve update notifications by default");

NotificationPreferences disabledCategories = new NotificationPreferences
{
    Categories = new Dictionary<NotificationCategory, bool>
    {
        [NotificationCategory.Router] = false,
        [NotificationCategory.Vpn] = false,
        [NotificationCategory.NetworkHealth] = false,
        [NotificationCategory.Firmware] = false,
        [NotificationCategory.AdGuard] = false,
        [NotificationCategory.Device] = false,
        [NotificationCategory.ApplicationUpdates] = false
    }
};
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Router }), "Router and WAN suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Vpn }), "VPN suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.NetworkHealth }), "Network Health suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Firmware }), "firmware suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.AdGuard }), "AdGuard suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Device }), "client and device suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }), "automatic update suppression is central");
Require(disabledCategories.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }, bypassCategoryPreference: true), "manual update feedback bypasses only the category preference");
Require(!new NotificationPreferences { Enabled = false }.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }, bypassCategoryPreference: true), "manual feedback still honours the master notification preference");
string preferencesFolder = Path.Combine(Path.GetTempPath(), "RouterPilot-notification-preferences-" + Guid.NewGuid().ToString("N"));
var preferencesStorage = new SettingsService(preferencesFolder);
preferencesStorage.Save(new AppSettings { NotificationPreferences = disabledCategories });
Require(!preferencesStorage.Load().NotificationPreferences.IsCategoryEnabled(NotificationCategory.ApplicationUpdates), "category preferences persist after reload");

var sshFactory = new SshConnectionFactory();
ConnectionInfo passwordConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
{
    Host = "router.example",
    Port = 22,
    Username = "root",
    AuthenticationMethod = SshAuthenticationMethod.Password,
    Password = "fixture-password"
});
Require(passwordConnection.Port == 22, "default SSH port is 22");
Require(passwordConnection.AuthenticationMethods.Single() is PasswordAuthenticationMethod, "password authentication is constructed");
ConnectionInfo customPortConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
{
    Host = "router.example",
    Port = 2222,
    Username = "root",
    AuthenticationMethod = SshAuthenticationMethod.Password,
    Password = "fixture-password"
});
Require(customPortConnection.Port == 2222, "custom SSH port is passed to ConnectionInfo");
RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings { Host = "router.example", Port = 0, Username = "root", Password = "fixture-password" }), "invalid zero SSH port is rejected");
RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings { Host = "router.example", Port = 65536, Username = "root", Password = "fixture-password" }), "invalid high SSH port is rejected");

string sshFixtureDirectory = Path.Combine(Path.GetTempPath(), "RouterPilot-ssh-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sshFixtureDirectory);
string unencryptedKeyPath = Path.Combine(sshFixtureDirectory, "id_rsa");
string encryptedKeyPath = Path.Combine(sshFixtureDirectory, "id_rsa_encrypted");
string invalidKeyPath = Path.Combine(sshFixtureDirectory, "invalid-key");
const string fixturePassphrase = "fixture-key-passphrase";
try
{
    using (RSA rsa = RSA.Create(2048))
    {
        File.WriteAllText(unencryptedKeyPath, rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(encryptedKeyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(
            fixturePassphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
    }
    File.WriteAllText(invalidKeyPath, "not an SSH private key");

    ConnectionInfo privateKeyConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example",
        Port = 2222,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = unencryptedKeyPath
    });
    Require(privateKeyConnection.AuthenticationMethods.Single() is PrivateKeyAuthenticationMethod, "unencrypted private-key authentication is constructed");

    ConnectionInfo encryptedKeyConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example",
        Port = 2222,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = encryptedKeyPath,
        PrivateKeyPassphrase = fixturePassphrase
    });
    Require(encryptedKeyConnection.AuthenticationMethods.Single() is PrivateKeyAuthenticationMethod, "encrypted private-key authentication accepts its passphrase");
    RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example", Port = 2222, Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = encryptedKeyPath, PrivateKeyPassphrase = "wrong-passphrase"
    }), "incorrect key passphrase fails cleanly");
    RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example", Port = 2222, Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = invalidKeyPath
    }), "invalid SSH key fails cleanly");
}
finally
{
    Directory.Delete(sshFixtureDirectory, recursive: true);
}
RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings
{
    Host = "router.example", Port = 2222, Username = "root",
    AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
    PrivateKeyPath = Path.Combine(Path.GetTempPath(), "missing-routerpilot-key")
}), "missing SSH key fails cleanly");

string sshSettingsFolder = Path.Combine(Path.GetTempPath(), "RouterPilot-ssh-settings-" + Guid.NewGuid().ToString("N"));
var sshSettingsStorage = new SettingsService(sshSettingsFolder);
sshSettingsStorage.Save(new AppSettings
{
    RouterHost = "router.example",
    Username = "root",
    RememberPassword = true,
    EncryptedPassword = sshSettingsStorage.EncryptPassword("fixture-password")
});
AppSettings migratedSshSettings = sshSettingsStorage.Load();
Require(migratedSshSettings.SshPort == 22 && migratedSshSettings.SshAuthenticationMethod == SshAuthenticationMethod.Password, "existing settings migrate to password authentication on port 22");
Require(sshSettingsStorage.DecryptPassword(migratedSshSettings.EncryptedPassword) == "fixture-password", "existing protected password is preserved during SSH migration");
AppSettings migratedSshSettingsAgain = sshSettingsStorage.Load();
Require(migratedSshSettingsAgain.SshPort == 22 && migratedSshSettingsAgain.SshAuthenticationMethod == SshAuthenticationMethod.Password, "SSH migration is idempotent");
sshSettingsStorage.Save(new AppSettings
{
    RouterHost = "router.example",
    Username = "root",
    SshPort = 2222,
    SshAuthenticationMethod = SshAuthenticationMethod.PrivateKey,
    PrivateKeyPath = "key-a",
    EncryptedPrivateKeyPassphrase = sshSettingsStorage.EncryptPassword("fixture-key-passphrase")
});
Require(sshSettingsStorage.DecryptPassword(sshSettingsStorage.Load().EncryptedPrivateKeyPassphrase) == "fixture-key-passphrase", "private-key passphrase remains protected and persists");
AppSettings isolatedSshSettings = new() { SshPort = 2201, SshAuthenticationMethod = SshAuthenticationMethod.PrivateKey, PrivateKeyPath = "key-a" };
AppSettings otherSshSettings = new() { SshPort = 2202, SshAuthenticationMethod = SshAuthenticationMethod.Password, PrivateKeyPath = "key-b" };
Require(isolatedSshSettings.SshPort != otherSshSettings.SshPort && isolatedSshSettings.SshAuthenticationMethod != otherSshSettings.SshAuthenticationMethod, "active router settings keep SSH configuration isolated");
Require(!new InvalidOperationException("SSH private key could not be found or opened.").Message.Contains("fixture-password", StringComparison.Ordinal), "SSH diagnostics do not expose credentials");

MethodInfo? parseBlocklists = typeof(RouterManager).GetMethod("ParseBlocklists", BindingFlags.Static | BindingFlags.NonPublic);
Require(parseBlocklists is not null, "blocklist parser is available for deterministic coverage");
using JsonDocument blocklistDocument = JsonDocument.Parse("""
    { "filters": [
      { "id": 1, "name": "Enabled list", "url": "https://example.test/enabled.txt", "enabled": true, "rules_count": 922337203685477, "last_updated": "2026-01-01T00:00:00Z" },
      { "id": 2, "name": "Disabled list", "url": "https://example.test/disabled.txt", "enabled": false, "rules_count": 7 }
    ] }
    """);
var parsedBlocklists = (List<AdGuardBlocklist>)parseBlocklists!.Invoke(null, [blocklistDocument.RootElement])!;
Require(parsedBlocklists.Count == 2, "blocklist parser reads filters");
Require(parsedBlocklists[0].Enabled && parsedBlocklists[0].RuleCount == 922337203685477, "blocklist parser preserves enabled state and 64-bit rule count");
Require(!parsedBlocklists[1].Enabled && parsedBlocklists[1].RuleCount == 7, "blocklist parser reads disabled state");
using JsonDocument emptyBlocklistDocument = JsonDocument.Parse("{ \"filters\": [] }");
Require(((List<AdGuardBlocklist>)parseBlocklists.Invoke(null, [emptyBlocklistDocument.RootElement])!).Count == 0, "blocklist parser accepts an empty list");
using JsonDocument malformedBlocklistDocument = JsonDocument.Parse("{ \"filters\": [ { \"name\": \"no URL\" } ] }");
Require(((List<AdGuardBlocklist>)parseBlocklists.Invoke(null, [malformedBlocklistDocument.RootElement])!).Count == 0, "blocklist parser ignores malformed entries");
Console.WriteLine("Network Health, notification, blocklist and SSH fixtures passed: 74 checks.");

static void RequireThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
