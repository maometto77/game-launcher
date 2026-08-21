using System.Data.Common;
using System.Text;
using System.Text.Json;
using Dapper;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Default <see cref="ICatalogListingRepository"/>.
/// </summary>
public sealed class CatalogListingRepository : ICatalogListingRepository
{
    /// <summary>
    /// Character immediately after <c>|</c>, used to bound a match-key range
    /// scan.
    /// </summary>
    /// <remarks>
    /// A range comparison is used rather than <c>LIKE 'key|%'</c> because SQLite
    /// only optimises <c>LIKE</c> into an index scan under conditions that depend
    /// on collation and a compile-time pragma. A range is unconditionally
    /// index-friendly.
    /// </remarks>
    private const char AfterSeparator = '}';

    private const string SelectListingColumns = """
        SELECT l.RowId, l.ListingId, l.Title, l.SortTitle, l.Year, l.DeveloperId, l.PublisherId,
               d.Name AS Developer, p.Name AS Publisher, l.Description, l.SystemRequirements,
               l.MatchKey, l.CoverImageUrl, l.CoverImagePath, l.PrimarySourceKey, l.FieldProvenance,
               l.UserOverride, l.IsDownloadable, l.IsHidden, l.ContentHash, l.CreatedAt, l.UpdatedAt
        FROM   CatalogListing l
        LEFT   JOIN Developer d ON d.Id = l.DeveloperId
        LEFT   JOIN Publisher p ON p.Id = l.PublisherId
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CatalogListingRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for persistence diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CatalogListingRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<CatalogListingRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CatalogListing?> GetAsync(
        string listingId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var listing = await connection.QuerySingleOrDefaultAsync<CatalogListing>(
            new CommandDefinition(
                $"{SelectListingColumns} WHERE l.ListingId = @ListingId;",
                new { ListingId = listingId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (listing is null)
        {
            return null;
        }

        await PopulateCollectionsAsync(connection, [listing], cancellationToken).ConfigureAwait(false);

        return listing;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogListing>> FindByTitleKeyAsync(
        string titleKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(titleKey))
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CatalogListing>(
            new CommandDefinition(
                $"{SelectListingColumns} WHERE l.MatchKey >= @Low AND l.MatchKey < @High;",
                new { Low = titleKey + '|', High = titleKey + AfterSeparator },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAliasAsync(
        string matchKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT ListingId FROM ListingAlias WHERE MatchKey = @MatchKey;",
                new { MatchKey = matchKey },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AddAliasAsync(
        string matchKey,
        string listingId,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // An existing alias is left alone. A key already bound to a listing must
        // not be silently rebound by a later observation; that is a merge, and
        // merges are explicit.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT OR IGNORE INTO ListingAlias (MatchKey, ListingId, Source, CreatedAt)
                VALUES (@MatchKey, @ListingId, @Source, @CreatedAt);
                """,
                new { MatchKey = matchKey, ListingId = listingId, Source = source, CreatedAt = DateTimeOffset.Now },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<CatalogListingPage> QueryAsync(
        CatalogListingQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new DynamicParameters();
        var where = new List<string>();
        var from = new StringBuilder(SelectListingColumns);

        var matchExpression = BuildMatchExpression(query.SearchText);

        if (matchExpression is not null)
        {
            // Joining the index rather than filtering with a subquery keeps the
            // bm25 score available for ordering.
            from.Append("""

                JOIN CatalogListingSearch s ON s.ListingId = l.ListingId AND CatalogListingSearch MATCH @Match
                """);

            parameters.Add("Match", matchExpression);
        }

        if (!query.IncludeHidden)
        {
            where.Add("l.IsHidden = 0");
        }

        if (query.DownloadableOnly)
        {
            where.Add("l.IsDownloadable = 1");
        }

        if (query.YearFrom is { } yearFrom)
        {
            where.Add("l.Year >= @YearFrom");
            parameters.Add("YearFrom", yearFrom);
        }

        if (query.YearTo is { } yearTo)
        {
            where.Add("l.Year <= @YearTo");
            parameters.Add("YearTo", yearTo);
        }

        if (!string.IsNullOrWhiteSpace(query.Developer))
        {
            where.Add("d.Name = @Developer COLLATE NOCASE");
            parameters.Add("Developer", query.Developer);
        }

        if (!string.IsNullOrWhiteSpace(query.Publisher))
        {
            where.Add("p.Name = @Publisher COLLATE NOCASE");
            parameters.Add("Publisher", query.Publisher);
        }

        if (!string.IsNullOrWhiteSpace(query.Genre))
        {
            where.Add("""
                EXISTS (SELECT 1 FROM ListingGenre lg JOIN Genre g ON g.Id = lg.GenreId
                        WHERE lg.ListingId = l.ListingId AND g.Name = @Genre COLLATE NOCASE)
                """);

            parameters.Add("Genre", query.Genre);
        }

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            where.Add("""
                EXISTS (SELECT 1 FROM ListingPlatform lp JOIN Platform pl ON pl.Id = lp.PlatformId
                        WHERE lp.ListingId = l.ListingId AND pl.Name = @Platform COLLATE NOCASE)
                """);

            parameters.Add("Platform", query.Platform);
        }

        var whereClause = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);

        var total = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT(*) FROM ({from}{whereClause});",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        parameters.Add("Skip", query.Skip);
        parameters.Add("Take", query.Take);

        var rows = await connection.QueryAsync<CatalogListing>(
            new CommandDefinition(
                $"{from}{whereClause} ORDER BY {OrderBy(query.Sort, matchExpression is not null)} " +
                "LIMIT @Take OFFSET @Skip;",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var items = rows.AsList();

        await PopulateCollectionsAsync(connection, items, cancellationToken).ConfigureAwait(false);

        return new CatalogListingPage(items, total);
    }

    /// <inheritdoc />
    public async Task<CatalogFacets> GetFacetsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Each of these is a GROUP BY over an indexed join. The same counts over
        // JSON-encoded values would be a full scan and a parse per row, which is
        // the whole reason these entities are normalised.
        var genres = await QueryFacetAsync(
            connection,
            """
            SELECT g.Name AS Name, CAST(COUNT(*) AS INTEGER) AS Total
            FROM   ListingGenre lg
            JOIN   Genre g ON g.Id = lg.GenreId
            JOIN   CatalogListing l ON l.ListingId = lg.ListingId AND l.IsHidden = 0
            GROUP  BY g.Id ORDER BY Total DESC, Name;
            """,
            cancellationToken).ConfigureAwait(false);

        var platforms = await QueryFacetAsync(
            connection,
            """
            SELECT pl.Name AS Name, CAST(COUNT(*) AS INTEGER) AS Total
            FROM   ListingPlatform lp
            JOIN   Platform pl ON pl.Id = lp.PlatformId
            JOIN   CatalogListing l ON l.ListingId = lp.ListingId AND l.IsHidden = 0
            GROUP  BY pl.Id ORDER BY Total DESC, Name;
            """,
            cancellationToken).ConfigureAwait(false);

        var developers = await QueryFacetAsync(
            connection,
            """
            SELECT d.Name AS Name, CAST(COUNT(*) AS INTEGER) AS Total
            FROM   CatalogListing l
            JOIN   Developer d ON d.Id = l.DeveloperId
            WHERE  l.IsHidden = 0
            GROUP  BY d.Id ORDER BY Total DESC, Name;
            """,
            cancellationToken).ConfigureAwait(false);

        var publishers = await QueryFacetAsync(
            connection,
            """
            SELECT p.Name AS Name, CAST(COUNT(*) AS INTEGER) AS Total
            FROM   CatalogListing l
            JOIN   Publisher p ON p.Id = l.PublisherId
            WHERE  l.IsHidden = 0
            GROUP  BY p.Id ORDER BY Total DESC, Name;
            """,
            cancellationToken).ConfigureAwait(false);

        return new CatalogFacets(genres, platforms, developers, publishers);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM CatalogListing;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpsertManyAsync(
        IReadOnlyList<CatalogListing> listings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listings);

        if (listings.Count == 0)
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Resolving lookups repeatedly inside one batch would issue a query per
        // listing per entity; almost every batch repeats the same handful.
        var lookups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var written = 0;

        foreach (var listing in listings)
        {
            if (await UpsertOneAsync(connection, transaction, listing, lookups, cancellationToken)
                    .ConfigureAwait(false))
            {
                written++;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Wrote {Written} of {Total} listings.", written, listings.Count);

        return written;
    }

    /// <inheritdoc />
    public async Task<ListingSourceRecord?> GetSourceAsync(
        string sourceKey,
        string sourceItemId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ListingSourceRecord>(
            new CommandDefinition(
                """
                SELECT SourceKey, SourceItemId, ListingId, SourceUrl, NormalizedJson, RawPayload,
                       SourceUpdatedAt, FetchedAt, SourceContentHash, Rank, LastError
                FROM   ListingSource
                WHERE  SourceKey = @SourceKey AND SourceItemId = @SourceItemId;
                """,
                new { SourceKey = sourceKey, SourceItemId = sourceItemId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertSourceAsync(
        ListingSourceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO ListingSource (SourceKey, SourceItemId, ListingId, SourceUrl, NormalizedJson,
                                           RawPayload, SourceUpdatedAt, FetchedAt, SourceContentHash,
                                           Rank, LastError)
                VALUES (@SourceKey, @SourceItemId, @ListingId, @SourceUrl, @NormalizedJson,
                        @RawPayload, @SourceUpdatedAt, @FetchedAt, @SourceContentHash, @Rank, @LastError)
                ON CONFLICT (SourceKey, SourceItemId) DO UPDATE SET
                    ListingId         = excluded.ListingId,
                    SourceUrl         = excluded.SourceUrl,
                    NormalizedJson    = excluded.NormalizedJson,
                    RawPayload        = excluded.RawPayload,
                    SourceUpdatedAt   = excluded.SourceUpdatedAt,
                    FetchedAt         = excluded.FetchedAt,
                    SourceContentHash = excluded.SourceContentHash,
                    Rank              = excluded.Rank,
                    LastError         = excluded.LastError;
                """,
                record,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceListing>> GetSourceListingsAsync(
        string listingId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT NormalizedJson FROM ListingSource WHERE ListingId = @ListingId ORDER BY Rank, SourceKey;",
                new { ListingId = listingId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var listings = new List<SourceListing>();

        foreach (var json in rows)
        {
            var listing = Deserialize(json);

            if (listing is not null)
            {
                listings.Add(listing);
            }
        }

        return listings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetListingIdsWithSourcesAsync(
        string? sourceKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT DISTINCT ListingId
                FROM   ListingSource
                WHERE  (@SourceKey IS NULL OR SourceKey = @SourceKey);
                """,
                new { SourceKey = sourceKey },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task SetImagePathAsync(
        string listingId,
        string remoteUrl,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE ListingImage SET LocalPath = @LocalPath
                WHERE  ListingId = @ListingId AND RemoteUrl = @RemoteUrl;

                UPDATE CatalogListing SET CoverImagePath = @LocalPath
                WHERE  ListingId = @ListingId AND CoverImageUrl = @RemoteUrl;
                """,
                new { ListingId = listingId, RemoteUrl = remoteUrl, LocalPath = localPath },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GetPinnedImagePathsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT i.LocalPath
                FROM   ListingImage i
                JOIN   Game g ON g.ListingId = i.ListingId
                WHERE  i.LocalPath IS NOT NULL

                UNION

                SELECT l.CoverImagePath
                FROM   CatalogListing l
                JOIN   Game g ON g.ListingId = l.ListingId
                WHERE  l.CoverImagePath IS NOT NULL;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<long> StartRunAsync(
        string sourceKey,
        ImportMode mode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO CatalogImportRun (SourceKey, Mode, StartedAt) VALUES (@SourceKey, @Mode, @StartedAt);
                SELECT last_insert_rowid();
                """,
                new { SourceKey = sourceKey, Mode = (int)mode, StartedAt = DateTimeOffset.Now },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task CheckpointRunAsync(CatalogImportRun run, CancellationToken cancellationToken = default) =>
        SaveRunAsync(run, complete: false, cancellationToken);

    /// <inheritdoc />
    public Task CompleteRunAsync(CatalogImportRun run, CancellationToken cancellationToken = default) =>
        SaveRunAsync(run, complete: true, cancellationToken);

    /// <inheritdoc />
    public async Task<CatalogImportRun?> GetLastRunAsync(
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Search passes are excluded, and that exclusion is load-bearing. A
        // search covers only what its term matched, but it is a completed run
        // over the same source, so treating it as the previous pass would move
        // the incremental watermark to the moment it ran — and the next
        // incremental import would skip everything the search did not happen to
        // match. The rows are still written; they are just not evidence of
        // having covered anything.
        return await connection.QuerySingleOrDefaultAsync<CatalogImportRun>(
            new CommandDefinition(
                """
                SELECT RunId, SourceKey, Mode, StartedAt, CompletedAt, Cursor,
                       ItemsSeen, ItemsChanged, ItemsFailed, ListingsAdded, LastError
                FROM   CatalogImportRun
                WHERE  SourceKey = @SourceKey
                AND    Mode <> @SearchMode
                ORDER  BY StartedAt DESC, RunId DESC
                LIMIT  1;
                """,
                new { SourceKey = sourceKey, SearchMode = (int)ImportMode.Search },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Writes a run's counters, optionally marking it finished.</summary>
    /// <param name="run">The run to save.</param>
    /// <param name="complete">Whether to stamp a completion time.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    private async Task SaveRunAsync(CatalogImportRun run, bool complete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE CatalogImportRun
                SET    Cursor        = @Cursor,
                       ItemsSeen     = @ItemsSeen,
                       ItemsChanged  = @ItemsChanged,
                       ItemsFailed   = @ItemsFailed,
                       ListingsAdded = @ListingsAdded,
                       LastError     = @LastError,
                       CompletedAt   = @CompletedAt
                WHERE  RunId = @RunId;
                """,
                new
                {
                    run.RunId,
                    run.Cursor,
                    run.ItemsSeen,
                    run.ItemsChanged,
                    run.ItemsFailed,
                    run.ListingsAdded,
                    run.LastError,
                    CompletedAt = complete ? DateTimeOffset.Now : (DateTimeOffset?)null
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one listing and everything hanging off it.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="listing">The listing to write.</param>
    /// <param name="lookups">Per-batch cache of resolved lookup identifiers.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> when anything was written.</returns>
    private static async Task<bool> UpsertOneAsync(
        DbConnection connection,
        DbTransaction transaction,
        CatalogListing listing,
        Dictionary<string, int> lookups,
        CancellationToken cancellationToken)
    {
        var existing = await connection.QuerySingleOrDefaultAsync<ExistingListing>(
            new CommandDefinition(
                """
                SELECT ContentHash, CreatedAt, CoverImageUrl, CoverImagePath
                FROM   CatalogListing WHERE ListingId = @ListingId;
                """,
                new { listing.ListingId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Nothing a source can influence has changed, so there is nothing to
        // write. This is what makes a second import pass nearly free.
        if (existing is not null &&
            string.Equals(existing.ContentHash, listing.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTimeOffset.Now;

        listing.CreatedAt = existing?.CreatedAt ?? now;
        listing.UpdatedAt = now;

        // A cached cover survives a metadata refresh as long as it is still the
        // same image. Re-fetching artwork the user already has would be a slow
        // and pointless side effect of updating a description.
        if (existing is not null &&
            string.Equals(existing.CoverImageUrl, listing.CoverImageUrl, StringComparison.Ordinal))
        {
            listing.CoverImagePath = existing.CoverImagePath;
        }

        listing.DeveloperId = await ResolveLookupAsync(
            connection, transaction, "Developer", listing.Developer, lookups, cancellationToken)
            .ConfigureAwait(false);

        listing.PublisherId = await ResolveLookupAsync(
            connection, transaction, "Publisher", listing.Publisher, lookups, cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO CatalogListing (ListingId, Title, SortTitle, Year, DeveloperId, PublisherId,
                                            Description, SystemRequirements, MatchKey, CoverImageUrl,
                                            CoverImagePath, PrimarySourceKey, FieldProvenance, UserOverride,
                                            IsDownloadable, IsHidden, ContentHash, CreatedAt, UpdatedAt)
                VALUES (@ListingId, @Title, @SortTitle, @Year, @DeveloperId, @PublisherId,
                        @Description, @SystemRequirements, @MatchKey, @CoverImageUrl,
                        @CoverImagePath, @PrimarySourceKey, @FieldProvenance, @UserOverride,
                        @IsDownloadable, @IsHidden, @ContentHash, @CreatedAt, @UpdatedAt)
                ON CONFLICT (ListingId) DO UPDATE SET
                    Title              = excluded.Title,
                    SortTitle          = excluded.SortTitle,
                    Year               = excluded.Year,
                    DeveloperId        = excluded.DeveloperId,
                    PublisherId        = excluded.PublisherId,
                    Description        = excluded.Description,
                    SystemRequirements = excluded.SystemRequirements,
                    MatchKey           = excluded.MatchKey,
                    CoverImageUrl      = excluded.CoverImageUrl,
                    CoverImagePath     = excluded.CoverImagePath,
                    PrimarySourceKey   = excluded.PrimarySourceKey,
                    FieldProvenance    = excluded.FieldProvenance,
                    IsDownloadable     = excluded.IsDownloadable,
                    ContentHash        = excluded.ContentHash,
                    UpdatedAt          = excluded.UpdatedAt;
                """,
                listing,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // UserOverride and IsHidden are deliberately absent from the update list:
        // both are the user's, and an import has no business overwriting them.

        await ReplaceFacetsAsync(connection, transaction, listing, lookups, cancellationToken)
            .ConfigureAwait(false);

        await ReplaceDownloadsAsync(connection, transaction, listing, cancellationToken).ConfigureAwait(false);
        await ReplaceImagesAsync(connection, transaction, listing, cancellationToken).ConfigureAwait(false);
        await ReindexAsync(connection, transaction, listing, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Replaces a listing's genre, platform and tag rows.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="listing">The listing being written.</param>
    /// <param name="lookups">Per-batch cache of resolved lookup identifiers.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    private static async Task ReplaceFacetsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CatalogListing listing,
        Dictionary<string, int> lookups,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM ListingGenre    WHERE ListingId = @ListingId;
                DELETE FROM ListingPlatform WHERE ListingId = @ListingId;
                DELETE FROM ListingTag      WHERE ListingId = @ListingId;
                """,
                new { listing.ListingId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var genre in listing.Genres)
        {
            var id = await ResolveLookupAsync(connection, transaction, "Genre", genre, lookups, cancellationToken)
                .ConfigureAwait(false);

            if (id is null)
            {
                continue;
            }

            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT OR IGNORE INTO ListingGenre (ListingId, GenreId) VALUES (@ListingId, @GenreId);",
                    new { listing.ListingId, GenreId = id.Value },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (var platform in listing.Platforms)
        {
            var id = await ResolveLookupAsync(
                connection, transaction, "Platform", platform, lookups, cancellationToken).ConfigureAwait(false);

            if (id is null)
            {
                continue;
            }

            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT OR IGNORE INTO ListingPlatform (ListingId, PlatformId) VALUES (@ListingId, @PlatformId);",
                    new { listing.ListingId, PlatformId = id.Value },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (var tag in listing.Tags)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT OR IGNORE INTO ListingTag (ListingId, Tag) VALUES (@ListingId, @Tag);",
                    new { listing.ListingId, Tag = tag },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Replaces a listing's download rows.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="listing">The listing being written.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    private static async Task ReplaceDownloadsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CatalogListing listing,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM ListingDownload WHERE ListingId = @ListingId;",
                new { listing.ListingId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var download in listing.Downloads)
        {
            download.ListingId = listing.ListingId;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO ListingDownload
                        (ListingId, SourceKey, Url, FileName, SizeBytes, Md5, Sha1, Sha256, Format, Kind, MirrorRank)
                    VALUES (@ListingId, @SourceKey, @Url, @FileName, @SizeBytes, @Md5, @Sha1, @Sha256, @Format,
                            @Kind, @MirrorRank);
                    """,
                    download,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Replaces a listing's image rows, carrying cached paths across.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="listing">The listing being written.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <remarks>
    /// Cached paths are read before the delete and re-applied by address. Without
    /// this, refreshing metadata would silently discard every image already
    /// fetched and make the catalogue re-download them on next display.
    /// </remarks>
    private static async Task ReplaceImagesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CatalogListing listing,
        CancellationToken cancellationToken)
    {
        var cached = (await connection.QueryAsync<CachedImageRow>(
            new CommandDefinition(
                "SELECT RemoteUrl, LocalPath FROM ListingImage WHERE ListingId = @ListingId AND LocalPath IS NOT NULL;",
                new { listing.ListingId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(row => row.RemoteUrl, row => row.LocalPath, StringComparer.OrdinalIgnoreCase);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM ListingImage WHERE ListingId = @ListingId;",
                new { listing.ListingId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var image in listing.Images)
        {
            image.ListingId = listing.ListingId;

            if (image.LocalPath is null && cached.TryGetValue(image.RemoteUrl, out var path))
            {
                image.LocalPath = path;
            }

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO ListingImage
                        (ListingId, SourceKey, Kind, RemoteUrl, LocalPath, Width, Height, SortOrder)
                    VALUES (@ListingId, @SourceKey, @Kind, @RemoteUrl, @LocalPath, @Width, @Height, @SortOrder);
                    """,
                    image,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Rewrites a listing's row in the search index.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="listing">The listing being written.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <remarks>
    /// In the same transaction as the listing itself, so the index can never
    /// disagree with the table it describes.
    /// </remarks>
    private static async Task ReindexAsync(
        DbConnection connection,
        DbTransaction transaction,
        CatalogListing listing,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM CatalogListingSearch WHERE ListingId = @ListingId;

                INSERT INTO CatalogListingSearch (ListingId, Title, Developer, Publisher, Genres, Description)
                VALUES (@ListingId, @Title, @Developer, @Publisher, @Genres, @Description);
                """,
                new
                {
                    listing.ListingId,
                    listing.Title,
                    Developer = listing.Developer ?? string.Empty,
                    Publisher = listing.Publisher ?? string.Empty,
                    Genres = string.Join(' ', listing.Genres),
                    Description = listing.Description ?? string.Empty
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Finds or creates a row in a lookup table.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="table">Lookup table name. Never user input.</param>
    /// <param name="name">The value to resolve.</param>
    /// <param name="cache">Per-batch cache.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The identifier, or <see langword="null"/> when the value is blank.</returns>
    /// <remarks>
    /// The table name is interpolated because a table cannot be parameterised.
    /// Every call site passes a compile-time constant, and no caller may ever
    /// pass anything else.
    /// </remarks>
    private static async Task<int?> ResolveLookupAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string? name,
        Dictionary<string, int> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = table == "Genre" || table == "Platform"
            ? name.Trim().ToLowerInvariant()
            : CompanyNormalizer.Normalize(name);

        if (normalized.Length == 0)
        {
            return null;
        }

        var cacheKey = table + '\u001F' + normalized;

        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var id = await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                $"SELECT Id FROM {table} WHERE NormalizedName = @NormalizedName;",
                new { NormalizedName = normalized },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (id is null)
        {
            id = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    $"""
                     INSERT INTO {table} (Name, NormalizedName) VALUES (@Name, @NormalizedName);
                     SELECT last_insert_rowid();
                     """,
                    new { Name = name.Trim(), NormalizedName = normalized },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        cache[cacheKey] = id.Value;

        return id;
    }

    /// <summary>Loads genres, platforms, tags, downloads and images for a page of listings.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="listings">The listings to populate.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <remarks>
    /// Five queries for the whole page rather than five per listing. A page of
    /// sixty tiles would otherwise cost three hundred round trips.
    /// </remarks>
    private static async Task PopulateCollectionsAsync(
        DbConnection connection,
        IReadOnlyList<CatalogListing> listings,
        CancellationToken cancellationToken)
    {
        if (listings.Count == 0)
        {
            return;
        }

        var ids = listings.Select(listing => listing.ListingId).ToArray();
        var byId = listings.ToDictionary(listing => listing.ListingId, StringComparer.Ordinal);

        var genres = await connection.QueryAsync<ListingValueRow>(
            new CommandDefinition(
                """
                SELECT lg.ListingId, g.Name AS Value FROM ListingGenre lg
                JOIN   Genre g ON g.Id = lg.GenreId
                WHERE  lg.ListingId IN @Ids ORDER BY g.Name;
                """,
                new { Ids = ids },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var platforms = await connection.QueryAsync<ListingValueRow>(
            new CommandDefinition(
                """
                SELECT lp.ListingId, pl.Name AS Value FROM ListingPlatform lp
                JOIN   Platform pl ON pl.Id = lp.PlatformId
                WHERE  lp.ListingId IN @Ids ORDER BY pl.Name;
                """,
                new { Ids = ids },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var tags = await connection.QueryAsync<ListingValueRow>(
            new CommandDefinition(
                "SELECT ListingId, Tag AS Value FROM ListingTag WHERE ListingId IN @Ids ORDER BY Tag;",
                new { Ids = ids },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var sources = await connection.QueryAsync<ListingValueRow>(
            new CommandDefinition(
                """
                SELECT ListingId, SourceKey AS Value FROM ListingSource
                WHERE  ListingId IN @Ids ORDER BY Rank, SourceKey;
                """,
                new { Ids = ids },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var downloads = await connection.QueryAsync<ListingDownload>(
            new CommandDefinition(
                """
                SELECT Id, ListingId, SourceKey, Url, FileName, SizeBytes, Md5, Sha1, Sha256, Format, Kind, MirrorRank
                FROM   ListingDownload WHERE ListingId IN @Ids ORDER BY MirrorRank;
                """,
                new { Ids = ids },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var images = await connection.QueryAsync<ListingImage>(
            new CommandDefinition(
                """
                SELECT Id, ListingId, SourceKey, Kind, RemoteUrl, LocalPath, Width, Height, SortOrder
                FROM   ListingImage WHERE ListingId IN @Ids ORDER BY Kind, SortOrder;
                """,
                new { Ids = ids },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var group in genres.GroupBy(row => row.ListingId, StringComparer.Ordinal))
        {
            byId[group.Key].Genres = group.Select(row => row.Value).ToArray();
        }

        foreach (var group in platforms.GroupBy(row => row.ListingId, StringComparer.Ordinal))
        {
            byId[group.Key].Platforms = group.Select(row => row.Value).ToArray();
        }

        foreach (var group in tags.GroupBy(row => row.ListingId, StringComparer.Ordinal))
        {
            byId[group.Key].Tags = group.Select(row => row.Value).ToArray();
        }

        foreach (var group in sources.GroupBy(row => row.ListingId, StringComparer.Ordinal))
        {
            byId[group.Key].SourceKeys = group.Select(row => row.Value).Distinct(StringComparer.Ordinal).ToArray();
        }

        foreach (var group in downloads.GroupBy(row => row.ListingId, StringComparer.Ordinal))
        {
            byId[group.Key].Downloads = group.ToArray();
        }

        foreach (var group in images.GroupBy(row => row.ListingId, StringComparer.Ordinal))
        {
            byId[group.Key].Images = group.ToArray();
        }
    }

    /// <summary>
    /// Turns user input into a safe FTS5 MATCH expression.
    /// </summary>
    /// <param name="searchText">What the user typed.</param>
    /// <returns>The expression, or <see langword="null"/> when there is nothing to search for.</returns>
    /// <remarks>
    /// FTS5 has its own query syntax, so raw input containing a quote, a caret or
    /// the word <c>NOT</c> is a syntax error rather than a search. Reducing input
    /// to bare tokens and quoting each one makes any input a literal search, and
    /// the trailing star on the final token is what makes type-ahead feel
    /// immediate.
    /// </remarks>
    private static string? BuildMatchExpression(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return null;
        }

        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var character in searchText)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        var expression = new StringBuilder();

        for (var index = 0; index < tokens.Count; index++)
        {
            if (index > 0)
            {
                expression.Append(' ');
            }

            expression.Append('"').Append(tokens[index]).Append('"');

            if (index == tokens.Count - 1)
            {
                expression.Append('*');
            }
        }

        return expression.ToString();
    }

    /// <summary>Builds the ORDER BY clause for a sort option.</summary>
    /// <param name="sort">The requested order.</param>
    /// <param name="searching">Whether a full-text match is part of the query.</param>
    /// <returns>The clause body.</returns>
    private static string OrderBy(CatalogListingSort sort, bool searching) => sort switch
    {
        CatalogListingSort.Title => "l.SortTitle COLLATE NOCASE",
        CatalogListingSort.YearDescending => "l.Year IS NULL, l.Year DESC, l.SortTitle COLLATE NOCASE",
        CatalogListingSort.YearAscending => "l.Year IS NULL, l.Year ASC, l.SortTitle COLLATE NOCASE",
        CatalogListingSort.RecentlyAdded => "l.CreatedAt DESC, l.SortTitle COLLATE NOCASE",

        // Relevance only means something when something was searched for.
        _ => searching
            ? "bm25(CatalogListingSearch, 0.0, 10.0, 3.0, 3.0, 2.0, 1.0), l.SortTitle COLLATE NOCASE"
            : "l.SortTitle COLLATE NOCASE"
    };

    /// <summary>Deserialises a stored observation, tolerating a bad row.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The observation, or <see langword="null"/> when it cannot be read.</returns>
    private SourceListing? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SourceListing>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // One unreadable row must not stop a listing from merging the rest.
            _logger.LogWarning(ex, "A stored source observation could not be read and was skipped.");
            return null;
        }
    }

    /// <summary>
    /// Runs one facet query and projects it onto the public shape.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The facet query.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Facet values, most common first.</returns>
    /// <remarks>
    /// Materialised through a mutable row type rather than straight into the
    /// public record. SQLite reports an aggregate column's type as BLOB, which
    /// makes Dapper's constructor matching fail on a positional record — and it
    /// fails at run time, in a query, rather than at compile time.
    /// </remarks>
    private static async Task<IReadOnlyList<CatalogFacet>> QueryFacetAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<FacetRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => new CatalogFacet(row.Name, (int)row.Total)).ToArray();
    }

    /// <summary>One facet value and its count, as SQLite returns it.</summary>
    private sealed class FacetRow
    {
        /// <summary>The facet value.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>How many listings carry it.</summary>
        public long Total { get; set; }
    }

    /// <summary>One value belonging to a listing, from a join or tag table.</summary>
    /// <remarks>
    /// A named type rather than a tuple because Dapper matches tuple elements by
    /// position, which makes a column reorder a silent data swap instead of a
    /// compile error.
    /// </remarks>
    private sealed class ListingValueRow
    {
        /// <summary>The listing the value belongs to.</summary>
        public string ListingId { get; set; } = string.Empty;

        /// <summary>The value.</summary>
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>An image whose bytes have already been cached locally.</summary>
    private sealed class CachedImageRow
    {
        /// <summary>The image's remote address.</summary>
        public string RemoteUrl { get; set; } = string.Empty;

        /// <summary>Where it was cached.</summary>
        public string? LocalPath { get; set; }
    }

    /// <summary>The parts of an existing row that a write needs to preserve.</summary>
    private sealed class ExistingListing
    {
        /// <summary>Hash of the content last written.</summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>When the listing first appeared.</summary>
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>The cover address last written.</summary>
        public string? CoverImageUrl { get; set; }

        /// <summary>Where the cover was cached.</summary>
        public string? CoverImagePath { get; set; }
    }
}
