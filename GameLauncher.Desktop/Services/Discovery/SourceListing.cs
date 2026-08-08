namespace GameLauncher.Desktop.Services.Discovery;

/// <summary>
/// What an image found on a source is for.
/// </summary>
public enum ListingImageKind
{
    /// <summary>Portrait box art for a catalogue tile.</summary>
    Cover = 0,

    /// <summary>In-game screenshot.</summary>
    Screenshot = 1,

    /// <summary>Wide banner for the top of a details page.</summary>
    Hero = 2
}

/// <summary>
/// What a downloadable file contains.
/// </summary>
public enum DownloadKind
{
    /// <summary>The game itself.</summary>
    Game = 0,

    /// <summary>A manual or other documentation.</summary>
    Manual = 1,

    /// <summary>Anything else worth offering — patches, soundtracks, extras.</summary>
    Extra = 2,

    /// <summary>
    /// A BitTorrent payload delivering the game.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Game"/> because it needs a transport that may
    /// not be installed. An install offers it only after the direct addresses,
    /// so a launcher without aria2c never reaches for it.
    /// </remarks>
    Torrent = 3
}

/// <summary>
/// One image a source knows about, as a reference rather than as bytes.
/// </summary>
/// <param name="Url">Direct address of the image.</param>
/// <param name="Kind">What the image is for.</param>
/// <param name="Width">Pixel width, or zero when the source did not say.</param>
/// <param name="Height">Pixel height, or zero when the source did not say.</param>
/// <param name="SortOrder">Display order within its kind; lower sorts first.</param>
/// <remarks>
/// Deliberately a reference. Importing several thousand listings with half a
/// dozen images each would mean tens of thousands of transfers during an import
/// that should take minutes, so images are fetched when something actually
/// displays them.
/// </remarks>
public sealed record ListingImageRef(
    Uri Url,
    ListingImageKind Kind,
    int Width,
    int Height,
    int SortOrder);

/// <summary>
/// One downloadable file a source offers.
/// </summary>
public sealed record ListingDownloadRef
{
    /// <summary>Direct address of the file.</summary>
    public required Uri Url { get; init; }

    /// <summary>File name the source reports, or <see langword="null"/> to derive one.</summary>
    public string? FileName { get; init; }

    /// <summary>Size in bytes, or <see langword="null"/> when the source did not say.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>MD5 digest as hex, or <see langword="null"/>.</summary>
    public string? Md5 { get; init; }

    /// <summary>SHA-1 digest as hex, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Preferred over <see cref="Md5"/> when both are present. Both lengths are
    /// recognised by <see cref="Download.ChecksumAlgorithm.Auto"/>, so either
    /// flows into the existing download path unchanged.
    /// </remarks>
    public string? Sha1 { get; init; }

    /// <summary>Format label from the source, such as <c>ZIP</c>.</summary>
    public string? Format { get; init; }

    /// <summary>What the file contains.</summary>
    public DownloadKind Kind { get; init; } = DownloadKind.Game;

    /// <summary>
    /// Preference order among mirrors of the same file; lower is tried first.
    /// </summary>
    public int MirrorRank { get; init; }

    /// <summary>Gets the strongest digest available, or <see langword="null"/> when there is none.</summary>
    public string? BestChecksum => string.IsNullOrWhiteSpace(Sha1) ? Md5 : Sha1;
}

/// <summary>
/// Everything one source says about one game, after normalisation.
/// </summary>
/// <remarks>
/// <para>
/// This is an observation, not a conclusion. Several of these — one per source —
/// are merged into a single <see cref="Models.CatalogListing"/>, and the merge is
/// a pure function of them, so a source's view is never edited in place.
/// </para>
/// <para>
/// <see cref="RawPayload"/> is what makes that worth doing: normalisation and
/// merge rules can be changed and re-applied to the whole catalogue offline,
/// without going back to the network.
/// </para>
/// </remarks>
public sealed record SourceListing
{
    /// <summary>Dispatch key of the source that produced this.</summary>
    public required string SourceKey { get; init; }

    /// <summary>The source's own identifier for the item.</summary>
    public required string SourceItemId { get; init; }

    /// <summary>Human-visible page this came from, kept for attribution.</summary>
    public required Uri SourceUrl { get; init; }

    /// <summary>Title as the source gives it, after title normalisation.</summary>
    public required string Title { get; init; }

    /// <summary>Release year, or <see langword="null"/> when unknown.</summary>
    public int? Year { get; init; }

    /// <summary>Plain-text description, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Developer, or <see langword="null"/>.</summary>
    public string? Developer { get; init; }

    /// <summary>Publisher, or <see langword="null"/>.</summary>
    public string? Publisher { get; init; }

    /// <summary>Genres, already mapped to the canonical vocabulary.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Platforms the source lists.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Free-form tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>System requirements as free text, or <see langword="null"/>.</summary>
    public string? SystemRequirements { get; init; }

    /// <summary>Images the source knows about.</summary>
    public IReadOnlyList<ListingImageRef> Images { get; init; } = [];

    /// <summary>Files the source offers.</summary>
    public IReadOnlyList<ListingDownloadRef> Downloads { get; init; } = [];

    /// <summary>
    /// Whether the source permits downloading the item.
    /// </summary>
    /// <remarks>
    /// False for Internet Archive items marked <c>access-restricted-item</c> or
    /// filed under <c>stream_only</c>. Such items are worth listing and must not
    /// be offered for install: the transfer would fail with a 403, which is a
    /// worse experience than an explained absence.
    /// </remarks>
    public bool IsDownloadable { get; init; } = true;

    /// <summary>When the source last changed the item, or <see langword="null"/>.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; init; }

    /// <summary>
    /// The unmodified payload the source returned.
    /// </summary>
    /// <remarks>
    /// Stored so a parser or merge-rule fix can be re-applied to the whole
    /// catalogue without re-fetching anything. It is also the only way to
    /// diagnose a parse that produced the wrong answer rather than no answer.
    /// </remarks>
    public required string RawPayload { get; init; }
}
