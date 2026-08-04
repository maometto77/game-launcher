namespace GameLauncher.Desktop.Models;

/// <summary>
/// A named, ordered grouping of games, equivalent to a Steam category.
/// </summary>
/// <remarks>
/// A game belongs to at most one collection, via <see cref="Game.CollectionId"/>.
/// Collections are the exclusive, structural grouping; <see cref="Game.Tags"/>
/// is the overlapping, ad-hoc one. Keeping both means the user can file a game
/// in exactly one place while still labelling it freely.
/// </remarks>
public sealed class Collection
{
    /// <summary>Auto-incrementing primary key. Zero for a collection not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Display name of the collection. Unique, case-insensitively.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Manual ordering position in the sidebar. Lower values sort first; ties
    /// break alphabetically by <see cref="Name"/>.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>When the collection was created.</summary>
    public DateTimeOffset DateAdded { get; set; }
}
