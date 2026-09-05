using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class TailscaleConfigurationService : ITailscaleConfigurationService
{
    private readonly IRouterManagerProvider _provider;
    private readonly IActiveRouterContext _active;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public TailscaleConfigurationService(IRouterManagerProvider provider, IActiveRouterContext active)
    { _provider = provider; _active = active; }

    public async Task<TailscaleConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        string profile = _active.CurrentProfileId; long version = _active.Version;
        try
        {
            RouterManager router = await _provider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await router.GetTailscaleConfigAsync(cancellationToken).ConfigureAwait(false);
            if (profile != _active.CurrentProfileId || version != _active.Version) return TailscaleConfigurationSnapshot.Unknown;
            return Parse(document.RootElement);
        }
        catch (OperationCanceledException) { throw; }
        catch { return TailscaleConfigurationSnapshot.Unknown; }
    }

    public async Task<TailscaleMutationResult> SetAccessAsync(TailscaleAccessField field, bool value, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string profile = _active.CurrentProfileId; long version = _active.Version;
            RouterManager router = await _provider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument currentDocument = await router.GetTailscaleConfigAsync(cancellationToken).ConfigureAwait(false);
            JsonObject current = ExtractSettings(currentDocument.RootElement);
            string property = field == TailscaleAccessField.Lan ? "lan_enabled" : "wan_enabled";
            if (!TryBoolean(current[property], out _)) return new(false, Snapshot(current), "This Tailscale setting is currently unknown.");
            if (profile != _active.CurrentProfileId || version != _active.Version) return new(false, TailscaleConfigurationSnapshot.Unknown, "The active router changed before the setting could be applied.");
            JsonObject requested = (JsonObject)current.DeepClone(); requested[property] = value;
            using JsonDocument setDocument = await router.SetTailscaleConfigAsync(JsonSerializer.SerializeToElement(requested), cancellationToken).ConfigureAwait(false);
            if (profile != _active.CurrentProfileId || version != _active.Version) return new(false, TailscaleConfigurationSnapshot.Unknown, "The active router changed while applying the setting.");
            using JsonDocument verifiedDocument = await router.GetTailscaleConfigAsync(cancellationToken).ConfigureAwait(false);
            JsonObject verified = ExtractSettings(verifiedDocument.RootElement);
            bool verifiedValue = TryBoolean(verified[property], out bool parsed) && parsed == value;
            return new(verifiedValue, Snapshot(verified), verifiedValue ? string.Empty : "The router did not confirm the requested Tailscale setting.");
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, TailscaleConfigurationSnapshot.Unknown, "RouterPilot couldn't apply the Tailscale setting. The current router state has been refreshed."); }
        finally { _mutationGate.Release(); }
    }

    private static JsonObject ExtractSettings(JsonElement root)
    {
        if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Tailscale configuration request failed.");
        if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Tailscale configuration is unavailable.");
        return JsonNode.Parse(result.GetRawText())!.AsObject();
    }
    private static TailscaleConfigurationSnapshot Parse(JsonElement root)
    { return Snapshot((JsonNode.Parse(root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object ? result.GetRawText() : "{}") ?? new JsonObject()).AsObject()); }
    private static TailscaleConfigurationSnapshot Snapshot(JsonObject settings) => new(ReadBool(settings["lan_enabled"]), ReadBool(settings["wan_enabled"]), Capability(settings["lan_enabled"]), Capability(settings["wan_enabled"]));
    private static TailscaleCapabilityState Capability(JsonNode? node) => TryBoolean(node, out _) ? TailscaleCapabilityState.Supported : TailscaleCapabilityState.Unknown;
    private static bool? ReadBool(JsonNode? node) => TryBoolean(node, out bool value) ? value : null;
    private static bool TryBoolean(JsonNode? node, out bool value)
    { value = false; if (node is not JsonValue json) return false; if (json.TryGetValue<bool>(out value)) return true; if (json.TryGetValue<int>(out int number) && number is 0 or 1) { value = number == 1; return true; } if (json.TryGetValue<string>(out string? text) && (text == "0" || text == "1")) { value = text == "1"; return true; } return false; }
}
