using Dapper;

namespace GameLauncher.Relay.Data.Repositories;

/// <summary>Persistence for the shared game catalog.</summary>
public interface ICatalogRepository
{
    /// <summary>
    /// Resolves a fingerprint to its canonical catalog entry.
    /// </summary>
    /// <param name="fingerprint">The fingerprint to resolve.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The canonical entry, or <see langword="null"/> when unknown.</returns>
    Task<RelayCatalogEntry?> ResolveByFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a catalog identity to the entry that currently represents it,
    /// following merge redirects.
    /// </summary>
    /// <param name="catalogId">Any catalog identity, current or superseded.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The canonical entry, or <see langword="null"/> when unknown.</returns>
    Task<RelayCatalogEntry?> ResolveCanonicalAsync(
        string catalogId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an entry and binds a fingerprint to it.
    /// </summary>
    /// <param name="entry">The entry to create.</param>
    /// <param name="fingerprint">The fingerprint that produced it.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>
    /// The entry now bound to the fingerprint. When a concurrent request created
    /// it first, that existing entry is returned instead.
    /// </returns>
    /// <remarks>
    /// Catalog creation is open, so two clients adding the same new game at the
    /// same moment is expected rather than exceptional. The alias insert is
    /// conditional and the winner is re-read, so one entry results either way.
    /// </remarks>
    Task<RelayCatalogEntry> CreateAsync(
        RelayCatalogEntry entry,
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Records that a user has a title in their library.</summary>
    /// <param name="friendCode">The user.</param>
    /// <param name="catalogId">The title.</param>
    /// <param name="addedAt">When it was added.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task RecordOwnershipAsync(
        string friendCode,
        string catalogId,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper-backed <see cref="ICatalogRepository"/>.</summary>
public sealed class CatalogRepository : ICatalogRepository
{
    /// <summary>Bound so a redirect cycle fails loudly instead of hanging.</summary>
    private const int MaximumRedirectHops = 16;

    private const string SelectColumns = """
        SELECT CatalogId, CanonicalTitle, Company, SupersededByCatalogId, CreatedAt, UpdatedAt
        FROM   CatalogEntry
        """;

    private readonly IRelayConnectionFactory _connectionFactory;
    private readonly ILogger<CatalogRepository> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for catalog diagnostics.</param>
    public CatalogRepository(IRelayConnectionFactory connectionFactory, ILogger<CatalogRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<RelayCatalogEntry?> ResolveByFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var catalogId = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT CatalogId FROM CatalogAlias WHERE Fingerprint = @Fingerprint;",
            new { Fingerprint = fingerprint }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return string.IsNullOrEmpty(catalogId)
            ? null
            : await ResolveCanonicalAsync(catalogId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RelayCatalogEntry?> ResolveCanonicalAsync(
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var current = await connection.QuerySingleOrDefaultAsync<RelayCatalogEntry>(new CommandDefinition(
            $"{SelectColumns} WHERE CatalogId = @CatalogId;",
            new { CatalogId = catalogId }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        for (var hop = 0; current?.SupersededByCatalogId is { } next && hop < MaximumRedirectHops; hop++)
        {
            current = await connection.QuerySingleOrDefaultAsync<RelayCatalogEntry>(new CommandDefinition(
                $"{SelectColumns} WHERE CatalogId = @CatalogId;",
                new { CatalogId = next }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (current?.SupersededByCatalogId is not null)
        {
            _logger.LogError(
                "Catalog redirect chain from {CatalogId} exceeded {Hops} hops.", catalogId, MaximumRedirectHops);
        }

        return current;
    }

    /// <inheritdoc />
    public async Task<RelayCatalogEntry> CreateAsync(
        RelayCatalogEntry entry,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO CatalogEntry
                    (CatalogId, CanonicalTitle, Company, SupersededByCatalogId, CreatedAt, UpdatedAt)
                VALUES
                    (@CatalogId, @CanonicalTitle, @Company, @SupersededByCatalogId, @CreatedAt, @UpdatedAt);
                """,
                entry, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // DO NOTHING, not DO UPDATE: if another request bound this fingerprint
            // first, theirs stands. Rebinding a fingerprint is a merge decision,
            // never a side effect of a race.
            var bound = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO CatalogAlias (Fingerprint, CatalogId, CreatedAt)
                VALUES (@Fingerprint, @CatalogId, @CreatedAt)
                ON CONFLICT (Fingerprint) DO NOTHING;
                """,
                new { Fingerprint = fingerprint, entry.CatalogId, entry.CreatedAt },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (bound == 0)
            {
                // Lost the race. Roll back our entry and adopt the winner's, so the
                // two clients converge on one identity.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Fingerprint {Fingerprint} was bound concurrently; adopting the existing entry.", fingerprint);

                return await ResolveByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException(
                           "The fingerprint was bound concurrently but could not then be resolved.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Created catalog entry {CatalogId} for {Title}.", entry.CatalogId, entry.CanonicalTitle);

            return entry;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RecordOwnershipAsync(
        string friendCode,
        string catalogId,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO UserLibrary (FriendCode, CatalogId, AddedAt)
            VALUES (@FriendCode, @CatalogId, @AddedAt)
            ON CONFLICT (FriendCode, CatalogId) DO NOTHING;
            """,
            new { FriendCode = friendCode, CatalogId = catalogId, AddedAt = addedAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
