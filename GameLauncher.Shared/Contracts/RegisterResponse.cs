namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Response body for a successful <c>POST /register</c> call.
/// </summary>
/// <remarks>
/// The <see cref="AuthToken"/> is returned exactly once, at registration time.
/// The relay stores only a hash of it and cannot reissue or recover it, so the
/// client must persist it locally on receipt.
/// </remarks>
public sealed record RegisterResponse
{
    /// <summary>The newly minted public friend code, in <c>GL-XXXXX-XXXXX</c> form.</summary>
    public required string FriendCode { get; init; }

    /// <summary>
    /// Bearer token authenticating this device. Treat as a secret.
    /// </summary>
    /// <remarks>
    /// The token identifies a <em>device</em>, not the user. A second machine
    /// gets its own token against the same <see cref="FriendCode"/>, so one can
    /// be revoked without disturbing the other.
    /// </remarks>
    public required string AuthToken { get; init; }

    /// <summary>
    /// Identifier of the device record this token belongs to.
    /// </summary>
    /// <remarks>
    /// Not a secret. Lets a client name itself in a future device list without
    /// revealing its token.
    /// </remarks>
    public required string DeviceId { get; init; }
}
