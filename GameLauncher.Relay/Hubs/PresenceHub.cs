using GameLauncher.Relay.Data.Repositories;
using GameLauncher.Relay.Security;
using GameLauncher.Shared.Contracts;
using GameLauncher.Shared.Enums;
using GameLauncher.Shared.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameLauncher.Relay.Hubs;

/// <summary>
/// Real-time presence and friend requests.
/// </summary>
/// <remarks>
/// <para>
/// Strongly typed over <see cref="IPresenceClient"/>, the same interface the
/// desktop client subscribes against, so a renamed server-to-client call is a
/// build error rather than a silent no-op at runtime.
/// </para>
/// <para>
/// Every broadcast goes only to <em>accepted</em> friends. A pending request
/// leaks nothing beyond the requester's display name.
/// </para>
/// </remarks>
[Authorize]
public sealed class PresenceHub : Hub<IPresenceClient>
{
    private readonly IUserRepository _users;
    private readonly IFriendshipRepository _friendships;
    private readonly IPresenceRepository _presence;
    private readonly IDeviceRepository _devices;
    private readonly PresenceTracker _tracker;
    private readonly ILogger<PresenceHub> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="users">User lookup.</param>
    /// <param name="friendships">Friendship state.</param>
    /// <param name="presence">Presence persistence.</param>
    /// <param name="devices">Device last-seen stamping.</param>
    /// <param name="tracker">Counts live connections per user.</param>
    /// <param name="logger">Logger for hub diagnostics.</param>
    public PresenceHub(
        IUserRepository users,
        IFriendshipRepository friendships,
        IPresenceRepository presence,
        IDeviceRepository devices,
        PresenceTracker tracker,
        ILogger<PresenceHub> logger)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _friendships = friendships ?? throw new ArgumentNullException(nameof(friendships));
        _presence = presence ?? throw new ArgumentNullException(nameof(presence));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the authenticated caller's friend code.</summary>
    private string FriendCode => Context.User!.GetFriendCode();

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var friendCode = FriendCode;
        var isFirstConnection = _tracker.Add(friendCode);

        if (Context.User?.GetDeviceId() is { } deviceId)
        {
            await _devices.TouchAsync(deviceId, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }

        // Only the transition matters. A second device connecting must not
        // re-announce a user who was already online.
        if (isFirstConnection)
        {
            await PublishPresenceAsync(friendCode, null, null, isOnline: true).ConfigureAwait(false);
        }

        _logger.LogDebug("{FriendCode} connected (first connection: {First}).", friendCode, isFirstConnection);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var friendCode = FriendCode;
        var wasLastConnection = _tracker.Remove(friendCode);

        if (wasLastConnection)
        {
            await _presence.SetOfflineAsync(friendCode, DateTimeOffset.UtcNow).ConfigureAwait(false);
            await NotifyFriendsAsync(friendCode).ConfigureAwait(false);
        }

        _logger.LogDebug("{FriendCode} disconnected (last connection: {Last}).", friendCode, wasLastConnection);

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports what the caller is playing, or clears it.
    /// </summary>
    /// <param name="gameTitle">Title being played, or <see langword="null"/> when none.</param>
    /// <param name="catalogId">Shared catalog identity of that title, when known.</param>
    /// <returns>A task that completes once friends have been notified.</returns>
    public async Task UpdatePresence(string? gameTitle, string? catalogId)
    {
        await PublishPresenceAsync(FriendCode, gameTitle, catalogId, isOnline: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the caller's last-seen time.
    /// </summary>
    /// <returns>A task that completes when the stamp has been written.</returns>
    /// <remarks>
    /// Not a liveness mechanism: SignalR detects a dropped connection on its own.
    /// This only keeps last-seen meaningful for a client that stays connected for
    /// days without playing anything.
    /// </remarks>
    public async Task Heartbeat()
    {
        if (Context.User?.GetDeviceId() is { } deviceId)
        {
            await _devices.TouchAsync(deviceId, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }

        var existing = await _presence.GetAsync(FriendCode).ConfigureAwait(false);

        await _presence.UpsertAsync(new RelayPresence
        {
            FriendCode = FriendCode,
            CurrentGameTitle = existing?.CurrentGameTitle,
            CurrentGameCatalogId = existing?.CurrentGameCatalogId,
            IsOnline = true,
            LastSeenAt = DateTimeOffset.UtcNow
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a friend request.
    /// </summary>
    /// <param name="targetFriendCode">The friend code to add.</param>
    /// <returns>A task that completes once the request has been recorded.</returns>
    /// <exception cref="HubException">
    /// The code is malformed, unknown, the caller's own, or a relationship
    /// already exists.
    /// </exception>
    public async Task SendFriendRequest(string targetFriendCode)
    {
        var target = FriendCodeContract.Normalize(targetFriendCode);
        var caller = FriendCode;

        if (!FriendCodeContract.IsValid(target))
        {
            throw new HubException("That is not a valid friend code.");
        }

        if (string.Equals(target, caller, StringComparison.Ordinal))
        {
            throw new HubException("You cannot add yourself.");
        }

        if (await _users.GetAsync(target).ConfigureAwait(false) is null)
        {
            // Deliberately the same message as an invalid code, so this cannot be
            // used to test which friend codes exist.
            throw new HubException("That is not a valid friend code.");
        }

        if (await _friendships.FindBetweenAsync(caller, target).ConfigureAwait(false) is { } existing)
        {
            throw new HubException(existing.Status == FriendshipStatus.Accepted
                ? "You are already friends."
                : "There is already a pending request between you.");
        }

        var now = DateTimeOffset.UtcNow;

        await _friendships.AddAsync(new RelayFriendship
        {
            UserFriendCode = caller,
            FriendFriendCode = target,
            Status = FriendshipStatus.Pending,
            CreatedAt = now
        }).ConfigureAwait(false);

        var callerUser = await _users.GetAsync(caller).ConfigureAwait(false);

        // Delivered to every device the target has online. If none is, they see it
        // in their friend list on next fetch — the record is already stored.
        await Clients.User(target).FriendRequestReceived(new FriendRequestDto
        {
            FromFriendCode = caller,
            FromDisplayName = callerUser?.DisplayName ?? caller,
            SentAt = now
        }).ConfigureAwait(false);

        _logger.LogInformation("{Caller} sent a friend request to {Target}.", caller, target);
    }

    /// <summary>
    /// Accepts or rejects a pending request addressed to the caller.
    /// </summary>
    /// <param name="requesterFriendCode">Who sent the request.</param>
    /// <param name="accept">Whether to accept it.</param>
    /// <returns>A task that completes once the outcome has been recorded and sent.</returns>
    /// <exception cref="HubException">There is no pending request from that user.</exception>
    public async Task RespondFriendRequest(string requesterFriendCode, bool accept)
    {
        var requester = FriendCodeContract.Normalize(requesterFriendCode);
        var caller = FriendCode;
        var now = DateTimeOffset.UtcNow;

        var existing = await _friendships.FindBetweenAsync(caller, requester).ConfigureAwait(false);

        // The direction check is the authorisation: only the addressee may answer,
        // so a caller cannot accept a request they sent themselves.
        if (existing is null ||
            existing.Status != FriendshipStatus.Pending ||
            !string.Equals(existing.UserFriendCode, requester, StringComparison.Ordinal))
        {
            throw new HubException("There is no pending request from that user.");
        }

        if (accept)
        {
            await _friendships.AcceptAsync(requester, caller, now).ConfigureAwait(false);
        }
        else
        {
            // Deleted rather than recorded as refused: the requester can try again
            // later, and the relay keeps no list of who declined whom.
            await _friendships.RemoveAsync(requester, caller).ConfigureAwait(false);
        }

        var callerUser = await _users.GetAsync(caller).ConfigureAwait(false);

        await Clients.User(requester).FriendRequestResolved(new FriendRequestResultDto
        {
            FriendCode = caller,
            DisplayName = callerUser?.DisplayName ?? caller,
            Accepted = accept,
            RespondedAt = now
        }).ConfigureAwait(false);

        if (accept)
        {
            // Now that they are friends, each needs the other's current presence
            // rather than waiting for the next change.
            await SendCurrentPresenceAsync(caller, requester).ConfigureAwait(false);
            await SendCurrentPresenceAsync(requester, caller).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "{Caller} {Outcome} the friend request from {Requester}.",
            caller, accept ? "accepted" : "rejected", requester);
    }

    /// <summary>Stores presence and broadcasts it to accepted friends.</summary>
    /// <param name="friendCode">Whose presence changed.</param>
    /// <param name="gameTitle">Title being played, if any.</param>
    /// <param name="catalogId">Catalog identity of that title, if known.</param>
    /// <param name="isOnline">Whether the user is online.</param>
    private async Task PublishPresenceAsync(
        string friendCode,
        string? gameTitle,
        string? catalogId,
        bool isOnline)
    {
        await _presence.UpsertAsync(new RelayPresence
        {
            FriendCode = friendCode,
            CurrentGameTitle = gameTitle,
            CurrentGameCatalogId = catalogId,
            IsOnline = isOnline,
            LastSeenAt = DateTimeOffset.UtcNow
        }).ConfigureAwait(false);

        await NotifyFriendsAsync(friendCode).ConfigureAwait(false);
    }

    /// <summary>Sends a user's stored presence to all of their accepted friends.</summary>
    /// <param name="friendCode">Whose presence to send.</param>
    private async Task NotifyFriendsAsync(string friendCode)
    {
        var friends = await _friendships.GetAcceptedFriendCodesAsync(friendCode).ConfigureAwait(false);
        if (friends.Count == 0)
        {
            return;
        }

        var dto = await BuildPresenceAsync(friendCode).ConfigureAwait(false);
        if (dto is null)
        {
            return;
        }

        await Clients.Users(friends).PresenceChanged(dto).ConfigureAwait(false);
    }

    /// <summary>Sends one user's presence to one recipient.</summary>
    /// <param name="subject">Whose presence to send.</param>
    /// <param name="recipient">Who receives it.</param>
    private async Task SendCurrentPresenceAsync(string subject, string recipient)
    {
        var dto = await BuildPresenceAsync(subject).ConfigureAwait(false);
        if (dto is not null)
        {
            await Clients.User(recipient).PresenceChanged(dto).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the wire presence for a user.</summary>
    /// <param name="friendCode">The user.</param>
    /// <returns>The presence, or <see langword="null"/> when the user is unknown.</returns>
    private async Task<PresenceDto?> BuildPresenceAsync(string friendCode)
    {
        var user = await _users.GetAsync(friendCode).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var presence = await _presence.GetAsync(friendCode).ConfigureAwait(false);

        return new PresenceDto
        {
            FriendCode = friendCode,
            DisplayName = user.DisplayName,
            CurrentGameTitle = presence?.CurrentGameTitle,

            // Authoritative rather than the stored flag: the tracker knows whether
            // a connection is live right now, and the stored row may be a moment
            // out of date.
            IsOnline = _tracker.IsOnline(friendCode),
            LastSeenAt = presence?.LastSeenAt ?? user.CreatedAt
        };
    }
}
