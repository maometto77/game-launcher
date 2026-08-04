using System.IO;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Resolves every on-disk location the launcher writes to.
/// </summary>
/// <remarks>
/// <para>
/// All writable state lives under
/// <c>%LOCALAPPDATA%\GameLauncher</c>. Nothing is written next to the
/// executable, so the app works when installed to Program Files without needing
/// elevation, and a roaming profile is not burdened with an artwork cache or a
/// database.
/// </para>
/// <para>
/// Every property returns an absolute path and every directory is created on
/// first use by <see cref="EnsureCreated"/>, so consumers never have to
/// defensively create folders.
/// </para>
/// </remarks>
public interface IAppPaths
{
    /// <summary>Root folder holding all launcher state.</summary>
    string RootDirectory { get; }

    /// <summary>Full path to the SQLite database file.</summary>
    string DatabaseFile { get; }

    /// <summary>Folder holding cached and extracted cover/hero artwork.</summary>
    string ArtworkDirectory { get; }

    /// <summary>Folder holding achievement icons.</summary>
    string AchievementIconDirectory { get; }

    /// <summary>Folder holding cached friend avatars.</summary>
    string AvatarDirectory { get; }

    /// <summary>Folder holding rolling log files.</summary>
    string LogDirectory { get; }

    /// <summary>Folder used for in-progress downloads before they are extracted.</summary>
    string DownloadDirectory { get; }

    /// <summary>Default parent folder into which downloaded games are installed.</summary>
    string DefaultInstallDirectory { get; }

    /// <summary>Full path to the user settings file.</summary>
    string SettingsFile { get; }

    /// <summary>
    /// Creates every directory this instance describes if it does not already
    /// exist.
    /// </summary>
    /// <exception cref="IOException">A directory could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The process lacks permission to create a directory.</exception>
    void EnsureCreated();
}

/// <summary>
/// Default <see cref="IAppPaths"/> implementation rooted at
/// <c>%LOCALAPPDATA%\GameLauncher</c>.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    private const string AppFolderName = "GameLauncher";

    /// <summary>
    /// Initialises a new instance rooted at the per-user local application data
    /// folder.
    /// </summary>
    public AppPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName))
    {
    }

    /// <summary>
    /// Initialises a new instance rooted at an explicit folder.
    /// </summary>
    /// <param name="rootDirectory">Absolute path to use as the root.</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is null, blank, or not absolute.</exception>
    /// <remarks>
    /// Tests use this overload to redirect all state into a temporary folder,
    /// which is why the root is injectable rather than hard-coded.
    /// </remarks>
    public AppPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory must be provided.", nameof(rootDirectory));
        }

        if (!Path.IsPathRooted(rootDirectory))
        {
            throw new ArgumentException("Root directory must be an absolute path.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <inheritdoc />
    public string RootDirectory { get; }

    /// <inheritdoc />
    public string DatabaseFile => Path.Combine(RootDirectory, "gamelauncher.db");

    /// <inheritdoc />
    public string ArtworkDirectory => Path.Combine(RootDirectory, "artwork");

    /// <inheritdoc />
    public string AchievementIconDirectory => Path.Combine(RootDirectory, "achievements");

    /// <inheritdoc />
    public string AvatarDirectory => Path.Combine(RootDirectory, "avatars");

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <inheritdoc />
    public string DownloadDirectory => Path.Combine(RootDirectory, "downloads");

    /// <inheritdoc />
    public string DefaultInstallDirectory => Path.Combine(RootDirectory, "games");

    /// <inheritdoc />
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    /// <inheritdoc />
    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     RootDirectory,
                     ArtworkDirectory,
                     AchievementIconDirectory,
                     AvatarDirectory,
                     LogDirectory,
                     DownloadDirectory,
                     DefaultInstallDirectory
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
