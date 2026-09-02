using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly ObservableCollection<TimelineEvent> _events = new();
    private bool _initialized;

    public TimelineService(Dispatcher dispatcher, ApplicationDataPathProvider paths)
    {
        _dispatcher = dispatcher;
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

        await _dispatcher.InvokeAsync(() =>
        {
            if (_initialized)
                return;
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

        // Timeline history is intentionally session-local; no disk persistence.
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
        // Session-local only.
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
        // Session-local only.
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
    }
}
