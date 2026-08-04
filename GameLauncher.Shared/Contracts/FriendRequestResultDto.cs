namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Pushed to the original requester when their friend request is answered.
/// </summary>
/// <remarks>
/// A rejection is reported to the requester so their UI can stop showing the
/// request as pending. The relay deletes the underlying row on rejection, so
/// this message is the only notice the requester receives.
/// </remarks>
public sealed record FriendRequestResultDto
{
    /// <summary>Friend code of the user who answered the request.</summary>
    public required string FriendCode { get; init; }

    /// <summary>Display name of the user who answered the request.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// <see langword="true"/> when the request was accepted;
    /// <see langword="false"/> when it was rejected.
    /// </summary>
    public bool Accepted { get; init; }

    /// <summary>When the response was recorded.</summary>
    public DateTimeOffset RespondedAt { get; init; }
}
