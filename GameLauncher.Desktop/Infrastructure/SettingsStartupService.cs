using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Loads user settings and applies the selected theme during host startup.
/// </summary>
/// <remarks>
/// Registered ahead of every other hosted service, because hosted services start
/// in registration order and the theme must be in place before the shell window
/// is constructed. A window built against the default palette cannot be
/// recoloured afterwards: views resolve their brushes with
/// <c>StaticResource</c>, which binds once.
/// </remarks>
public sealed class SettingsStartupService : IHostedService
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ILogger<SettingsStartupService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="settings">Settings persistence.</param>
    /// <param name="theme">Applies the palette.</param>
    /// <param name="logger">Logger for startup diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SettingsStartupService(
        ISettingsService settings,
        IThemeService theme,
        ILogger<SettingsStartupService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);

        _theme.Apply(settings.Theme);

        // The friend code is logged because it is public and identifies this
        // installation in support conversations. The auth token never is.
        _logger.LogInformation(
            "Settings loaded. Friend code {FriendCode}, relay {Relay}.",
            settings.FriendCode,
            settings.HasRelay ? settings.RelayUrl : "not configured");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
