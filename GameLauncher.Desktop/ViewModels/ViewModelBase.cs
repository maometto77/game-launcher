using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Base class for every view model in the application.
/// </summary>
/// <remarks>
/// <para>
/// Adds a navigation lifecycle on top of <see cref="ObservableObject"/>. View
/// models load their data in <see cref="OnNavigatedToAsync"/> rather than in
/// their constructor, which keeps construction cheap and synchronous, lets the
/// navigation service surface load failures in one place, and makes a view model
/// constructible in a test without touching the database.
/// </para>
/// <para>
/// View models hold no business logic. They orchestrate services, expose state
/// for binding, and translate results into something a view can render.
/// </para>
/// </remarks>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _errorMessage;

    /// <summary>
    /// Gets or sets a value indicating whether a long-running operation is in
    /// flight, so the view can show progress and disable input.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Gets or sets a message describing the most recent failure, or
    /// <see langword="null"/> when the last operation succeeded.
    /// </summary>
    /// <remarks>
    /// Intended for errors the user can act on. Diagnostic detail belongs in the
    /// log, not here.
    /// </remarks>
    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="ErrorMessage"/> currently holds
    /// a message.
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// Called by the navigation service once this view model has become the
    /// active page. Override to load data.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancelled when the user navigates away before loading completes.
    /// </param>
    /// <returns>A task that completes when the view model is ready to display.</returns>
    public virtual Task OnNavigatedToAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called by the navigation service immediately before this view model stops
    /// being the active page. Override to release resources such as file
    /// watchers or timers.
    /// </summary>
    /// <returns>A task that completes when teardown has finished.</returns>
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>
    /// Clears any error currently being displayed.
    /// </summary>
    protected void ClearError() => SetErrorMessage(null);

    /// <summary>
    /// Sets <see cref="ErrorMessage"/> and raises change notification for
    /// <see cref="HasError"/> alongside it.
    /// </summary>
    /// <param name="message">The message to show, or <see langword="null"/> to clear.</param>
    protected void SetErrorMessage(string? message)
    {
        ErrorMessage = message;
        OnPropertyChanged(nameof(HasError));
    }
}
