using RouterPilot.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Xml.Linq;

static class Program
{
    static int Main()
    {
        Assert(AdGuardHomeMaintenanceService.Compare("v0.107.75", "v0.107.79", out int older) && older < 0, "older stable comparison");
        Assert(AdGuardHomeMaintenanceService.Compare("v0.107.79", "0.107.79", out int equal) && equal == 0, "equal stable comparison");
        Assert(AdGuardHomeMaintenanceService.Compare("v0.107.80", "v0.107.79", out int newer) && newer > 0, "newer stable comparison");
        Assert(AdGuardHomeMaintenanceService.Compare("AdGuard Home, version v0.107.75", "v0.107.79", out int binaryOutput) && binaryOutput < 0, "installed binary version parsing");
        Assert(!AdGuardHomeMaintenanceService.Compare("unknown", "v0.107.79", out _), "malformed installed version");
        Assert(!AdGuardHomeMaintenanceService.Compare("v0.107.79", "preview", out _), "malformed latest version");
        using JsonDocument releases = JsonDocument.Parse("[{\"tag_name\":\"v0.108.0-b.1\",\"draft\":false,\"prerelease\":true},{\"tag_name\":\"v0.107.80\",\"draft\":true,\"prerelease\":false},{\"tag_name\":\"v0.107.79\",\"draft\":false,\"prerelease\":false}]");
        Assert(AdGuardHomeMaintenanceService.SelectLatestStableVersion(releases.RootElement) == "v0.107.79", "draft and prerelease ignored");
        using JsonDocument malformed = JsonDocument.Parse("{}");
        Assert(AdGuardHomeMaintenanceService.SelectLatestStableVersion(malformed.RootElement) is null, "malformed release response");
        Assert(AdGuardHomeUpdaterCommand.Build(false, false) == AdGuardHomeUpdaterCommand.BaseCommand, "no options");
        Assert(AdGuardHomeUpdaterCommand.Build(true, false).EndsWith(" --select-release", StringComparison.Ordinal), "select release");
        Assert(AdGuardHomeUpdaterCommand.Build(false, true).EndsWith(" --ignore-free-space", StringComparison.Ordinal), "ignore free space");
        Assert(AdGuardHomeUpdaterCommand.Build(true, true).EndsWith(" --select-release --ignore-free-space", StringComparison.Ordinal), "deterministic option order");
        Assert(!AdGuardHomeUpdaterCommand.BaseCommand.Contains("--force", StringComparison.Ordinal), "no force arguments");
        Assert(AdGuardHomeUpdaterCommand.CanOpenTerminal(false, false), "normal confirmation");
        Assert(!AdGuardHomeUpdaterCommand.CanOpenTerminal(true, false), "high-risk acknowledgement required");
        Assert(AdGuardHomeUpdaterCommand.CanOpenTerminal(true, true), "high-risk acknowledgement accepted");
        Assert(TailscaleMaintenanceService.Compare("v1.82.0", "v1.84.1", out int tailscaleOlder) && tailscaleOlder < 0, "tailscale older stable comparison");
        Assert(TailscaleMaintenanceService.Compare("1.84.1", "v1.84.1", out int tailscaleEqual) && tailscaleEqual == 0, "tailscale equal stable comparison");
        Assert(!TailscaleMaintenanceService.Compare("v1.84.1-tiny", "v1.84.1", out _), "community suffix handled conservatively");
        using JsonDocument tailscaleReleases = JsonDocument.Parse("[{\"tag_name\":\"v1.85.0-rc1\",\"draft\":false,\"prerelease\":true},{\"tag_name\":\"v1.84.2\",\"draft\":true,\"prerelease\":false},{\"tag_name\":\"v1.84.1\",\"draft\":false,\"prerelease\":false}]");
        Assert(TailscaleMaintenanceService.SelectLatestStableVersion(tailscaleReleases.RootElement) == "v1.84.1", "tailscale draft and prerelease ignored");
        TailscaleUpdaterOptions noOptions = new(false, false, false, false, false, false);
        Assert(TailscaleUpdaterCommand.Build(noOptions) == TailscaleUpdaterCommand.BaseCommand, "tailscale no options");
        TailscaleUpdaterOptions allOptions = new(true, true, true, true, true, true);
        Assert(TailscaleUpdaterCommand.Build(allOptions).EndsWith(" --select-release --ssh --no-tiny --no-upx --skip-config --ignore-free-space", StringComparison.Ordinal), "tailscale deterministic option order");
        Assert(!TailscaleUpdaterCommand.Build(allOptions).Contains("--force", StringComparison.Ordinal), "tailscale force never generated");
        Assert(!TailscaleUpdaterCommand.Build(allOptions).Contains("--testing", StringComparison.Ordinal), "tailscale testing never generated");
        Assert(!TailscaleUpdaterCommand.Build(allOptions).Contains("--no-download", StringComparison.Ordinal), "tailscale no-download never generated");
        Assert(TailscaleUpdaterCommand.CanOpenTerminal(noOptions, false), "tailscale normal confirmation");
        Assert(!TailscaleUpdaterCommand.CanOpenTerminal(allOptions, false), "tailscale high-risk acknowledgement required");
        Assert(TailscaleUpdaterCommand.CanOpenTerminal(allOptions, true), "tailscale high-risk acknowledgement accepted");
        Assert(TailscaleUpdaterCommand.RestoreCommand.EndsWith(" --restore", StringComparison.Ordinal), "tailscale restore isolated");
        Assert(!TailscaleUpdaterCommand.CanOpenRestoreTerminal(false) && TailscaleUpdaterCommand.CanOpenRestoreTerminal(true), "tailscale restore acknowledgement required");
        Assert(!TailscaleUpdaterCommand.RestoreCommand.Contains("--select-release", StringComparison.Ordinal) && !TailscaleUpdaterCommand.RestoreCommand.Contains("--ssh", StringComparison.Ordinal), "tailscale restore excludes update options");
        Assert(!TailscaleUpdaterCommand.RestoreCommand.Contains("--force", StringComparison.Ordinal) && !TailscaleUpdaterCommand.RestoreCommand.Contains("--testing", StringComparison.Ordinal) && !TailscaleUpdaterCommand.RestoreCommand.Contains("--no-download", StringComparison.Ordinal), "tailscale restore excludes unsafe options");
        ProcessStartInfo? capturedStartInfo = null;
        MaintenanceExternalLauncher terminalLauncher = new(
            () => "C:\\Windows\\System32\\cmd.exe",
            _ => { },
            startInfo => { capturedStartInfo = startInfo; return null; });
        MaintenanceExternalLaunchResult terminalLaunched = terminalLauncher.LaunchInteractiveSsh("router.example", "root", 22, TailscaleUpdaterCommand.BaseCommand);
        Assert(terminalLaunched.Status == MaintenanceExternalLaunchStatus.Launched, "terminal launch success is structured");
        Assert(capturedStartInfo is not null && capturedStartInfo.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase), "terminal command shell selected");
        Assert(capturedStartInfo is not null && string.Join(" ", capturedStartInfo.ArgumentList).Contains("ssh.exe -p 22 root@router.example", StringComparison.Ordinal), "ssh target passed without credentials");
        Assert(capturedStartInfo is not null && !string.Join(" ", capturedStartInfo.ArgumentList).Contains("password", StringComparison.OrdinalIgnoreCase), "terminal arguments contain no credentials");
        Assert(capturedStartInfo is not null && !string.Join(" ", capturedStartInfo.ArgumentList).Contains("wget", StringComparison.Ordinal), "updater command stays out of terminal arguments");
        MaintenanceExternalLauncher missingLauncher = new(() => null, _ => { }, _ => null);
        Assert(missingLauncher.LaunchInteractiveSsh("router.example", "root", 22, TailscaleUpdaterCommand.BaseCommand).Status == MaintenanceExternalLaunchStatus.LauncherUnavailable, "terminal executable unavailable");
        MaintenanceExternalLauncher missingFile = new(() => "cmd.exe", _ => { }, _ => throw new FileNotFoundException("missing"));
        Assert(missingFile.LaunchInteractiveSsh("router.example", "root", 22, TailscaleUpdaterCommand.BaseCommand).Status == MaintenanceExternalLaunchStatus.LauncherUnavailable, "missing terminal executable contained");
        MaintenanceExternalLauncher win32Failure = new(() => "cmd.exe", _ => { }, _ => throw new Win32Exception("unavailable"));
        Assert(win32Failure.LaunchInteractiveSsh("router.example", "root", 22, TailscaleUpdaterCommand.BaseCommand).Status == MaintenanceExternalLaunchStatus.LaunchFailed, "Win32 launch failure contained");
        MaintenanceExternalLauncher invalidFailure = new(() => "cmd.exe", _ => { }, _ => throw new InvalidOperationException("invalid"));
        Assert(invalidFailure.LaunchInteractiveSsh("router.example", "root", 22, AdGuardHomeUpdaterCommand.BaseCommand).Status == MaintenanceExternalLaunchStatus.LaunchFailed, "invalid launch failure contained");
        MaintenanceExternalLauncher accessFailure = new(() => "cmd.exe", _ => { }, _ => throw new UnauthorizedAccessException("denied"));
        Assert(accessFailure.LaunchInteractiveSsh("router.example", "root", 22, TailscaleUpdaterCommand.BaseCommand).Status == MaintenanceExternalLaunchStatus.LaunchFailed, "access launch failure contained");
        MaintenanceExternalLauncher clipboardFailure = new(() => "cmd.exe", _ => throw new ExternalException("clipboard unavailable"), _ => null);
        MaintenanceExternalLaunchResult launchedWithoutClipboard = clipboardFailure.LaunchInteractiveSsh("router.example", "root", 22, TailscaleUpdaterCommand.BaseCommand);
        Assert(launchedWithoutClipboard.Status == MaintenanceExternalLaunchStatus.LaunchedWithoutClipboard && !launchedWithoutClipboard.ClipboardCopied, "clipboard failure remains non-fatal");
        MaintenanceExternalLauncher linkFailure = new(() => "cmd.exe", _ => { }, _ => throw new Win32Exception("browser unavailable"));
        Assert(linkFailure.OpenUri(new Uri("https://github.com/admonstrator/glinet-tailscale-updater")).Status == MaintenanceExternalLaunchStatus.LaunchFailed, "project link launch failure contained");
        string rcLocal = "#!/bin/sh\n. /usr/bin/enable-adguardhome-update-check\necho keep\nexit 0\n";
        string sysupgrade = "/etc/AdGuardHome\n/custom/preserve\n/usr/bin/enable-adguardhome-update-check\n";
        Assert(AdGuardUpdaterIntegrationCleanup.RemoveRcLocalIntegration(rcLocal) == "#!/bin/sh\necho keep\nexit 0\n", "targeted rc.local cleanup");
        Assert(AdGuardUpdaterIntegrationCleanup.RemoveSysupgradeEntries(sysupgrade) == "/custom/preserve\n", "targeted sysupgrade cleanup");
        Assert(AdGuardUpdaterIntegrationCleanup.RemoveRcLocalIntegration("echo keep\n") == "echo keep\n", "missing startup line safe");
        Assert(!AdGuardUpdaterIntegrationCleanup.RouterCommand.Contains("rm -rf /etc/AdGuardHome", StringComparison.Ordinal) && !AdGuardUpdaterIntegrationCleanup.RouterCommand.Contains("/etc/init.d/adguardhome", StringComparison.Ordinal), "cleanup preserves AdGuard configuration and service");
        Assert(!AdGuardUpdaterIntegrationCleanup.RouterCommand.Contains("rm -f /root/AdGuardHome_backup.tar.gz", StringComparison.Ordinal) && AdGuardUpdaterIntegrationCleanup.RouterCommand.Contains("rm -f /usr/bin/enable-adguardhome-update-check", StringComparison.Ordinal), "cleanup preserves backup and removes only helper");
        string notices = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "RouterPilot", "THIRD_PARTY_NOTICES.txt"));
        Assert(notices.Contains("GL.iNet Tailscale Updater", StringComparison.Ordinal) && notices.Contains("Aaron Viehl", StringComparison.Ordinal) && notices.Contains("runtime-downloaded", StringComparison.Ordinal), "tailscale updater attribution retained");
        XDocument maintenance = XDocument.Load(Path.Combine(Directory.GetCurrentDirectory(), "RouterPilot", "Views", "MaintenanceView.xaml"));
        XElement contentHost = maintenance.Descendants().Single(element => element.Name.LocalName == "StackPanel" && (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "MaintenanceContent");
        string[] directSections = contentHost.Elements().Where(element => element.Name.LocalName == "Border")
            .Select(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? string.Empty).ToArray();
        Assert(directSections.Contains("CommunityToolsSection", StringComparer.Ordinal), "community acknowledgement hosted directly");
        Assert(directSections.Contains("AdGuardHomeSection", StringComparer.Ordinal), "adguard hosted directly");
        Assert(directSections.Contains("TailscaleSection", StringComparer.Ordinal), "tailscale hosted directly");
        string maintenanceXaml = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "RouterPilot", "Views", "MaintenanceView.xaml"));
        Assert(maintenanceXaml.Contains("AdGuard Home updater by admon", StringComparison.Ordinal) && maintenanceXaml.Contains("https://github.com/admonstrator/glinet-adguard-updater", StringComparison.Ordinal), "adguard credit and project link");
        Assert(maintenanceXaml.Contains("GL.iNet Tailscale updater by admon", StringComparison.Ordinal) && maintenanceXaml.Contains("https://github.com/admonstrator/glinet-tailscale-updater", StringComparison.Ordinal), "tailscale credit and project link");
        Console.WriteLine("AdGuard Home maintenance harness: PASS");
        return 0;
    }
    static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException($"Failed: {name}"); }
}
