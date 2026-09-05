using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RouterPilot.Services;

public partial class RouterManager
{
    /// <summary>
    /// Developer-harness access to the GL.iNet Tailscale configuration RPC.
    /// Production UI does not call this method.
    /// </summary>
    internal async Task<JsonDocument> GetTailscaleConfigAsync(CancellationToken cancellationToken)
    {
        string sessionId = await _sessionService.GetAdminTokenAsync(cancellationToken).ConfigureAwait(false);
        return await _sessionService.CallAsync(
            sessionId,
            "tailscale",
            "get_config",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the complete settings object discovered from get_config directly
    /// as the set_config RPC parameters. The harness refuses to call this
    /// unless lan_enabled is present and the request shape is established.
    /// </summary>
    internal async Task<JsonDocument> SetTailscaleConfigAsync(
        JsonElement settings,
        CancellationToken cancellationToken)
    {
        if (settings.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tailscale settings must be a JSON object.", nameof(settings));

        string sessionId = await _sessionService.GetAdminTokenAsync(cancellationToken).ConfigureAwait(false);
        return await _sessionService.CallAsync(
            sessionId,
            "tailscale",
            "set_config",
            settings,
            cancellationToken).ConfigureAwait(false);
    }
}
