namespace GameLauncher.Shared.Contracts;

/// <summary>
/// A single user's presence, broadcast to their accepted friends.
/// </summary>
/// <remarks>
/// Display state is intentionally derived by the client rather than carried on
/// the wire: "Playing {game}" when <see cref="IsOnline"/> is
/// <see langword="true"/> and <see cref="CurrentGameTitle"/> is set, plain
/// "Online" when online with no game, and "Offline" otherwise. Sending a
/// redundant pre-computed state alongside the raw fields would invite the two
/// to disagree.
/// </remarks>
public sealed record PresenceDto
{
    /// <summary>Friend code of the user this presence describes.</summary>
    public required string FriendCode { get; init; }

    /// <summary>Current display name of the user.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Title of the game currently being played, or <see langword="null"/> when
    /// the user is not in a game.
    /// </summary>
    public string? CurrentGameTitle { get; init; }

    /// <summary>Whether the user currently holds a live relay connection.</summary>
    public bool IsOnline { get; init; }

    /// <summary>
    /// When the user was last seen. Updated on disconnect and on heartbeat, so
    /// it stays meaningful for both online and offline users.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; init; }
}
