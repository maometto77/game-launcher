using GameLauncher.Shared.Enums;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// One entry in the friends list, merged from the local cache and live relay
/// updates.
/// </summary>
public sealed record FriendListEntry
{
    /// <summary>The friend's public code.</summary>
    public required string FriendCode { get; init; }

    /// <summary>The friend's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>State of the relationship.</summary>
    public FriendshipStatus Status { get; init; } = FriendshipStatus.Accepted;

    /// <summary>Whether this is an incoming request awaiting an answer.</summary>
    public bool IsIncomingRequest { get; init; }

    /// <summary>Title the friend is playing, or was last seen playing.</summary>
    public string? CurrentGameTitle { get; init; }

    /// <summary>Whether the friend is online right now.</summary>
    public bool IsOnline { get; init; }

    /// <summary>When the friend was last seen.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>
    /// Whether this entry came from the local cache rather than a live relay
    /// update.
    /// </summary>
    /// <remarks>
    /// Surfaced so the UI can say the data is stale instead of asserting that
    /// everybody is offline. "Last seen two hours ago" and "definitely offline"
    /// are different claims, and only one of them is true when the launcher
    /// cannot reach the relay.
    /// </remarks>
    public bool IsFromCache { get; init; }
}

/// <summary>
/// Maintains the friends list across online and offline periods.
/// </summary>
/// <remarks>
/// Cache-first by design: the list is populated from local storage before any
/// network call is attempted, so the Friends page has content immediately and
/// keeps it if the relay never answers.
/// </remarks>
public interface IFriendsService
{
    /// <summary>Gets the current friends list.</summary>
    IReadOnlyList<FriendListEntry> Friends { get; }

    /// <summary>Gets the relay connection state.</summary>
    RelayConnectionState ConnectionState { get; }

    /// <summary>Raised on the UI thread whenever the list changes.</summary>
    event EventHandler? FriendsChanged;

    /// <summary>Raised on the UI thread whenever the connection state changes.</summary>
    event EventHandler<RelayConnectionStateChanged>? ConnectionStateChanged;

    /// <summary>
    /// Loads the cached list. Does not touch the network.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes when the cached list is available.</returns>
    Task LoadFromCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the authoritative list from the relay and refreshes the cache.
    /// </summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>
    /// <see langword="true"/> when the relay answered; <see langword="false"/>
    /// when it was unreachable and the cached list was kept.
    /// </returns>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a friend request.</summary>
    /// <param name="friendCode">The code to add.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the relay has accepted the request.</returns>
    /// <exception cref="RelayApiException">Offline, or the relay refused.</exception>
    Task SendRequestAsync(string friendCode, CancellationToken cancellationToken = default);

    /// <summary>Accepts or rejects an incoming request.</summary>
    /// <param name="friendCode">Who sent it.</param>
    /// <param name="accept">Whether to accept.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the relay has recorded the answer.</returns>
    /// <exception cref="RelayApiException">Offline, or the relay refused.</exception>
    Task RespondToRequestAsync(
        string friendCode,
        bool accept,
        CancellationToken cancellationToken = default);
}
