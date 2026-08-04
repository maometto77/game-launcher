namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Base class for view models hosted in a modal window.
/// </summary>
/// <remarks>
/// A dialog view model must be able to close its window without holding a
/// reference to it. It raises <see cref="CloseRequested"/> carrying the result;
/// the window subscribes, assigns <c>DialogResult</c> and closes. That keeps the
/// direction of dependency pointing from view to view model.
/// </remarks>
public abstract class DialogViewModelBase : ViewModelBase
{
    /// <summary>
    /// Raised when the view model wants its window closed. The argument is the
    /// dialog result to report.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>
    /// Requests that the hosting window close and report a result.
    /// </summary>
    /// <param name="result">
    /// <see langword="true"/> when the dialog was accepted; otherwise
    /// <see langword="false"/>.
    /// </param>
    protected void RequestClose(bool result) => CloseRequested?.Invoke(this, result);
}
