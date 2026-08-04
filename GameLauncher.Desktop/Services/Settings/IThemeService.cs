using System.Windows;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Settings;

/// <summary>
/// Applies the selected colour palette to the application.
/// </summary>
/// <remarks>
/// A theme is a whole-dictionary swap: every palette declares the same key set,
/// and views reference those keys and never a literal colour.
/// </remarks>
public interface IThemeService
{
    /// <summary>Gets the theme currently applied.</summary>
    AppTheme Current { get; }

    /// <summary>
    /// Applies a theme to the application's resources.
    /// </summary>
    /// <param name="theme">The theme to apply.</param>
    /// <remarks>
    /// Takes effect for elements created afterwards. Views bind their colours
    /// with <c>StaticResource</c>, which resolves once when an element is loaded,
    /// so windows already on screen keep the palette they were built with — which
    /// is why the settings page tells the user a theme change applies on restart
    /// rather than appearing to half-work.
    /// </remarks>
    void Apply(AppTheme theme);
}

/// <summary>
/// Default <see cref="IThemeService"/>.
/// </summary>
public sealed class ThemeService : IThemeService
{
    /// <summary>Marks the palette entry among the application's merged dictionaries.</summary>
    private const string PaletteMarker = "Resources/Theme/Palette.";

    private readonly ILogger<ThemeService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for theme diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <inheritdoc />
    public void Apply(AppTheme theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            // No application object outside a running WPF host, such as in a
            // non-UI test. Recording the choice is all that is meaningful.
            Current = theme;
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;

        // Located by source rather than by index: relying on the palette being
        // first would break silently the moment App.xaml's order changed.
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(PaletteMarker, StringComparison.OrdinalIgnoreCase) == true);

        if (existing is null)
        {
            _logger.LogWarning("No palette dictionary was found; the theme was left unchanged.");
            return;
        }

        var replacement = new ResourceDictionary { Source = BuildUri(theme) };

        // Replaced in place so the palette keeps its position ahead of the
        // dictionaries that resolve against it.
        dictionaries[dictionaries.IndexOf(existing)] = replacement;

        Current = theme;
        _logger.LogInformation("Applied the {Theme} theme.", theme);
    }

    /// <summary>Builds the pack URI of a theme's palette dictionary.</summary>
    /// <param name="theme">The theme to locate.</param>
    /// <returns>An absolute pack URI.</returns>
    private static Uri BuildUri(AppTheme theme) => new(
        $"pack://application:,,,/GameLauncher.Desktop;component/Resources/Theme/Palette.{theme}.xaml",
        UriKind.Absolute);
}
