using RouterPilot.Services;

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
        Assert(AdGuardHomeUpdaterCommand.Build(false, false) == AdGuardHomeUpdaterCommand.BaseCommand, "no options");
        Assert(AdGuardHomeUpdaterCommand.Build(true, false).EndsWith(" --select-release", StringComparison.Ordinal), "select release");
        Assert(AdGuardHomeUpdaterCommand.Build(false, true).EndsWith(" --ignore-free-space", StringComparison.Ordinal), "ignore free space");
        Assert(AdGuardHomeUpdaterCommand.Build(true, true).EndsWith(" --select-release --ignore-free-space", StringComparison.Ordinal), "deterministic option order");
        Assert(!AdGuardHomeUpdaterCommand.BaseCommand.Contains("--force", StringComparison.Ordinal), "no force arguments");
        Assert(AdGuardHomeUpdaterCommand.CanOpenTerminal(false, false), "normal confirmation");
        Assert(!AdGuardHomeUpdaterCommand.CanOpenTerminal(true, false), "high-risk acknowledgement required");
        Assert(AdGuardHomeUpdaterCommand.CanOpenTerminal(true, true), "high-risk acknowledgement accepted");
        Console.WriteLine("AdGuard Home maintenance harness: PASS");
        return 0;
    }
    static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException($"Failed: {name}"); }
}
