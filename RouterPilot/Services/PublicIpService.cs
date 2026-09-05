using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Reads the address observed from the router's own Internet route.  The
/// command is executed through the router's existing read-only SSH channel;
/// RouterPilot's Windows egress path is never consulted.
/// </summary>
public sealed class PublicIpService : IPublicIpService, IDisposable
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _sync = new();
    private PublicIpResult _current = PublicIpResult.Initial;
    private string? _lastConfirmedIp;
    private bool _disposed;

    public PublicIpResult Current { get { lock (_sync) return _current; } }

    public event Action<PublicIpResult>? ResultChanged;

    public event Action<string?, string>? PublicIpChanged;

    public async Task<PublicIpResult> RefreshAsync(
        RouterManager router,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
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
                string output = await router.GetRouterPublicIpObservationAsync(linked.Token).ConfigureAwait(false);
                string? parsed = ParseRouterObservedAddress(output);
                if (!IPAddress.TryParse(parsed, out IPAddress? address))
                {
                    return Publish(new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.Unavailable, "The router could not observe a public address."));
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
                return Publish(new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.Unavailable, "The router-side public-IP observation is unavailable."));
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

    internal static string? ParseRouterObservedAddress(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (Match match in Regex.Matches(output, @"(?<![0-9A-Fa-f:.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9A-Fa-f:.])"))
        {
            if (IPAddress.TryParse(match.Value, out IPAddress? candidate) &&
                candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                IsPublicIpv4(candidate))
            {
                return candidate.ToString();
            }
        }

        return null;
    }

    private static bool IsPublicIpv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        int first = bytes[0];
        int second = bytes[1];
        return !IPAddress.IsLoopback(address) &&
            first != 10 &&
            !(first == 172 && second is >= 16 and <= 31) &&
            !(first == 192 && second == 168) &&
            !(first == 100 && second is >= 64 and <= 127) &&
            first != 0 && first != 127 && first < 224;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshGate.Dispose();
    }
}
