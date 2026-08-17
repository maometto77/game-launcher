using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing;

/// <summary>
/// Why a site cannot supply a download.
/// </summary>
public enum SourcingRefusal
{
    /// <summary>Nothing refused it; a payload was produced.</summary>
    None = 0,

    /// <summary>No adapter recognises the address.</summary>
    Unsupported = 1,

    /// <summary>
    /// The site's own <c>robots.txt</c> disallows the path a download would need.
    /// </summary>
    /// <remarks>
    /// A permanent answer, not a transient failure. Retrying, waiting or trying
    /// harder does not change it, and the correct response is to look elsewhere
    /// for the same game.
    /// </remarks>
    DisallowedByRobots = 2,

    /// <summary>The site was reachable but published no download for this item.</summary>
    NoPayload = 3,

    /// <summary>The site could not be reached.</summary>
    Unreachable = 4
}

/// <summary>
/// What an adapter produced for one item.
/// </summary>
/// <param name="Downloads">Addresses the game can be fetched from, best first.</param>
/// <param name="Refusal">Why nothing was produced, when <paramref name="Downloads"/> is empty.</param>
/// <param name="Explanation">A sentence a person can act on, or <see langword="null"/>.</param>
public sealed record SourcingPayload(
    IReadOnlyList<ListingDownload> Downloads,
    SourcingRefusal Refusal = SourcingRefusal.None,
    string? Explanation = null)
{
    /// <summary>An empty payload for an address no adapter handles.</summary>
    public static SourcingPayload Unsupported { get; } =
        new([], SourcingRefusal.Unsupported, "No sourcing adapter handles that address.");

    /// <summary>Gets a value indicating whether anything can be downloaded.</summary>
    public bool HasDownloads => Downloads.Count > 0;
}

/// <summary>
/// Turns a page on some site into the addresses a game can be fetched from.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ICatalogSource"/> on purpose. A source answers "what
/// games exist and what are they"; an adapter answers "given this page, what can
/// actually be downloaded". They are different questions with different failure
/// modes, and a site can be perfectly good at one and unable to answer the other
/// — which is exactly the situation MyAbandonware is in.
/// </para>
/// <para>
/// An adapter that cannot supply a download says which kind of refusal it is,
/// because the caller's next move depends on it: a site that is merely
/// unreachable is worth retrying, and one whose rules forbid the path is not.
/// </para>
/// </remarks>
public interface ISourcingAdapter
{
    /// <summary>Dispatch key, matching the catalogue source it belongs to.</summary>
    string Key { get; }

    /// <summary>Human-readable name, for explanations shown to a person.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Determines whether this adapter handles an address.
    /// </summary>
    /// <param name="url">The page address.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    bool CanHandle(string url);

    /// <summary>
    /// Works out what can be downloaded from a page.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="url">The page address on this adapter's site.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The payload, or a refusal explaining why there is none.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    Task<SourcingPayload> ExtractDownloadPayloadAsync(
        CatalogListing listing,
        string url,
        CancellationToken cancellationToken = default);
}
