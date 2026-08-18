using System.Globalization;
using System.Text.Json;
using GameLauncher.Desktop.Services.Discovery.Normalization;

namespace GameLauncher.Desktop.Services.Discovery.Sources.SharedCatalog;

/// <summary>
/// Turns a shared catalogue feed document into source observations.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no database, no network, no clock, no logger. Everything interesting
/// about reading a feed — which entries are usable, which addresses are allowed,
/// how a digest is recognised — is decided here and can be tested against a
/// captured document.
/// </para>
/// <para>
/// Read with <see cref="JsonDocument"/> rather than deserialised into records,
/// for two reasons. Each entry's exact text is needed verbatim for
/// <see cref="SourceListing.RawPayload"/>, and round-tripping through a record
/// would quietly drop any member this build does not know about. And a single
/// unusable entry has to be skippable: deserialising the whole document means
/// one bad row costs the user every other row in the file.
/// </para>
/// </remarks>
public static class SharedCatalogParser
{
    /// <summary>Schemes a feed may point at.</summary>
    /// <remarks>
    /// A feed is remote content, and every address in it is followed by this
    /// application. Without this, a published feed could name <c>file://</c> and
    /// have the launcher read from the machine it is running on.
    /// </remarks>
    private static readonly string[] AllowedSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps];

    /// <summary>Lengths of the hex digests the download path can verify.</summary>
    private static readonly int[] DigestLengths = [32, 40, 64, 128];

    /// <summary>
    /// Reads a feed document.
    /// </summary>
    /// <param name="json">The document as fetched.</param>
    /// <param name="feedUrl">
    /// Where it was fetched from. Relative addresses inside the document resolve
    /// against this, and it stands in for an entry that names no page of its own.
    /// </param>
    /// <param name="sourceKey">Dispatch key to stamp on every observation.</param>
    /// <returns>What parsed, and a line for everything that did not.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="SharedCatalogFormatException">
    /// The document is not a shared catalogue feed, or is a newer version than
    /// this build understands.
    /// </exception>
    public static SharedCatalogParseResult Parse(string json, Uri feedUrl, string sourceKey)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(feedUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new SharedCatalogFormatException(
                $"'{feedUrl}' did not return valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SharedCatalogFormatException(
                    $"'{feedUrl}' is not a catalogue feed: the document is a {root.ValueKind}, not an object.");
            }

            var discriminator = String(root, "feed");

            if (!string.Equals(discriminator, SharedCatalogFeed.Discriminator, StringComparison.OrdinalIgnoreCase))
            {
                throw new SharedCatalogFormatException(
                    $"'{feedUrl}' is not a catalogue feed. Expected a \"feed\" member of " +
                    $"\"{SharedCatalogFeed.Discriminator}\"" +
                    (discriminator is null ? ", but there was none." : $", but found \"{discriminator}\"."));
            }

            var version = Int32(root, "version") ?? 1;

            if (version > SharedCatalogFeed.SupportedVersion)
            {
                throw new SharedCatalogFormatException(
                    $"'{feedUrl}' is a version {version} feed, and this build reads version " +
                    $"{SharedCatalogFeed.SupportedVersion}. Update Don to read it.");
            }

            var listings = new List<SourceListing>();
            var warnings = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                var position = 0;

                foreach (var entry in entries.EnumerateArray())
                {
                    var listing = ReadEntry(entry, position++, feedUrl, sourceKey, seen, warnings);

                    if (listing is not null)
                    {
                        listings.Add(listing);
                    }
                }
            }
            else
            {
                warnings.Add("The feed has no \"entries\" array, so nothing was imported.");
            }

            return new SharedCatalogParseResult(
                String(root, "name"),
                Timestamp(root, "updated"),
                listings,
                warnings);
        }
    }

    /// <summary>
    /// Reads one entry, or explains why it was skipped.
    /// </summary>
    /// <param name="entry">The entry element.</param>
    /// <param name="position">Index in the array, used to name an entry with no id.</param>
    /// <param name="feedUrl">Base for relative addresses.</param>
    /// <param name="sourceKey">Dispatch key to stamp on the observation.</param>
    /// <param name="seen">Identifiers already taken.</param>
    /// <param name="warnings">Collects the reason when this returns null.</param>
    /// <returns>The observation, or <see langword="null"/> when unusable.</returns>
    private static SourceListing? ReadEntry(
        JsonElement entry,
        int position,
        Uri feedUrl,
        string sourceKey,
        HashSet<string> seen,
        List<string> warnings)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"Entry {position} is a {entry.ValueKind}, not an object; skipped.");
            return null;
        }

        var id = String(entry, "id");
        var title = String(entry, "title");

        if (string.IsNullOrWhiteSpace(id))
        {
            warnings.Add($"Entry {position} has no \"id\"; skipped.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            warnings.Add($"Entry '{id}' has no \"title\"; skipped.");
            return null;
        }

        // The identifier is what ties an entry to the row it produced last time.
        // Two entries claiming one identifier would take turns overwriting each
        // other on every import, so the second is refused rather than allowed to
        // make the catalogue depend on import order.
        if (!seen.Add(id))
        {
            warnings.Add($"Entry '{id}' repeats an identifier already used earlier in the feed; skipped.");
            return null;
        }

        var downloads = ReadDownloads(entry, feedUrl, id, warnings);

        return new SourceListing
        {
            SourceKey = sourceKey,
            SourceItemId = id,
            SourceUrl = Address(entry, "page", feedUrl) ?? feedUrl,
            Title = title,
            Year = Int32(entry, "year"),
            Description = String(entry, "description"),
            Developer = String(entry, "developer"),
            Publisher = String(entry, "publisher"),
            Genres = GenreVocabulary.MapMany(Strings(entry, "genres")),
            Platforms = Strings(entry, "platforms"),
            Tags = Strings(entry, "tags"),
            SystemRequirements = String(entry, "requirements"),
            Images = ReadImages(entry, feedUrl),
            Downloads = downloads,

            // An entry with nothing to fetch is still worth listing — a feed is
            // as much a record of what exists as an offer to install it — but it
            // must not present an install button that cannot do anything.
            IsDownloadable = downloads.Count > 0,

            SourceUpdatedAt = Timestamp(entry, "updated"),
            RawPayload = entry.GetRawText()
        };
    }

    /// <summary>Reads an entry's downloads, skipping any with an unusable address.</summary>
    /// <param name="entry">The entry element.</param>
    /// <param name="feedUrl">Base for relative addresses.</param>
    /// <param name="id">Entry identifier, for warnings.</param>
    /// <param name="warnings">Collects skipped addresses.</param>
    /// <returns>The usable downloads, in document order.</returns>
    private static IReadOnlyList<ListingDownloadRef> ReadDownloads(
        JsonElement entry,
        Uri feedUrl,
        string id,
        List<string> warnings)
    {
        if (!entry.TryGetProperty("downloads", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var downloads = new List<ListingDownloadRef>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = Address(element, "url", feedUrl);

            if (url is null)
            {
                var raw = String(element, "url");

                warnings.Add(raw is null
                    ? $"Entry '{id}' has a download with no \"url\"; skipped."
                    : $"Entry '{id}' has a download at '{raw}', which is not an http or https address; skipped.");

                continue;
            }

            downloads.Add(new ListingDownloadRef
            {
                Url = url,
                FileName = String(element, "fileName") ?? FileNameFrom(url),
                SizeBytes = Int64(element, "size"),
                Sha256 = Digest(element, "sha256"),
                Sha1 = Digest(element, "sha1"),
                Md5 = Digest(element, "md5"),
                Format = String(element, "format") ?? FormatFrom(url),
                Kind = DownloadKindFrom(String(element, "kind")),

                // Document order is the publisher's preference order. They know
                // which of their mirrors is nearest and fastest; nothing here does.
                MirrorRank = downloads.Count
            });
        }

        return downloads;
    }

    /// <summary>Reads an entry's images, skipping any with an unusable address.</summary>
    /// <param name="entry">The entry element.</param>
    /// <param name="feedUrl">Base for relative addresses.</param>
    /// <returns>The usable images, in document order.</returns>
    private static IReadOnlyList<ListingImageRef> ReadImages(JsonElement entry, Uri feedUrl)
    {
        if (!entry.TryGetProperty("images", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var images = new List<ListingImageRef>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = Address(element, "url", feedUrl);

            if (url is null)
            {
                continue;
            }

            images.Add(new ListingImageRef(
                url,
                ImageKindFrom(String(element, "kind")),
                Int32(element, "width") ?? 0,
                Int32(element, "height") ?? 0,
                images.Count));
        }

        return images;
    }

    /// <summary>Maps a feed's download kind, defaulting to the game itself.</summary>
    /// <param name="value">The kind as written, or <see langword="null"/>.</param>
    /// <returns>The mapped kind.</returns>
    private static DownloadKind DownloadKindFrom(string? value) => value?.ToLowerInvariant() switch
    {
        "manual" => DownloadKind.Manual,
        "extra" => DownloadKind.Extra,
        "torrent" => DownloadKind.Torrent,
        _ => DownloadKind.Game
    };

    /// <summary>Maps a feed's image kind, defaulting to a screenshot.</summary>
    /// <param name="value">The kind as written, or <see langword="null"/>.</param>
    /// <returns>The mapped kind.</returns>
    /// <remarks>
    /// Screenshot rather than cover, because a wrong screenshot is a small
    /// mistake and a wrong cover is the tile the user sees for that game.
    /// </remarks>
    private static ListingImageKind ImageKindFrom(string? value) => value?.ToLowerInvariant() switch
    {
        "cover" => ListingImageKind.Cover,
        "hero" => ListingImageKind.Hero,
        _ => ListingImageKind.Screenshot
    };

    /// <summary>
    /// Reads an address, resolving it against the feed and rejecting anything
    /// that is not http or https.
    /// </summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <param name="feedUrl">Base for a relative address.</param>
    /// <returns>The absolute address, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Relative addresses are supported on purpose. A group hosting the feed and
    /// the files together can then write <c>files/quake.zip</c> and move the
    /// whole thing to another domain without editing a single entry.
    /// </remarks>
    private static Uri? Address(JsonElement element, string name, Uri feedUrl)
    {
        var value = String(element, name);

        if (value is null || !Uri.TryCreate(feedUrl, value, out var resolved))
        {
            return null;
        }

        return AllowedSchemes.Contains(resolved.Scheme, StringComparer.OrdinalIgnoreCase) ? resolved : null;
    }

    /// <summary>Reads a hex digest, ignoring any prefix and anything malformed.</summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The lowercase digest, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A digest that is not one of the recognised lengths is dropped rather than
    /// passed on. Carried forward, it would fail verification on a file that was
    /// downloaded perfectly, and the user would be told their download was
    /// corrupt when the feed was simply wrong.
    /// </remarks>
    private static string? Digest(JsonElement element, string name)
    {
        var value = String(element, name);

        if (value is null)
        {
            return null;
        }

        // Some publishers write "sha256:abc..."; the prefix is redundant next to
        // a member that already says which algorithm this is.
        var separator = value.IndexOf(':', StringComparison.Ordinal);

        if (separator >= 0)
        {
            value = value[(separator + 1)..];
        }

        return DigestLengths.Contains(value.Length) && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : null;
    }

    /// <summary>Derives a file name from an address.</summary>
    /// <param name="url">The address.</param>
    /// <returns>The last path segment, or <see langword="null"/>.</returns>
    private static string? FileNameFrom(Uri url)
    {
        var name = Path.GetFileName(url.AbsolutePath);

        return string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name);
    }

    /// <summary>Derives a format label from an address.</summary>
    /// <param name="url">The address.</param>
    /// <returns>The uppercased extension, or <see langword="null"/>.</returns>
    private static string? FormatFrom(Uri url)
    {
        var extension = Path.GetExtension(url.AbsolutePath);

        return extension.Length > 1 ? extension[1..].ToUpperInvariant() : null;
    }

    /// <summary>Reads a non-empty string member.</summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The trimmed value, or <see langword="null"/>.</returns>
    private static string? String(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Reads a string array, dropping empty elements.</summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The values, or an empty list.</returns>
    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = item.GetString()?.Trim();

            if (!string.IsNullOrEmpty(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    /// <summary>Reads a 32-bit integer member, accepting a numeric string.</summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static int? Int32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        // Hand-written feeds quote their numbers often enough to be worth
        // accepting, and "1996" is unambiguous either way.
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Reads a 64-bit integer member, accepting a numeric string.</summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static long? Int64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Reads an ISO-8601 timestamp member.</summary>
    /// <param name="element">Element holding the member.</param>
    /// <param name="name">Member name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static DateTimeOffset? Timestamp(JsonElement element, string name)
    {
        var value = String(element, name);

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
