using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Implements AdGuard Home's documented native browser-session login:
/// POST /control/login with { name, password }, followed by the session cookie
/// issued by AdGuard Home.  It deliberately never logs either credential.
/// </summary>
internal static class AdGuardNativeSessionAuthenticator
{
    internal static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new AdGuardAuthenticationException();

        string json = JsonSerializer.Serialize(new { name = username, password });
        using var request = new HttpRequestMessage(HttpMethod.Post, "control/login")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AdGuardAuthenticationException();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("AdGuard Home login was unavailable.", null, response.StatusCode);
    }

    internal static async Task<AdGuardConnectionTestResult> TestConnectionAsync(
        Uri baseUri,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = AdGuardHttpClientSecurityPolicy.AllowAutoRedirect,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            await LoginAsync(client, username, password, cancellationToken).ConfigureAwait(false);
            using HttpResponseMessage response = await client.GetAsync(
                "control/status", cancellationToken).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? AdGuardConnectionTestResult.AuthenticationFailed
                : response.IsSuccessStatusCode
                    ? AdGuardConnectionTestResult.Connected
                    : AdGuardConnectionTestResult.Unavailable;
        }
        catch (AdGuardAuthenticationException)
        {
            return AdGuardConnectionTestResult.AuthenticationFailed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AdGuardConnectionTestResult.Unavailable;
        }
        catch (HttpRequestException)
        {
            return AdGuardConnectionTestResult.Unavailable;
        }
    }
}

internal class AdGuardAuthenticationException : Exception
{
}

internal sealed class AdGuardDedicatedCredentialsTransportException : AdGuardAuthenticationException
{
}
