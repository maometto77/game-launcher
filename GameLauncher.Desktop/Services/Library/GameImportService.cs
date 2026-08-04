using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Default <see cref="IGameImportService"/>.
/// </summary>
public sealed class GameImportService : IGameImportService
{
    private readonly IGameRepository _games;
    private readonly IExecutableInspector _inspector;
    private readonly IIconExtractionService _icons;
    private readonly ILibraryService _library;
    private readonly ICatalogService _catalog;
    private readonly IAppPaths _paths;
    private readonly ILogger<GameImportService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Game persistence.</param>
    /// <param name="inspector">Reads executable metadata.</param>
    /// <param name="icons">Extracts placeholder cover art.</param>
    /// <param name="library">Used to measure install size.</param>
    /// <param name="catalog">Assigns the shared catalog identity.</param>
    /// <param name="paths">Supplies the artwork directory.</param>
    /// <param name="logger">Logger for import diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GameImportService(
        IGameRepository games,
        IExecutableInspector inspector,
        IIconExtractionService icons,
        ILibraryService library,
        ICatalogService catalog,
        IAppPaths paths,
        ILogger<GameImportService> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GameImportResult> ImportAsync(
        GameImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return new GameImportResult(GameImportStatus.Failed, null, "No executable was supplied.");
        }

        var executablePath = Path.GetFullPath(request.ExecutablePath);

        if (!File.Exists(executablePath))
        {
            return new GameImportResult(
                GameImportStatus.Failed, null, $"The executable no longer exists at {executablePath}.");
        }

        // Checked before any expensive work: importing the same executable twice
        // would give the user two indistinguishable library entries.
        var existing = await _games.FindByExecutablePathAsync(executablePath, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new GameImportResult(
                GameImportStatus.AlreadyInLibrary,
                existing,
                $"{existing.Title} is already in your library.");
        }

        try
        {
            var info = await _inspector.InspectAsync(executablePath, cancellationToken).ConfigureAwait(false);

            var title = !string.IsNullOrWhiteSpace(request.Title)
                ? request.Title.Trim()
                : info.SuggestedTitle;

            var installDirectory = !string.IsNullOrWhiteSpace(request.InstallDirectory)
                ? Path.GetFullPath(request.InstallDirectory)
                : GameScanService.ResolveInstallDirectory(executablePath);

            var coverArtPath = request.ExtractIcon
                ? await _icons
                    .ExtractToPngAsync(executablePath, _paths.ArtworkDirectory, title, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            var installSize = request.MeasureInstallSize
                ? await _library.MeasureDirectorySizeAsync(installDirectory, cancellationToken).ConfigureAwait(false)
                : 0;

            // Assigned before the insert because Game.CatalogId is a foreign key:
            // the entry has to exist first. Matching on fingerprint here means a
            // second copy of an already-known game reuses its identity, and any
            // achievements already authored against that title apply immediately.
            var catalogEntry = await _catalog
                .EnsureEntryAsync(title, info, cancellationToken)
                .ConfigureAwait(false);

            var game = new Game
            {
                CatalogId = catalogEntry.CatalogId,
                Title = title,
                ExecutablePath = executablePath,
                InstallDir = installDirectory,
                InstallSizeBytes = installSize,
                CoverArtPath = coverArtPath,
                HeroArtPath = null,
                PlaytimeSeconds = 0,
                LastPlayedAt = null,
                DateAdded = DateTimeOffset.Now,
                Tags = NormaliseTags(request.Tags),
                CollectionId = request.CollectionId,
                Notes = null,
                SourceUrl = request.SourceUrl
            };

            await _games.AddAsync(game, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Imported {Title} from {Path} ({Platform}, {Bytes} bytes installed, catalog {CatalogId}).",
                game.Title, executablePath, info.PlatformSummary, installSize, catalogEntry.CatalogId);

            return new GameImportResult(GameImportStatus.Added, game, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Importing {Path} failed.", executablePath);
            return new GameImportResult(GameImportStatus.Failed, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameImportResult>> ImportManyAsync(
        IReadOnlyCollection<GameImportRequest> requests,
        IProgress<GameImportResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var results = new List<GameImportResult>(requests.Count);

        // Sequential on purpose. Imports contend on one SQLite writer and on disk
        // for the folder-size walks, so running them in parallel would add
        // contention without shortening the wall clock.
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ImportAsync(request, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(result);
        }

        var added = results.Count(result => result.Status == GameImportStatus.Added);
        _logger.LogInformation("Imported {Added} of {Total} selected games.", added, results.Count);

        return results;
    }

    /// <summary>
    /// Trims, de-duplicates and drops blanks from a tag list.
    /// </summary>
    /// <param name="tags">Tags as supplied by the caller.</param>
    /// <returns>A clean tag set, never null.</returns>
    private static IReadOnlyList<string> NormaliseTags(IReadOnlyList<string>? tags) =>
        tags is null
            ? []
            : tags
                .Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
}
