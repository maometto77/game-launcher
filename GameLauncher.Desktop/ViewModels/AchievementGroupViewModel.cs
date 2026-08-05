namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Presents the achievements belonging to one title, or the library-wide set.
/// </summary>
/// <remarks>
/// Grouping is by catalog identity rather than by game row, because that is what
/// an achievement actually belongs to. A title still catalogued but no longer
/// installed therefore keeps its group, which is the visible consequence of
/// achievements outliving an uninstall.
/// </remarks>
public sealed class AchievementGroupViewModel
{
    /// <summary>Heading used for achievements that belong to no single title.</summary>
    public const string LibraryWideTitle = "Across your library";

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="title">Heading for the group.</param>
    /// <param name="catalogId">Catalog identity the group covers, or <see langword="null"/> for library-wide.</param>
    /// <param name="items">The achievements shown under this heading.</param>
    /// <param name="totalCount">
    /// How many achievements the group holds before filtering, so the summary
    /// keeps reporting the real total when only part of the list is on screen.
    /// </param>
    /// <param name="unlockedCount">How many of those have been earned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public AchievementGroupViewModel(
        string title,
        string? catalogId,
        IReadOnlyList<AchievementItemViewModel> items,
        int totalCount,
        int unlockedCount)
    {
        Title = title;
        CatalogId = catalogId;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        TotalCount = totalCount;
        UnlockedCount = unlockedCount;
    }

    /// <summary>Gets the heading for this group.</summary>
    public string Title { get; }

    /// <summary>Gets the catalog identity, or <see langword="null"/> for library-wide achievements.</summary>
    public string? CatalogId { get; }

    /// <summary>Gets the achievements shown under this heading, after filtering.</summary>
    public IReadOnlyList<AchievementItemViewModel> Items { get; }

    /// <summary>Gets how many achievements the group holds, ignoring the filter.</summary>
    public int TotalCount { get; }

    /// <summary>Gets how many of the group's achievements have been earned.</summary>
    public int UnlockedCount { get; }

    /// <summary>Gets completion as a percentage, from 0 to 100.</summary>
    public double CompletionPercent => TotalCount == 0
        ? 0
        : (double)UnlockedCount / TotalCount * 100d;

    /// <summary>Gets the group's progress as text.</summary>
    public string SummaryText => $"{UnlockedCount} of {TotalCount} unlocked";
}
