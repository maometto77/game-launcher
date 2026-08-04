using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Home landing page.
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly ILogger<HomeViewModel> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public HomeViewModel(ILogger<HomeViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Home page opened.");
        return Task.CompletedTask;
    }
}
