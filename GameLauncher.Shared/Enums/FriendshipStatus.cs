namespace GameLauncher.Shared.Enums;

/// <summary>
/// Lifecycle state of a friendship record held by the relay.
/// </summary>
/// <remarks>
/// A friendship is stored as a single directed row created by the requesting
/// user. Presence is only exchanged once the row reaches
/// <see cref="Accepted"/>; a <see cref="Pending"/> row leaks nothing beyond the
/// requester's display name.
/// </remarks>
public enum FriendshipStatus
{
    /// <summary>A request has been sent but the recipient has not answered yet.</summary>
    Pending = 0,

    /// <summary>Both parties agreed to the friendship and presence is shared.</summary>
    Accepted = 1
}
