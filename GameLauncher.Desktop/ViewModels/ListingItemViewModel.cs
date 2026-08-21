using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Images;
using GameLauncher.Desktop.Services.Discovery.Sources;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One source that describes a listing, as it appears on a card.
/// </summary>
/// <param name="Label">Short name shown on the badge.</param>
/// <param name="IsMetadataOnly">
/// Whether the source describes the game without being able to supply it.
/// </param>
/// <param name="SourceKey">The dispatch key, for installing from this source.</param>
/// <param name="ListingId">The listing this badge belongs to.</param>
/// <remarks>
/// <para>
/// The metadata-only distinction is carried as data rather than inferred from
/// the label, because it is the one thing the badge has to get right. A source
/// that can be installed from and a source that merely knows about a game are
/// different answers to "where can I get this", and drawing them identically
/// invites exactly the wrong conclusion.
/// </para>
/// <para>
/// The listing id rides along so a badge is a complete instruction on its own.
/// A pressed badge is the command's whole argument, which keeps the binding a
/// plain <c>CommandParameter</c> rather than something that has to reach back up
/// the visual tree for the other half of what it means.
/// </para>
/// </remarks>
public sealed record SourceBadge(string Label, bool IsMetadataOnly, string SourceKey, string ListingId)
{
    /// <summary>Gets a value indicating whether installing from this source is offered.</summary>
    public bool CanInstallFrom => !IsMetadataOnly;
}

/// <summary>
/// One catalogue listing as a card.
/// </summary>
/// <remarks>
/// Artwork and the save badge are both resolved lazily rather than in the
/// constructor. A page of sixty cards built eagerly would start sixty downloads
/// and sixty manifest lookups whether or not anyone scrolled far enough to see
/// them.
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

        SourceBadges = listing.SourceKeys
            .Select(sourceKey => Describe(sourceKey, listing.ListingId))
            .ToArray();
    }

    /// <summary>Gets the listing behind this card.</summary>
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

    /// <summary>Gets the sources that describe this game.</summary>
    public IReadOnlyList<SourceBadge> SourceBadges { get; }

    /// <summary>Gets a value indicating whether any source badge should be drawn.</summary>
    public bool HasSourceBadges => SourceBadges.Count > 0;

    /// <summary>
    /// Gets every source named in one line, for the badge strip's tooltip.
    /// </summary>
    /// <remarks>
    /// The strip is a fixed height so that a grid of cards lines up, which means
    /// a listing described by an unusual number of sources can have one fall
    /// outside it. The tooltip is where that one is still readable.
    /// </remarks>
    public string SourceSummary => string.Join(", ", SourceBadges.Select(badge => badge.Label));

    /// <summary>Gets the local path of the cover, or <see langword="null"/> until it is fetched.</summary>
    [ObservableProperty]
    private string? _coverPath;

    /// <summary>
    /// Whether the save manifest knows where this game keeps its saves.
    /// </summary>
    /// <remarks>
    /// Only ever set from an already-loaded manifest, so browsing never triggers
    /// a download to answer it.
    /// </remarks>
    [ObservableProperty]
    private bool _hasKnownSaves;

    /// <summary>What the card's primary button says.</summary>
    [ObservableProperty]
    private string _actionText = "Install";

    /// <summary>Whether the primary button can be pressed.</summary>
    [ObservableProperty]
    private bool _isActionEnabled = true;

    /// <summary>Whether this game is somewhere in the download queue.</summary>
    [ObservableProperty]
    private bool _isQueued;

    /// <summary>Whether this game has been installed.</summary>
    [ObservableProperty]
    private bool _isInstalled;

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

    /// <summary>
    /// Updates the primary button from the queue's view of this game.
    /// </summary>
    /// <param name="job">The job for this listing, or <see langword="null"/> when there is none.</param>
    /// <remarks>
    /// The button reports the queue's state rather than its own, so pressing
    /// Install once and navigating away and back still shows what is happening.
    /// </remarks>
    public void ApplyQueueState(DownloadJob? job)
    {
        IsQueued = job is not null && !job.IsTerminal;
        IsInstalled = job?.Phase == DownloadPhase.Completed;

        (ActionText, IsActionEnabled) = job?.Phase switch
        {
            null => ("Install", IsDownloadable),
            DownloadPhase.Queued => ("Queued", false),
            DownloadPhase.Paused => ("Paused", false),
            DownloadPhase.Downloading when job.Fraction is { } fraction =>
                ($"{fraction * 100:0}%", false),
            DownloadPhase.Downloading => ("Downloading", false),
            DownloadPhase.Resolving => ("Finding…", false),
            DownloadPhase.Verifying => ("Verifying", false),
            DownloadPhase.Extracting => ("Extracting", false),
            DownloadPhase.Detecting => ("Detecting", false),
            DownloadPhase.ReadyToInstall => ("Finish install", true),
            DownloadPhase.Completed => ("Installed", false),
            DownloadPhase.Failed => ("Retry", true),
            _ => ("Install", IsDownloadable)
        };
    }

    /// <summary>Records whether the save manifest covers this game.</summary>
    /// <param name="known">Whether a save location was resolved.</param>
    public void ApplySaveState(bool known) => HasKnownSaves = known;

    /// <summary>
    /// Turns a source key into something worth showing on a card.
    /// </summary>
    /// <param name="sourceKey">The dispatch key.</param>
    /// <param name="listingId">The listing the badge belongs to.</param>
    /// <returns>The badge.</returns>
    /// <remarks>
    /// MyAbandonware is labelled as metadata because that is the whole truth
    /// about it: its own rules disallow automated downloads, so a badge implying
    /// the game can be fetched from there would be misleading.
    /// </remarks>
    private static SourceBadge Describe(string sourceKey, string listingId) => sourceKey switch
    {
        InternetArchiveCatalogSource.SourceKey =>
            new SourceBadge("Archive.org", false, sourceKey, listingId),

        MyAbandonwareCatalogSource.SourceKey =>
            new SourceBadge("MyAbandonware metadata", true, sourceKey, listingId),

        // A custom feed, named by whatever its manifest called itself. It exists
        // to supply downloads, so it is not marked as metadata-only.
        _ => new SourceBadge(sourceKey, false, sourceKey, listingId)
    };
}
