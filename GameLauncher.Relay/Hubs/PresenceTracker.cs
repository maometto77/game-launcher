using System.Collections.Concurrent;
using System.Security.Claims;
using GameLauncher.Relay.Security;
using Microsoft.AspNetCore.SignalR;

namespace GameLauncher.Relay.Hubs;

/// <summary>
/// Maps a SignalR connection to the friend code that owns it.
/// </summary>
/// <remarks>
/// Makes <c>Clients.User(friendCode)</c> address the <em>person</em> rather than
/// one connection, so a message reaches every device they have online. That is
/// the whole of what multi-device delivery requires — without it, presence would
/// have to be fanned out per connection and every future feature would have to
/// remember to do so.
/// </remarks>
public sealed class FriendCodeUserIdProvider : IUserIdProvider
{
    /// <inheritdoc />
    public string? GetUserId(HubConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.User?.FindFirstValue(RelayClaims.FriendCode);
    }
}

/// <summary>
/// Tracks how many live connections each user currently has.
/// </summary>
/// <remarks>
/// <para>
/// A user is online while <em>any</em> of their devices is connected, so
/// presence cannot be derived from a single connection's lifetime. This holds
/// the count.
/// </para>
/// <para>
/// Deliberately in-process. That is correct for a single self-hosted instance,
/// which is the deployment target, and wrong the moment the relay runs on more
/// than one node — at which point this needs to move behind a shared store and
/// SignalR needs a backplane. The interface is small precisely so that swap is
/// contained.
/// </para>
/// </remarks>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, int> _connections = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a new connection for a user.
    /// </summary>
    /// <param name="friendCode">The connecting user.</param>
    /// <returns>
    /// <see langword="true"/> when this was the user's first connection, meaning
    /// they have just come online.
    /// </returns>
    public bool Add(string friendCode)
    {
        var updated = _connections.AddOrUpdate(friendCode, 1, (_, existing) => existing + 1);
        return updated == 1;
    }

    /// <summary>
    /// Records that a connection has closed.
    /// </summary>
    /// <param name="friendCode">The disconnecting user.</param>
    /// <returns>
    /// <see langword="true"/> when the user has no connections left, meaning they
    /// have just gone offline.
    /// </returns>
    public bool Remove(string friendCode)
    {
        // A compare-and-swap loop rather than a lock: two devices disconnecting at
        // once must not both conclude they were the last one, or the user would be
        // marked offline twice and their friends notified twice.
        while (_connections.TryGetValue(friendCode, out var current))
        {
            if (current <= 1)
            {
                if (_connections.TryRemove(new KeyValuePair<string, int>(friendCode, current)))
                {
                    return true;
                }

                continue;
            }

            if (_connections.TryUpdate(friendCode, current - 1, current))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Determines whether a user has any live connection.</summary>
    /// <param name="friendCode">The user to test.</param>
    /// <returns><see langword="true"/> when at least one device is connected.</returns>
    public bool IsOnline(string friendCode) => _connections.ContainsKey(friendCode);
}
