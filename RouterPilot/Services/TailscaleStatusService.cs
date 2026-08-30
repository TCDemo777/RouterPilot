using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class TailscaleStatusService : ITailscaleStatusService
{
    private readonly IRouterManagerProvider _routerProvider;
    private readonly IActiveRouterContext _activeRouter;

    public TailscaleStatusService(IRouterManagerProvider routerProvider, IActiveRouterContext activeRouter)
    { _routerProvider = routerProvider; _activeRouter = activeRouter; }

    public async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string profileId = _activeRouter.CurrentProfileId;
        long sessionVersion = _activeRouter.Version;
        bool IsCurrent() => profileId == _activeRouter.CurrentProfileId && sessionVersion == _activeRouter.Version;
        RouterManager router;
        try { router = await _routerProvider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return TailscaleStatus.Unavailable("Router communication is unavailable."); }

        string installed = await router.RunReadOnlySshCommandAsync("command -v tailscale 2>/dev/null || true", cancellationToken).ConfigureAwait(false);
        if (!IsCurrent()) return TailscaleStatus.Unavailable("Router session changed while reading Tailscale status.");
        if (installed.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase)) return TailscaleStatus.Unavailable("Router communication is unavailable.");
        if (string.IsNullOrWhiteSpace(installed)) return new(TailscaleState.NotInstalled, "Tailscale is not installed on this router.", string.Empty, string.Empty, string.Empty, [], []);

        string version = await router.RunReadOnlySshCommandAsync("tailscale version 2>/dev/null || true", cancellationToken).ConfigureAwait(false);
        string json = await router.RunReadOnlySshCommandAsync("tailscale status --json 2>/dev/null || true", cancellationToken).ConfigureAwait(false);
        if (!IsCurrent()) return TailscaleStatus.Unavailable("Router session changed while reading Tailscale status.");
        if (json.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) || json.Contains("invalid option", StringComparison.OrdinalIgnoreCase))
            return new(TailscaleState.Incompatible, "This Tailscale version does not provide compatible status information.", FirstLine(version), string.Empty, string.Empty, [], []);
        if (string.IsNullOrWhiteSpace(json))
        {
            string process = await router.RunReadOnlySshCommandAsync("pidof tailscaled 2>/dev/null || true", cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(process)
                ? new(TailscaleState.Stopped, "The Tailscale service is not running.", FirstLine(version), string.Empty, string.Empty, [], [])
                : new(TailscaleState.NeedsLogin, "Tailscale is available but is not connected to a Tailnet.", FirstLine(version), string.Empty, string.Empty, [], []);
        }
        TailscaleStatus parsed = ParseStatus(json, FirstLine(version));
        if (parsed.State == TailscaleState.Connected && parsed.Addresses.Count == 0)
        {
            string addresses = await router.RunReadOnlySshCommandAsync("tailscale ip 2>/dev/null || true", cancellationToken).ConfigureAwait(false);
            if (!IsCurrent()) return TailscaleStatus.Unavailable("Router session changed while reading Tailscale status.");
            parsed = parsed with { Addresses = addresses.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() };
        }
        return parsed;
    }

    public static TailscaleStatus ParseStatus(string json, string version = "")
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string backend = String(root, "BackendState");
            TailscaleState state = backend switch
            {
                "Running" => TailscaleState.Connected,
                "NeedsLogin" or "NeedsMachineAuth" or "NoState" => TailscaleState.NeedsLogin,
                "Stopped" => TailscaleState.Stopped,
                _ => string.IsNullOrWhiteSpace(backend) ? TailscaleState.Incompatible : TailscaleState.Unavailable
            };
            JsonElement self = root.TryGetProperty("Self", out JsonElement selfValue) ? selfValue : default;
            var addresses = state == TailscaleState.Connected ? Strings(self, "TailscaleIPs") : (IReadOnlyList<string>)[];
            var peers = new List<TailscalePeer>();
            JsonElement peerMap = default;
            bool peerDataAvailable = state == TailscaleState.Connected && root.TryGetProperty("Peer", out peerMap) && peerMap.ValueKind == JsonValueKind.Object;
            if (peerDataAvailable)
                foreach (JsonProperty property in peerMap.EnumerateObject())
                {
                    JsonElement peer = property.Value;
                    peers.Add(new(String(peer, "HostName"), CleanDns(String(peer, "DNSName")), Strings(peer, "TailscaleIPs"),
                        peer.TryGetProperty("Online", out JsonElement online) && online.ValueKind is JsonValueKind.True or JsonValueKind.False ? online.GetBoolean() : null));
                }
            string detail = state switch
            {
                TailscaleState.Connected => "Connected",
                TailscaleState.NeedsLogin => "Tailscale is available but is not connected to a Tailnet.",
                TailscaleState.Stopped => "The Tailscale service is not running.",
                _ => "Tailscale status is currently unavailable."
            };
            return new(state, detail, version, state == TailscaleState.Connected ? String(self, "HostName") : string.Empty, state == TailscaleState.Connected ? CleanDns(String(self, "DNSName")) : string.Empty, addresses, peers) { PeerDataAvailable = peerDataAvailable };
        }
        catch (JsonException) { return new(TailscaleState.Incompatible, "This Tailscale version does not provide compatible status information.", version, string.Empty, string.Empty, [], []); }
    }

    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
    private static IReadOnlyList<string> Strings(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray() : [];
    private static string CleanDns(string value) => value.TrimEnd('.');
    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
}
