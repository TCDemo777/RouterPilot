using System.Net.Http;
using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Combines the router-authoritative installed version with the official AdGuard Home stable-release feed.</summary>
public sealed class AdGuardHomeMaintenanceService
{
    private static readonly Uri ReleasesUri = new("https://api.github.com/repos/AdguardTeam/AdGuardHome/releases?per_page=20");
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private long _generation;

    public AdGuardHomeMaintenanceService(IRouterManagerProvider routerManagerProvider)
    {
        _routerManagerProvider = routerManagerProvider;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RouterPilot/2.4");
    }

    public AdGuardHomeMaintenanceSnapshot Current { get; private set; } = AdGuardHomeMaintenanceSnapshot.Empty;
    public event EventHandler? Changed;

    public void ResetForRouterSession()
    {
        Interlocked.Increment(ref _generation);
        Current = AdGuardHomeMaintenanceSnapshot.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        long generation = Interlocked.Read(ref _generation);
        try
        {
            Publish(Current with { UpdateStatus = AdGuardHomeUpdateStatus.Checking, ErrorCategory = null }, generation);
            string? installed = null;
            bool? running = null;
            try
            {
                RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
                AdGuardStatus status = await router.GetAdGuardStatusAsync(cancellationToken).ConfigureAwait(false);
                installed = NormalizeVersion(status.Version);
                running = status.IsRunning;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* The official check can still complete; expose unavailable installed version. */ }

            string? latest;
            try { latest = await GetLatestStableAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                Publish(new AdGuardHomeMaintenanceSnapshot(installed, null, running, AdGuardHomeUpdateStatus.Unavailable, "official-release-unavailable", DateTimeOffset.UtcNow), generation);
                return;
            }

            AdGuardHomeUpdateStatus state = Compare(installed, latest, out int comparison)
                ? comparison < 0 ? AdGuardHomeUpdateStatus.UpdateAvailable : AdGuardHomeUpdateStatus.UpToDate
                : AdGuardHomeUpdateStatus.Unavailable;
            Publish(new AdGuardHomeMaintenanceSnapshot(installed, latest, running, state,
                state == AdGuardHomeUpdateStatus.Unavailable ? "version-unavailable" : null, DateTimeOffset.UtcNow), generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(Current with { UpdateStatus = AdGuardHomeUpdateStatus.Unavailable, ErrorCategory = "cancelled", CheckedAt = DateTimeOffset.UtcNow }, generation);
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

    /// <summary>Allows a mutation only after an authoritative comparison found a newer official release.</summary>
    public static bool CanLaunchUpdater(AdGuardHomeUpdateStatus updateStatus, bool routerConnected, bool operationRunning) =>
        updateStatus == AdGuardHomeUpdateStatus.UpdateAvailable && routerConnected && !operationRunning;

    public static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        Match match = Regex.Match(value, @"(?<![\d])v?(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out version) && version is not null;
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

    private static string? NormalizeVersion(string? raw) => TryParseVersion(raw, out Version? version) ? "v" + version : null;
    private void Publish(AdGuardHomeMaintenanceSnapshot value, long generation)
    {
        if (generation != Interlocked.Read(ref _generation)) return;
        Current = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
