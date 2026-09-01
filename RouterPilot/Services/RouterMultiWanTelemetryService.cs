using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class RouterMultiWanTelemetryService
{
    private readonly GLInetSshService _ssh;
    public RouterMultiWanTelemetryService(GLInetSshService ssh) => _ssh = ssh;

    public async Task<RouterMultiWanSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The bounded interface scan supplies ordinary WAN telemetry only.
            // Until a platform Multi-WAN service/configuration source is
            // available, capability and mode remain explicitly unknown.
            string output = await _ssh.RunCommandAsync(
                "printf 'S|unknown|unknown|unknown|||\\n'; default_dev=$(ip -4 route show default 2>/dev/null | awk 'NR==1 {print $5}'); for iface in wan wwan repeater tethering modem; do json=$(ubus call network.interface.$iface status 2>/dev/null) || json=; [ -n \"$json\" ] || continue; up=$(echo \"$json\" | jsonfilter -e '@.up'); dev=$(echo \"$json\" | jsonfilter -e '@.l3_device'); gw=$(echo \"$json\" | jsonfilter -e '@.route[0].nexthop'); ip=$(echo \"$json\" | jsonfilter -e '@.ipv4-address[0].address'); is_default=$([ -n \"$dev\" ] && [ \"$dev\" = \"$default_dev\" ] && echo 1 || echo 0); printf 'W|%s|%s|%s|%s|%s|1|%s|%s|%s|%s||%s|%s||||\\n' \"$iface\" \"$iface\" \"$([ \"$iface\" = wan ] && echo ethernet || echo unknown)\" \"$iface\" \"$dev\" \"$up\" \"$up\" \"$up\" \"$is_default\" \"$gw\" \"$ip\"; done",
                cancellationToken);
            return RouterMultiWanParser.Parse(output, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Debug.WriteLine($"Multi-WAN telemetry unavailable ({exception.GetType().Name}).");
            return RouterMultiWanSnapshot.Unknown;
        }
    }
}
