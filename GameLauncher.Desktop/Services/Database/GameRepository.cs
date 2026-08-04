using System.Text.Json;
using Dapper;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Dapper-backed <see cref="IGameRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads rely on <see cref="DapperConfiguration.StringListHandler"/> to turn the
/// <c>Tags</c> column into a list, because Dapper selects a handler from the
/// destination property's declared type.
/// </para>
/// <para>
/// Writes serialise tags explicitly instead. Dapper resolves a *parameter* by
/// the value's runtime type, and it treats an array or list as a set to expand
/// into an <c>IN (...)</c> clause — so passing the list straight through would
/// produce broken SQL rather than a stored JSON array. Serialising at the call
/// site keeps that from being a trap for whoever edits these queries next.
/// </para>
/// </remarks>
public sealed class GameRepository : IGameRepository
{
    private const string SelectColumns = """
        SELECT Id, GlobalKey, CatalogId, Title, CoverArtPath, HeroArtPath, ExecutablePath, InstallDir,
               InstallSizeBytes, PlaytimeSeconds, LastPlayedAt, DateAdded, Tags,
               CollectionId, Notes, SourceUrl, UpdatedAt
        FROM   Game
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<GameRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for persistence diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GameRepository(IDbConnectionFactory connectionFactory, ILogger<GameRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Game>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<Game>(
            new CommandDefinition(
                $"{SelectColumns} ORDER BY Title COLLATE NOCASE;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<Game?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Game>(
            new CommandDefinition(
                $"{SelectColumns} WHERE Id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Game?> FindByExecutablePathAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // NOCASE matches Windows path semantics: the same file reached through
        // differently-cased text is the same file.
        return await connection.QueryFirstOrDefaultAsync<Game>(
            new CommandDefinition(
                $"{SelectColumns} WHERE ExecutablePath = @ExecutablePath COLLATE NOCASE;",
                new { ExecutablePath = executablePath },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Game>> GetByCollectionAsync(
        int collectionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<Game>(
            new CommandDefinition(
                $"{SelectColumns} WHERE CollectionId = @CollectionId ORDER BY Title COLLATE NOCASE;",
                new { CollectionId = collectionId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Game>> GetRecentlyPlayedAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<Game>(
            new CommandDefinition(
                $"""
                 {SelectColumns}
                 WHERE  LastPlayedAt IS NOT NULL
                 ORDER  BY LastPlayedAt DESC
                 LIMIT  @Limit;
                 """,
                new { Limit = limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> AddAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Assigned here rather than by a column default so the caller's instance
        // carries the same key that was stored, without a follow-up read.
        if (string.IsNullOrWhiteSpace(game.GlobalKey))
        {
            game.GlobalKey = NewGlobalKey();
        }

        game.UpdatedAt = DateTimeOffset.Now;

        var id = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO Game (GlobalKey, CatalogId, Title, CoverArtPath, HeroArtPath, ExecutablePath,
                                  InstallDir, InstallSizeBytes, PlaytimeSeconds, LastPlayedAt, DateAdded,
                                  Tags, CollectionId, Notes, SourceUrl, UpdatedAt)
                VALUES (@GlobalKey, @CatalogId, @Title, @CoverArtPath, @HeroArtPath, @ExecutablePath,
                        @InstallDir, @InstallSizeBytes, @PlaytimeSeconds, @LastPlayedAt, @DateAdded,
                        @Tags, @CollectionId, @Notes, @SourceUrl, @UpdatedAt);

                SELECT last_insert_rowid();
                """,
                ToParameters(game),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        game.Id = (int)id;
        _logger.LogInformation("Added game {Title} (id {Id}).", game.Title, game.Id);
        return game.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        game.UpdatedAt = DateTimeOffset.Now;

        // GlobalKey is deliberately absent from the SET list: it is identity, and
        // rewriting it would sever the link to any synchronised copy.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE Game
                SET    CatalogId        = @CatalogId,
                       Title            = @Title,
                       CoverArtPath     = @CoverArtPath,
                       HeroArtPath      = @HeroArtPath,
                       ExecutablePath   = @ExecutablePath,
                       InstallDir       = @InstallDir,
                       InstallSizeBytes = @InstallSizeBytes,
                       PlaytimeSeconds  = @PlaytimeSeconds,
                       LastPlayedAt     = @LastPlayedAt,
                       DateAdded        = @DateAdded,
                       Tags             = @Tags,
                       CollectionId     = @CollectionId,
                       Notes            = @Notes,
                       SourceUrl        = @SourceUrl,
                       UpdatedAt        = @UpdatedAt
                WHERE  Id = @Id;
                """,
                ToParameters(game, includeId: true),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM Game WHERE Id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected > 0)
        {
            _logger.LogInformation("Deleted game {Id} from the library.", id);
        }

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> AddPlaytimeAsync(
        int gameId,
        long additionalSeconds,
        DateTimeOffset lastPlayedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalSeconds);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE Game
                SET    PlaytimeSeconds = PlaytimeSeconds + @AdditionalSeconds,
                       LastPlayedAt    = @LastPlayedAt
                WHERE  Id = @GameId;
                """,
                new { GameId = gameId, AdditionalSeconds = additionalSeconds, LastPlayedAt = lastPlayedAt },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<int> AssignCollectionAsync(
        IReadOnlyCollection<int> gameIds,
        int? collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameIds);

        if (gameIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Here the list expansion Dapper performs on an enumerable parameter is
        // exactly what is wanted: @Ids becomes an IN clause.
        return await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE Game SET CollectionId = @CollectionId WHERE Id IN @Ids;",
                new { CollectionId = collectionId, Ids = gameIds },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM Game;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetTotalPlaytimeSecondsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // COALESCE keeps an empty library returning 0 rather than NULL.
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COALESCE(SUM(PlaytimeSeconds), 0) FROM Game;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the parameter object for an insert or update, serialising tags to
    /// JSON text.
    /// </summary>
    /// <param name="game">The game being persisted.</param>
    /// <param name="includeId">Whether to include the identifier, for updates.</param>
    /// <returns>An object whose members match the query's named parameters.</returns>
    private static object ToParameters(Game game, bool includeId = false)
    {
        var parameters = new DynamicParameters();

        if (includeId)
        {
            parameters.Add(nameof(Game.Id), game.Id);
        }

        parameters.Add(nameof(Game.GlobalKey), game.GlobalKey);
        parameters.Add(nameof(Game.CatalogId), game.CatalogId);
        parameters.Add(nameof(Game.UpdatedAt), game.UpdatedAt);
        parameters.Add(nameof(Game.Title), game.Title);
        parameters.Add(nameof(Game.CoverArtPath), game.CoverArtPath);
        parameters.Add(nameof(Game.HeroArtPath), game.HeroArtPath);
        parameters.Add(nameof(Game.ExecutablePath), game.ExecutablePath);
        parameters.Add(nameof(Game.InstallDir), game.InstallDir);
        parameters.Add(nameof(Game.InstallSizeBytes), game.InstallSizeBytes);
        parameters.Add(nameof(Game.PlaytimeSeconds), game.PlaytimeSeconds);
        parameters.Add(nameof(Game.LastPlayedAt), game.LastPlayedAt);
        parameters.Add(nameof(Game.DateAdded), game.DateAdded);
        parameters.Add(nameof(Game.CollectionId), game.CollectionId);
        parameters.Add(nameof(Game.Notes), game.Notes);
        parameters.Add(nameof(Game.SourceUrl), game.SourceUrl);

        // See the class remarks: passed as text, never as a collection.
        parameters.Add(nameof(Game.Tags), JsonSerializer.Serialize(game.Tags ?? Array.Empty<string>()));

        return parameters;
    }

    /// <summary>
    /// Generates a new global key.
    /// </summary>
    /// <returns>32 lowercase hexadecimal characters.</returns>
    /// <remarks>
    /// The <c>"N"</c> format matches byte for byte what the v2 migration's
    /// <c>lower(hex(randomblob(16)))</c> produced when it backfilled existing
    /// rows, so keys generated here and there are indistinguishable.
    /// </remarks>
    internal static string NewGlobalKey() => Guid.NewGuid().ToString("N");
}
