using System.Windows;
using System.Windows.Threading;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// Several services raise events from threads the user interface does not own —
/// process-exit callbacks, SignalR message handlers, achievement polling timers.
/// Updating an <c>ObservableCollection</c> from one of those throws outright,
/// which makes this an abstraction rather than a convenience. Services take this
/// interface instead of touching <see cref="Application.Current"/>, so they stay
/// testable without a running WPF application.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Gets a value indicating whether the caller is already on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Runs an action on the UI thread, executing it inline when the caller is
    /// already there.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    void Invoke(Action action);

    /// <summary>
    /// Queues an action to run on the UI thread without waiting for it.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <returns>A task that completes when the action has run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    Task InvokeAsync(Action action);
}

/// <summary>
/// Default <see cref="IUiDispatcher"/>, backed by the WPF dispatcher.
/// </summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// Initialises a new instance bound to the application's dispatcher.
    /// </summary>
    /// <remarks>
    /// Falls back to the current thread's dispatcher when no WPF application is
    /// running, which is what lets this type be constructed in a test host.
    /// </remarks>
    public UiDispatcher()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <inheritdoc />
    public bool IsOnUiThread => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            // Running inline avoids a needless queue hop and keeps the call
            // re-entrant-safe for callers already on the UI thread.
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}
