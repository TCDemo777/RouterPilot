namespace RouterPilot.Services;

public static class AdGuardHomeUpdaterCommand
{
    public const string BaseCommand = "wget -O update-adguardhome.sh https://get.admon.me/adguard && sh update-adguardhome.sh";

    public static string Build(bool selectRelease, bool ignoreFreeSpace)
    {
        List<string> arguments = [];
        if (selectRelease) arguments.Add("--select-release");
        if (ignoreFreeSpace) arguments.Add("--ignore-free-space");
        return arguments.Count == 0 ? BaseCommand : BaseCommand + " " + string.Join(" ", arguments);
    }

    public static bool CanOpenTerminal(bool ignoreFreeSpace, bool riskAcknowledged) => !ignoreFreeSpace || riskAcknowledged;
}
