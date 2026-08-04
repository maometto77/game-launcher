namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Uniform error body returned by the relay's HTTP endpoints.
/// </summary>
/// <remarks>
/// <see cref="Error"/> is a stable machine-readable code intended for client
/// branching; <see cref="Detail"/> is free text intended for logs and
/// diagnostics. Client code should never branch on <see cref="Detail"/>.
/// </remarks>
public sealed record ErrorResponse
{
    /// <summary>Stable, machine-readable error code, for example <c>invalid_friend_code</c>.</summary>
    public required string Error { get; init; }

    /// <summary>Optional human-readable elaboration.</summary>
    public string? Detail { get; init; }
}
