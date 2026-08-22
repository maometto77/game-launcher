namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// CSS selectors overriding what the crawler would otherwise infer.
/// </summary>
/// <remarks>
/// <para>
/// Every field is optional and every field is an override. Left empty, the
/// crawler guesses — which works on a site laid out the way most sites are laid
/// out, and does not work on the rest. Naming one selector fixes one guess
/// without having to describe the whole page.
/// </para>
/// <para>
/// Ordinary CSS, evaluated by the same engine a browser's
/// <c>querySelectorAll</c> uses, so a selector can be worked out in the
/// browser's own inspector and pasted in. That is the point of using CSS here
/// rather than inventing a path syntax.
/// </para>
/// </remarks>
public sealed class CrawlSelectors
{
    /// <summary>Container for one game in a listing page.</summary>
    public string? Item { get; set; }

    /// <summary>Link from a listing entry to its detail page.</summary>
    public string? DetailLink { get; set; }

    /// <summary>Link to the next listing page.</summary>
    public string? NextPage { get; set; }

    /// <summary>The game's title.</summary>
    public string? Title { get; set; }

    /// <summary>The game's description.</summary>
    public string? Description { get; set; }

    /// <summary>Cover image.</summary>
    public string? Cover { get; set; }

    /// <summary>Screenshots.</summary>
    public string? Screenshots { get; set; }

    /// <summary>Release date or year.</summary>
    public string? Date { get; set; }

    /// <summary>Genres or tags.</summary>
    public string? Genres { get; set; }

    /// <summary>Developer.</summary>
    public string? Developer { get; set; }

    /// <summary>Publisher.</summary>
    public string? Publisher { get; set; }

    /// <summary>Platforms.</summary>
    public string? Platforms { get; set; }

    /// <summary>System requirements.</summary>
    public string? Requirements { get; set; }

    /// <summary>Gets a value indicating whether anything at all was overridden.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Item) &&
        string.IsNullOrWhiteSpace(DetailLink) &&
        string.IsNullOrWhiteSpace(NextPage) &&
        string.IsNullOrWhiteSpace(Title) &&
        string.IsNullOrWhiteSpace(Description) &&
        string.IsNullOrWhiteSpace(Cover) &&
        string.IsNullOrWhiteSpace(Screenshots) &&
        string.IsNullOrWhiteSpace(Date) &&
        string.IsNullOrWhiteSpace(Genres) &&
        string.IsNullOrWhiteSpace(Developer) &&
        string.IsNullOrWhiteSpace(Publisher) &&
        string.IsNullOrWhiteSpace(Platforms) &&
        string.IsNullOrWhiteSpace(Requirements);
}
