using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace RouterPilot.Services;

public enum MaintenanceExternalLaunchStatus
{
    Launched,
    LaunchedWithoutClipboard,
    LauncherUnavailable,
    LaunchFailed
}

/// <summary>
/// Result of a user-initiated external handoff. Launching a terminal is not
/// evidence that the updater completed or changed the router.
/// </summary>
public sealed record MaintenanceExternalLaunchResult(
    MaintenanceExternalLaunchStatus Status,
    Exception? Exception = null)
{
    public bool IsLaunched => Status is MaintenanceExternalLaunchStatus.Launched
        or MaintenanceExternalLaunchStatus.LaunchedWithoutClipboard;

    public bool ClipboardCopied => Status == MaintenanceExternalLaunchStatus.Launched;
}

/// <summary>
/// Keeps shell, terminal and browser handoffs outside the WPF event boundary.
/// It never waits for or inspects a shell-owned process.
/// </summary>
public sealed class MaintenanceExternalLauncher
{
    private readonly Func<string?> _commandShellResolver;
    private readonly Action<string> _clipboardSetter;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;

    public MaintenanceExternalLauncher()
        : this(ResolveCommandShell, Clipboard.SetText, Process.Start)
    {
    }

    public MaintenanceExternalLauncher(
        Func<string?> commandShellResolver,
        Action<string> clipboardSetter,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        _commandShellResolver = commandShellResolver;
        _clipboardSetter = clipboardSetter;
        _processStarter = processStarter;
    }

    public MaintenanceExternalLaunchResult LaunchInteractiveSsh(
        string host,
        string username,
        int port,
        string reviewedCommand)
    {
        bool copied = TryCopy(reviewedCommand, out Exception? clipboardException);
        string? commandShell;
        try
        {
            commandShell = _commandShellResolver();
        }
        catch (Exception ex)
        {
            return new(MaintenanceExternalLaunchStatus.LauncherUnavailable, ex);
        }
        if (string.IsNullOrWhiteSpace(commandShell))
        {
            return new(MaintenanceExternalLaunchStatus.LauncherUnavailable, clipboardException);
        }

        ProcessStartInfo startInfo = new(commandShell) { UseShellExecute = true };
        startInfo.ArgumentList.Add("/k");
        startInfo.ArgumentList.Add($"ssh.exe -p {port} {username}@{host}");
        return Start(startInfo, copied, clipboardException);
    }

    public MaintenanceExternalLaunchResult OpenUri(Uri uri) =>
        Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, copied: true, clipboardException: null);

    public MaintenanceExternalLaunchResult OpenDirectory(string directory) =>
        Start(new ProcessStartInfo(directory) { UseShellExecute = true }, copied: true, clipboardException: null);

    private MaintenanceExternalLaunchResult Start(
        ProcessStartInfo startInfo,
        bool copied,
        Exception? clipboardException)
    {
        try
        {
            // A shell-executed process may legitimately be null. RouterPilot
            // does not own, wait for or dereference the external process.
            _ = _processStarter(startInfo);
            return new(copied
                ? MaintenanceExternalLaunchStatus.Launched
                : MaintenanceExternalLaunchStatus.LaunchedWithoutClipboard, clipboardException);
        }
        catch (Win32Exception ex)
        {
            return new(MaintenanceExternalLaunchStatus.LaunchFailed, ex);
        }
        catch (FileNotFoundException ex)
        {
            return new(MaintenanceExternalLaunchStatus.LauncherUnavailable, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(MaintenanceExternalLaunchStatus.LaunchFailed, ex);
        }
        catch (InvalidOperationException ex)
        {
            return new(MaintenanceExternalLaunchStatus.LaunchFailed, ex);
        }
        catch (Exception ex)
        {
            // The UI boundary logs this unexpected category; it must not cross
            // an event handler and terminate RouterPilot.
            return new(MaintenanceExternalLaunchStatus.LaunchFailed, ex);
        }
    }

    private bool TryCopy(string command, out Exception? exception)
    {
        try
        {
            _clipboardSetter(command);
            exception = null;
            return true;
        }
        catch (ExternalException ex)
        {
            exception = ex;
            return false;
        }
        catch (Exception ex)
        {
            exception = ex;
            return false;
        }
    }

    private static string? ResolveCommandShell()
    {
        string? commandShell = Environment.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrWhiteSpace(commandShell) && File.Exists(commandShell)
            ? commandShell
            : null;
    }
}
