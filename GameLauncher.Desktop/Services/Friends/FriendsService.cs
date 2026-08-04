using System.Collections.Concurrent;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Shared.Contracts;
using GameLauncher.Shared.Enums;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Default <see cref="IFriendsService"/>.
/// </summary>
/// <remarks>
/// Holds the merged list in a dictionary keyed on friend code and republishes a
/// sorted snapshot whenever anything changes. Live presence updates mutate a
/// single entry rather than triggering a refetch, which is what keeps a friend
/// launching a game from costing a round trip.
/// </remarks>
public sealed class FriendsService : IFriendsService, IDisposable
{
    private readonly IRelayHubClient _hub;
    private readonly IRelayApiClient _api;
    private readonly IFriendCacheRepository _cache;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<FriendsService> _logger;

    private readonly ConcurrentDictionary<string, FriendListEntry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="hub">Live connection supplying presence and request events.</param>
    /// <param name="api">HTTP client used to fetch the authoritative list.</param>
    /// <param name="cache">Local cache read at startup and refreshed after a fetch.</param>
    /// <param name="dispatcher">Marshals change notifications onto the UI thread.</param>
    /// <param name="logger">Logger for friends diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public FriendsService(
        IRelayHubClient hub,
        IRelayApiClient api,
        IFriendCacheRepository cache,
        IUiDispatcher dispatcher,
        ILogger<FriendsService> logger)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _hub.PresenceChanged += OnPresenceChanged;
        _hub.FriendRequestReceived += OnFriendRequestReceived;
        _hub.FriendRequestResolved += OnFriendRequestResolved;
        _hub.StateChanged += OnConnectionStateChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<FriendListEntry> Friends { get; private set; } = [];

    /// <inheritdoc />
    public RelayConnectionState ConnectionState => _hub.State;

    /// <inheritdoc />
    public event EventHandler? FriendsChanged;

    /// <inheritdoc />
    public event EventHandler<RelayConnectionStateChanged>? ConnectionStateChanged;

    /// <inheritdoc />
    public async Task LoadFromCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = await _cache.GetAllAsync(cancellationToken).ConfigureAwait(false);

            _entries.Clear();

            foreach (var friend in cached)
            {
                _entries[friend.FriendCode] = new FriendListEntry
                {
                    FriendCode = friend.FriendCode,
                    DisplayName = friend.DisplayName,
                    Status = FriendshipStatus.Accepted,
                    CurrentGameTitle = friend.LastKnownGame,

                    // Never claimed online from cache. The launcher has no
                    // evidence anyone is online until the relay says so.
                    IsOnline = false,
                    LastSeenAt = friend.LastSeenAt,
                    IsFromCache = true
                };
            }

            Publish();
            _logger.LogDebug("Loaded {Count} friends from the local cache.", cached.Count);
        }
        catch (Exception ex)
        {
            // A failed cache read must not stop the Friends page opening; it just
            // starts empty and fills in when the relay answers.
            _logger.LogError(ex, "Loading the friend cache failed.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_api.IsConfigured)
        {
            return false;
        }

        try
        {
            var response = await _api.GetFriendsAsync(cancellationToken).ConfigureAwait(false);

            _entries.Clear();

            foreach (var friend in response.Friends)
            {
                _entries[friend.FriendCode] = new FriendListEntry
                {
                    FriendCode = friend.FriendCode,
                    DisplayName = friend.DisplayName,
                    Status = friend.Status,
                    IsIncomingRequest = friend.IsIncomingRequest,
                    CurrentGameTitle = friend.CurrentGameTitle,
                    IsOnline = friend.IsOnline,
                    LastSeenAt = friend.LastSeenAt,
                    IsFromCache = false
                };
            }

            Publish();
            await WriteCacheAsync(response.Friends, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Refreshed {Count} friends from the relay.", response.Friends.Count);
            return true;
        }
        catch (RelayApiException ex)
        {
            // Expected whenever the relay is down. The cached list stays on screen.
            _logger.LogDebug(ex, "Refreshing friends from the relay failed; keeping the cache.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Refreshing friends failed unexpectedly.");
            return false;
        }
    }

    /// <inheritdoc />
    public Task SendRequestAsync(string friendCode, CancellationToken cancellationToken = default) =>
        _hub.SendFriendRequestAsync(FriendCodeContract.Normalize(friendCode), cancellationToken);

    /// <inheritdoc />
    public async Task RespondToRequestAsync(
        string friendCode,
        bool accept,
        CancellationToken cancellationToken = default)
    {
        await _hub.RespondFriendRequestAsync(
            FriendCodeContract.Normalize(friendCode), accept, cancellationToken).ConfigureAwait(false);

        // Refetched rather than patched locally: accepting changes both the
        // relationship and the presence the caller is now entitled to see, and the
        // relay is the only place that knows both.
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a live presence update to a single entry.</summary>
    /// <param name="sender">The hub client.</param>
    /// <param name="presence">The friend's new presence.</param>
    private void OnPresenceChanged(object? sender, PresenceDto presence)
    {
        _entries.AddOrUpdate(
            presence.FriendCode,
            _ => new FriendListEntry
            {
                FriendCode = presence.FriendCode,
                DisplayName = presence.DisplayName,
                Status = FriendshipStatus.Accepted,
                CurrentGameTitle = presence.CurrentGameTitle,
                IsOnline = presence.IsOnline,
                LastSeenAt = presence.LastSeenAt,
                IsFromCache = false
            },
            (_, existing) => existing with
            {
                DisplayName = presence.DisplayName,
                CurrentGameTitle = presence.CurrentGameTitle,
                IsOnline = presence.IsOnline,
                LastSeenAt = presence.LastSeenAt,
                IsFromCache = false
            });

        Publish();
        _ = PersistPresenceAsync(presence);
    }

    /// <summary>Adds an incoming request to the list as it arrives.</summary>
    /// <param name="sender">The hub client.</param>
    /// <param name="request">The incoming request.</param>
    private void OnFriendRequestReceived(object? sender, FriendRequestDto request)
    {
        _entries[request.FromFriendCode] = new FriendListEntry
        {
            FriendCode = request.FromFriendCode,
            DisplayName = request.FromDisplayName,
            Status = FriendshipStatus.Pending,
            IsIncomingRequest = true,
            LastSeenAt = request.SentAt,
            IsFromCache = false
        };

        Publish();
    }

    /// <summary>Reflects the answer to a request this user sent.</summary>
    /// <param name="sender">The hub client.</param>
    /// <param name="result">The outcome.</param>
    private void OnFriendRequestResolved(object? sender, FriendRequestResultDto result)
    {
        if (result.Accepted)
        {
            _entries[result.FriendCode] = new FriendListEntry
            {
                FriendCode = result.FriendCode,
                DisplayName = result.DisplayName,
                Status = FriendshipStatus.Accepted,
                LastSeenAt = result.RespondedAt,
                IsFromCache = false
            };
        }
        else
        {
            // Rejection removes the row entirely, matching the relay, which
            // deletes rather than recording a refusal.
            _entries.TryRemove(result.FriendCode, out _);
        }

        Publish();
    }

    /// <summary>Republishes connection state, and refreshes on reconnect.</summary>
    /// <param name="sender">The hub client.</param>
    /// <param name="change">The new state.</param>
    private void OnConnectionStateChanged(object? sender, RelayConnectionStateChanged change)
    {
        ConnectionStateChanged?.Invoke(this, change);

        if (change.State == RelayConnectionState.Connected)
        {
            // Presence changes that happened while disconnected were never sent to
            // this client, so a reconnect needs a full fetch to catch up.
            _ = RefreshAsync(CancellationToken.None);
        }
        else if (change.State is RelayConnectionState.Offline or RelayConnectionState.Reconnecting)
        {
            MarkEverybodyOffline();
        }
    }

    /// <summary>
    /// Clears live presence when the connection is lost.
    /// </summary>
    /// <remarks>
    /// Without this the list would keep showing friends as online and in-game
    /// indefinitely after the connection dropped — a confident claim the launcher
    /// has no basis for.
    /// </remarks>
    private void MarkEverybodyOffline()
    {
        var changed = false;

        foreach (var (code, entry) in _entries)
        {
            if (!entry.IsOnline && entry.IsFromCache)
            {
                continue;
            }

            _entries[code] = entry with { IsOnline = false, IsFromCache = true };
            changed = true;
        }

        if (changed)
        {
            Publish();
        }
    }

    /// <summary>Writes the accepted friends to the local cache.</summary>
    /// <param name="friends">The authoritative list.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteCacheAsync(
        IReadOnlyList<FriendDto> friends,
        CancellationToken cancellationToken)
    {
        try
        {
            // Only accepted friends are cached. A pending request is transient
            // two-party state, and showing one from cache after it was answered
            // elsewhere would be worse than not showing it at all.
            var accepted = friends
                .Where(friend => friend.Status == FriendshipStatus.Accepted)
                .Select(friend => new FriendCache
                {
                    FriendCode = friend.FriendCode,
                    DisplayName = friend.DisplayName,
                    LastKnownGame = friend.CurrentGameTitle,
                    LastSeenAt = friend.LastSeenAt
                })
                .ToArray();

            await _cache.ReplaceAllAsync(accepted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writing the friend cache failed.");
        }
    }

    /// <summary>Keeps the cache current as presence arrives.</summary>
    /// <param name="presence">The presence to record.</param>
    private async Task PersistPresenceAsync(PresenceDto presence)
    {
        try
        {
            await _cache.UpsertAsync(new FriendCache
            {
                FriendCode = presence.FriendCode,
                DisplayName = presence.DisplayName,
                LastKnownGame = presence.CurrentGameTitle,
                LastSeenAt = presence.LastSeenAt
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Caching presence for {FriendCode} failed.", presence.FriendCode);
        }
    }

    /// <summary>Publishes a sorted snapshot and notifies listeners.</summary>
    private void Publish()
    {
        Friends = _entries.Values
            // Actionable first: incoming requests, then who is online, then the
            // rest alphabetically.
            .OrderByDescending(entry => entry.IsIncomingRequest)
            .ThenByDescending(entry => entry.IsOnline)
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _dispatcher.Invoke(() => FriendsChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Detaches from the hub client.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _hub.PresenceChanged -= OnPresenceChanged;
        _hub.FriendRequestReceived -= OnFriendRequestReceived;
        _hub.FriendRequestResolved -= OnFriendRequestResolved;
        _hub.StateChanged -= OnConnectionStateChanged;
    }
}
