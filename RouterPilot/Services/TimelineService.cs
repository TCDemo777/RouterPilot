using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.ComponentModel;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Application-scoped, safe activity timeline. Event producers own detection; this service only stores/presents events.</summary>
public sealed class TimelineService : INotifyPropertyChanged
{
    private const int MaximumEntries = 1000;
    private readonly Dispatcher _dispatcher;
    private readonly string _path;
    private readonly ObservableCollection<TimelineEvent> _events = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initialized;

    public TimelineService(Dispatcher dispatcher, ApplicationDataPathProvider paths)
    {
        _dispatcher = dispatcher;
        _path = Path.Combine(paths.CurrentPath, "timeline.json");
        Events = new ReadOnlyObservableCollection<TimelineEvent>(_events);
    }

    public ReadOnlyObservableCollection<TimelineEvent> Events { get; }
    public event EventHandler? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int UnreadCount => _events.Count(item => !item.IsRead);

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        List<TimelineEvent> loaded = [];
        try
        {
            if (File.Exists(_path))
            {
                await using FileStream stream = File.OpenRead(_path);
                TimelineStore? store = await JsonSerializer.DeserializeAsync<TimelineStore>(stream);
                loaded = store?.Events ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt optional history must never affect router startup.
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (_initialized)
                return;
            foreach (TimelineEvent item in loaded.OrderByDescending(entry => entry.Timestamp).Take(MaximumEntries))
                _events.Add(item);
            _initialized = true;
            Changed?.Invoke(this, EventArgs.Empty);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
        });
    }

    public async Task<bool> AddAsync(TimelineEvent entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync();
        bool added = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(entry.DeduplicationKey) &&
                _events.Any(existing => string.Equals(existing.DeduplicationKey, entry.DeduplicationKey, StringComparison.Ordinal)))
                return;

            _events.Insert(0, entry);
            while (_events.Count > MaximumEntries)
                _events.RemoveAt(_events.Count - 1);
            added = true;
            Changed?.Invoke(this, EventArgs.Empty);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
        });

        if (added)
            await FlushAsync(cancellationToken);
        return added;
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        bool changed = false;
        await _dispatcher.InvokeAsync(() =>
        {
            foreach (TimelineEvent item in _events.Where(item => !item.IsRead))
            {
                item.IsRead = true;
                changed = true;
            }
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
            }
        });
        if (changed)
            await FlushAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        await _dispatcher.InvokeAsync(() =>
        {
            _events.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
        });
        await FlushAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            List<TimelineEvent> snapshot = await _dispatcher.InvokeAsync(
                () => _events.ToList(), DispatcherPriority.Send, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(new TimelineStore { Events = snapshot }, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed class TimelineStore
    {
        public int FormatVersion { get; init; } = 1;
        public List<TimelineEvent> Events { get; init; } = [];
    }
}
