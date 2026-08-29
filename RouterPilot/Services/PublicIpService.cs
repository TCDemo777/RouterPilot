using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Reads only the address observed for RouterPilot's current Internet route.
/// It intentionally does not use WAN, VPN virtual-address, or VPN endpoint data.
/// </summary>
public sealed class PublicIpService : IPublicIpService, IDisposable
{
    private static readonly Uri ProviderUri = new("https://api64.ipify.org");
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _sync = new();
    private PublicIpResult _current = PublicIpResult.Initial;
    private string? _lastConfirmedIp;
    private bool _disposed;

    public PublicIpService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RouterPilot/1.0");
    }

    public PublicIpResult Current { get { lock (_sync) return _current; } }

    public event Action<PublicIpResult>? ResultChanged;

    public event Action<string?, string>? PublicIpChanged;

    public async Task<PublicIpResult> RefreshAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PublicIpResult snapshot = Current;
        if (!forceRefresh && snapshot.Status == PublicIpStatus.Available &&
            DateTimeOffset.UtcNow - snapshot.CheckedAt < FreshFor)
        {
            return snapshot;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = Current;
            if (!forceRefresh && snapshot.Status == PublicIpStatus.Available &&
                DateTimeOffset.UtcNow - snapshot.CheckedAt < FreshFor)
            {
                return snapshot;
            }

            Publish(new PublicIpResult(snapshot.PublicIp, snapshot.CheckedAt, PublicIpStatus.Loading, null));
            using var timeout = new CancellationTokenSource(LookupTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(ProviderUri, linked.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string text = (await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false)).Trim();
                if (!IPAddress.TryParse(text, out IPAddress? address))
                {
                    return Publish(new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.Unavailable, "The public-IP service returned an invalid address."));
                }

                return Publish(new PublicIpResult(address.ToString(), DateTimeOffset.UtcNow, PublicIpStatus.Available, null));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return Publish(new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.TimedOut, "The public-IP lookup timed out."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Publish(new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.Unavailable, "The public-IP service is unavailable."));
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private PublicIpResult Publish(PublicIpResult result)
    {
        string? previousIp;
        string? currentIp = result.Status == PublicIpStatus.Available
            ? NormalizeConfirmedIp(result.PublicIp)
            : null;

        if (result.Status == PublicIpStatus.Available && currentIp is null)
        {
            result = result with
            {
                PublicIp = null,
                Status = PublicIpStatus.Unavailable,
                FailureReason = "The public-IP service returned an invalid address."
            };
        }
        else if (currentIp is not null)
        {
            result = result with { PublicIp = currentIp };
        }

        lock (_sync)
        {
            previousIp = _lastConfirmedIp;
            _current = result;
            if (currentIp is not null)
            {
                _lastConfirmedIp = currentIp;
            }
        }
        ResultChanged?.Invoke(result);
        // The first confirmed address establishes the session baseline. Only
        // a later confirmed, normalized address may be a user-visible change.
        if (previousIp is not null && currentIp is not null &&
            !string.Equals(previousIp, currentIp, StringComparison.Ordinal))
        {
            PublicIpChanged?.Invoke(previousIp, currentIp);
        }
        return result;
    }

    private static string? NormalizeConfirmedIp(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return IPAddress.TryParse(candidate, out IPAddress? address) ? address.ToString() : null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _refreshGate.Dispose();
    }
}
