using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    /// <summary>Reads fixed restore preconditions and a known updater adjustment without returning configuration contents.</summary>
    public async Task<TailscaleRecoveryState> GetTailscaleRecoveryStateAsync(CancellationToken cancellationToken = default)
    {
        const string command = "backup=0; latest=''; [ -d /root/tailscale_config_backup ] && latest=$(find /root/tailscale_config_backup -maxdepth 1 -type f -name '*.tar.gz' -print -quit 2>/dev/null); [ -n \"$latest\" ] && backup=1; size=''; [ \"$backup\" = 1 ] && size=$(wc -c < \"$latest\" 2>/dev/null); persist=0; grep -Eq '^(/usr/sbin/tailscale|/usr/sbin/tailscaled|/etc/config/tailscale|/usr/bin/gl_tailscale|/root/tailscale_config_backup/)' /etc/sysupgrade.conf 2>/dev/null && persist=1; " +
            "romtail=0; [ -f /rom/usr/sbin/tailscale ] && romtail=1; romdaemon=0; [ -f /rom/usr/sbin/tailscaled ] && romdaemon=1; " +
            "tail=0; [ -x /usr/sbin/tailscale ] && tail=1; daemon=0; [ -x /usr/sbin/tailscaled ] && daemon=1; " +
            "gl=0; [ -f /usr/bin/gl_tailscale ] && gl=1; stateful=0; grep -Fq -- '--stateful-filtering=false' /usr/bin/gl_tailscale 2>/dev/null && stateful=1; " +
            "running=0; pidof tailscaled >/dev/null 2>&1 && running=1; " +
            "printf 'A=%s\\nB=%s\\nC=%s\\nD=%s\\nE=%s\\nF=%s\\nG=%s\\nH=%s\\nI=%s\\nJ=%s\\n' \"$backup\" \"$size\" \"$persist\" \"$romtail\" \"$romdaemon\" \"$tail\" \"$daemon\" \"$gl\" \"$stateful\" \"$running\"";
        string output = await _ssh.RunCommandAsync(command, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = output.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        bool? Flag(string key) => values.TryGetValue(key, out string? value) && (value == "0" || value == "1") ? value == "1" : null;
        long? Size() => values.TryGetValue("B", out string? value) && long.TryParse(value, out long size) && size >= 0 ? size : null;
        bool? detected = Flag("A") is true || Flag("C") is true || Flag("I") is true ? true :
            Flag("A") is false && Flag("C") is false && Flag("I") is false ? false : null;
        return new TailscaleRecoveryState(detected, Flag("A"), Size(), Flag("D"), Flag("E"), Flag("F"), Flag("G"), Flag("H"), Flag("I"), Flag("J"));
    }
}
