namespace RouterPilot.Services;

public sealed record TailscaleUpdaterOptions(
    bool SelectRelease,
    bool EnableSsh,
    bool UseFullBinaries,
    bool DisableUpx,
    bool SkipGlInetConfiguration,
    bool IgnoreFreeSpace);

/// <summary>Builds only the fixed, reviewed community-updater flags; it never accepts user supplied arguments.</summary>
public static class TailscaleUpdaterCommand
{
    public const string BaseCommand = "wget -q https://get.admon.me/tailscale -O update-tailscale.sh ; sh update-tailscale.sh";
    public const string RestoreCommand = BaseCommand + " --restore";

    public static string Build(TailscaleUpdaterOptions options)
    {
        List<string> arguments = [];
        if (options.SelectRelease) arguments.Add("--select-release");
        if (options.EnableSsh) arguments.Add("--ssh");
        if (options.UseFullBinaries) arguments.Add("--no-tiny");
        if (options.DisableUpx) arguments.Add("--no-upx");
        if (options.SkipGlInetConfiguration) arguments.Add("--skip-config");
        if (options.IgnoreFreeSpace) arguments.Add("--ignore-free-space");
        return arguments.Count == 0 ? BaseCommand : BaseCommand + " " + string.Join(" ", arguments);
    }

    public static bool CanOpenTerminal(TailscaleUpdaterOptions options, bool riskAcknowledged) =>
        !options.IgnoreFreeSpace || riskAcknowledged;
}
