using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Persistence for achievement definitions and their unlocks.
/// </summary>
public interface IAchievementRepository
{
    /// <summary>Gets every achievement definition in the library.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All definitions, game-specific and library-wide.</returns>
    Task<IReadOnlyList<AchievementDefinition>> GetAllDefinitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the definitions belonging to one catalog entry.</summary>
    /// <param name="catalogId">Shared catalog identity of the owning title.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Definitions for that title, excluding library-wide ones.</returns>
    /// <remarks>
    /// Keyed on catalog identity rather than a local game row, so one authored
    /// achievement set applies to every user who owns the title, and so
    /// achievements survive the game being uninstalled.
    /// </remarks>
    Task<IReadOnlyList<AchievementDefinition>> GetDefinitionsForCatalogAsync(
        string catalogId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the library-wide definitions, which belong to no single title.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Definitions whose catalog identity is null.</returns>
    Task<IReadOnlyList<AchievementDefinition>> GetLibraryWideDefinitionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Gets a single definition.</summary>
    /// <param name="id">Identifier of the definition.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The definition, or <see langword="null"/> if no such row exists.</returns>
    Task<AchievementDefinition?> GetDefinitionByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Inserts a definition and returns its new identifier.</summary>
    /// <param name="definition">The definition to insert. Its identifier is ignored.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The identifier assigned to the new row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    Task<int> AddDefinitionAsync(AchievementDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing definition.</summary>
    /// <param name="definition">The definition to update.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    Task<bool> UpdateDefinitionAsync(AchievementDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Deletes a definition and any unlock recorded against it.</summary>
    /// <param name="id">Identifier of the definition to delete.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was deleted; otherwise <see langword="false"/>.</returns>
    Task<bool> DeleteDefinitionAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Gets every recorded unlock.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All unlocks, most recent first.</returns>
    Task<IReadOnlyList<AchievementUnlock>> GetUnlocksAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the identifiers of every unlocked definition.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>A set for cheap membership tests while evaluating rules.</returns>
    Task<IReadOnlySet<int>> GetUnlockedDefinitionIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an unlock, if the achievement is not already unlocked.
    /// </summary>
    /// <param name="definitionId">Identifier of the definition that was earned.</param>
    /// <param name="unlockedAt">When it was earned.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>
    /// <see langword="true"/> when this call performed the unlock;
    /// <see langword="false"/> when it was already unlocked.
    /// </returns>
    /// <remarks>
    /// The return value is what drives the toast: evaluators run repeatedly
    /// against a condition that stays true, so only the transition should notify.
    /// Idempotence is enforced in SQL rather than by a check-then-insert, which
    /// would race the memory poller against the save-file watcher.
    /// </remarks>
    Task<bool> UnlockAsync(
        int definitionId,
        DateTimeOffset unlockedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Gets recorded progress for a set of definitions.</summary>
    /// <param name="definitionIds">Definitions of interest.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Progress keyed by definition identifier, omitting those with none recorded.</returns>
    Task<IReadOnlyDictionary<int, AchievementProgress>> GetProgressAsync(
        IReadOnlyCollection<int> definitionIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records progress towards an achievement, never letting it go backwards.
    /// </summary>
    /// <param name="definitionId">The definition being progressed.</param>
    /// <param name="currentValue">The observed value.</param>
    /// <param name="updatedAt">When it was observed.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> when the stored value increased.</returns>
    /// <remarks>
    /// Monotonic by design. A save file rolled back, or a memory read landing on a
    /// per-run counter that has just reset, must not make a progress bar appear to
    /// lose ground the player never lost.
    /// </remarks>
    Task<bool> RecordProgressAsync(
        int definitionId,
        double currentValue,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the number of achievements unlocked across the library.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The count of unlock rows.</returns>
    Task<int> GetUnlockCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets unlocks that have never been pushed to a relay.
    /// </summary>
    /// <param name="limit">Maximum number to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Pending unlocks, oldest first, with the catalog identity to send them under.</returns>
    /// <remarks>
    /// The outbound queue: an indexed predicate rather than a diff against the
    /// server. Unlocks whose catalog entry is still provisional are excluded,
    /// because the relay would not recognise a <c>local:</c> identity — they
    /// become eligible once their entry is promoted.
    /// </remarks>
    Task<IReadOnlyList<PendingUnlock>> GetUnsyncedUnlocksAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Stamps unlocks as pushed.</summary>
    /// <param name="definitionIds">Definitions whose unlocks were accepted.</param>
    /// <param name="syncedAt">When they were pushed.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of rows stamped.</returns>
    Task<int> MarkUnlocksSyncedAsync(
        IReadOnlyCollection<int> definitionIds,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every unlock's synchronisation watermark, re-queuing them all.
    /// </summary>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of unlocks re-queued.</returns>
    /// <remarks>
    /// Used when the launcher is pointed at a different relay. The new relay has
    /// never seen any of this user's history, so a watermark recorded against the
    /// previous one is meaningless and would silently withhold everything earned
    /// so far. Re-pushing is safe because the merge is earliest-wins and
    /// idempotent.
    /// </remarks>
    Task<int> ResetUnlockSyncStateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An unlock awaiting synchronisation, joined to the identity it must be sent
/// under.
/// </summary>
/// <remarks>
/// Declared with init properties rather than as a positional record on purpose.
/// Dapper matches a positional record's constructor against the raw column types
/// — <c>Int64</c> and <c>String</c> from SQLite — which bypasses the registered
/// <see cref="DateTimeOffset"/> type handler and fails to materialise. Property
/// mapping applies the handler correctly.
/// </remarks>
public sealed record PendingUnlock
{
    /// <summary>Local definition identifier, used to stamp the unlock afterwards.</summary>
    public int DefinitionId { get; init; }

    /// <summary>Shared catalog identity of the owning title.</summary>
    public string CatalogId { get; init; } = string.Empty;

    /// <summary>Stable handle of the achievement.</summary>
    public string ApiName { get; init; } = string.Empty;

    /// <summary>When it was earned.</summary>
    public DateTimeOffset UnlockedAt { get; init; }
}
