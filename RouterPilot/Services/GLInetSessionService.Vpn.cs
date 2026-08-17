using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RouterPilot.Services;

public sealed partial class GLInetSessionService
{
    internal Task<JsonDocument> CallVpnAsync(string sessionId, VpnRpcOperation operation, int? tunnelId = null, bool? enabled = null, CancellationToken cancellationToken = default)
    {
        string method;
        object payload;
        switch (operation)
        {
            case VpnRpcOperation.GetTunnels:
                method = "get_tunnel";
                payload = new { };
                break;
            case VpnRpcOperation.GetProfiles:
                method = "get_all_config_list";
                payload = new { };
                break;
            case VpnRpcOperation.SetTunnelEnabled when tunnelId is > 0 && enabled.HasValue:
                method = "set_tunnel";
                payload = new { tunnel_id = tunnelId.Value, enabled = enabled.Value };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
        return PostRpcAsync(new { jsonrpc = "2.0", id = 7, method = "call", @params = new object[] { sessionId, "vpn-client", method, payload } }, cancellationToken);
    }
}

internal enum VpnRpcOperation
{
    GetTunnels,
    GetProfiles,
    SetTunnelEnabled
}
