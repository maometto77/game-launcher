namespace GameLauncher.Desktop.Services.Discovery.Normalization;

/// <summary>
/// Puts a source's raw observation into the shape the rest of the pipeline
/// expects.
/// </summary>
/// <remarks>
/// Pure: no database, no network, no clock. A source is responsible for reading
/// its own payload; deciding what a title, a genre or a company name should look
/// like afterwards belongs here, so the answer is the same whichever source it
/// came from.
/// </remarks>
public interface IListingNormalizer
{
    /// <summary>
    /// Normalises every field of a source observation.
    /// </summary>
    /// <param name="listing">The observation as its source produced it.</param>
    /// <returns>The normalised observation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listing"/> is <see langword="null"/>.</exception>
    SourceListing Normalize(SourceListing listing);

    /// <summary>
    /// Builds the key two listings must share to be considered the same game.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="year">Release year, or <see langword="null"/>.</param>
    /// <returns>The match key.</returns>
    string ComputeMatchKey(string title, int? year);

    /// <summary>
    /// Builds the year-independent key used for the matcher's second pass.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The normalised title.</returns>
    string ComputeTitleKey(string title);
}
