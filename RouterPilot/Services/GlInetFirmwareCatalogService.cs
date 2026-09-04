using System.Net.Http;
using System.IO;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class GlInetFirmwareCatalogService
{
    private const string Endpoint = "https://firmware-api.gl-inet.com/cloud-api/model/info";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public GlInetFirmwareCatalogService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<GlInetFirmwareRelease?> GetLatestAsync(string model, string channel = "stable", CancellationToken cancellationToken = default)
    {
        string modelId = NormalizeModel(model);
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        using HttpRequestMessage request = new(HttpMethod.Get, $"{Endpoint}?model={Uri.EscapeDataString(modelId.ToUpperInvariant())}");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("Firmware catalog response was too large.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 12 }, cancellationToken).ConfigureAwait(false);
        return ParseLatest(document.RootElement, channel);
    }

    public async Task<GlInetFirmwareRelease?> GetReleaseAsync(string model, string version, string channel = "stable", CancellationToken cancellationToken = default)
    {
        string modelId = NormalizeModel(model);
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(version)) return null;
        using HttpRequestMessage request = new(HttpMethod.Get, $"{Endpoint}?model={Uri.EscapeDataString(modelId.ToUpperInvariant())}");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 12 }, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Array) return null;
        string wantedStage = channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "RELEASE";
        foreach (JsonElement item in info.EnumerateArray())
            if (item.TryGetProperty("version", out JsonElement v) && string.Equals(v.GetString()?.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("stage", out JsonElement stage) && string.Equals(stage.GetString(), wantedStage, StringComparison.OrdinalIgnoreCase))
                return new GlInetFirmwareRelease(v.GetString()!.Trim(), stage.GetString()!.Trim(),
                    item.TryGetProperty("release_time", out JsonElement date) && DateTimeOffset.TryParse(date.GetString(), out DateTimeOffset parsed) ? parsed : null,
                    ReadDownloadUrl(item), ReadReleaseNotes(item));
        return null;
    }

    internal static GlInetFirmwareRelease? ParseLatest(JsonElement root, string channel = "stable")
    {
        if (!root.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0 ||
            !root.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Array)
            return null;

        string wantedStage = channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "RELEASE";
        List<GlInetFirmwareRelease> releases = info.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                           item.TryGetProperty("version", out JsonElement version) &&
                           version.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(version.GetString()) &&
                           item.TryGetProperty("stage", out JsonElement stage) &&
                           string.Equals(stage.GetString(), wantedStage, StringComparison.OrdinalIgnoreCase))
            .Select(item => new GlInetFirmwareRelease(
                item.GetProperty("version").GetString()!.Trim(),
                item.GetProperty("stage").GetString()!.Trim(),
                item.TryGetProperty("release_time", out JsonElement date) && DateTimeOffset.TryParse(date.GetString(), out DateTimeOffset parsed) ? parsed : null,
                ReadDownloadUrl(item), ReadReleaseNotes(item)))
            .ToList();
        GlInetFirmwareRelease? latest = null;
        foreach (GlInetFirmwareRelease release in releases)
            if (latest is null || (RouterManager.TryCompareFirmwareVersions(latest.Version, release.Version, out int comparison) && comparison < 0))
                latest = release;
        return latest;
    }

    private static string? ReadDownloadUrl(JsonElement item)
    {
        if (!item.TryGetProperty("download", out JsonElement downloads) || downloads.ValueKind != JsonValueKind.Array)
            return null;
        return downloads.EnumerateArray().Select(download => download.TryGetProperty("link", out JsonElement link) ? link.GetString() : null)
            .FirstOrDefault(link => Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? ReadReleaseNotes(JsonElement item)
    {
        foreach (string name in new[] { "release_notes", "release_note", "changelog", "change_log", "description" })
            if (item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return SanitizeReleaseNotes(value.GetString()!);
        return null;
    }

    internal static string SanitizeReleaseNotes(string value)
    {
        string safe = System.Text.RegularExpressions.Regex.Replace(value, "<script\\b[^>]*>.*?</script\\s*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        safe = System.Text.RegularExpressions.Regex.Replace(safe, "<style\\b[^>]*>.*?</style\\s*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        safe = System.Text.RegularExpressions.Regex.Replace(safe, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(safe).Replace("\r\n", "\n").Trim();
    }

    internal static string NormalizeModel(string model)
    {
        string value = model.Trim();
        if (value.Length == 0 || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || value.Equals("-", StringComparison.Ordinal) || value.Equals("Connection Failed", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        int glPrefix = value.IndexOf("GL-", StringComparison.OrdinalIgnoreCase);
        value = glPrefix >= 0 ? value[(glPrefix + 3)..].Trim() : value;
        int separator = value.IndexOfAny([' ', '(', '/']);
        return separator >= 0 ? value[..separator] : value;
    }
}
