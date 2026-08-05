using System.Windows;
using GameLauncher.Desktop.ViewModels;

namespace GameLauncher.Desktop.Views;

/// <summary>
/// Modal dialog for authoring or editing one achievement definition.
/// </summary>
public partial class AchievementEditorWindow : Window
{
    private readonly AchievementEditorViewModel _viewModel;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="viewModel">Dialog view model supplied by the container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public AchievementEditorWindow(AchievementEditorViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Loads the game and collection lists once the window is on screen.</summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">Event data.</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.OnNavigatedToAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The view model reports load failures through its own error banner;
            // an exception escaping here would reach the dispatcher unhandled.
        }
    }

    /// <summary>Applies the view model's requested dialog result and closes.</summary>
    /// <param name="sender">The view model.</param>
    /// <param name="result">The result to report to the caller.</param>
    private void OnCloseRequested(object? sender, bool result)
    {
        // Assigning DialogResult closes a modally-shown window on its own, so
        // Close() must not also be called or WPF raises for a double close.
        DialogResult = result;
    }

    /// <summary>Detaches event handlers.</summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">Event data.</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
