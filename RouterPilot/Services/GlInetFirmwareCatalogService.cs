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
                ReadDownloadUrl(item)))
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
