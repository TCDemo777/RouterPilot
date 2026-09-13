using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    public async Task<SqmConfiguration> GetNativeSqmConfigurationAsync(CancellationToken cancellationToken = default)
    {
        string sid = await _sessionService.GetAdminTokenAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "sqm", "get_config", new { }, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error))
        {
            int code = error.TryGetProperty("code", out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : 0;
            // JSON-RPC method-not-found is the only response that proves this
            // router lacks the native SQM API. Other RPC errors are transient
            // availability failures and must not silently fall back to UCI.
            throw new SqmNativeCapabilityException(code, code == -32601);
        }
        if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("enable", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !result.TryGetProperty("download", out JsonElement download) || !download.TryGetInt32(out int down) ||
            !result.TryGetProperty("upload", out JsonElement upload) || !upload.TryGetInt32(out int up) ||
            !result.TryGetProperty("qdisc", out JsonElement qdisc))
            throw new SqmNativeCapabilityException(0, false);

        SqmConfiguration configuration = new(enabled.ValueKind == JsonValueKind.True, down, up, qdisc.GetString() ?? string.Empty);
        if (!configuration.IsValid)
            throw new SqmNativeCapabilityException(0, false);
        return configuration;
    }

    public async Task SetNativeSqmConfigurationAsync(SqmConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsValid) throw new ArgumentException("Invalid native SQM configuration.", nameof(configuration));
        string sid = await _sessionService.GetAdminTokenAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "sqm", "set_config", new
        {
            enable = configuration.Enable,
            download = configuration.DownloadMbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            upload = configuration.UploadMbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            qdisc = configuration.Qdisc
        }, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("The router did not apply SQM settings.");
    }
}

public sealed class SqmNativeCapabilityException : Exception
{
    public SqmNativeCapabilityException(int code, bool capabilityAbsent)
        : base(capabilityAbsent ? $"Native SQM capability is unavailable ({code})." : $"Native SQM response is unavailable or malformed ({code}).")
    {
        CapabilityAbsent = capabilityAbsent;
    }

    public bool CapabilityAbsent { get; }
}
