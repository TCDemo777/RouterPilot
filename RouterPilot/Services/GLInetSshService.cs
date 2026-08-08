using System;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RouterPilot.Services
{
    public sealed class GLInetSshService : IDisposable
    {
        private readonly string _ip;
        private readonly string _username;
        private readonly string _password;
        private readonly ISshHostKeyTrustService _hostKeyTrustService;
        private readonly SemaphoreSlim _commandGate = new(1, 1);
        private SshClient? _client;
        private bool _disposed;

        public GLInetSshService(
            string ip,
            string username,
            string password,
            ISshHostKeyTrustService hostKeyTrustService)
        {
            _ip = ip;
            _username = username;
            _password = password;
            _hostKeyTrustService = hostKeyTrustService;
        }

        public Task<string> RunCommandAsync(string command)
        {
            return RunCommandAsync(command, CancellationToken.None);
        }

        public async Task<string> RunCommandAsync(
            string command,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);

            // BusyBox ash treats CR as part of shell tokens.  Normalize raw
            // multiline C# commands before sending them to OpenWrt.
            command = command.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            await _commandGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return await Task.Run(
                        () => ExecuteCommand(command),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private string ExecuteCommand(string command)
        {
            try
            {
                EnsureConnected();

                using SshCommand result =
                    _client!.CreateCommand(command);

                result.CommandTimeout =
                    TimeSpan.FromSeconds(20);

                string output = result.Execute();

                return output +
                    Environment.NewLine +
                    result.Error;
            }
            catch (SshAuthenticationException)
            {
                ResetClient();
                return "SSH_AUTH_FAILED";
            }
            catch (SshConnectionException)
            {
                ResetClient();
                return "SSH_CONNECTION_FAILED";
            }
            catch (System.Net.Sockets.SocketException)
            {
                ResetClient();
                return "SSH_NETWORK_FAILED";
            }
            catch (Exception ex)
            {
                ResetClient();
                return "SSH_ERROR: " + ex.GetType().Name;
            }
        }

        private void EnsureConnected()
        {
            if (_client is { IsConnected: true })
            {
                return;
            }

            ResetClient();

            _client = new SshClient(
                _ip,
                _username,
                _password)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            };

            _client.ConnectionInfo.Timeout =
                TimeSpan.FromSeconds(5);

            _client.HostKeyReceived += OnHostKeyReceived;

            _client.Connect();

            if (!_client.IsConnected)
            {
                throw new SshConnectionException(
                    "SSH connection failed.");
            }
        }

        private void OnHostKeyReceived(
            object? sender,
            HostKeyEventArgs eventArgs)
        {
            SshHostKeyTrustDecision decision =
                _hostKeyTrustService.Evaluate(
                    _ip,
                    eventArgs.FingerPrintSHA256);

            eventArgs.CanTrust = decision is
                SshHostKeyTrustDecision.Trusted or
                SshHostKeyTrustDecision.TrustedAfterFirstUse;
        }

        private void ResetClient()
        {
            if (_client is null)
            {
                return;
            }

            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch
            {
                // The connection is already unusable. Disposal below is enough.
            }

            _client.Dispose();
            _client = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _commandGate.Wait();
            try
            {
                ResetClient();
            }
            finally
            {
                _commandGate.Release();
                _commandGate.Dispose();
            }
        }
    }
}
