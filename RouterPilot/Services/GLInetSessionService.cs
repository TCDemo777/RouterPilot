using CryptSharp;
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RouterPilot.Services
{
    public sealed partial class GLInetSessionService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _rpcUrl;
        private readonly string _username;
        private readonly string _password;
        private readonly string _routerHost;
        private readonly IRouterCertificateTrustService _certificateTrustService;
        private bool _disposed;
        private string? _currentSessionId;
        private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public GLInetSessionService(
        string routerIp,
        string username,
        string password,
        IRouterCertificateTrustService certificateTrustService)
        {
            if (string.IsNullOrWhiteSpace(routerIp))
            {
                throw new ArgumentException(
                    "Router IP cannot be empty.",
                    nameof(routerIp));
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException(
                    "Username cannot be empty.",
                    nameof(username));
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException(
                    "Password cannot be empty.",
                    nameof(password));
            }

            _username = username;
            _password = password;
            _routerHost = routerIp.Trim();
            _certificateTrustService = certificateTrustService ??
                throw new ArgumentNullException(
                    nameof(certificateTrustService));

            string normalisedRouterIp = routerIp
                .Trim()
                .TrimEnd('/');

            if (!normalisedRouterIp.StartsWith(
                    "http://",
                    StringComparison.OrdinalIgnoreCase) &&
                !normalisedRouterIp.StartsWith(
                    "https://",
                    StringComparison.OrdinalIgnoreCase))
            {
                normalisedRouterIp = "https://" + normalisedRouterIp;
            }

            _rpcUrl = normalisedRouterIp + "/rpc";
            System.Diagnostics.Debug.WriteLine($"RPC URL = {_rpcUrl}");

            HttpClientHandler handler = new()
            {
                UseCookies = false,
                AllowAutoRedirect = false,

                ServerCertificateCustomValidationCallback =
                    ValidateRouterCertificate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        private bool ValidateRouterCertificate(
            HttpRequestMessage _,
            X509Certificate2? certificate,
            X509Chain? __,
            System.Net.Security.SslPolicyErrors sslPolicyErrors)
        {
            if (certificate is null ||
                sslPolicyErrors.HasFlag(
                    System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                return false;
            }

            RouterCertificateTrustDecision decision =
                _certificateTrustService.Evaluate(
                    _routerHost,
                    certificate,
                    sslPolicyErrors);

            return decision is RouterCertificateTrustDecision.Trusted or
                RouterCertificateTrustDecision.TrustedAfterFirstUse;
        }

        public async Task<string> GetAdminTokenAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!string.IsNullOrWhiteSpace(_currentSessionId))
            {
                return _currentSessionId;
            }

            await _sessionGate.WaitAsync(cancellationToken);
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentSessionId))
                {
                    return _currentSessionId;
                }

                ChallengeResult challenge =
                    await GetChallengeAsync(cancellationToken);

                string cryptPassword =
                    GenerateCryptPassword(
                        _password,
                        challenge.Algorithm,
                        challenge.Salt);

                string loginText =
    $"{_username}:{cryptPassword}:{challenge.Nonce}";

                string loginHash =
                    Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(loginText)))
                        .ToLowerInvariant();

                string sessionId = await LoginAsync(
                    loginHash,
                    cancellationToken);
                _currentSessionId = sessionId;
                return sessionId;
            }
            finally
            {
                _sessionGate.Release();
            }
        }

        internal string GetCurrentSessionId() => _currentSessionId ?? throw new InvalidOperationException("No authenticated GL.iNet session is available.");

        internal void InvalidateSession()
        {
            ThrowIfDisposed();
            _currentSessionId = null;
        }

        /// <summary>Calls a documented GL.iNet SDK4 ubus RPC method using an authenticated session SID.</summary>
        public Task<JsonDocument> CallAsync(
            string sessionId,
            string service,
            string method,
            CancellationToken cancellationToken = default)
        {
            return CallAsync(
                sessionId,
                service,
                method,
                new { },
                cancellationToken);
        }

        internal Task<JsonDocument> CallAsync(
            string sessionId,
            string service,
            string method,
            object parameters,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(service);
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            ArgumentNullException.ThrowIfNull(parameters);

            return PostRpcAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "call",
                    @params = new object[] { sessionId, service, method, parameters }
                },
                cancellationToken);
        }

        internal Task<JsonDocument> CallPortForwardAsync(string sessionId, PortForwardRpcOperation operation, object parameters, CancellationToken cancellationToken = default)
        {
            string method = operation switch
            {
                PortForwardRpcOperation.Add => "add_port_forward",
                PortForwardRpcOperation.Update => "set_port_forward",
                PortForwardRpcOperation.Delete => "remove_port_forward",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            return PostRpcAsync(new { jsonrpc = "2.0", id = 4, method = "call", @params = new object[] { sessionId, "firewall", method, parameters } }, cancellationToken);
        }
#if DEBUG
        internal Task<JsonDocument> CallPortForwardVerifierAsync(string sessionId, string operation, object parameters)
        {
            string method = operation switch
            {
                "add" => "add_port_forward",
                "set" => "set_port_forward",
                "remove" => "remove_port_forward",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            return PostRpcAsync(new { jsonrpc = "2.0", id = 4, method = "call", @params = new object[] { sessionId, "firewall", method, parameters } }, CancellationToken.None);
        }
#endif

        private async Task<ChallengeResult> GetChallengeAsync(
            CancellationToken cancellationToken)
        {
            object request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "challenge",
                @params = new
                {
                    username = _username
                }
            };

            using JsonDocument document =
                await PostRpcAsync(
                    request,
                    cancellationToken);

            JsonElement root =
                document.RootElement;

            ThrowIfRpcError(root);

            if (!root.TryGetProperty(
                    "result",
                    out JsonElement result))
            {
                throw new InvalidOperationException(
                    "The router challenge response did not contain a result.");
            }

            int algorithm =
                result.GetProperty("alg").GetInt32();

            string salt =
                result.GetProperty("salt").GetString()
                ?? throw new InvalidOperationException(
                    "The router challenge did not return a salt.");

            string nonce =
                result.GetProperty("nonce").GetString()
                ?? throw new InvalidOperationException(
                    "The router challenge did not return a nonce.");

            return new ChallengeResult(
                algorithm,
                salt,
                nonce);
        }

        private async Task<string> LoginAsync(
            string loginHash,
            CancellationToken cancellationToken)
        {
            object request = new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "login",
                @params = new
                {
                    username = _username,
                    hash = loginHash
                }
            };

            using JsonDocument document =
                await PostRpcAsync(
                    request,
                    cancellationToken);

            JsonElement root =
                document.RootElement;

            ThrowIfRpcError(root);

            if (!root.TryGetProperty(
                    "result",
                    out JsonElement result))
            {
                throw new InvalidOperationException(
                    "The router login response did not contain a result.");
            }

            string? sid =
                result.TryGetProperty(
                    "sid",
                    out JsonElement sidElement)
                ? sidElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException(
                    "The router accepted the login request but did not return a SID.");
            }

            return sid;
        }

        private async Task<JsonDocument> PostRpcAsync(
            object request,
            CancellationToken cancellationToken)
        {
            string json =
                JsonSerializer.Serialize(request);

            using StringContent content = new(
                json,
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await _httpClient.PostAsync(
                    _rpcUrl,
                    content,
                    cancellationToken);

            string responseText =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Router RPC returned HTTP {(int)response.StatusCode} " +
                    $"{response.StatusCode}.");
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                throw new InvalidOperationException(
                    "The router returned an empty RPC response.");
            }

            try
            {
                return JsonDocument.Parse(responseText);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The router returned invalid JSON.",
                    exception);
            }
        }

        private static string GenerateCryptPassword(
    string password,
    int algorithm,
    string salt)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException(
                    "Password cannot be empty.",
                    nameof(password));
            }

            if (string.IsNullOrWhiteSpace(salt))
            {
                throw new InvalidOperationException(
                    "The router challenge returned an empty salt.");
            }

            string cleanSalt = salt
                .Trim()
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim('$');

            return algorithm switch
            {
                5 => Crypter.Sha256.Crypt(
                    password,
                    $"$5${cleanSalt}"),

                6 => Crypter.Sha512.Crypt(
                    password,
                    $"$6${cleanSalt}"),

                _ => throw new NotSupportedException(
                    $"GL.iNet password algorithm {algorithm} is not supported.")
            };
        }

        private static void ThrowIfRpcError(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                    "error",
                    out JsonElement error))
            {
                return;
            }

            int? code =
                error.TryGetProperty(
                    "code",
                    out JsonElement codeElement)
                ? codeElement.GetInt32()
                : null;

            string message =
                error.TryGetProperty(
                    "message",
                    out JsonElement messageElement)
                ? messageElement.GetString()
                    ?? "Unknown RPC error"
                : "Unknown RPC error";

            throw new InvalidOperationException(
                $"GL.iNet RPC error " +
                $"{code?.ToString() ?? "unknown"}.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _currentSessionId = null;
            DisposeVpnStatusSocket();
            _httpClient.Dispose();
            _sessionGate.Dispose();
        }

        private sealed record ChallengeResult(
            int Algorithm,
            string Salt,
            string Nonce);
    }

}
