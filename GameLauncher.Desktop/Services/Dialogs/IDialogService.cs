namespace GameLauncher.Desktop.Services.Dialogs;

/// <summary>
/// Shows modal prompts and system pickers on behalf of a view model.
/// </summary>
/// <remarks>
/// View models must not reference window types directly, or they stop being
/// testable and start depending on there being a UI at all. This interface is
/// the seam: a test substitutes a fake that answers without showing anything.
/// </remarks>
public interface IDialogService
{
    /// <summary>
    /// Asks the user to confirm an action.
    /// </summary>
    /// <param name="title">Dialog caption.</param>
    /// <param name="message">The question being asked.</param>
    /// <param name="isDestructive">
    /// When <see langword="true"/>, the dialog is presented as a warning and
    /// defaults to the cancelling answer.
    /// </param>
    /// <returns><see langword="true"/> when the user confirmed.</returns>
    bool Confirm(string title, string message, bool isDestructive = false);

    /// <summary>Shows an informational message.</summary>
    /// <param name="title">Dialog caption.</param>
    /// <param name="message">The message to show.</param>
    void ShowInformation(string title, string message);

    /// <summary>Shows an error message.</summary>
    /// <param name="title">Dialog caption.</param>
    /// <param name="message">The message to show.</param>
    void ShowError(string title, string message);

    /// <summary>
    /// Asks the user to pick a single existing file.
    /// </summary>
    /// <param name="title">Picker caption.</param>
    /// <param name="filter">Win32 filter string, for example <c>Executables|*.exe</c>.</param>
    /// <param name="initialDirectory">Folder to open at, if it exists.</param>
    /// <returns>The chosen path, or <see langword="null"/> when cancelled.</returns>
    string? PickFile(string title, string filter, string? initialDirectory = null);

    /// <summary>
    /// Asks the user to pick a folder.
    /// </summary>
    /// <param name="title">Picker caption.</param>
    /// <param name="initialDirectory">Folder to open at, if it exists.</param>
    /// <returns>The chosen path, or <see langword="null"/> when cancelled.</returns>
    string? PickFolder(string title, string? initialDirectory = null);
}
