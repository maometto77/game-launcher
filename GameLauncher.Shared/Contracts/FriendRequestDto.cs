namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Pushed to a connected client when somebody sends them a friend request.
/// </summary>
public sealed record FriendRequestDto
{
    /// <summary>Friend code of the user who sent the request.</summary>
    public required string FromFriendCode { get; init; }

    /// <summary>Display name of the user who sent the request.</summary>
    public required string FromDisplayName { get; init; }

    /// <summary>When the request was created.</summary>
    public DateTimeOffset SentAt { get; init; }
}
