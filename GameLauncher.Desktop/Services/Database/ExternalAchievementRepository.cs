using Dapper;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Stores achievements observed in local achievement files.
/// </summary>
public interface IExternalAchievementRepository
{
    /// <summary>
    /// Reads everything known for one application.
    /// </summary>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Its rows, unlocked first and then by name.</returns>
    Task<IReadOnlyList<ExternalAchievement>> GetForAppAsync(
        int steamAppId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a snapshot of one file, and reports what became unlocked.
    /// </summary>
    /// <param name="entries">Everything the file described.</param>
    /// <param name="observedAt">When it was read.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The rows that were locked before this snapshot and are unlocked now.
    /// </returns>
    /// <remarks>
    /// The return value is the point. A file is re-read whenever it changes and
    /// almost always says the same thing as last time; announcing every unlocked
    /// achievement in it would fire a toast per achievement on every save. Only
    /// the transition is new information, and only the transition is reported.
    /// </remarks>
    Task<IReadOnlyList<ExternalAchievement>> ApplySnapshotAsync(
        IReadOnlyList<ExternalAchievement> entries,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts unlocked achievements for an application.
    /// </summary>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>How many are unlocked, and how many are known.</returns>
    Task<(int Unlocked, int Total)> GetTallyAsync(
        int steamAppId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IExternalAchievementRepository"/>.
/// </summary>
/// <remarks>
/// Writes are an upsert on (source, application, API name), so re-reading a file
/// is idempotent. Progress and unlock state move forward only: a file that
/// regresses — which happens when a save is restored from a backup, or an
/// emulator rewrites a file it has lost track of — must not take an achievement
/// away that the user has already been told they earned.
/// </remarks>
public sealed class ExternalAchievementRepository : IExternalAchievementRepository
{
    private const string SelectColumns = """
        SELECT Id, SourceKey, SteamAppId, ApiName, Kind, IsUnlocked, UnlockedAt,
               CurrentValue, TargetValue, SourcePath, ObservedAt
        FROM   ExternalAchievement
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ExternalAchievementRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies connections.</param>
    /// <param name="logger">Logger for storage diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ExternalAchievementRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<ExternalAchievementRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalAchievement>> GetForAppAsync(
        int steamAppId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ExternalAchievement>(
            new CommandDefinition(
                $"""
                 {SelectColumns}
                 WHERE  SteamAppId = @SteamAppId
                 ORDER  BY IsUnlocked DESC, UnlockedAt DESC, ApiName
                 """,
                new { SteamAppId = steamAppId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalAchievement>> ApplySnapshotAsync(
        IReadOnlyList<ExternalAchievement> entries,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Read the whole application's state once rather than querying per entry:
        // a file describes a few hundred achievements and this runs on every save.
        var existing = (await connection.QueryAsync<ExternalAchievement>(
                new CommandDefinition(
                    $"""
                     {SelectColumns}
                     WHERE  SteamAppId = @SteamAppId
                     """,
                    new { entries[0].SteamAppId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(row => row.Identity, StringComparer.Ordinal);

        var newlyUnlocked = new List<ExternalAchievement>();

        foreach (var entry in entries)
        {
            entry.ObservedAt = observedAt;

            var known = existing.GetValueOrDefault(entry.Identity);

            // Never go backwards. A restored save or a rewritten file must not
            // un-earn something the user has already been shown.
            if (known is not null)
            {
                entry.IsUnlocked |= known.IsUnlocked;
                entry.UnlockedAt ??= known.UnlockedAt;

                if (known.CurrentValue is { } previous && entry.CurrentValue is { } current && previous > current)
                {
                    entry.CurrentValue = previous;
                }

                entry.TargetValue ??= known.TargetValue;
            }

            if (entry.IsUnlocked && known is not { IsUnlocked: true })
            {
                newlyUnlocked.Add(entry);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ExternalAchievement
                    (SourceKey, SteamAppId, ApiName, Kind, IsUnlocked, UnlockedAt,
                     CurrentValue, TargetValue, SourcePath, ObservedAt)
                VALUES
                    (@SourceKey, @SteamAppId, @ApiName, @Kind, @IsUnlocked, @UnlockedAt,
                     @CurrentValue, @TargetValue, @SourcePath, @ObservedAt)
                ON CONFLICT (SourceKey, SteamAppId, ApiName) DO UPDATE SET
                    Kind         = excluded.Kind,
                    IsUnlocked   = excluded.IsUnlocked,
                    UnlockedAt   = excluded.UnlockedAt,
                    CurrentValue = excluded.CurrentValue,
                    TargetValue  = excluded.TargetValue,
                    SourcePath   = excluded.SourcePath,
                    ObservedAt   = excluded.ObservedAt;
                """,
                entry,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (newlyUnlocked.Count > 0)
        {
            _logger.LogInformation(
                "{Count} achievement(s) newly unlocked for app {AppId}.",
                newlyUnlocked.Count, entries[0].SteamAppId);
        }

        return newlyUnlocked;
    }

    /// <inheritdoc />
    public async Task<(int Unlocked, int Total)> GetTallyAsync(
        int steamAppId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleAsync<Tally>(
            new CommandDefinition(
                """
                SELECT CAST(COALESCE(SUM(IsUnlocked), 0) AS INTEGER) AS Unlocked,
                       CAST(COUNT(*) AS INTEGER)                     AS Total
                FROM   ExternalAchievement
                WHERE  SteamAppId = @SteamAppId
                  AND  Kind = 0;
                """,
                new { SteamAppId = steamAppId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (row.Unlocked, row.Total);
    }

    /// <summary>
    /// Row shape for the tally query.
    /// </summary>
    /// <remarks>
    /// A class rather than a positional record because SQLite reports an
    /// aggregate's type as BLOB, which Dapper cannot bind to a record's
    /// constructor — the same reason the catalogue facet query has one.
    /// </remarks>
    private sealed class Tally
    {
        public int Unlocked { get; init; }

        public int Total { get; init; }
    }
}
