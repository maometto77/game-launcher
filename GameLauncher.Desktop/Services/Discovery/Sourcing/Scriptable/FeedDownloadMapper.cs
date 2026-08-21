using System.IO;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// Turns a parsed feed payload into download rows.
/// </summary>
/// <remarks>
/// Pure: nodes and a manifest in, rows out. Every rule about what counts as a
/// torrent, which addresses are usable and how a file name is chosen lives here,
/// where it can be tested against a captured payload rather than a live feed.
/// </remarks>
public static class FeedDownloadMapper
{
    /// <summary>Schemes the download stack can actually fetch.</summary>
    /// <remarks>
    /// <c>file</c> is absent deliberately. A feed that could name a local path
    /// would turn "add this manifest" into "copy anything on this machine", and
    /// the download service rejects it for the same reason.
    /// </remarks>
    private static readonly string[] UsableSchemes = ["http", "https", "magnet"];

    /// <summary>
    /// Maps a payload's items to downloads.
    /// </summary>
    /// <param name="payload">The parsed payload.</param>
    /// <param name="manifest">The manifest describing where its fields are.</param>
    /// <param name="listingId">Listing the rows belong to.</param>
    /// <returns>The downloads, in the order the feed listed them.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// An item whose address is missing or unusable is skipped rather than
    /// failing the batch. A feed listing twenty files of which one is malformed
    /// should still yield nineteen.
    /// </remarks>
    public static IReadOnlyList<ListingDownload> Map(
        FeedNode payload,
        FeedManifest manifest,
        string listingId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(listingId);

        var downloads = new List<ListingDownload>();
        var rank = 0;

        foreach (var item in payload.ListAt(manifest.Items))
        {
            var address = item.String(manifest.Map.Url);

            if (!IsUsable(address, out var url))
            {
                continue;
            }

            var kind = ClassifyKind(url);

            downloads.Add(new ListingDownload
            {
                ListingId = listingId,
                SourceKey = manifest.Key,
                Url = url.AbsoluteUri,
                FileName = item.String(manifest.Map.FileName) ?? DeriveFileName(url),
                SizeBytes = item.Int64(manifest.Map.SizeBytes),
                Sha256 = Digest(item.String(manifest.Map.Sha256)),
                Sha1 = Digest(item.String(manifest.Map.Sha1)),
                Md5 = Digest(item.String(manifest.Map.Md5)),
                Format = item.String(manifest.Map.Format) ?? DeriveFormat(url, kind),
                Kind = kind,

                // Feed order is the publisher's preference, and there is nothing
                // better to go on. A publisher who lists a fast mirror first
                // meant it.
                MirrorRank = rank++
            });
        }

        return downloads;
    }

    /// <summary>
    /// Decides whether an address is one the download stack can fetch.
    /// </summary>
    /// <param name="address">The address as the feed published it.</param>
    /// <param name="url">The parsed address, when usable.</param>
    /// <returns><see langword="true"/> when it can be used.</returns>
    private static bool IsUsable(string? address, out Uri url)
    {
        url = null!;

        if (string.IsNullOrWhiteSpace(address) ||
            !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!UsableSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>
    /// Works out what an address delivers.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <returns>The kind.</returns>
    /// <remarks>
    /// The same two rules the download service applies when choosing a
    /// transport, so a row classified here reaches aria2 for exactly the
    /// addresses aria2 is needed for.
    /// </remarks>
    private static DownloadKind ClassifyKind(Uri url) =>
        string.Equals(url.Scheme, "magnet", StringComparison.OrdinalIgnoreCase) ||
        url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? DownloadKind.Torrent
            : DownloadKind.Game;

    /// <summary>
    /// Picks a file name for an address the feed did not name.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <returns>The name, or <see langword="null"/> to let the download service decide.</returns>
    /// <remarks>
    /// A magnet URI has no path to take a name from — the torrent names its own
    /// contents — so this returns nothing rather than inventing one.
    /// </remarks>
    private static string? DeriveFileName(Uri url)
    {
        if (string.Equals(url.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segment = url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1]).Trim('/') : string.Empty;

        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    /// <summary>Labels the format from the address, when the feed did not.</summary>
    /// <param name="url">The address.</param>
    /// <param name="kind">What the address delivers.</param>
    /// <returns>The label, or <see langword="null"/> when there is nothing to go on.</returns>
    private static string? DeriveFormat(Uri url, DownloadKind kind)
    {
        if (kind == DownloadKind.Torrent)
        {
            return "Torrent";
        }

        var extension = Path.GetExtension(url.AbsolutePath);

        return extension.Length > 1 ? extension[1..].ToUpperInvariant() : null;
    }

    /// <summary>
    /// Keeps a digest only if it looks like one.
    /// </summary>
    /// <param name="value">The published value.</param>
    /// <returns>The digest in lower case, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// A field holding "unknown", "n/a" or a prose sentence is worse than an
    /// absent one: the download service would compare against it and fail every
    /// transfer with a checksum mismatch that is really a feed typo.
    /// </para>
    /// <para>
    /// An <c>md5:</c> or <c>sha256:</c> prefix is stripped rather than rejected.
    /// Real repositories publish that form — Zenodo does — and dropping a
    /// perfectly good checksum over its punctuation would lose the verification
    /// this launcher otherwise gets for free.
    /// </para>
    /// <para>
    /// The accepted lengths are exactly those the download service can verify.
    /// Which field a digest is mapped to is only a label: the algorithm is
    /// inferred from the digest's length when the transfer is checked.
    /// </para>
    /// </remarks>
    internal static string? Digest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        var separator = trimmed.IndexOf(':', StringComparison.Ordinal);

        if (separator >= 0)
        {
            trimmed = trimmed[(separator + 1)..];
        }

        return trimmed.Length is 32 or 40 or 64 or 128 && trimmed.All(Uri.IsHexDigit)
            ? trimmed.ToLowerInvariant()
            : null;
    }
}
