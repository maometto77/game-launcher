using System.Windows;
using Microsoft.Win32;

namespace GameLauncher.Desktop.Services.Dialogs;

/// <summary>
/// Default <see cref="IDialogService"/>, backed by WPF message boxes and the
/// Win32 common dialogs.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public bool Confirm(string title, string message, bool isDestructive = false)
    {
        var result = MessageBox.Show(
            Owner,
            message,
            title,
            MessageBoxButton.YesNo,
            isDestructive ? MessageBoxImage.Warning : MessageBoxImage.Question,

            // A destructive action defaults to No, so that dismissing the dialog
            // with Enter or Escape cannot delete anything.
            isDestructive ? MessageBoxResult.No : MessageBoxResult.Yes);

        return result == MessageBoxResult.Yes;
    }

    /// <inheritdoc />
    public void ShowInformation(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <inheritdoc />
    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <inheritdoc />
    public string? PickFile(string title, string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        // OpenFolderDialog is the native folder picker, added to WPF in .NET 8.
        // It replaces the old shell-based workarounds and the WinForms
        // FolderBrowserDialog, so no extra dependency is needed.
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Gets the window that should own modal dialogs.
    /// </summary>
    /// <remarks>
    /// Owning the dialog keeps it centred over the app and modal to it. Resolved
    /// on each call rather than injected, because the main window does not exist
    /// when this service is constructed.
    /// </remarks>
    private static Window? Owner =>
        Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
        ?? Application.Current?.MainWindow;
}
