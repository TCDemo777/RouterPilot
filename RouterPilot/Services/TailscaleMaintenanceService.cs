using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Combines existing router-authoritative Tailscale status with official upstream stable releases.</summary>
public sealed class TailscaleMaintenanceService
{
    private static readonly Uri ReleasesUri = new("https://api.github.com/repos/tailscale/tailscale/releases?per_page=20");
    private readonly ITailscaleStatusService _tailscaleStatusService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private long _generation;

    public TailscaleMaintenanceService(ITailscaleStatusService tailscaleStatusService)
    {
        _tailscaleStatusService = tailscaleStatusService;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RouterPilot/2.3");
    }

    public TailscaleMaintenanceSnapshot Current { get; private set; } = TailscaleMaintenanceSnapshot.Empty;
    public event EventHandler? Changed;

    public void ResetForRouterSession()
    {
        Interlocked.Increment(ref _generation);
        Current = TailscaleMaintenanceSnapshot.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        long generation = Interlocked.Read(ref _generation);
        try
        {
            Publish(Current with { UpdateStatus = TailscaleUpdateStatus.Checking, ErrorCategory = null }, generation);
            TailscaleStatus status;
            try { status = await _tailscaleStatusService.GetStatusAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { status = TailscaleStatus.Unavailable("Router communication is unavailable."); }

            string? installed = NormalizeVersion(status.Version);
            string? latest;
            try { latest = await GetLatestStableAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                Publish(new TailscaleMaintenanceSnapshot(installed, null, status.State, TailscaleUpdateStatus.Unavailable,
                    "official-release-unavailable", DateTimeOffset.UtcNow), generation);
                return;
            }

            TailscaleUpdateStatus updateStatus = Compare(installed, latest, out int comparison)
                ? comparison < 0 ? TailscaleUpdateStatus.UpdateAvailable : TailscaleUpdateStatus.UpToDate
                : TailscaleUpdateStatus.Unavailable;
            Publish(new TailscaleMaintenanceSnapshot(installed, latest, status.State, updateStatus,
                updateStatus == TailscaleUpdateStatus.Unavailable ? "version-unavailable" : null, DateTimeOffset.UtcNow), generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(Current with { UpdateStatus = TailscaleUpdateStatus.Unavailable, ErrorCategory = "cancelled", CheckedAt = DateTimeOffset.UtcNow }, generation);
        }
        finally { _gate.Release(); }
    }

    public static bool Compare(string? installed, string? latest, out int comparison)
    {
        comparison = 0;
        if (!TryParseVersion(installed, out Version? current) || !TryParseVersion(latest, out Version? available)) return false;
        comparison = current!.CompareTo(available);
        return true;
    }

    public static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        Match match = Regex.Match(value.Trim(), @"^v?(\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out version) && version is not null;
    }

    public static string? SelectLatestStableVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement release in root.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean() ||
                release.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean()) continue;
            if (release.TryGetProperty("tag_name", out JsonElement tag) && tag.ValueKind == JsonValueKind.String && TryParseVersion(tag.GetString(), out _))
                return NormalizeVersion(tag.GetString());
        }
        return null;
    }

    private async Task<string?> GetLatestStableAsync(CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, ReleasesUri);
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return SelectLatestStableVersion(document.RootElement);
    }

    private static string? NormalizeVersion(string? raw) => TryParseVersion(raw, out Version? version) ? "v" + version : null;
    private void Publish(TailscaleMaintenanceSnapshot value, long generation)
    {
        if (generation != Interlocked.Read(ref _generation)) return;
        Current = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
