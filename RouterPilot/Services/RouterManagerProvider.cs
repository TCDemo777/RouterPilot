using System;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Configuration;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class RouterManagerProvider : IRouterManagerProvider
{
    private sealed record ConnectionSignature(
        string Host,
        string Username,
        string EncryptedPassword,
        int SshPort,
        SshAuthenticationMethod SshAuthenticationMethod,
        string PrivateKeyPath,
        string EncryptedPrivateKeyPassphrase,
        bool UseRouterHttps,
        int RouterPort,
        bool UseAdGuardHttps,
        int AdGuardPort);

    private readonly SettingsService _settingsService;
    private readonly ISshHostKeyTrustService _hostKeyTrustService;
    private readonly IRouterCertificateTrustService _certificateTrustService;
    private readonly AdGuardTransportSecurityService _adGuardTransportSecurity;
    private readonly ISshConnectionFactory _sshConnectionFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _disposeLock = new();
    private RouterManager? _manager;
    private ConnectionSignature? _signature;
    private long _invalidationVersion;
    private long _managerInvalidationVersion = -1;
    private Task? _disposeTask;
    private volatile bool _disposed;

    public RouterManagerProvider(
        SettingsService settingsService,
        ISshHostKeyTrustService hostKeyTrustService,
        IRouterCertificateTrustService certificateTrustService,
        AdGuardTransportSecurityService adGuardTransportSecurity,
        ISshConnectionFactory sshConnectionFactory)
    {
        _settingsService = settingsService;
        _hostKeyTrustService = hostKeyTrustService;
        _certificateTrustService = certificateTrustService;
        _adGuardTransportSecurity = adGuardTransportSecurity;
        _sshConnectionFactory = sshConnectionFactory;
    }

    public async Task<RouterManager> GetRouterManagerAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            AppSettings settings = _settingsService.Load();
            var signature = new ConnectionSignature(
                settings.RouterHost.Trim(),
                settings.Username.Trim(),
                settings.EncryptedPassword,
                settings.SshPort,
                settings.SshAuthenticationMethod,
                settings.PrivateKeyPath,
                settings.EncryptedPrivateKeyPassphrase,
                settings.UseRouterHttps,
                settings.RouterPort,
                settings.UseAdGuardHttps,
                settings.AdGuardPort);
            long invalidationVersion =
                Interlocked.Read(ref _invalidationVersion);

            if (_manager is not null &&
                signature == _signature &&
                invalidationVersion == _managerInvalidationVersion)
            {
                return _manager;
            }

            cancellationToken.ThrowIfCancellationRequested();

            RouterManager? oldManager = _manager;
            _manager = null;
            _signature = null;

            if (oldManager is not null)
            {
                await DisposeManagerAsync(oldManager).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string password = _settingsService.DecryptPassword(
                settings.EncryptedPassword);
            string keyPassphrase = settings.SshAuthenticationMethod == SshAuthenticationMethod.PrivateKey
                ? _settingsService.DecryptPassword(settings.EncryptedPrivateKeyPassphrase)
                : string.Empty;
            var sshSettings = new SshConnectionSettings
            {
                Host = RouterConnectionOptions.NormaliseHost(settings.RouterHost),
                Port = settings.SshPort,
                Username = settings.Username.Trim(),
                AuthenticationMethod = settings.SshAuthenticationMethod,
                Password = password,
                PrivateKeyPath = settings.PrivateKeyPath,
                PrivateKeyPassphrase = keyPassphrase
            };

            _manager = new RouterManager(
                settings.RouterHost,
                settings.Username,
                password,
                sshSettings,
                _sshConnectionFactory,
                _hostKeyTrustService,
                _certificateTrustService,
                settings.AdGuardPort,
                settings.UseAdGuardHttps,
                _adGuardTransportSecurity);
            _signature = signature;
            _managerInvalidationVersion = invalidationVersion;
            return _manager;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Invalidate()
    {
        if (_disposed)
            return;

        Interlocked.Increment(ref _invalidationVersion);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        RouterManager? manager;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            manager = _manager;
            _manager = null;
            _signature = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (manager is not null)
        {
            await DisposeManagerAsync(manager).ConfigureAwait(false);
        }

        _lifecycleGate.Dispose();
    }

    private static Task DisposeManagerAsync(RouterManager manager)
    {
        return Task.Run(manager.Dispose);
    }
}
