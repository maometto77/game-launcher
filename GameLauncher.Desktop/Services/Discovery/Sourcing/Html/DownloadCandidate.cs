using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Crawling;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Html;

/// <summary>
/// One publicly reachable address a game might be fetched from.
/// </summary>
/// <remarks>
/// <para>
/// The unit a resolution strategy produces. Deliberately not a new download
/// model: it carries what a page or a script can tell us and then becomes a
/// <see cref="ListingDownload"/>, which is what the rest of the launcher already
/// understands. A parallel model would have meant a parallel verification path,
/// a parallel mirror ranking and a parallel set of bugs.
/// </para>
/// <para>
/// Nothing here transfers anything. A candidate is a claim about where a file
/// is, made before any request for the file has been sent.
/// </para>
/// </remarks>
public sealed record DownloadCandidate
{
    /// <summary>The address itself.</summary>
    public required Uri Address { get; init; }

    /// <summary>The page it was found on.</summary>
    public required Uri SourcePage { get; init; }

    /// <summary>The file's name, when it was published or can be inferred.</summary>
    public string? FileName { get; init; }

    /// <summary>The file's size in bytes, when it was published.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>A published SHA-256 digest.</summary>
    public string? Sha256 { get; init; }

    /// <summary>A published SHA-1 digest.</summary>
    public string? Sha1 { get; init; }

    /// <summary>A published MD5 digest.</summary>
    public string? Md5 { get; init; }

    /// <summary>A published media type.</summary>
    public string? MimeType { get; init; }

    /// <summary>A format label, such as <c>ZIP</c>.</summary>
    public string? Format { get; init; }

    /// <summary>
    /// Where this candidate ranks against its siblings; higher is tried first.
    /// </summary>
    /// <remarks>
    /// A page listing several mirrors is stating a preference by listing them in
    /// an order, and a strategy may know more — that one mirror is the project's
    /// own and another is a community copy. Expressed as a number so the resolver
    /// can merge candidates from several strategies without knowing what any of
    /// them meant.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>Whatever else the strategy wants recorded, for diagnostics.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets what kind of transfer this address needs.
    /// </summary>
    /// <remarks>
    /// A torrent needs an external engine that may not be installed, so the
    /// download stack has to know which it is before it starts. Decided from the
    /// address because that is the only thing that reliably says so.
    /// </remarks>
    public DownloadKind Kind =>
        Address.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) ||
        Address.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? DownloadKind.Torrent
            : DownloadKind.Game;

    /// <summary>
    /// Turns this candidate into a row the download stack understands.
    /// </summary>
    /// <param name="listingId">The listing being installed.</param>
    /// <param name="sourceKey">The adapter that produced it.</param>
    /// <param name="mirrorRank">Where it sits in the merged mirror list.</param>
    /// <returns>The download row.</returns>
    /// <exception cref="ArgumentException"><paramref name="listingId"/> is null or blank.</exception>
    public ListingDownload ToDownload(string listingId, string sourceKey, int mirrorRank)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        return new ListingDownload
        {
            ListingId = listingId,
            SourceKey = sourceKey,
            Url = Address.AbsoluteUri,
            FileName = FileName ?? InferFileName(),
            SizeBytes = SizeBytes,

            // Passed through as published. Which of the three is set only
            // decides the label; the existing verification path infers the
            // algorithm from the digest's length, and prefers the strongest of
            // whatever is present.
            Sha256 = HexDigest.Clean(Sha256),
            Sha1 = HexDigest.Clean(Sha1),
            Md5 = HexDigest.Clean(Md5),
            Format = Format,
            Kind = Kind,
            MirrorRank = mirrorRank
        };
    }

    /// <summary>
    /// Guesses a file name from the address when none was published.
    /// </summary>
    /// <returns>The name, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The last path segment, when it looks like a file rather than a directory.
    /// Only a display and disk-naming convenience: the download stack works
    /// without it, and a wrong guess is better left null than recorded.
    /// </remarks>
    private string? InferFileName()
    {
        if (Address.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segment = Address.Segments.Length > 0
            ? Uri.UnescapeDataString(Address.Segments[^1]).Trim('/')
            : string.Empty;

        return segment.Length > 0 && segment.Contains('.', StringComparison.Ordinal) ? segment : null;
    }
}
