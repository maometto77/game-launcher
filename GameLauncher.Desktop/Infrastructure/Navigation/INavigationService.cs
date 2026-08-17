using GameLauncher.Desktop.ViewModels;

namespace GameLauncher.Desktop.Infrastructure.Navigation;

/// <summary>
/// Implemented by a view model that needs an argument when navigated to.
/// </summary>
/// <typeparam name="TParameter">Type of the argument the view model requires.</typeparam>
/// <remarks>
/// Keeps navigation arguments type-checked at the call site. The alternative —
/// an <see cref="object"/> parameter cast inside the target — turns a wrong
/// argument into a runtime crash instead of a build error.
/// </remarks>
public interface INavigationTarget<in TParameter>
{
    /// <summary>
    /// Supplies the navigation argument. Called before
    /// <see cref="ViewModelBase.OnNavigatedToAsync"/>.
    /// </summary>
    /// <param name="parameter">The argument supplied by the caller.</param>
    /// <param name="cancellationToken">Cancelled if navigation is superseded.</param>
    /// <returns>A task that completes when the argument has been applied.</returns>
    Task InitializeAsync(TParameter parameter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Moves the shell between pages and owns the back stack.
/// </summary>
/// <remarks>
/// View models are resolved from the DI container on each navigation, so a page
/// always starts from a clean state with freshly injected services.
/// </remarks>
public interface INavigationService
{
    /// <summary>Gets the view model currently being displayed, or <see langword="null"/> before first navigation.</summary>
    ViewModelBase? Current { get; }

    /// <summary>Gets a value indicating whether there is a previous page to return to.</summary>
    bool CanGoBack { get; }

    /// <summary>Raised after <see cref="Current"/> changes, on the UI thread.</summary>
    event EventHandler<ViewModelBase?>? CurrentChanged;

    /// <summary>
    /// Navigates to a page, reusing the same instance every time.
    /// </summary>
    /// <typeparam name="TViewModel">The page to show.</typeparam>
    /// <param name="cancellationToken">Cancels the navigation and the page's load.</param>
    /// <returns>A task that completes once the page has loaded.</returns>
    /// <remarks>
    /// For pages reached from the sidebar, where losing state on every visit
    /// would be felt: a search box that empties itself, a filter that resets,
    /// a scroll position that jumps back to the top. The page still gets
    /// <c>OnNavigatedToAsync</c> each time, so it can refresh what should be
    /// fresh while keeping what should not.
    /// </remarks>
    Task NavigateToKeptAliveAsync<TViewModel>(CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase;

    /// <summary>
    /// Navigates to a view model resolved from the container.
    /// </summary>
    /// <typeparam name="TViewModel">The view model type to display.</typeparam>
    /// <param name="cancellationToken">Cancels the target's load.</param>
    /// <returns>A task that completes once the target has loaded.</returns>
    Task NavigateToAsync<TViewModel>(CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase;

    /// <summary>
    /// Navigates to a view model that requires an argument.
    /// </summary>
    /// <typeparam name="TViewModel">The view model type to display.</typeparam>
    /// <typeparam name="TParameter">Type of the argument.</typeparam>
    /// <param name="parameter">The argument passed to the target.</param>
    /// <param name="cancellationToken">Cancels the target's load.</param>
    /// <returns>A task that completes once the target has loaded.</returns>
    Task NavigateToAsync<TViewModel, TParameter>(
        TParameter parameter,
        CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase, INavigationTarget<TParameter>;

    /// <summary>
    /// Returns to the previous page. Does nothing when
    /// <see cref="CanGoBack"/> is <see langword="false"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the restored page's load.</param>
    /// <returns>A task that completes once the previous page is active again.</returns>
    Task GoBackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the back stack, so the next <see cref="GoBackAsync"/> is a no-op.
    /// </summary>
    /// <remarks>
    /// Used when the shell moves sideways — a different section, or a different
    /// tab within one. Returning from Downloads to a game page the user opened
    /// out of the library some minutes ago would be disorienting.
    /// </remarks>
    void ClearHistory();
}
