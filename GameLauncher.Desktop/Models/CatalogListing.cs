using GameLauncher.Desktop.Services.Discovery;

namespace GameLauncher.Desktop.Models;

/// <summary>
/// A game the launcher knows exists, merged from every source that describes it.
/// </summary>
/// <remarks>
/// <para>
/// This is the discovery catalogue, and it is deliberately <em>not</em>
/// <see cref="CatalogEntry"/>. That type is the shared identity of a title the
/// user has installed, minted from a fingerprint of the executable and
/// reconciled with a relay; its primary key is rewritten during promotion and
/// demotion. A listing has no executable to fingerprint, is never synchronised,
/// and its identifier is never rewritten.
/// </para>
/// <para>
/// The two meet exactly once: installing a listing records
/// <c>Game.ListingId</c>, after which the existing import path mints a
/// <see cref="CatalogEntry"/> from the executable that is now on disk. Discovery
/// never participates in identity, and the relay never learns that a listing
/// existed.
/// </para>
/// <para>
/// Every field is derived. The authoritative inputs are the
/// <see cref="ListingSourceRecord"/> rows, and this row can be rebuilt from them
/// at any time without touching the network.
/// </para>
/// </remarks>
public sealed class CatalogListing
{
    /// <summary>Prefix marking a locally minted listing identifier.</summary>
    public const string IdPrefix = "lst_";

    /// <summary>
    /// Auto-incrementing surrogate key.
    /// </summary>
    /// <remarks>
    /// Exists only because FTS5 external-content tables link on an integer
    /// <c>rowid</c> and <see cref="ListingId"/> is text. Nothing else uses it,
    /// and it is never transmitted or displayed.
    /// </remarks>
    public long RowId { get; set; }

    /// <summary>Stable identity, <c>lst_</c> followed by 32 hexadecimal characters.</summary>
    /// <remarks>
    /// Minted locally and never rewritten. Unlike a catalog id there is no
    /// authority to promote it against, which is what makes cascading updates
    /// unnecessary here.
    /// </remarks>
    public string ListingId { get; set; } = string.Empty;

    /// <summary>Display title, in reading order.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Title with any leading article removed, for alphabetical ordering.</summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>Release year, or <see langword="null"/> when no source knew it.</summary>
    public int? Year { get; set; }

    /// <summary>Identifier of the developer, or <see langword="null"/>.</summary>
    public int? DeveloperId { get; set; }

    /// <summary>Developer name, resolved from the lookup table on read.</summary>
    public string? Developer { get; set; }

    /// <summary>Identifier of the publisher, or <see langword="null"/>.</summary>
    public int? PublisherId { get; set; }

    /// <summary>Publisher name, resolved from the lookup table on read.</summary>
    public string? Publisher { get; set; }

    /// <summary>Plain-text description, or <see langword="null"/>.</summary>
    public string? Description { get; set; }

    /// <summary>Free-text system requirements, or <see langword="null"/>.</summary>
    public string? SystemRequirements { get; set; }

    /// <summary>The key used to recognise this game across sources.</summary>
    public string MatchKey { get; set; } = string.Empty;

    /// <summary>Address of the preferred cover image, or <see langword="null"/>.</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>Local path of the cached cover, or <see langword="null"/> until fetched.</summary>
    /// <remarks>
    /// Null is the normal state for most of the catalogue. Images are fetched
    /// when something displays them, not during import.
    /// </remarks>
    public string? CoverImagePath { get; set; }

    /// <summary>Dispatch key of the source that contributed the most fields.</summary>
    public string PrimarySourceKey { get; set; } = string.Empty;

    /// <summary>
    /// Which source each merged field came from, as a JSON object.
    /// </summary>
    /// <remarks>
    /// Read by a human diagnosing a merge rule, never joined or filtered, which
    /// is why it is one column rather than a table. See
    /// <see cref="Services.Discovery.Matching.MergeTraceEntry"/> for the deeper
    /// layer that records the candidates each rule chose between.
    /// </remarks>
    public string? FieldProvenance { get; set; }

    /// <summary>
    /// Fields the user has corrected by hand, as a JSON object, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Applied after the merge rather than inside it, so the merge stays a pure
    /// function of the source rows and a re-import cannot discard a correction.
    /// </remarks>
    public string? UserOverride { get; set; }

    /// <summary>Whether any source offers a downloadable file.</summary>
    public bool IsDownloadable { get; set; } = true;

    /// <summary>Whether the user has hidden this listing from the catalogue.</summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// Hash of the merged content, used to skip writes that would change nothing.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>When the listing first appeared.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the listing last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Genres, populated by queries that ask for them.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Platforms, populated by queries that ask for them.</summary>
    public IReadOnlyList<string> Platforms { get; set; } = [];

    /// <summary>Free-form tags, populated by queries that ask for them.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>
    /// Which sources describe this game, populated by queries that ask for them.
    /// </summary>
    /// <remarks>
    /// Read from the observation rows rather than inferred from
    /// <see cref="PrimarySourceKey"/>, which names only the source that won the
    /// most fields. A card showing where a game can be found has to show all of
    /// them, not the winner.
    /// </remarks>
    public IReadOnlyList<string> SourceKeys { get; set; } = [];

    /// <summary>Downloadable files, populated by queries that ask for them.</summary>
    public IReadOnlyList<ListingDownload> Downloads { get; set; } = [];

    /// <summary>Images, populated by queries that ask for them.</summary>
    public IReadOnlyList<ListingImage> Images { get; set; } = [];
}

/// <summary>
/// One downloadable file offered for a listing.
/// </summary>
/// <remarks>
/// Several rows for one listing are the normal case, not an error: the same file
/// is served by more than one mirror, and different sources offer different
/// files. Mirrors are unioned across sources and never replace one another.
/// </remarks>
public sealed class ListingDownload
{
    /// <summary>Auto-incrementing primary key.</summary>
    public long Id { get; set; }

    /// <summary>Listing this file belongs to.</summary>
    public string ListingId { get; set; } = string.Empty;

    /// <summary>Source that reported it.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Direct address of the file.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>File name, or <see langword="null"/> to derive one.</summary>
    public string? FileName { get; set; }

    /// <summary>Size in bytes, or <see langword="null"/> when unknown.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>MD5 digest as hex, or <see langword="null"/>.</summary>
    public string? Md5 { get; set; }

    /// <summary>SHA-1 digest as hex, or <see langword="null"/>.</summary>
    public string? Sha1 { get; set; }

    /// <summary>Format label, such as <c>ZIP</c>.</summary>
    public string? Format { get; set; }

    /// <summary>What the file contains.</summary>
    public DownloadKind Kind { get; set; }

    /// <summary>Preference order among mirrors; lower is tried first.</summary>
    public int MirrorRank { get; set; }

    /// <summary>
    /// Gets the strongest digest available, ready for
    /// <see cref="Services.Download.ChecksumAlgorithm.Auto"/>.
    /// </summary>
    public string? BestChecksum => string.IsNullOrWhiteSpace(Sha1) ? Md5 : Sha1;
}

/// <summary>
/// One image belonging to a listing.
/// </summary>
public sealed class ListingImage
{
    /// <summary>Auto-incrementing primary key.</summary>
    public long Id { get; set; }

    /// <summary>Listing this image belongs to.</summary>
    public string ListingId { get; set; } = string.Empty;

    /// <summary>Source that reported it.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>What the image is for.</summary>
    public ListingImageKind Kind { get; set; }

    /// <summary>Remote address of the image.</summary>
    public string RemoteUrl { get; set; } = string.Empty;

    /// <summary>Local cached path, or <see langword="null"/> until fetched.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Pixel width, or zero when unknown.</summary>
    public int Width { get; set; }

    /// <summary>Pixel height, or zero when unknown.</summary>
    public int Height { get; set; }

    /// <summary>Display order within its kind.</summary>
    public int SortOrder { get; set; }
}
