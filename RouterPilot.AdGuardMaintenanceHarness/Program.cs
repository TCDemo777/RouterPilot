using RouterPilot.Services;
using System.Text.Json;
using System.IO;

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
        string notices = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "RouterPilot", "THIRD_PARTY_NOTICES.txt"));
        Assert(notices.Contains("GL.iNet Tailscale Updater", StringComparison.Ordinal) && notices.Contains("Aaron Viehl", StringComparison.Ordinal) && notices.Contains("runtime-downloaded", StringComparison.Ordinal), "tailscale updater attribution retained");
        Console.WriteLine("AdGuard Home maintenance harness: PASS");
        return 0;
    }
    static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException($"Failed: {name}"); }
}
