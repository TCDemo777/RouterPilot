using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class RouterDnsTelemetryService
{
    private readonly GLInetSshService _ssh;
    public RouterDnsTelemetryService(GLInetSshService ssh) => _ssh = ssh;

    public async Task<RouterDnsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string output = await _ssh.RunCommandAsync(
                "runtime=unknown; service=unknown; if pgrep -x dnsmasq >/dev/null 2>&1; then runtime=running; service=dnsmasq; elif pgrep -x smartdns >/dev/null 2>&1; then runtime=running; service=smartdns; fi; resolvers=$(awk '/^nameserver[[:space:]]+/ {print $2}' /tmp/resolv.conf.d/resolv.conf.auto 2>/dev/null); configured=$(uci -q get dhcp.@dnsmasq[0].server 2>/dev/null); mode=automatic; [ -n \"$configured\" ] && mode=manual; capability=unknown; [ \"$runtime\" = running ] || [ -n \"$resolvers\" ] || [ -n \"$configured\" ] && capability=supported; printf 'S|%s|%s|%s|%s|unknown|unknown|unknown\\n' \"$capability\" \"$service\" \"$mode\" \"$runtime\"; printf '%s\\n' \"$resolvers\" | sed '/^$/d; s/^/U|/'; printf '%s' \"$configured\" | tr ' ' '\\n' | sed '/^$/d; s/^/U|/'",
                cancellationToken);
            return RouterDnsParser.Parse(output, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Debug.WriteLine($"Router DNS telemetry unavailable ({exception.GetType().Name}).");
            return RouterDnsSnapshot.Unknown;
        }
    }
}
