namespace GameLauncher.Desktop.Models;

/// <summary>
/// One recorded play session, from launch to process exit.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Game.PlaytimeSeconds"/> holds the running total, which is what the
/// UI reads. This table holds the individual sessions that produced it, which is
/// what makes the total auditable and lets "last played" and per-session history
/// be reconstructed rather than merely asserted.
/// </para>
/// <para>
/// A row is written when the game launches, with <see cref="EndedAt"/> left
/// null, and completed when the process exits. A row that still has a null
/// <see cref="EndedAt"/> on startup is therefore a session that was interrupted
/// by a crash or power loss; the launcher closes those out rather than letting
/// them accumulate.
/// </para>
/// </remarks>
public sealed class PlaySession
{
    /// <summary>Auto-incrementing primary key. Local to this database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Globally unique identity for this session, as 32 lowercase hexadecimal
    /// characters.
    /// </summary>
    /// <remarks>
    /// The unit of playtime synchronisation. Cumulative totals cannot be merged
    /// across devices — they either double-count or lose one side — whereas a
    /// session keyed on a globally unique value is a distinct fact that has
    /// either been seen before or has not, which makes merging idempotent.
    /// </remarks>
    public string SessionKey { get; set; } = string.Empty;

    /// <summary>
    /// The device that recorded this session, or <see langword="null"/> when the
    /// launcher has not yet registered with a relay.
    /// </summary>
    /// <remarks>
    /// Without it a merge cannot distinguish two genuinely concurrent sessions on
    /// different machines from one session reported twice.
    /// </remarks>
    public string? DeviceId { get; set; }

    /// <summary>
    /// When this session was pushed to a relay, or <see langword="null"/> if it
    /// never has been.
    /// </summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>Identifier of the game that was played.</summary>
    public int GameId { get; set; }

    /// <summary>When the game process was started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// When the game process exited, or <see langword="null"/> while the session
    /// is still in progress.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Duration of the session in seconds, or <see langword="null"/> while it is
    /// still in progress.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived from the timestamps so that a session closed
    /// out after an unclean shutdown can record the duration actually credited,
    /// which may deliberately differ from the raw wall-clock gap.
    /// </remarks>
    public long? DurationSeconds { get; set; }

    /// <summary>Gets a value indicating whether this session is still running.</summary>
    public bool IsInProgress => EndedAt is null;
}
