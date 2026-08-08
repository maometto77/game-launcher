using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Images;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One catalogue listing as a tile.
/// </summary>
/// <remarks>
/// Artwork is resolved lazily by <see cref="LoadCoverAsync"/> rather than in the
/// constructor. A page of sixty tiles constructed eagerly would start sixty
/// downloads whether or not the user scrolled far enough to see them.
/// </remarks>
public sealed partial class ListingItemViewModel : ObservableObject
{
    private readonly IListingImageCache _images;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="listing">The listing to present.</param>
    /// <param name="images">Resolves cover art on demand.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ListingItemViewModel(CatalogListing listing, IListingImageCache images)
    {
        Listing = listing ?? throw new ArgumentNullException(nameof(listing));
        _images = images ?? throw new ArgumentNullException(nameof(images));

        // A path already recorded by an earlier visit shows immediately; anything
        // else waits for LoadCoverAsync.
        _coverPath = listing.CoverImagePath;
    }

    /// <summary>Gets the listing behind this tile.</summary>
    public CatalogListing Listing { get; }

    /// <summary>Gets the listing's identity.</summary>
    public string ListingId => Listing.ListingId;

    /// <summary>Gets the title to display.</summary>
    public string Title => Listing.Title;

    /// <summary>Gets the release year as text, or an em dash when unknown.</summary>
    public string YearText => Listing.Year?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "—";

    /// <summary>Gets the developer, publisher, or an empty string.</summary>
    public string Attribution => Listing.Developer ?? Listing.Publisher ?? string.Empty;

    /// <summary>Gets the genres as a single line.</summary>
    public string GenreText => string.Join(", ", Listing.Genres);

    /// <summary>Gets a value indicating whether the listing can be installed.</summary>
    public bool IsDownloadable => Listing.IsDownloadable;

    /// <summary>Gets the local path of the cover, or <see langword="null"/> until it is fetched.</summary>
    [ObservableProperty]
    private string? _coverPath;

    /// <summary>
    /// Fetches the cover if it is not already cached.
    /// </summary>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>A task that completes when the cover has been resolved or given up on.</returns>
    public async Task LoadCoverAsync(CancellationToken cancellationToken = default)
    {
        if (CoverPath is not null || string.IsNullOrWhiteSpace(Listing.CoverImageUrl))
        {
            return;
        }

        CoverPath = await _images
            .GetAsync(Listing.ListingId, Listing.CoverImageUrl, cancellationToken)
            .ConfigureAwait(true);
    }
}
