using System.Windows;
using GameLauncher.Desktop.ViewModels;

namespace GameLauncher.Desktop.Views;

/// <summary>
/// Modal dialog for installing a game from a direct download link.
/// </summary>
public partial class InstallFromUrlWindow : Window
{
    private readonly InstallFromUrlViewModel _viewModel;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="viewModel">Dialog view model supplied by the container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public InstallFromUrlWindow(InstallFromUrlViewModel viewModel)
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

    /// <summary>Detaches event handlers.</summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">Event data.</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Closed -= OnClosed;
    }
}
