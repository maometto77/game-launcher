using Dapper;
using GameLauncher.Desktop.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Dapper-backed <see cref="ICollectionRepository"/>.
/// </summary>
public sealed class CollectionRepository : ICollectionRepository
{
    /// <summary>SQLite's extended result code for a unique constraint violation.</summary>
    private const int SqliteConstraintUnique = 2067;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CollectionRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for persistence diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CollectionRepository(IDbConnectionFactory connectionFactory, ILogger<CollectionRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<Collection>(
            new CommandDefinition(
                """
                SELECT Id, Name, SortOrder, DateAdded
                FROM   Collection
                ORDER  BY SortOrder, Name COLLATE NOCASE;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<Collection?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Collection>(
            new CommandDefinition(
                "SELECT Id, Name, SortOrder, DateAdded FROM Collection WHERE Id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> GetGameCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // LEFT JOIN so a collection with no games still appears, with a zero
        // count. An INNER JOIN would silently omit empty collections and the
        // sidebar would appear to lose them.
        //
        // Mapped to a named row type rather than a ValueTuple: Dapper matches
        // tuple elements by position, not by column name, so a change to the
        // SELECT list would rebind the columns silently.
        var rows = await connection.QueryAsync<CollectionGameCountRow>(
            new CommandDefinition(
                """
                SELECT   c.Id AS CollectionId, COUNT(g.Id) AS GameCount
                FROM     Collection c
                LEFT JOIN Game g ON g.CollectionId = c.Id
                GROUP BY c.Id;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(row => row.CollectionId, row => row.GameCount);
    }

    /// <inheritdoc />
    public async Task<int> AddAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var id = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO Collection (Name, SortOrder, DateAdded)
                    VALUES (@Name, @SortOrder, @DateAdded);

                    SELECT last_insert_rowid();
                    """,
                    new { collection.Name, collection.SortOrder, collection.DateAdded },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            collection.Id = (int)id;
            _logger.LogInformation("Created collection {Name} (id {Id}).", collection.Name, collection.Id);
            return collection.Id;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            // Translate the storage-level violation into a domain error the UI
            // can show, rather than leaking SQLite detail up to the view model.
            throw new InvalidOperationException(
                $"A collection named '{collection.Name}' already exists.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE Collection SET Name = @Name, SortOrder = @SortOrder WHERE Id = @Id;",
                    new { collection.Name, collection.SortOrder, collection.Id },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            return affected > 0;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new InvalidOperationException(
                $"A collection named '{collection.Name}' already exists.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Game.CollectionId is declared ON DELETE SET NULL, so the games survive
        // and simply become uncollected.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM Collection WHERE Id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected > 0)
        {
            _logger.LogInformation("Deleted collection {Id}.", id);
        }

        return affected > 0;
    }

    /// <summary>
    /// Row shape for the collection game-count aggregate.
    /// </summary>
    private sealed class CollectionGameCountRow
    {
        /// <summary>Identifier of the collection.</summary>
        public int CollectionId { get; init; }

        /// <summary>Number of games filed under it.</summary>
        public int GameCount { get; init; }
    }
}
