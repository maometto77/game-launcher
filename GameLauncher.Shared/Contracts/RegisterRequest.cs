namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Request body for <c>POST /register</c>, issued once on a client's first run.
/// </summary>
/// <remarks>
/// Registration is anonymous: the caller supplies only a display name and the
/// relay mints both the friend code and the auth token. There is no password,
/// because the token is the credential.
/// </remarks>
public sealed record RegisterRequest
{
    /// <summary>
    /// Human-readable name shown to friends.
    /// </summary>
    /// <remarks>
    /// Not unique and not an identifier — two users may share a display name.
    /// The friend code is the identity.
    /// </remarks>
    public required string DisplayName { get; init; }
}
