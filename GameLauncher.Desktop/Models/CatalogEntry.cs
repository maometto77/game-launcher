namespace GameLauncher.Desktop.Models;

/// <summary>
/// The shared identity of a game <em>title</em>, as opposed to one person's
/// installation of it.
/// </summary>
/// <remarks>
/// <para>
/// This is the anchor every cross-user feature hangs off. Achievements, stats
/// and — in future — presence and cloud sync all reference
/// <see cref="CatalogId"/> rather than a local <c>Game.Id</c>, because a local
/// identifier means nothing to anybody else: two people who own the same game
/// have different rows, different integer keys and different
/// <c>Game.GlobalKey</c> values.
/// </para>
/// <para>
/// An entry begins life <see cref="IsProvisional"/>, with an id minted locally
/// and prefixed <c>local:</c>, so the launcher is fully functional offline and
/// without a relay configured. On first contact with a relay the entry is
/// promoted: the relay either recognises the <see cref="MatchFingerprint"/> and
/// returns the existing catalog id, or assigns a new one. Promotion rewrites the
/// primary key, and every reference follows automatically through
/// <c>ON UPDATE CASCADE</c>.
/// </para>
/// </remarks>
public sealed class CatalogEntry
{
    /// <summary>Prefix marking an id that was minted locally and not yet promoted.</summary>
    public const string ProvisionalPrefix = "local:";

    /// <summary>Value of <see cref="Source"/> for an entry no relay has seen.</summary>
    public const string LocalSource = "local";

    /// <summary>
    /// The shared identity. Server-assigned once promoted, otherwise
    /// <c>local:</c> followed by a locally generated key.
    /// </summary>
    public string CatalogId { get; set; } = string.Empty;

    /// <summary>
    /// Identifies which relay assigned <see cref="CatalogId"/>, or
    /// <see cref="LocalSource"/> while provisional.
    /// </summary>
    /// <remarks>
    /// Recorded because catalog ids are only unique within the authority that
    /// issued them. A user who moves between two self-hosted relays would
    /// otherwise silently merge two unrelated games that happened to be given the
    /// same id.
    /// </remarks>
    public string Source { get; set; } = LocalSource;

    /// <summary>Whether this entry still carries a locally minted identity.</summary>
    public bool IsProvisional { get; set; } = true;

    /// <summary>The title this entry is known by.</summary>
    /// <remarks>
    /// A relay's canonical title wins once promoted, so two users who named the
    /// same game differently still converge on one catalog entry.
    /// </remarks>
    public string CanonicalTitle { get; set; } = string.Empty;

    /// <summary>
    /// The fingerprint this entry was originally created from.
    /// </summary>
    /// <remarks>
    /// Provenance only. Fingerprint lookups go through <see cref="CatalogAlias"/>,
    /// because one title legitimately has several fingerprints and a single
    /// column cannot hold them. Keeping this field as the authoritative lookup as
    /// well would give two sources of truth that could drift apart.
    /// </remarks>
    public string MatchFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The entry this one was merged into, or <see langword="null"/> when this
    /// entry is canonical.
    /// </summary>
    /// <remarks>
    /// An absorbed entry is kept rather than deleted, because a client somewhere
    /// may still hold its id. Set once, when an operator merges two titles;
    /// lookups follow the chain to the surviving entry.
    /// </remarks>
    public string? SupersededByCatalogId { get; set; }

    /// <summary>Gets a value indicating whether this entry has been merged into another.</summary>
    public bool IsSuperseded => !string.IsNullOrEmpty(SupersededByCatalogId);

    /// <summary>When this entry was created locally.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this entry was last modified locally.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When this entry was last reconciled with a relay, or
    /// <see langword="null"/> if it never has been.
    /// </summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>
    /// Gets a value indicating whether the identifier was minted locally.
    /// </summary>
    public bool HasLocalIdentity =>
        CatalogId.StartsWith(ProvisionalPrefix, StringComparison.Ordinal);
}
