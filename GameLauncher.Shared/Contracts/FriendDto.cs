using GameLauncher.Shared.Enums;

namespace GameLauncher.Shared.Contracts;

/// <summary>
/// One entry in a user's friend list, carrying both the relationship state and
/// the relay's last known presence for that person.
/// </summary>
/// <remarks>
/// Pending and accepted friendships are returned by the same endpoint so the
/// client can render outstanding requests without a second round trip. For a
/// <see cref="FriendshipStatus.Pending"/> entry the presence fields are always
/// neutral (offline, no game): presence is not shared until both sides agree.
/// </remarks>
public sealed record FriendDto
{
    /// <summary>The friend's public friend code.</summary>
    public required string FriendCode { get; init; }

    /// <summary>The friend's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>State of the relationship.</summary>
    public FriendshipStatus Status { get; init; }

    /// <summary>
    /// For a <see cref="FriendshipStatus.Pending"/> entry, whether the other
    /// party initiated it and this user owes a response.
    /// </summary>
    /// <remarks>
    /// Distinguishes "they want to be your friend" (actionable — show
    /// accept/reject) from "you asked them" (informational — show as awaiting).
    /// Always <see langword="false"/> once the friendship is accepted.
    /// </remarks>
    public bool IsIncomingRequest { get; init; }

    /// <summary>
    /// Title of the game the friend is playing, or <see langword="null"/> when
    /// they are not in a game or the friendship is still pending.
    /// </summary>
    public string? CurrentGameTitle { get; init; }

    /// <summary>Whether the friend currently holds a live relay connection.</summary>
    public bool IsOnline { get; init; }

    /// <summary>When the friend was last seen by the relay.</summary>
    public DateTimeOffset LastSeenAt { get; init; }
}
