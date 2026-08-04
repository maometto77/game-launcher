namespace GameLauncher.Desktop.Models;

/// <summary>
/// Maps one fingerprint onto a catalog entry.
/// </summary>
/// <remarks>
/// <para>
/// A single title legitimately produces several fingerprints. A re-release
/// changes the product name; a game shipped with a separate launcher has one
/// fingerprint for the launcher and another for the game itself; two storefronts
/// ship builds with different company strings. Modelling that as many aliases to
/// one entry means those are recognised as the same title without any of them
/// being declared "wrong".
/// </para>
/// <para>
/// This is also what makes merging safe. When an operator decides two entries are
/// one title, the absorbed entry's aliases are repointed at the survivor —
/// nobody's <see cref="CatalogEntry.CatalogId"/> is rewritten, so a client that
/// already synchronised the old identity keeps working.
/// </para>
/// </remarks>
public sealed class CatalogAlias
{
    /// <summary>The fingerprint. Primary key: one fingerprint resolves to exactly one title.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>The catalog entry this fingerprint resolves to.</summary>
    public string CatalogId { get; set; } = string.Empty;

    /// <summary>Which authority recorded this alias.</summary>
    public string Source { get; set; } = CatalogEntry.LocalSource;

    /// <summary>When the alias was recorded.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
