using GameLauncher.Shared.Contracts;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// The launcher's live connection to the relay.
/// </summary>
/// <remarks>
/// <para>
/// The real-time networking seam. Every member is an operation the UI or a
/// service actually needs, so a fake implementation is enough to drive the
/// friends layer in a test without a server.
/// </para>
/// <para>
/// Nothing here blocks on the network for the caller's benefit: starting is
/// fire-and-forget supervision, and every send fails fast when offline rather
/// than queueing at this level. Queueing that matters is the sync service's job.
/// </para>
/// </remarks>
public interface IRelayHubClient : IAsyncDisposable, IDisposable
{
    /// <summary>Gets the current connection state.</summary>
    RelayConnectionState State { get; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    event EventHandler<RelayConnectionStateChanged>? StateChanged;

    /// <summary>Raised on the UI thread when a friend's presence changes.</summary>
    event EventHandler<PresenceDto>? PresenceChanged;

    /// <summary>Raised on the UI thread when somebody sends a friend request.</summary>
    event EventHandler<FriendRequestDto>? FriendRequestReceived;

    /// <summary>Raised on the UI thread when a sent request is answered.</summary>
    event EventHandler<FriendRequestResultDto>? FriendRequestResolved;

    /// <summary>
    /// Begins connecting and keeps the connection alive until stopped.
    /// </summary>
    /// <param name="cancellationToken">Stops the supervisor.</param>
    /// <returns>A task that completes once supervision has started, not once connected.</returns>
    /// <remarks>
    /// Returns immediately. Waiting for a connection would make application
    /// startup depend on the relay being reachable, which is precisely what
    /// offline-first forbids.
    /// </remarks>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the connection and the supervisor.</summary>
    /// <param name="cancellationToken">Cancels the stop.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports what this user is playing.
    /// </summary>
    /// <param name="gameTitle">Title being played, or <see langword="null"/> when none.</param>
    /// <param name="catalogId">Shared catalog identity of that title, if known.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when the update reached the relay.</returns>
    /// <remarks>
    /// Returns false rather than throwing when offline. Presence is ephemeral —
    /// a missed update is superseded by the next one — so there is nothing worth
    /// queueing and nothing worth interrupting the user over.
    /// </remarks>
    Task<bool> UpdatePresenceAsync(
        string? gameTitle,
        string? catalogId,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a friend request.</summary>
    /// <param name="friendCode">The code to add.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the relay has accepted the request.</returns>
    /// <exception cref="RelayApiException">Offline, or the relay refused.</exception>
    Task SendFriendRequestAsync(string friendCode, CancellationToken cancellationToken = default);

    /// <summary>Accepts or rejects a pending request.</summary>
    /// <param name="friendCode">Who sent it.</param>
    /// <param name="accept">Whether to accept.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the relay has recorded the answer.</returns>
    /// <exception cref="RelayApiException">Offline, or the relay refused.</exception>
    Task RespondFriendRequestAsync(
        string friendCode,
        bool accept,
        CancellationToken cancellationToken = default);
}
