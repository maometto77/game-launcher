using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Default <see cref="ILibraryService"/>.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private readonly IGameRepository _games;
    private readonly ILogger<LibraryService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Game persistence.</param>
    /// <param name="logger">Logger for library diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public LibraryService(IGameRepository games, ILogger<LibraryService> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> SaveNotesAsync(
        int gameId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var game = await _games.GetByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return false;
        }

        // Blank and whitespace-only notes are stored as null, so "has notes"
        // checks throughout the UI need only test for null.
        game.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        return await _games.UpdateAsync(game, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        game.Title = game.Title?.Trim() is { Length: > 0 } title ? title : "Untitled game";
        game.Notes = string.IsNullOrWhiteSpace(game.Notes) ? null : game.Notes.Trim();

        // Normalise tags: trimmed, de-duplicated case-insensitively, blanks dropped.
        game.Tags = game.Tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return await _games.UpdateAsync(game, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UninstallResult> UninstallAsync(
        int gameId,
        bool deleteFiles,
        CancellationToken cancellationToken = default)
    {
        var game = await _games.GetByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return new UninstallResult(EntryRemoved: false, FilesDeleted: false, FileDeletionError: null);
        }

        var filesDeleted = false;
        string? deletionError = null;

        if (deleteFiles)
        {
            (filesDeleted, deletionError) = await Task
                .Run(() => TryDeleteInstallDirectory(game), cancellationToken)
                .ConfigureAwait(false);
        }

        // Removed regardless of the deletion outcome; see the interface remarks.
        var removed = await _games.DeleteAsync(gameId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Uninstalled {Title}: entry removed={Removed}, files deleted={Deleted}.",
            game.Title, removed, filesDeleted);

        return new UninstallResult(removed, filesDeleted, deletionError);
    }

    /// <inheritdoc />
    public Task<long> MeasureDirectorySizeAsync(
        string? directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Task.FromResult(0L);
        }

        return Task.Run(() =>
        {
            long total = 0;

            try
            {
                // Enumerated rather than materialised into an array: an install
                // can hold tens of thousands of files and only the running sum is
                // needed.
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A single unreadable file should not abandon the measurement.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not fully measure {Directory}; reporting {Bytes} bytes.",
                    directory, total);
            }

            return total;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a game's install directory, guarding against paths that would be
    /// catastrophic to remove.
    /// </summary>
    /// <param name="game">The game being uninstalled.</param>
    /// <returns>Whether deletion happened, and a reason when it did not.</returns>
    private (bool Deleted, string? Error) TryDeleteInstallDirectory(Game game)
    {
        var directory = game.InstallDir;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (false, null);
        }

        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);

        // A malformed or partially-filled record could name a drive root or a
        // system folder. Recursively deleting one of those would be
        // unrecoverable, so refuse rather than trust the stored path.
        if (IsProtectedPath(full))
        {
            _logger.LogError(
                "Refused to delete {Directory} for {Title}: it is a drive root or system folder.",
                full, game.Title);

            return (false, $"'{full}' looks like a system folder, so it was left untouched.");
        }

        try
        {
            Directory.Delete(full, recursive: true);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Directory} for {Title}.", full, game.Title);
            return (false, $"The files could not be deleted: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines whether a path is too dangerous to delete recursively.
    /// </summary>
    /// <param name="fullPath">An absolute, normalised path.</param>
    /// <returns><see langword="true"/> when deletion must be refused.</returns>
    private static bool IsProtectedPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);

        // The path is the drive root itself.
        if (string.IsNullOrEmpty(fullPath) ||
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        foreach (var candidate in protectedRoots)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            var normalised = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);

            // Equal to a protected folder is refused. Being *inside* Program Files
            // is not: that is where games legitimately install.
            if (string.Equals(fullPath, normalised, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
