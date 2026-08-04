namespace GameLauncher.Shared.Contracts;

/// <summary>
/// One earned achievement, as exchanged with the relay.
/// </summary>
/// <remarks>
/// Identified by catalog id and api name rather than by any row identifier.
/// Row ids are local to whichever database produced them, and a catalog merge
/// may delete one; the api name is the stable authored handle and survives both.
/// </remarks>
public sealed record AchievementUnlockDto
{
    /// <summary>Shared catalog identity of the title.</summary>
    public required string CatalogId { get; init; }

    /// <summary>Stable handle of the achievement within that title.</summary>
    public required string ApiName { get; init; }

    /// <summary>When the achievement was earned.</summary>
    public DateTimeOffset UnlockedAt { get; init; }
}

/// <summary>
/// Pushes locally earned achievements to the relay.
/// </summary>
/// <remarks>
/// Safe to retry. Merging takes the earliest unlock time, so replaying a batch
/// whose response was lost cannot move an earned-on date or create a duplicate.
/// </remarks>
public sealed record AchievementSyncRequest
{
    /// <summary>The unlocks to push. May be empty, which makes the call a pure fetch.</summary>
    public IReadOnlyList<AchievementUnlockDto> Unlocks { get; init; } = [];

    /// <summary>
    /// Catalog identities the caller wants the server's view of, beyond those
    /// named in <see cref="Unlocks"/>.
    /// </summary>
    /// <remarks>
    /// Lets a client that has just reinstalled recover its history without having
    /// anything to push.
    /// </remarks>
    public IReadOnlyList<string> IncludeCatalogIds { get; init; } = [];
}

/// <summary>
/// The relay's authoritative view after a sync.
/// </summary>
public sealed record AchievementSyncResponse
{
    /// <summary>How many pushed unlocks were new to the relay.</summary>
    public int Accepted { get; init; }

    /// <summary>
    /// Every unlock the relay holds for the titles involved.
    /// </summary>
    /// <remarks>
    /// Returned so the client can adopt an earlier unlock time recorded by
    /// another device, and recover unlocks it has no local record of.
    /// </remarks>
    public IReadOnlyList<AchievementUnlockDto> Unlocks { get; init; } = [];
}
