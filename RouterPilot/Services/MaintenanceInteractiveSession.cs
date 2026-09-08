using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using RouterPilot.Configuration;
using RouterPilot.Models;

namespace RouterPilot.Services;

public enum MaintenanceInteractiveOperationKind
{
    TailscaleUpdate,
    AdGuardHomeUpdate,
    DevelopmentProbe
}

/// <summary>
/// Represents one reviewed community-maintenance command. Construction is
/// deliberately limited to RouterPilot's fixed command builders.
/// </summary>
public sealed record MaintenanceInteractiveOperation
{
    private MaintenanceInteractiveOperation(
        MaintenanceInteractiveOperationKind kind,
        string title,
        string command)
    {
        Kind = kind;
        Title = title;
        Command = command;
    }

    public MaintenanceInteractiveOperationKind Kind { get; }
    public string Title { get; }
    public string Command { get; }

    public static MaintenanceInteractiveOperation CreateTailscaleUpdate(
        TailscaleUpdaterOptions options) => new(
            MaintenanceInteractiveOperationKind.TailscaleUpdate,
            "Tailscale Community Updater",
            TailscaleUpdaterCommand.Build(options));

    public static MaintenanceInteractiveOperation CreateAdGuardHomeUpdate(
        bool selectRelease,
        bool ignoreFreeSpace) => new(
            MaintenanceInteractiveOperationKind.AdGuardHomeUpdate,
            "AdGuard Home Community Updater",
            AdGuardHomeUpdaterCommand.Build(selectRelease, ignoreFreeSpace));

    /// <summary>
    /// Developer/harness-only harmless PTY validation. It is not exposed by
    /// RouterPilot's production UI and cannot perform router mutation.
    /// </summary>
    public static MaintenanceInteractiveOperation CreateDevelopmentProbe() => new(
        MaintenanceInteractiveOperationKind.DevelopmentProbe,
        "Maintenance Console Safe Test",
        "printf 'Continue? [y/N] '; read -r routerpilot_probe_input; printf 'ANSWER=<%s>\\n' \"$routerpilot_probe_input\"");

    internal bool IsApproved() => Kind switch
    {
        MaintenanceInteractiveOperationKind.TailscaleUpdate =>
            IsApprovedTailscaleCommand(Command),
        MaintenanceInteractiveOperationKind.AdGuardHomeUpdate =>
            IsApprovedAdGuardCommand(Command),
        MaintenanceInteractiveOperationKind.DevelopmentProbe =>
            Command == "printf 'Continue? [y/N] '; read -r routerpilot_probe_input; printf 'ANSWER=<%s>\\n' \"$routerpilot_probe_input\"",
        _ => false
    };

    private static bool IsApprovedTailscaleCommand(string command)
    {
        for (int flags = 0; flags < 64; flags++)
        {
            var options = new TailscaleUpdaterOptions(
                (flags & 1) != 0,
                (flags & 2) != 0,
                (flags & 4) != 0,
                (flags & 8) != 0,
                (flags & 16) != 0,
                (flags & 32) != 0);
            if (command == TailscaleUpdaterCommand.Build(options))
                return true;
        }

        return false;
    }

    private static bool IsApprovedAdGuardCommand(string command) =>
        command == AdGuardHomeUpdaterCommand.Build(false, false) ||
        command == AdGuardHomeUpdaterCommand.Build(true, false) ||
        command == AdGuardHomeUpdaterCommand.Build(false, true) ||
        command == AdGuardHomeUpdaterCommand.Build(true, true);
}

public enum MaintenanceInteractiveSessionState
{
    Created,
    Connecting,
    Running,
    Completed,
    Failed,
    ConnectionLost,
    OutcomeUnknown,
    Disposed
}

public sealed record MaintenanceInteractiveSessionStatus(
    MaintenanceInteractiveSessionState State,
    string Message,
    int? ExitCode = null);

public interface IMaintenanceInteractiveSession : IAsyncDisposable
{
    event EventHandler<string>? OutputReceived;
    event EventHandler<MaintenanceInteractiveSessionStatus>? StatusChanged;

    MaintenanceInteractiveOperation Operation { get; }
    MaintenanceInteractiveSessionState State { get; }
    bool CanAcceptInput { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task SendInputAsync(string input, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public interface IMaintenanceInteractiveSessionFactory
{
    IMaintenanceInteractiveSession Create(MaintenanceInteractiveOperation operation);
}

/// <summary>
/// Opens a dedicated SSH client for a single reviewed Maintenance operation.
/// It never shares RouterPilot's serialized telemetry client and never exposes
/// credentials to views or command previews.
/// </summary>
public sealed class MaintenanceInteractiveSessionFactory : IMaintenanceInteractiveSessionFactory
{
    private readonly SettingsService _settingsService;
    private readonly IActiveRouterContext _activeRouter;
    private readonly ISshConnectionFactory _connectionFactory;
    private readonly ISshHostKeyTrustService _hostKeyTrustService;

    public MaintenanceInteractiveSessionFactory(
        SettingsService settingsService,
        IActiveRouterContext activeRouter,
        ISshConnectionFactory connectionFactory,
        ISshHostKeyTrustService hostKeyTrustService)
    {
        _settingsService = settingsService;
        _activeRouter = activeRouter;
        _connectionFactory = connectionFactory;
        _hostKeyTrustService = hostKeyTrustService;
    }

    public IMaintenanceInteractiveSession Create(MaintenanceInteractiveOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!operation.IsApproved())
            throw new InvalidOperationException("Maintenance operation is not approved.");

        RouterProfile profile = _activeRouter.CurrentProfile;
        string password = _settingsService.DecryptPassword(profile.EncryptedPassword);
        string keyPassphrase = profile.SshAuthenticationMethod == SshAuthenticationMethod.PrivateKey
            ? _settingsService.DecryptPassword(profile.EncryptedPrivateKeyPassphrase)
            : string.Empty;

        var settings = new SshConnectionSettings
        {
            Host = RouterConnectionOptions.NormaliseHost(profile.RouterHost),
            Port = profile.SshPort,
            Username = profile.Username.Trim(),
            AuthenticationMethod = profile.SshAuthenticationMethod,
            Password = password,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = keyPassphrase
        };

        return new MaintenanceInteractiveSession(
            operation,
            settings,
            _connectionFactory,
            _hostKeyTrustService);
    }
}

public static class MaintenanceInteractiveCommandWrapper
{
    private const string MarkerPrefix = "__ROUTERPILOT_MAINTENANCE_EXIT_";

    public static string Build(string approvedCommand, string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedCommand);
        if (string.IsNullOrWhiteSpace(nonce) || !Regex.IsMatch(nonce, "^[A-Fa-f0-9]{16,64}$"))
            throw new ArgumentException("Maintenance marker nonce is invalid.", nameof(nonce));

        return $"{{ {approvedCommand}; }}; __routerpilot_status=$?; printf '\\n{MarkerPrefix}{nonce}:%s\\n' \"$__routerpilot_status\"; exit \"$__routerpilot_status\"\n";
    }

    public static bool TryFindExitCode(string output, string nonce, out int exitCode)
    {
        exitCode = 0;
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(nonce))
            return false;

        Match match = Regex.Match(
            output,
            Regex.Escape(MarkerPrefix + nonce) + @":(?<code>\d+)(?:\r?\n|$)",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["code"].Value, out exitCode);
    }
}

/// <summary>
/// Creates bounded, terminal-style line input for an approved interactive
/// maintenance operation. The carriage return is the same control character
/// sent by an SSH terminal's Enter key; the allocated PTY translates it for
/// ordinary POSIX <c>read -r</c> prompts.
/// </summary>
public static class MaintenanceInteractiveInput
{
    public const int MaximumCharacters = 256;

    public static string BuildLine(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (input.Length > MaximumCharacters)
            throw new ArgumentOutOfRangeException(nameof(input), $"Maintenance input cannot exceed {MaximumCharacters} characters.");
        if (input.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Maintenance input must be one line.", nameof(input));

        return input + "\r";
    }
}

/// <summary>Bounded display-only transcript. It is intentionally not persisted.</summary>
public sealed class MaintenanceInteractiveTranscript
{
    private const int MaximumCharacters = 262_144;
    private static readonly Regex AnsiEscape = new("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    private readonly StringBuilder _content = new();

    public string Text => _content.ToString();

    public void Append(string output)
    {
        if (string.IsNullOrEmpty(output))
            return;

        string sanitized = AnsiEscape.Replace(output, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        _content.Append(sanitized);
        if (_content.Length > MaximumCharacters)
            _content.Remove(0, _content.Length - MaximumCharacters);
    }
}

internal sealed class MaintenanceInteractiveSession : IMaintenanceInteractiveSession
{
    private readonly SshConnectionSettings _settings;
    private readonly ISshConnectionFactory _connectionFactory;
    private readonly ISshHostKeyTrustService _hostKeyTrustService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _sync = new();
    private SshClient? _client;
    private ShellStream? _shell;
    private string? _nonce;
    private string _markerBuffer = string.Empty;
    private bool _disposed;
    private bool _completionObserved;

    public MaintenanceInteractiveSession(
        MaintenanceInteractiveOperation operation,
        SshConnectionSettings settings,
        ISshConnectionFactory connectionFactory,
        ISshHostKeyTrustService hostKeyTrustService)
    {
        Operation = operation;
        _settings = settings;
        _connectionFactory = connectionFactory;
        _hostKeyTrustService = hostKeyTrustService;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<MaintenanceInteractiveSessionStatus>? StatusChanged;

    public MaintenanceInteractiveOperation Operation { get; }
    public MaintenanceInteractiveSessionState State { get; private set; } = MaintenanceInteractiveSessionState.Created;
    public bool CanAcceptInput => State == MaintenanceInteractiveSessionState.Running && !_completionObserved;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State != MaintenanceInteractiveSessionState.Created)
            throw new InvalidOperationException("Maintenance session has already been started.");

        Transition(MaintenanceInteractiveSessionState.Connecting, "Connecting to router through SSH.");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

        try
        {
            await Task.Run(() => ConnectAndStartShell(linked.Token), linked.Token).ConfigureAwait(false);
            Transition(MaintenanceInteractiveSessionState.Running, "Running reviewed maintenance operation.");
            _ = ReadOutputAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!_completionObserved)
                Transition(MaintenanceInteractiveSessionState.OutcomeUnknown, "Maintenance operation was interrupted; outcome unknown.");
            CloseTransport();
        }
        catch (Exception exception) when (IsExpectedConnectionFailure(exception))
        {
            Transition(MaintenanceInteractiveSessionState.Failed, "Unable to open Maintenance SSH session.");
            CloseTransport();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Maintenance SSH session failed ({DiagnosticRedactor.FailureCategory(exception)}).");
            Transition(MaintenanceInteractiveSessionState.Failed, "Unable to open Maintenance SSH session.");
            CloseTransport();
        }
    }

    public async Task SendInputAsync(string input, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanAcceptInput)
            return;

        string terminalLine = MaintenanceInteractiveInput.BuildLine(input);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!CanAcceptInput || _shell is null)
                    throw new InvalidOperationException("Maintenance session is no longer accepting input.");

                // SSH.NET documents that the string overload flushes after it
                // buffers the text. WriteAsync did not provide that guarantee,
                // leaving the updater's line-based read waiting indefinitely.
                _shell.Write(terminalLine);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _completionObserved)
            return;

        try
        {
            if (_shell is not null)
            {
                byte[] interrupt = [0x03];
                await _shell.WriteAsync(interrupt.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsExpectedConnectionFailure(exception))
        {
            // The dedicated channel will be closed below; no outcome is inferred.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Maintenance cancellation failed ({DiagnosticRedactor.FailureCategory(exception)}).");
        }
        finally
        {
            _lifetimeCancellation.Cancel();
            CloseTransport();
            Transition(MaintenanceInteractiveSessionState.OutcomeUnknown, "Maintenance operation interrupted; outcome unknown.");
        }
    }

    private void ConnectAndStartShell(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _client = _connectionFactory.CreateClient(_settings);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
        _client.HostKeyReceived += OnHostKeyReceived;
        _client.Connect();
        if (!_client.IsConnected)
            throw new SshConnectionException("Maintenance SSH connection failed.");
        cancellationToken.ThrowIfCancellationRequested();

        _shell = _client.CreateShellStream("xterm", 120, 40, 0, 0, 8192);
        _nonce = Guid.NewGuid().ToString("N");
        string wrapper = MaintenanceInteractiveCommandWrapper.Build(Operation.Command, _nonce);
        _shell.Write(wrapper);
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _shell is not null)
            {
                int read = await _shell.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                string output = Encoding.UTF8.GetString(buffer, 0, read);
                OutputReceived?.Invoke(this, output);
                _markerBuffer = (_markerBuffer + output)[^Math.Min(_markerBuffer.Length + output.Length, 1024)..];
                if (_nonce is not null && MaintenanceInteractiveCommandWrapper.TryFindExitCode(_markerBuffer, _nonce, out int exitCode))
                {
                    _completionObserved = true;
                    Transition(
                        exitCode == 0 ? MaintenanceInteractiveSessionState.Completed : MaintenanceInteractiveSessionState.Failed,
                        exitCode == 0 ? "Maintenance command completed. Refreshing router state." : "Maintenance command finished with an error.",
                        exitCode);
                    CloseTransport();
                    return;
                }
            }

            if (!_completionObserved && !_lifetimeCancellation.IsCancellationRequested)
            {
                Transition(MaintenanceInteractiveSessionState.ConnectionLost, "Connection to router closed.");
                Transition(MaintenanceInteractiveSessionState.OutcomeUnknown, "Connection to router closed; outcome unknown.");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedConnectionFailure(exception))
        {
            if (!_completionObserved)
            {
                Transition(MaintenanceInteractiveSessionState.ConnectionLost, "Connection to router closed.");
                Transition(MaintenanceInteractiveSessionState.OutcomeUnknown, "Connection to router closed; outcome unknown.");
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Maintenance SSH output failed ({DiagnosticRedactor.FailureCategory(exception)}).");
            if (!_completionObserved)
            {
                Transition(MaintenanceInteractiveSessionState.Failed, "Maintenance SSH session failed; outcome unknown.");
                CloseTransport();
            }
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs eventArgs)
    {
        SshHostKeyTrustDecision decision = _hostKeyTrustService.Evaluate(_settings.Host, eventArgs.FingerPrintSHA256);
        eventArgs.CanTrust = decision is SshHostKeyTrustDecision.Trusted or SshHostKeyTrustDecision.TrustedAfterFirstUse;
    }

    private void Transition(MaintenanceInteractiveSessionState state, string message, int? exitCode = null)
    {
        State = state;
        StatusChanged?.Invoke(this, new MaintenanceInteractiveSessionStatus(state, message, exitCode));
    }

    private void CloseTransport()
    {
        lock (_sync)
        {
            try { _shell?.Dispose(); } catch { }
            _shell = null;
            try
            {
                if (_client?.IsConnected == true)
                    _client.Disconnect();
            }
            catch { }
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }

    private static bool IsExpectedConnectionFailure(Exception exception) => exception is
        SshAuthenticationException or
        SshConnectionException or
        SshException or
        SocketException or
        IOException or
        InvalidOperationException or
        ObjectDisposedException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetimeCancellation.Cancel();

        // ShellStream.Dispose/SshClient.Disconnect can wait for network I/O and
        // for the output reader's transport lock. Console Closed runs on WPF's
        // Dispatcher, so teardown must never execute there.
        await Task.Run(CloseTransport).ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
        Transition(MaintenanceInteractiveSessionState.Disposed, "Maintenance session closed.");
    }
}
