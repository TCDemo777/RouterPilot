using RouterPilot.Models;

namespace RouterPilot.Services;

internal sealed class RouterAdvancedTelemetryService
{
    private readonly GLInetSshService _ssh;
    public RouterAdvancedTelemetryService(GLInetSshService ssh) => _ssh = ssh;

    public async Task<RouterAdvancedSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        const string command = "printf '__GLCONFIG__\\n'; uci -q show glconfig.general 2>/dev/null; printf '__NETWORK__\\n'; uci -q show network.iot network.guest 2>/dev/null; printf '__FIREWALL__\\n'; uci -q show firewall.@zone[1] 2>/dev/null; printf '__SQM__\\n'; uci -q show sqm 2>/dev/null; printf '__ZEROTIER__\\n'; uci -q show zerotier 2>/dev/null; printf '__NAS__\\n'; uci -q show nas.conf 2>/dev/null; printf '__PROCESSES__\\n'; ps w 2>/dev/null | grep -E '[g]l-dpi|[m]inidlnad'";
        string output = await _ssh.RunCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return Parse(output);
    }

    internal static RouterAdvancedSnapshot Parse(string output)
    {
        string Value(string key) => output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?.Split('=', 2)[1].Trim(' ', '\'', '"') ?? string.Empty;
        bool? Bool(string key) => Value(key).ToLowerInvariant() switch { "1" or "yes" or "true" => true, "0" or "no" or "false" => false, _ => null };
        bool hasSection(string section) => output.Contains(section, StringComparison.Ordinal);
        return new RouterAdvancedSnapshot(
            Value("glconfig.general.mode") is { Length: > 0 } mode ? mode : "Unknown",
            Bool("network.iot.disabled") is bool disabledIot ? !disabledIot : null,
            Bool("network.guest.disabled") is bool disabledGuest ? !disabledGuest : null,
            Bool("network.guest.igmp_snooping"), Bool("network.iot.igmp_snooping"),
            Bool("firewall.@zone[1].masq"), Bool("firewall.@zone[1].masq6"),
            Bool("sqm.eth1.enabled"), Value("sqm.eth1.qdisc") is { Length: > 0 } qdisc ? qdisc : "Unknown",
            hasSection("/usr/bin/eco /usr/bin/gl-dpi"), Bool("zerotier.gl.enabled") is bool z ? true : hasSection("__ZEROTIER__") ? null : null,
            Bool("zerotier.gl.enabled"), Bool("nas.conf.webdav_enable"), Bool("nas.conf.webdav_wan_access"),
            hasSection("minidlnad"), DateTimeOffset.UtcNow);
    }
}
