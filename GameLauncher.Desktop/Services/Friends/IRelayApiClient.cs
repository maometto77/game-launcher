using GameLauncher.Shared.Contracts;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Raised when a relay call fails in a way the caller can act on.
/// </summary>
public sealed class RelayApiException : Exception
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">User-facing description of the failure.</param>
    /// <param name="isTransient">
    /// Whether retrying later is likely to succeed. A network failure or a 5xx is
    /// transient; a rejected token or a malformed request is not.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public RelayApiException(string message, bool isTransient, Exception? innerException = null)
        : base(message, innerException) =>
        IsTransient = isTransient;

    /// <summary>
    /// Gets a value indicating whether the operation is worth retrying.
    /// </summary>
    /// <remarks>
    /// Drives whether the sync queue keeps an item for another attempt or gives
    /// up on it. Retrying a permanently rejected item forever would stall the
    /// queue behind it.
    /// </remarks>
    public bool IsTransient { get; }
}

/// <summary>
/// The relay's HTTP surface, as the launcher sees it.
/// </summary>
/// <remarks>
/// The networking seam for everything that is not real-time. Substituting this
/// is what lets the sync queue be tested without a server, including the
/// failure paths that matter most.
/// </remarks>
public interface IRelayApiClient
{
    /// <summary>Gets a value indicating whether a relay address is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Asks the configured relay to identify itself.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The relay's instance identity and name.</returns>
    /// <exception cref="RelayApiException">The relay could not be reached or answered.</exception>
    /// <remarks>
    /// Anonymous and called before anything else, because which credentials to
    /// present depends on which relay is answering.
    /// </remarks>
    Task<RelayInfo> GetRelayInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers this installation and returns its credentials.
    /// </summary>
    /// <param name="displayName">Name to register under.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The friend code, device token and device identifier.</returns>
    /// <exception cref="RelayApiException">Registration failed.</exception>
    Task<RegisterResponse> RegisterAsync(string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the friend list with the relay's cached presence.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Friends and outstanding requests in both directions.</returns>
    /// <exception cref="RelayApiException">The call failed.</exception>
    Task<FriendListResponse> GetFriendsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a fingerprint to a shared catalog identity, creating one if the
    /// relay does not know it.
    /// </summary>
    /// <param name="request">The fingerprint and title to resolve.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The canonical catalog identity.</returns>
    /// <exception cref="RelayApiException">The call failed.</exception>
    Task<CatalogResolveResponse> ResolveCatalogAsync(
        CatalogResolveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes earned achievements and returns the relay's authoritative view.
    /// </summary>
    /// <param name="request">Unlocks to push, and titles to fetch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the relay accepted and what it holds.</returns>
    /// <exception cref="RelayApiException">The call failed.</exception>
    Task<AchievementSyncResponse> SyncAchievementsAsync(
        AchievementSyncRequest request,
        CancellationToken cancellationToken = default);
}
