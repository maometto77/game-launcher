namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Response body for <c>GET /friends</c>.
/// </summary>
/// <remarks>
/// Wrapped in an object rather than returned as a bare array so the contract
/// can grow additional top-level fields later without breaking existing
/// clients.
/// </remarks>
public sealed record FriendListResponse
{
    /// <summary>
    /// Accepted friends and outstanding requests in both directions, each with
    /// the relay's last known presence.
    /// </summary>
    public IReadOnlyList<FriendDto> Friends { get; init; } = Array.Empty<FriendDto>();
}
