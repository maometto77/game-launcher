namespace GameLauncher.Shared.Contracts;

/// <summary>
/// A relay's self-description, fetched before anything else.
/// </summary>
/// <remarks>
/// <see cref="RelayId"/> exists because a relay cannot be identified by its
/// address. The same relay moved from a laptop to a VPS keeps its data and its
/// catalog namespace but changes URL; two different relays could be reached
/// through the same URL over time. Comparing addresses gets both cases wrong, so
/// the relay reports an identity of its own instead.
/// </remarks>
public sealed record RelayInfo
{
    /// <summary>
    /// Stable identity of this relay instance, generated once and stored in its
    /// database.
    /// </summary>
    /// <remarks>
    /// Tied to the database rather than the deployment, which is what makes
    /// moving the relay to another host a non-event: the data moves with it, so
    /// the identity does too.
    /// </remarks>
    public required string RelayId { get; init; }

    /// <summary>Operator-facing name, for display in the launcher.</summary>
    public string? Name { get; init; }

    /// <summary>Schema version the relay is running.</summary>
    public int SchemaVersion { get; init; }
}
