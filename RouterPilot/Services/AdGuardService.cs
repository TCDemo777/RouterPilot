using System;
using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class AdGuardService : IDisposable
{
    private readonly HttpClient _client;
    private readonly RouterEndpointProvider _endpoints;
    private bool _disposed;

    public AdGuardService()
        : this(CreateEndpointsFromSavedSettings())
    {
    }

    public AdGuardService(RouterEndpointProvider endpoints)
    {
        _endpoints = endpoints ??
            throw new ArgumentNullException(nameof(endpoints));

        _client = new HttpClient
        {
            BaseAddress = endpoints.AdGuardBaseUri,
            Timeout = TimeSpan.FromSeconds(
                endpoints.Options.RequestTimeoutSeconds)
        };
    }

    public async Task<bool> IsAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response =
                await _client.GetAsync(
                    _endpoints.AdGuardBaseUri,
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

    // Compatibility overload for older callers.
    public async Task<bool> IsAvailableAsync(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            return false;

        try
        {
            using HttpResponseMessage response =
                await _client.GetAsync(uri).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<string?> GetStatusAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        string credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{username}:{password}"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            _endpoints.AdGuardControl("status"));

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);

        try
        {
            using HttpResponseMessage response =
                await _client.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? body
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException)
        {
            Debug.WriteLine($"AdGuard request failed ({DiagnosticRedactor.FailureCategory(ex)}).");
            return "Request failed.";
        }
    }

    private static RouterEndpointProvider CreateEndpointsFromSavedSettings()
    {
        var settingsService = new SettingsService();
        AppSettings settings = settingsService.Load();

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "A router address has not been configured.");
        }

        return new RouterEndpointProvider(
            settingsService.CreateConnectionOptions(settings));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }
}
