using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RouterPilot.Services;

public sealed class AdGuardApiClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;
    private readonly RouterEndpointProvider _endpoints;
    private readonly object _cookieLock = new();
    private bool _disposed;

    public AdGuardApiClient(RouterEndpointProvider endpoints)
    {
        _endpoints = endpoints ??
            throw new ArgumentNullException(nameof(endpoints));

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = AdGuardHttpClientSecurityPolicy.AllowAutoRedirect,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate
        };

        _client = new HttpClient(handler)
        {
            BaseAddress = endpoints.AdGuardBaseUri,
            Timeout = TimeSpan.FromSeconds(
                endpoints.Options.RequestTimeoutSeconds)
        };

        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void SetAdminToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        lock (_cookieLock)
        {
            _cookies.SetCookies(
                _endpoints.AdGuardBaseUri,
                $"Admin-Token={token}; Path=/");
        }
    }

    public async Task<AdGuardHttpResult> SendControlAsync(
        HttpMethod method,
        string endpoint,
        string? json,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var request = new HttpRequestMessage(
            method,
            _endpoints.AdGuardControl(endpoint));

        if (json is not null)
        {
            // Some GL.iNet builds require exactly application/json.
            request.Content = new ByteArrayContent(
                Encoding.UTF8.GetBytes(json));
            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type",
                "application/json");
        }

        using HttpResponseMessage response =
            await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        string content =
            await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

        return new AdGuardHttpResult(
            response.StatusCode,
            content);
    }

    public async Task<bool> IsAvailableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                _endpoints.AdGuardBaseUri);

            using HttpResponseMessage response =
                await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }
}

public sealed record AdGuardHttpResult(
    HttpStatusCode StatusCode,
    string Content)
{
    public bool IsSuccess =>
        (int)StatusCode is >= 200 and <= 299;

    public bool RequiresNewToken =>
        StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden;
}
