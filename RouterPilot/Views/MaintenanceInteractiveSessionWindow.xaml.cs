using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RouterPilot.Services;

namespace RouterPilot.Views;

/// <summary>
/// Modeless, RouterPilot-owned UI for exactly one reviewed SSH maintenance
/// command. It is never assigned as Application.MainWindow.
/// </summary>
public partial class MaintenanceInteractiveSessionWindow : Window
{
    private readonly IMaintenanceInteractiveSession _session;
    private readonly MaintenanceExternalLauncher _externalLauncher;
    private readonly Func<Task> _postOperationRefresh;
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private readonly MaintenanceInteractiveTranscript _transcript = new();
    private const int MaximumPendingOutputCharacters = 65_536;
    private readonly DispatcherTimer _outputFlushTimer;
    private int _pendingOutputCharacters;
    private bool _allowClose;
    private bool _refreshRequested;

    public MaintenanceInteractiveSessionWindow(
        Window owner,
        string routerDisplayName,
        IMaintenanceInteractiveSession session,
        MaintenanceExternalLauncher externalLauncher,
        Func<Task> postOperationRefresh)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        _postOperationRefresh = postOperationRefresh ?? throw new ArgumentNullException(nameof(postOperationRefresh));

        Owner = owner;
        InitializeComponent();
        Title = session.Operation.Title;
        TitleTextBlock.Text = session.Operation.Title;
        RouterTextBlock.Text = string.IsNullOrWhiteSpace(routerDisplayName) ? "Current router" : routerDisplayName;
        OperationTextBlock.Text = session.Operation.Title;
        CommandTextBox.Text = session.Operation.Command;

        _outputFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        _outputFlushTimer.Tick += OutputFlushTimer_Tick;
        _session.OutputReceived += Session_OutputReceived;
        _session.StatusChanged += Session_StatusChanged;
        Loaded += MaintenanceInteractiveSessionWindow_Loaded;
        Closing += MaintenanceInteractiveSessionWindow_Closing;
        Closed += MaintenanceInteractiveSessionWindow_Closed;
        DataObject.AddPastingHandler(InputTextBox, (_, eventArgs) => eventArgs.CancelCommand());
    }

    private async void MaintenanceInteractiveSessionWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _outputFlushTimer.Start();
        try
        {
            await _session.StartAsync();
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "Unable to open Maintenance SSH session.";
            System.Diagnostics.Debug.WriteLine($"Maintenance SSH session failed ({DiagnosticRedactor.FailureCategory(exception)}).");
            ApplyTerminalState();
        }
    }

    private void Session_OutputReceived(object? sender, string output)
    {
        if (string.IsNullOrEmpty(output))
            return;

        int pending = Interlocked.Add(ref _pendingOutputCharacters, output.Length);
        if (pending > MaximumPendingOutputCharacters)
        {
            Interlocked.Add(ref _pendingOutputCharacters, -output.Length);
            return;
        }

        _pendingOutput.Enqueue(output);
    }

    private void OutputFlushTimer_Tick(object? sender, EventArgs e)
    {
        bool changed = false;
        while (_pendingOutput.TryDequeue(out string? output))
        {
            Interlocked.Add(ref _pendingOutputCharacters, -output.Length);
            _transcript.Append(output);
            changed = true;
        }

        if (!changed)
            return;

        TranscriptTextBox.Text = _transcript.Text;
        TranscriptTextBox.CaretIndex = TranscriptTextBox.Text.Length;
        TranscriptTextBox.ScrollToEnd();
    }

    private void Session_StatusChanged(object? sender, MaintenanceInteractiveSessionStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Session_StatusChanged(sender, status));
            return;
        }

        StatusTextBlock.Text = status.ExitCode is int exitCode
            ? $"{status.Message} Exit code: {exitCode}."
            : status.Message;

        if (status.State == MaintenanceInteractiveSessionState.Running)
        {
            InputTextBox.IsEnabled = true;
            SendInputButton.IsEnabled = true;
            InputTextBox.Focus();
            return;
        }

        if (status.State is MaintenanceInteractiveSessionState.Completed or
            MaintenanceInteractiveSessionState.Failed or
            MaintenanceInteractiveSessionState.ConnectionLost or
            MaintenanceInteractiveSessionState.OutcomeUnknown)
        {
            ApplyTerminalState();
            if (!_refreshRequested && status.State is MaintenanceInteractiveSessionState.Completed or MaintenanceInteractiveSessionState.Failed)
            {
                _refreshRequested = true;
                _ = RefreshAfterOperationAsync();
            }
        }
    }

    private async Task RefreshAfterOperationAsync()
    {
        try
        {
            await _postOperationRefresh();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Maintenance post-operation refresh failed ({DiagnosticRedactor.FailureCategory(exception)}).");
        }
    }

    private void ApplyTerminalState()
    {
        InputTextBox.IsEnabled = false;
        SendInputButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
    }

    private async void SendInput_Click(object sender, RoutedEventArgs e) => await SendInputAsync();

    private async void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !InputTextBox.IsEnabled)
            return;
        e.Handled = true;
        await SendInputAsync();
    }

    private async Task SendInputAsync()
    {
        string input = InputTextBox.Text;
        if (string.IsNullOrWhiteSpace(input))
            return;

        try
        {
            await _session.SendInputAsync(input);
            InputTextBox.Clear();
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "Unable to send the updater response.";
            System.Diagnostics.Debug.WriteLine($"Maintenance input failed ({DiagnosticRedactor.FailureCategory(exception)}).");
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Interrupting a maintenance operation may leave its outcome unknown. Continue?",
                "Cancel maintenance operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await _session.CancelAsync();
        ApplyTerminalState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MaintenanceInteractiveSessionWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || _session.State is MaintenanceInteractiveSessionState.Completed or
            MaintenanceInteractiveSessionState.Failed or
            MaintenanceInteractiveSessionState.ConnectionLost or
            MaintenanceInteractiveSessionState.OutcomeUnknown or
            MaintenanceInteractiveSessionState.Disposed)
            return;

        if (MessageBox.Show(
                "A maintenance operation is still running. Closing this window may interrupt it and its outcome may be unknown.\n\nInterrupt and close?",
                "Maintenance operation running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _ = InterruptAndCloseAsync();
    }

    private async Task InterruptAndCloseAsync()
    {
        await _session.CancelAsync();
        _allowClose = true;
        Close();
    }

    private async void MaintenanceInteractiveSessionWindow_Closed(object? sender, EventArgs e)
    {
        _outputFlushTimer.Stop();
        _session.OutputReceived -= Session_OutputReceived;
        _session.StatusChanged -= Session_StatusChanged;
        await _session.DisposeAsync();
    }

    private void OpenAdmon_Click(object sender, RoutedEventArgs e)
    {
        MaintenanceExternalLaunchResult result = _externalLauncher.OpenUri(new Uri("https://admon.me"));
        if (result.IsLaunched)
            return;

        MessageBox.Show(
            "RouterPilot could not open the requested web page.",
            "Open Admon",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
