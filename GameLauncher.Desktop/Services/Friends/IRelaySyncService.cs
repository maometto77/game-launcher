namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// What one synchronisation pass achieved.
/// </summary>
/// <param name="CatalogEntriesPromoted">Provisional catalog identities replaced by assigned ones.</param>
/// <param name="UnlocksPushed">Achievement unlocks accepted by the relay.</param>
/// <param name="Completed">
/// Whether the pass finished. False when the relay became unreachable part way,
/// in which case whatever was not stamped stays queued.
/// </param>
public sealed record SyncResult(int CatalogEntriesPromoted, int UnlocksPushed, bool Completed)
{
    /// <summary>A result representing a pass that had nothing to do.</summary>
    public static SyncResult Nothing { get; } = new(0, 0, true);

    /// <summary>Gets a value indicating whether anything changed.</summary>
    public bool DidWork => CatalogEntriesPromoted > 0 || UnlocksPushed > 0;
}

/// <summary>
/// Drains the launcher's outbound queues to the relay.
/// </summary>
/// <remarks>
/// <para>
/// The queues are not a separate data structure. Each is an indexed predicate
/// over data the launcher already stores — provisional catalog entries,
/// unlocks with no <c>SyncedAt</c> — so nothing is lost if the process is killed
/// mid-pass, and there is no queue file to corrupt or replay.
/// </para>
/// <para>
/// Every operation is idempotent, which is what makes retrying after a failure
/// safe: a push whose response was lost simply pushes again, and the relay's
/// merge rules absorb it.
/// </para>
/// </remarks>
public interface IRelaySyncService
{
    /// <summary>
    /// Runs one synchronisation pass.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass achieved.</returns>
    /// <remarks>
    /// Never throws for an unreachable relay: that is the expected state for an
    /// offline-first launcher, and the caller gets
    /// <see cref="SyncResult.Completed"/> false instead.
    /// </remarks>
    Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
}
