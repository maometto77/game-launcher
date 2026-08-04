using System.Windows;
using GameLauncher.Desktop.ViewModels;

namespace GameLauncher.Desktop.Views;

/// <summary>
/// Modal dialog for discovering games by scanning a folder tree.
/// </summary>
public partial class ScanFolderWindow : Window
{
    private readonly ScanFolderViewModel _viewModel;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="viewModel">Dialog view model supplied by the container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public ScanFolderWindow(ScanFolderViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    /// <summary>Applies the view model's requested dialog result and closes.</summary>
    /// <param name="sender">The view model.</param>
    /// <param name="result">The result to report to the caller.</param>
    private void OnCloseRequested(object? sender, bool result) => DialogResult = result;

    /// <summary>Releases the view model's scan resources and detaches handlers.</summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">Event data.</param>
    private async void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Closed -= OnClosed;

        try
        {
            // Cancels a scan still running and unsubscribes from the result rows.
            await _viewModel.OnNavigatedFromAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The window is already gone; there is nowhere useful to report this.
        }
    }
}
