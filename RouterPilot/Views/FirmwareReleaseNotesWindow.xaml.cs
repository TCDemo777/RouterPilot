using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RouterPilot.Views;

public partial class FirmwareReleaseNotesWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();

    public FirmwareReleaseNotesWindow(string version, bool installed)
    {
        InitializeComponent();
        ReleaseLabel.Text = $"GL.iNet Firmware {version} ({(installed ? "installed release" : "latest release")})";
        Closed += (_, _) => _cancellation.Cancel();
        NotesText.Text = "Loading release notes…";
    }

    public async Task LoadAsync(Func<CancellationToken, Task<(string? Notes, DateTimeOffset? Date)>> loader)
    {
        try
        {
            (string? notes, DateTimeOffset? date) = await loader(_cancellation.Token);
            if (_cancellation.IsCancellationRequested) return;
            ReleaseDate.Text = date is { } value ? $"Release date: {value:yyyy-MM-dd}" : string.Empty;
            NotesText.Text = string.IsNullOrWhiteSpace(notes) ? "No release notes are available for this firmware." : notes;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch
        {
            if (!_cancellation.IsCancellationRequested) NotesText.Text = "Unable to load release notes.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
