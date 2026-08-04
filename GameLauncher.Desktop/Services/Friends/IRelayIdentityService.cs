namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// What establishing the relay identity produced.
/// </summary>
/// <param name="RelayId">The relay's instance identity, or <see langword="null"/> when unreachable.</param>
/// <param name="IsReady">Whether the launcher now holds usable credentials for it.</param>
/// <param name="RelayChanged">Whether this is a different relay from the one last used.</param>
/// <param name="EntriesMarkedForReResolution">Catalog entries demoted because they came from another relay.</param>
public sealed record RelayIdentityResult(
    string? RelayId,
    bool IsReady,
    bool RelayChanged,
    int EntriesMarkedForReResolution)
{
    /// <summary>A result for a relay that could not be reached.</summary>
    public static RelayIdentityResult Unreachable { get; } = new(null, false, false, 0);
}

/// <summary>
/// Establishes which relay the launcher is talking to, and migrates local state
/// when that changes.
/// </summary>
/// <remarks>
/// <para>
/// The relay is the authority for assigned catalog identities, and an identity
/// only means anything within the relay that issued it. Pointing the launcher at
/// a different relay therefore invalidates every assigned id it holds — silently
/// reusing one would attach this user's achievements to whatever unrelated title
/// happens to occupy that id on the new relay.
/// </para>
/// <para>
/// Detection is by relay-reported identity rather than by address, so moving a
/// relay to a new host is correctly seen as the same relay, and two relays
/// reachable at the same address over time are correctly seen as different ones.
/// </para>
/// </remarks>
public interface IRelayIdentityService
{
    /// <summary>
    /// Identifies the configured relay, selects or creates credentials for it,
    /// and migrates catalog identities if it has changed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was established.</returns>
    /// <remarks>
    /// Offline-safe: an unreachable relay leaves everything untouched and returns
    /// <see cref="RelayIdentityResult.Unreachable"/>. Idempotent: once the active
    /// relay matches what is stored, repeat calls do nothing.
    /// </remarks>
    Task<RelayIdentityResult> EstablishAsync(CancellationToken cancellationToken = default);
}
