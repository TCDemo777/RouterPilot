using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RouterPilot.Services;

public sealed class RefreshCoordinator : IAsyncDisposable
{
    private sealed class RefreshTaskRegistration
    {
        public required string Name { get; init; }

        public required TimeSpan Interval { get; set; }

        public required Func<CancellationToken, Task> Callback { get; init; }

        public bool Enabled { get; set; }

        public long LifecycleVersion { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        // Serializes ownership changes for the loop task and its token source.
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);

        public CancellationTokenSource? LoopCancellation { get; set; }

        public Task? LoopTask { get; set; }
    }

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, RefreshTaskRegistration> _tasks =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private Task? _disposeTask;

    public void Register(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> callback,
        bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_tasks.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"A refresh task named '{name}' is already registered.");
            }

            var registration = new RefreshTaskRegistration
            {
                Name = name,
                Interval = interval,
                Callback = callback,
                Enabled = enabled
            };

            _tasks.Add(name, registration);

            if (enabled)
            {
                StartLoopLocked(registration);
            }
        }
    }

    public async Task SetEnabledAsync(string name, bool enabled)
    {
        RefreshTaskRegistration registration;
        long lifecycleVersion;

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            registration = GetTask(name);

            if (registration.Enabled != enabled)
            {
                registration.Enabled = enabled;
                registration.LifecycleVersion++;
            }

            lifecycleVersion = registration.LifecycleVersion;
        }

        await ReconcileLoopAsync(
                registration,
                lifecycleVersion,
                forceRestart: false)
            .ConfigureAwait(false);
    }

    public void UpdateInterval(string name, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        RefreshTaskRegistration registration;
        long lifecycleVersion;

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            registration = GetTask(name);

            if (registration.Interval == interval)
            {
                return;
            }

            registration.Interval = interval;
            lifecycleVersion = ++registration.LifecycleVersion;
        }

        // A task may update its own interval from inside its callback. Queueing
        // avoids making that callback await the loop that currently owns it.
        _ = ObserveLifecycleTransitionAsync(
            ReconcileLoopAsync(
                registration,
                lifecycleVersion,
                forceRestart: true),
            registration.Name);
    }

    public Task<bool> RunNowAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            return ExecuteAsync(GetTask(name), cancellationToken);
        }
    }

    public async Task StopAllAsync()
    {
        (RefreshTaskRegistration Registration, long Version)[] tasks;

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            tasks = DisableAllLocked();
        }

        await Task.WhenAll(
                tasks.Select(task => ReconcileLoopAsync(
                    task.Registration,
                    task.Version,
                    forceRestart: false)))
            .ConfigureAwait(false);
    }

    private RefreshTaskRegistration GetTask(string name)
    {
        if (!_tasks.TryGetValue(name, out RefreshTaskRegistration? task))
        {
            throw new KeyNotFoundException(
                $"No refresh task named '{name}' is registered.");
        }

        return task;
    }

    private (RefreshTaskRegistration Registration, long Version)[]
        DisableAllLocked()
    {
        return _tasks.Values
            .Select(registration =>
            {
                registration.Enabled = false;
                long version = ++registration.LifecycleVersion;
                return (registration, version);
            })
            .ToArray();
    }

    private async Task ReconcileLoopAsync(
        RefreshTaskRegistration registration,
        long lifecycleVersion,
        bool forceRestart)
    {
        await registration.LifecycleGate.WaitAsync()
            .ConfigureAwait(false);

        try
        {
            CancellationTokenSource? oldCancellation;
            Task? oldLoop;

            lock (_syncRoot)
            {
                if (lifecycleVersion != registration.LifecycleVersion)
                {
                    return;
                }

                bool loopMatchesDesiredState = registration.Enabled
                    ? registration.LoopTask is not null
                    : registration.LoopTask is null;

                if (!forceRestart && loopMatchesDesiredState)
                {
                    return;
                }

                oldCancellation = registration.LoopCancellation;
                oldLoop = registration.LoopTask;
                registration.LoopCancellation = null;
                registration.LoopTask = null;
            }

            oldCancellation?.Cancel();

            if (oldLoop is not null)
            {
                await AwaitLoopCompletionAsync(
                        oldLoop,
                        registration.Name)
                    .ConfigureAwait(false);
            }

            oldCancellation?.Dispose();

            lock (_syncRoot)
            {
                if (!_disposed &&
                    lifecycleVersion == registration.LifecycleVersion &&
                    registration.Enabled)
                {
                    StartLoopLocked(registration);
                }
            }
        }
        finally
        {
            registration.LifecycleGate.Release();
        }
    }

    private static async Task ObserveLifecycleTransitionAsync(
        Task transition,
        string taskName)
    {
        try
        {
            await transition.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Refresh task '{taskName}' lifecycle transition failed ({ex.GetType().Name}).");
        }
    }

    private static async Task AwaitLoopCompletionAsync(
        Task loopTask,
        string taskName)
    {
        try
        {
            await loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Refresh task '{taskName}' loop stopped with an error ({ex.GetType().Name}).");
        }
    }

    private static void StartLoopLocked(
        RefreshTaskRegistration registration)
    {
        var cancellation = new CancellationTokenSource();
        registration.LoopCancellation = cancellation;
        registration.LoopTask = RunLoopAsync(
            registration,
            cancellation.Token);
    }

    private static async Task RunLoopAsync(
        RefreshTaskRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(registration.Interval);

            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                try
                {
                    await ExecuteAsync(registration, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Refresh task '{registration.Name}' failed ({ex.GetType().Name}).");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<bool> ExecuteAsync(
        RefreshTaskRegistration registration,
        CancellationToken cancellationToken)
    {
        if (!await registration.Gate
                .WaitAsync(0, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await registration.Callback(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            registration.Gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            (RefreshTaskRegistration Registration, long Version)[] tasks =
                DisableAllLocked();
            _disposeTask = DisposeCoreAsync(tasks);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(
        (RefreshTaskRegistration Registration, long Version)[] tasks)
    {
        await Task.WhenAll(
                tasks.Select(task => ReconcileLoopAsync(
                    task.Registration,
                    task.Version,
                    forceRestart: false)))
            .ConfigureAwait(false);

        foreach ((RefreshTaskRegistration registration, _) in tasks)
        {
            await registration.Gate.WaitAsync().ConfigureAwait(false);
            registration.Gate.Release();
            registration.Gate.Dispose();
            registration.LifecycleGate.Dispose();
        }

        lock (_syncRoot)
        {
            _tasks.Clear();
        }
    }
}
