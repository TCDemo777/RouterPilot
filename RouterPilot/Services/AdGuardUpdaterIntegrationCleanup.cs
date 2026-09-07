using System.Text.RegularExpressions;

namespace RouterPilot.Services;

/// <summary>Defines the exact, documented community-updater integration markers RouterPilot may remove.</summary>
public static class AdGuardUpdaterIntegrationCleanup
{
    public const string HelperPath = "/usr/bin/enable-adguardhome-update-check";
    public static readonly IReadOnlySet<string> SysupgradeEntries = new HashSet<string>(StringComparer.Ordinal)
    {
        "/root/AdGuardHome_backup.tar.gz",
        "/etc/AdGuardHome",
        "/usr/bin/AdGuardHome",
        HelperPath,
        "/etc/rc.local"
    };

    private static readonly Regex RcLocalIntegration = new(
        @"^\s*\.\s+/usr/bin/enable-adguardhome-update-check\s*$", RegexOptions.CultureInvariant);

    public static string RemoveRcLocalIntegration(string content) =>
        string.Join("\n", content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !RcLocalIntegration.IsMatch(line)));

    public static string RemoveSysupgradeEntries(string content) =>
        string.Join("\n", content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !SysupgradeEntries.Contains(line.Trim())));

    /// <summary>Fixed shell contract: only exact known lines and the updater-owned helper are changed.</summary>
    public const string RouterCommand =
        "command -v sha256sum >/dev/null 2>&1 && [ -f /etc/rc.local ] && [ -f /etc/sysupgrade.conf ] || { printf 'R=0\\nS=0\\nH=2\\n'; exit 0; }; " +
        "rc_expected=$(sed '\\|^[[:space:]]*\\.[[:space:]]*/usr/bin/enable-adguardhome-update-check[[:space:]]*$\\|d' /etc/rc.local 2>/dev/null | sha256sum 2>/dev/null | awk '{print $1}'); " +
        "sys_expected=$(sed -e '\\|^[[:space:]]*/root/AdGuardHome_backup.tar.gz[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/etc/AdGuardHome[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/usr/bin/AdGuardHome[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/usr/bin/enable-adguardhome-update-check[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/etc/rc.local[[:space:]]*$\\|d' /etc/sysupgrade.conf 2>/dev/null | sha256sum 2>/dev/null | awk '{print $1}'); " +
        "sed -i '\\|^[[:space:]]*\\.[[:space:]]*/usr/bin/enable-adguardhome-update-check[[:space:]]*$\\|d' /etc/rc.local 2>/dev/null; " +
        "sed -i -e '\\|^[[:space:]]*/root/AdGuardHome_backup.tar.gz[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/etc/AdGuardHome[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/usr/bin/AdGuardHome[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/usr/bin/enable-adguardhome-update-check[[:space:]]*$\\|d' -e '\\|^[[:space:]]*/etc/rc.local[[:space:]]*$\\|d' /etc/sysupgrade.conf 2>/dev/null; " +
        "rm -f /usr/bin/enable-adguardhome-update-check; " +
        "rc_actual=$(sha256sum /etc/rc.local 2>/dev/null | awk '{print $1}'); sys_actual=$(sha256sum /etc/sysupgrade.conf 2>/dev/null | awk '{print $1}'); helper=0; [ -e /usr/bin/enable-adguardhome-update-check ] && helper=1; " +
        "printf 'R=%s\\nS=%s\\nH=%s\\n' \"$([ \"$rc_expected\" = \"$rc_actual\" ] && echo 1 || echo 0)\" \"$([ \"$sys_expected\" = \"$sys_actual\" ] && echo 1 || echo 0)\" \"$helper\"";
}
