using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed record RouterPortTelemetryResult(
    RouterCapabilityState Capability,
    IReadOnlyList<RouterPortSnapshot> Ports);

public sealed class RouterPortTelemetryService
{
    private readonly GLInetSshService _ssh;

    public RouterPortTelemetryService(GLInetSshService ssh) => _ssh = ssh;

    public async Task<RouterPortTelemetryResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string output = await _ssh.RunCommandAsync(
                "for path in /sys/class/net/*; do [ -e \"$path\" ] || continue; name=${path##*/}; type=$(cat \"$path/type\" 2>/dev/null); oper=$(cat \"$path/operstate\" 2>/dev/null); carrier=$(cat \"$path/carrier\" 2>/dev/null); speed=$(cat \"$path/speed\" 2>/dev/null); duplex=$(cat \"$path/duplex\" 2>/dev/null); mac=$(cat \"$path/address\" 2>/dev/null); rx=$(cat \"$path/statistics/rx_bytes\" 2>/dev/null); tx=$(cat \"$path/statistics/tx_bytes\" 2>/dev/null); rxe=$(cat \"$path/statistics/rx_errors\" 2>/dev/null); txe=$(cat \"$path/statistics/tx_errors\" 2>/dev/null); rxd=$(cat \"$path/statistics/rx_dropped\" 2>/dev/null); txd=$(cat \"$path/statistics/tx_dropped\" 2>/dev/null); bridge=$(readlink \"$path/bridge\" 2>/dev/null); kind=unknown; [ \"$name\" = lo ] && kind=loopback; [ -d \"$path/wireless\" ] && kind=wireless; [ -d \"$path/bridge\" ] && kind=bridge; case \"$name\" in wg*|tun*|tailscale*) kind=vpn;; *.*) [ \"$kind\" = unknown ] && kind=vlan;; esac; [ \"$kind\" = unknown ] && [ \"$type\" = 1 ] && [ -e \"$path/device\" ] && kind=physical; echo \"$name|$type|$kind|$oper|$carrier|$speed|$duplex|$mac|$rx|$tx|$rxe|$txe|$rxd|$txd|$bridge||\"; done",
                cancellationToken);
            var normalized = new System.Text.StringBuilder();
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = line.Split('|');
                if (f.Length < 17) continue;
                string kind = f[2] switch { "physical" => "physical", "bridge" => "bridge", "wireless" => "wireless", "loopback" => "loopback", "vlan" => "vlan", "vpn" => "vpn", _ => "unknown" };
                normalized.Append("P|").Append(f[0]).Append("|").Append(kind).Append("||").Append(f[4]).Append("|").Append(f[5]).Append("|").Append(f[6]).Append("|").Append(f[7]).Append("|").Append(f[8]).Append("|").Append(f[9]).Append("|").Append(f[10]).Append("|").Append(f[11]).Append("|").Append(f[12]).Append("|").Append(f[13]).Append("|").Append(f[14]).Append("|||\n");
            }
            IReadOnlyList<RouterPortSnapshot> ports = RouterPortTelemetryParser.Parse(normalized.ToString());
            return new RouterPortTelemetryResult(ports.Count > 0 ? RouterCapabilityState.Supported : RouterCapabilityState.Unknown, ports);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Debug.WriteLine($"Router port telemetry unavailable ({exception.GetType().Name}).");
            return new RouterPortTelemetryResult(RouterCapabilityState.Unknown, Array.Empty<RouterPortSnapshot>());
        }
    }
}
