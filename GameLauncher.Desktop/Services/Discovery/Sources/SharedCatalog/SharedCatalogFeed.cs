namespace GameLauncher.Desktop.Services.Discovery.Sources.SharedCatalog;

/// <summary>
/// Constants describing the shared catalogue feed format.
/// </summary>
/// <remarks>
/// The feed is a single JSON document listing what one person or group has
/// gathered, published wherever they like and read by everyone pointed at it.
/// It is the one source here that is not somebody else's website: a group hosts
/// its own, so it publishes its own files and can therefore state a digest it
/// actually computed.
/// </remarks>
public static class SharedCatalogFeed
{
    /// <summary>
    /// Value the document's <c>feed</c> member must carry.
    /// </summary>
    /// <remarks>
    /// A discriminator, so that a URL pointing at some other JSON document fails
    /// with "this is not a catalogue feed" instead of parsing to zero entries
    /// and looking like an empty catalogue. The two are indistinguishable
    /// without it, and only one of them is the user's mistake to fix.
    /// </remarks>
    public const string Discriminator = "don-catalog";

    /// <summary>Highest format version this build understands.</summary>
    /// <remarks>
    /// A newer feed is refused rather than read as far as it parses. Reading
    /// what is recognised and ignoring the rest would silently drop entries or,
    /// worse, drop downloads from entries that kept their titles.
    /// </remarks>
    public const int SupportedVersion = 1;
}

/// <summary>
/// Thrown when a document is not a shared catalogue feed this build can read.
/// </summary>
/// <remarks>
/// Distinct from a per-entry problem. A malformed entry is skipped and reported
/// as a warning, because one bad row should not cost the user the other four
/// hundred; a document that is the wrong kind or the wrong version has no
/// salvageable content and is worth failing on.
/// </remarks>
public sealed class SharedCatalogFormatException : Exception
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">What was wrong with the document.</param>
    public SharedCatalogFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">What was wrong with the document.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SharedCatalogFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The outcome of reading one feed document.
/// </summary>
/// <param name="Name">The feed's own name, for display, or <see langword="null"/>.</param>
/// <param name="UpdatedAt">When the publisher last rebuilt it, or <see langword="null"/>.</param>
/// <param name="Listings">Entries that parsed, in document order.</param>
/// <param name="Warnings">
/// One line per entry that was skipped, naming the entry and the reason.
/// </param>
/// <remarks>
/// Warnings are returned rather than logged from inside the parser, because the
/// parser is pure — no database, no network, no clock, no logger — which is what
/// lets every rule below be tested against a captured document.
/// </remarks>
public sealed record SharedCatalogParseResult(
    string? Name,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<SourceListing> Listings,
    IReadOnlyList<string> Warnings);
