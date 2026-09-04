using RouterPilot.Models;

namespace RouterPilot.Services;

internal sealed class RouterAdvancedTelemetryService
{
    private readonly GLInetSshService _ssh;
    public RouterAdvancedTelemetryService(GLInetSshService ssh) => _ssh = ssh;

    public async Task<RouterAdvancedSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        const string command = "printf '__GLCONFIG__\\n'; uci -q show glconfig.general 2>/dev/null; printf '__NETWORK__\\n'; uci -q show network.iot 2>/dev/null; uci -q show network.guest 2>/dev/null; printf '__FIREWALL__\\n'; uci -q show firewall 2>/dev/null; printf '__SQM__\\n'; uci -q show sqm 2>/dev/null; printf '__ZEROTIER__\\n'; uci -q show zerotier 2>/dev/null; printf '__NAS__\\n'; uci -q show nas.conf 2>/dev/null; printf '__DPI__\\n'; uci -q show gl_dpi 2>/dev/null; printf '__PROCESSES__\\n'; ps w 2>/dev/null | grep -E '[g]l-dpi|[m]inidlnad|[z]erotier'";
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
            FirewallZoneBool(output, "wan", "masq"), FirewallZoneBool(output, "wan", "masq6"),
            Bool("sqm.eth1.enabled"), Value("sqm.eth1.qdisc") is { Length: > 0 } qdisc ? qdisc : "Unknown", Value("sqm.eth1.download") is { Length: > 0 } down ? down : "Unknown", Value("sqm.eth1.upload") is { Length: > 0 } up ? up : "Unknown",
            Bool("gl_dpi.enabled"), hasSection("/usr/bin/eco /usr/bin/gl-dpi"), hasSection("zerotier.gl=zerotier"),
            Bool("zerotier.gl.enabled"), Bool("nas.conf.webdav_enable"), Bool("nas.conf.webdav_wan_access"), null,
            null, hasSection("minidlnad"), DateTimeOffset.UtcNow);
    }

    private static bool? FirewallZoneBool(string output, string zoneName, string option)
    {
        string? section = output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("firewall.", StringComparison.Ordinal) && line.EndsWith(".name='" + zoneName + "'", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Split('.', 3)[1])
            .FirstOrDefault();
        if (section is null)
            section = "@zone[1]";

        string prefix = $"firewall.{section}.{option}=";
        string? value = output.Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?
            .Split('=', 2)[1].Trim(' ', '\'', '"');
        return value?.ToLowerInvariant() switch { "1" or "yes" or "true" => true, "0" or "no" or "false" => false, _ => null };
    }
}
