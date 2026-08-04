namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Asks the relay which shared catalog entry a game belongs to.
/// </summary>
/// <remarks>
/// Carries only publisher-supplied metadata. Nothing machine-specific is sent —
/// no install path, no file size — because the fingerprint has to be identical
/// for two people who installed the same game to different drives.
/// </remarks>
public sealed record CatalogResolveRequest
{
    /// <summary>Deterministic fingerprint computed by the client.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The title as the client knows it, used when creating a new entry.</summary>
    public required string Title { get; init; }

    /// <summary>Publisher from the executable's version resource, if any.</summary>
    public string? Company { get; init; }
}

/// <summary>
/// The catalog identity a fingerprint resolves to.
/// </summary>
public sealed record CatalogResolveResponse
{
    /// <summary>
    /// The canonical catalog identity.
    /// </summary>
    /// <remarks>
    /// Always canonical: the relay follows its own merge redirects before
    /// answering, so a client never adopts an identity that has already been
    /// merged into another.
    /// </remarks>
    public required string CatalogId { get; init; }

    /// <summary>The relay's title for the entry.</summary>
    public required string CanonicalTitle { get; init; }

    /// <summary>
    /// Whether this request created the entry rather than matching an existing one.
    /// </summary>
    /// <remarks>
    /// Informational. Catalog creation is open, so a miss legitimately creates an
    /// entry rather than failing.
    /// </remarks>
    public bool WasCreated { get; init; }
}
