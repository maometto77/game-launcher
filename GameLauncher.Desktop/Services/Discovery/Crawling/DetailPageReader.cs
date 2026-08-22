using GameLauncher.Desktop.Services.Discovery.Normalization;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// Turns a game's own page into a source observation.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="ListingPageParser"/>: that one finds which
/// pages are worth reading, this one reads one of them. Split because they fail
/// differently and are worth testing separately — a site can list its games
/// perfectly and describe them in a way nothing recognises, and the fix for each
/// is a different selector.
/// </para>
/// <para>
/// Deliberately tolerant. Every field but the title is optional, and a page that
/// yields nothing but a name still produces a usable listing: the catalogue
/// matches on titles, and a sparse entry that merges with a better-described one
/// from another source is the multi-source design working rather than a failure.
/// </para>
/// </remarks>
public static class DetailPageReader
{
    /// <summary>
    /// Reads one game's page.
    /// </summary>
    /// <param name="page">The page to read.</param>
    /// <param name="item">What the listing page said about it.</param>
    /// <param name="selectors">Selector overrides, possibly empty.</param>
    /// <param name="sourceKey">The key to attribute the observation to.</param>
    /// <param name="policy">What image and link addresses are acceptable.</param>
    /// <returns>The observation.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static SourceListing Read(
        CrawledPage page,
        CrawledItem item,
        CrawlSelectors selectors,
        string sourceKey,
        UrlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(selectors);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var document = page.Document;
        var baseAddress = page.BaseAddress;

        // The detail page's own title is preferred over the listing's: a listing
        // frequently truncates, and the page itself is where the full name is.
        var title = HtmlReader.Title(document, null, selectors.Title) ?? item.Title;

        var cover = Permitted(HtmlReader.Cover(document, baseAddress, selectors.Cover), policy);

        var images = new List<ListingImageRef>();

        if (cover is not null)
        {
            images.Add(new ListingImageRef(cover, ListingImageKind.Cover, 0, 0, 0));
        }

        foreach (var shot in HtmlReader.Screenshots(document, baseAddress, selectors.Screenshots, cover))
        {
            if (Permitted(shot, policy) is { } allowed)
            {
                images.Add(new ListingImageRef(allowed, ListingImageKind.Screenshot, 0, 0, images.Count));
            }
        }

        var genres = HtmlReader.Values(document, selectors.Genres, HtmlReader.GenreLabelSet);

        return new SourceListing
        {
            SourceKey = sourceKey,
            SourceItemId = item.SourceId,
            SourceUrl = item.DetailAddress,
            Title = title,
            Year = HtmlReader.Year(document, selectors.Date),
            Description = HtmlReader.Description(document, selectors.Description),

            Developer = HtmlReader.Text(document, selectors.Developer) ??
                        HtmlReader.LabelledValue(document, HtmlReader.DeveloperLabelSet),

            Publisher = HtmlReader.Text(document, selectors.Publisher) ??
                        HtmlReader.LabelledValue(document, HtmlReader.PublisherLabelSet),

            SystemRequirements = HtmlReader.Text(document, selectors.Requirements),

            // Only values already recognised as genres become genres. A tag list
            // is a general-purpose field and most of what is in it is not a
            // genre at all, so letting it through would fill the facet with
            // "download" and "windows".
            Genres = GenreVocabulary.MapKnown(genres),
            Platforms = HtmlReader.Values(document, selectors.Platforms, HtmlReader.PlatformLabelSet),

            // The unmapped values are kept as tags, where being arbitrary is the
            // point rather than a problem.
            Tags = genres,
            Images = images,

            // Nothing here. Addresses are the sourcing half's job, and a crawler
            // that also collected them would be doing that job worse and twice.
            Downloads = [],

            // A page describing a game is not a promise that it can be fetched.
            // Whether it can is decided at install time, by whichever adapter
            // claims this address.
            IsDownloadable = true,

            // The page itself, so a re-normalisation can be re-run offline
            // against exactly what the site served.
            RawPayload = document.Source.Text.Length <= MaxStoredPayload
                ? document.Source.Text
                : document.Source.Text[..MaxStoredPayload]
        };
    }

    /// <summary>Largest page body kept for later re-normalisation.</summary>
    /// <remarks>
    /// Stored so a merge-rule change can be applied to the whole catalogue
    /// offline, which is what the import pipeline's remerge mode does. Capped
    /// because a catalogue of several thousand pages would otherwise put several
    /// hundred megabytes of markup in the database to serve a rare operation.
    /// </remarks>
    private const int MaxStoredPayload = 256 * 1024;

    /// <summary>
    /// Keeps an address only if the policy permits it.
    /// </summary>
    /// <param name="address">The address found on the page.</param>
    /// <param name="policy">What is acceptable.</param>
    /// <returns>The address, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Images are addresses out of a page like any other, and an image address
    /// is fetched later by the artwork cache. Vetting them here means that
    /// happens against something already checked rather than against whatever
    /// the markup said.
    /// </remarks>
    private static Uri? Permitted(Uri? address, UrlPolicy policy)
    {
        if (address is null)
        {
            return null;
        }

        // Images may legitimately live on a CDN the crawl was not confined to,
        // so only the scheme and private-address rules apply here.
        var relaxed = policy with { AllowedHosts = [] };

        return UrlGuard.Inspect(address, relaxed).IsAllowed ? address : null;
    }
}
