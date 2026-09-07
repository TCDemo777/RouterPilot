using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    /// <summary>Reads only fixed community-updater recovery markers. It never returns file contents.</summary>
    public async Task<AdGuardHomeRecoveryState> GetAdGuardHomeRecoveryStateAsync(CancellationToken cancellationToken = default)
    {
        const string command = "backup=0; [ -f /root/AdGuardHome_backup.tar.gz ] && backup=1; " +
            "size=''; [ \"$backup\" = 1 ] && size=$(wc -c < /root/AdGuardHome_backup.tar.gz 2>/dev/null); " +
            "readable=0; binary=0; config=0; safe=0; " +
            "if [ \"$backup\" = 1 ] && tar -tzf /root/AdGuardHome_backup.tar.gz >/dev/null 2>&1; then readable=1; " +
            "tar -tzf /root/AdGuardHome_backup.tar.gz 2>/dev/null | grep -Eq '(^|/)usr/bin/AdGuardHome$' && binary=1; " +
            "tar -tzf /root/AdGuardHome_backup.tar.gz 2>/dev/null | grep -Eq '(^|/)AdGuardHome(/|$)' && config=1; " +
            "if ! tar -tzf /root/AdGuardHome_backup.tar.gz 2>/dev/null | grep -Eq '(^/|(^|/)\\.\\.(/|$))'; then safe=1; fi; fi; " +
            "helper=0; [ -f /usr/bin/enable-adguardhome-update-check ] && helper=1; " +
            "rc=0; grep -Fq '/usr/bin/enable-adguardhome-update-check' /etc/rc.local 2>/dev/null && rc=1; " +
            "syscount=$(grep -Ec '^[[:space:]]*(/root/AdGuardHome_backup.tar.gz|/etc/AdGuardHome|/usr/bin/AdGuardHome|/usr/bin/enable-adguardhome-update-check|/etc/rc\\.local)[[:space:]]*$' /etc/sysupgrade.conf 2>/dev/null); sys=0; [ \"$syscount\" -gt 0 ] 2>/dev/null && sys=1; " +
            "bin=0; [ -x /usr/bin/AdGuardHome ] && bin=1; configdir=0; [ -d /etc/AdGuardHome ] && configdir=1; " +
            "running=0; pgrep -f '[A]dGuardHome' >/dev/null 2>&1 && running=1; " +
            "printf 'B=%s\\nS=%s\\nR=%s\\nO=%s\\nC=%s\\nP=%s\\nH=%s\\nL=%s\\nY=%s\\nN=%s\\nX=%s\\nD=%s\\nG=%s\\n' \"$backup\" \"$size\" \"$readable\" \"$binary\" \"$config\" \"$safe\" \"$helper\" \"$rc\" \"$sys\" \"$syscount\" \"$bin\" \"$configdir\" \"$running\"";

        string output = await _ssh.RunCommandAsync(command, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = output.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        bool? Flag(string key) => values.TryGetValue(key, out string? value) && (value == "0" || value == "1") ? value == "1" : null;
        long? Size() => values.TryGetValue("S", out string? value) && long.TryParse(value, out long size) && size >= 0 ? size : null;
        int? Count() => values.TryGetValue("N", out string? value) && int.TryParse(value, out int count) && count >= 0 ? count : null;
        bool? detected = Flag("B") is true || Flag("H") is true || Flag("L") is true || Flag("Y") is true ? true :
            Flag("B") is false && Flag("H") is false && Flag("L") is false && Flag("Y") is false ? false : null;
        return new AdGuardHomeRecoveryState(detected, Flag("B"), Size(), Flag("R"), Flag("O"), Flag("C"), Flag("P"), Flag("H"), Flag("L"), Flag("Y"), Count(), Flag("X"), Flag("D"), Flag("G"));
    }

    /// <summary>Removes only exact documented community-updater integration markers, then verifies the intended file transforms.</summary>
    public async Task<AdGuardUpdaterCleanupResult> RemoveAdGuardUpdaterIntegrationAsync(CancellationToken cancellationToken = default)
    {
        string output = await _ssh.RunCommandAsync(AdGuardUpdaterIntegrationCleanup.RouterCommand, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = output.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        bool confirmed = values.TryGetValue("R", out string? rc) && rc == "1" &&
            values.TryGetValue("S", out string? sys) && sys == "1" &&
            values.TryGetValue("H", out string? helper) && helper == "0";
        return new AdGuardUpdaterCleanupResult(confirmed,
            confirmed ? "Updater integration removed and read-back verified." : "Cleanup outcome could not be confirmed.");
    }
}
