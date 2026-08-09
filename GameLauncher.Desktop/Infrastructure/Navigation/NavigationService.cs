using GameLauncher.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure.Navigation;

/// <summary>
/// Default <see cref="INavigationService"/>, backed by the DI container.
/// </summary>
/// <remarks>
/// <para>
/// Each navigation cancels the previous one. Without that, navigating away from
/// a slow page and straight back would leave two loads racing to write the same
/// view model, and whichever finished last would win.
/// </para>
/// <para>
/// Instances are kept on the back stack rather than factories, and
/// <see cref="ViewModelBase.OnNavigatedToAsync"/> runs again on return. That
/// preserves cheap view state while still refreshing data that may have changed
/// — a game uninstalled from a details page should not still be listed when the
/// user returns to the library.
/// </para>
/// </remarks>
public sealed class NavigationService : INavigationService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NavigationService> _logger;
    private readonly Stack<ViewModelBase> _backStack = new();

    private CancellationTokenSource? _navigationCts;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="services">Container used to resolve target view models.</param>
    /// <param name="logger">Logger for navigation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public NavigationService(IServiceProvider services, ILogger<NavigationService> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ViewModelBase? Current { get; private set; }

    /// <summary>Pages kept for the lifetime of the window, by type.</summary>
    private readonly Dictionary<Type, ViewModelBase> _keptAlive = [];

    /// <inheritdoc />
    public bool CanGoBack => _backStack.Count > 0;

    /// <inheritdoc />
    public event EventHandler<ViewModelBase?>? CurrentChanged;

    /// <inheritdoc />
    public Task NavigateToKeptAliveAsync<TViewModel>(CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase
    {
        // Resolved once and remembered. Page view models are registered
        // transient so an ordinary navigation starts clean; a section reached
        // from the sidebar wants the opposite, and this is where that choice is
        // made rather than by changing the registration for every caller.
        if (!_keptAlive.TryGetValue(typeof(TViewModel), out var target))
        {
            target = _services.GetRequiredService<TViewModel>();
            _keptAlive[typeof(TViewModel)] = target;
        }

        // Nothing is pushed. These are the shell's own destinations, reached
        // sideways from the sidebar or the tab strip, and Back to the one before
        // would leave the strip pointing at a page that is no longer shown.
        // Back exists for drilling into a game and coming out again.
        return NavigateCoreAsync(target, pushCurrentOntoBackStack: false, initialize: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task NavigateToAsync<TViewModel>(CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase
    {
        var target = _services.GetRequiredService<TViewModel>();
        return NavigateCoreAsync(target, pushCurrentOntoBackStack: true, initialize: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task NavigateToAsync<TViewModel, TParameter>(
        TParameter parameter,
        CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase, INavigationTarget<TParameter>
    {
        var target = _services.GetRequiredService<TViewModel>();
        return NavigateCoreAsync(
            target,
            pushCurrentOntoBackStack: true,
            initialize: token => target.InitializeAsync(parameter, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task GoBackAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoBack)
        {
            return;
        }

        var previous = _backStack.Pop();
        await NavigateCoreAsync(previous, pushCurrentOntoBackStack: false, initialize: null, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <inheritdoc />
    public void ClearHistory() => _backStack.Clear();

    /// <summary>
    /// Performs the navigation: tears down the outgoing page, swaps in the
    /// incoming one, then loads it.
    /// </summary>
    /// <param name="target">The view model to display.</param>
    /// <param name="pushCurrentOntoBackStack">
    /// Whether the outgoing page should become the back target. False when the
    /// navigation is itself a back operation.
    /// </param>
    /// <param name="initialize">Optional parameter-passing step run before loading.</param>
    /// <param name="cancellationToken">Caller's cancellation token.</param>
    private async Task NavigateCoreAsync(
        ViewModelBase target,
        bool pushCurrentOntoBackStack,
        Func<CancellationToken, Task>? initialize,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        // Supersede any load still running for the page being replaced.
        var previousCts = _navigationCts;
        _navigationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _navigationCts.Token;

        if (previousCts is not null)
        {
            await previousCts.CancelAsync().ConfigureAwait(true);
            previousCts.Dispose();
        }

        var outgoing = Current;
        if (outgoing is not null)
        {
            try
            {
                await outgoing.OnNavigatedFromAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // A page failing to tear down must not strand the user on it.
                _logger.LogError(ex, "Teardown of {ViewModel} failed; continuing with navigation.",
                    outgoing.GetType().Name);
            }

            if (pushCurrentOntoBackStack)
            {
                _backStack.Push(outgoing);
            }
        }

        Current = target;
        CurrentChanged?.Invoke(this, target);
        _logger.LogDebug("Navigated to {ViewModel}.", target.GetType().Name);

        try
        {
            if (initialize is not null)
            {
                await initialize(token).ConfigureAwait(true);
            }

            await target.OnNavigatedToAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected when the user navigates again before this load finishes.
            _logger.LogDebug("Load of {ViewModel} was superseded.", target.GetType().Name);
        }
        catch (Exception ex)
        {
            // The page is already on screen, so it surfaces the failure itself
            // rather than the navigation call throwing into the command handler.
            _logger.LogError(ex, "Loading {ViewModel} failed.", target.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Cancels any in-flight navigation and releases its token source.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
    }
}
