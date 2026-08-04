using GameLauncher.Shared.Contracts;

namespace GameLauncher.Shared.Hubs;

/// <summary>
/// Methods the relay invokes on a connected client.
/// </summary>
/// <remarks>
/// <para>
/// The relay implements its hub as a strongly-typed hub over this interface, so
/// the compiler checks every server-to-client call against this contract. The
/// desktop client subscribes by name using <c>nameof</c> against these members,
/// which keeps both ends bound to the same definition and turns a renamed
/// method into a build error rather than a silent runtime no-op.
/// </para>
/// <para>
/// This interface is a contract only — neither side inherits behaviour from it.
/// </para>
/// </remarks>
public interface IPresenceClient
{
    /// <summary>
    /// Notifies the client that an accepted friend's presence changed.
    /// </summary>
    /// <param name="presence">The friend's new presence.</param>
    /// <returns>A task that completes when the client has received the message.</returns>
    Task PresenceChanged(PresenceDto presence);

    /// <summary>
    /// Notifies the client that somebody has sent them a friend request.
    /// </summary>
    /// <param name="request">Details of the incoming request.</param>
    /// <returns>A task that completes when the client has received the message.</returns>
    Task FriendRequestReceived(FriendRequestDto request);

    /// <summary>
    /// Notifies the client that a friend request they sent has been answered.
    /// </summary>
    /// <param name="result">The outcome, including whether it was accepted.</param>
    /// <returns>A task that completes when the client has received the message.</returns>
    Task FriendRequestResolved(FriendRequestResultDto result);
}
