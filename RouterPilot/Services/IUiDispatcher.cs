using System.Windows.Threading;

namespace RouterPilot.Services;

/// <summary>
/// Small boundary between application services and WPF's UI thread. Services
/// depend on this abstraction rather than asking DI for a raw Dispatcher.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken).Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (_dispatcher.CheckAccess()) return Task.FromResult(action());
        return _dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken).Task;
    }
}
