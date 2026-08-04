using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Settings;

/// <summary>
/// Loads and saves user settings.
/// </summary>
/// <remarks>
/// The current settings are cached in memory after the first load, so that
/// consumers can read <see cref="Current"/> synchronously during startup without
/// each one hitting the disk.
/// </remarks>
public interface ISettingsService
{
    /// <summary>Gets the settings currently in effect.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after settings are saved, on the UI thread.</summary>
    event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>
    /// Loads settings from disk, creating a first-run identity if none exists.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The loaded settings.</returns>
    /// <remarks>
    /// A missing or unreadable file yields defaults rather than an error: losing
    /// preferences is an inconvenience, but refusing to start over it would be a
    /// far worse outcome.
    /// </remarks>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists settings and publishes them to listeners.
    /// </summary>
    /// <param name="settings">The settings to store.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generates the identity a client presents to the relay.
/// </summary>
public interface IIdentityGenerator
{
    /// <summary>
    /// Generates a friend code in the canonical <c>GL-XXXXX-XXXXX</c> form.
    /// </summary>
    /// <returns>A new friend code.</returns>
    string NewFriendCode();

    /// <summary>
    /// Suggests a display name for a new installation.
    /// </summary>
    /// <returns>A display name derived from the Windows user name.</returns>
    string SuggestDisplayName();
}
