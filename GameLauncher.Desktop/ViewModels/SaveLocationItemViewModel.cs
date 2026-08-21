using GameLauncher.Desktop.Services.Saves;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One resolved save location, as a row on the details page.
/// </summary>
/// <remarks>
/// <para>
/// Presentation only. Everything interesting about a location — where it is,
/// what it holds, whether it is there — was decided by
/// <see cref="ISavePathResolver"/>; this turns that into the three strings a row
/// needs and adds nothing.
/// </para>
/// <para>
/// A location that does not exist is shown rather than filtered out, and that is
/// the point of the panel. "Your saves are in this folder, and it is not there
/// yet" is a useful thing to know before a game is played for the first time,
/// and indistinguishable from "nothing is known about this game" if the missing
/// rows are quietly dropped.
/// </para>
/// </remarks>
public sealed class SaveLocationItemViewModel
{
    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="location">The resolved location.</param>
    /// <exception cref="ArgumentNullException"><paramref name="location"/> is <see langword="null"/>.</exception>
    public SaveLocationItemViewModel(SaveLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        Path = location.Path;
        Exists = location.Exists;

        KindLabel = location.Kind switch
        {
            SaveLocationKind.Directory => "Folder",
            SaveLocationKind.Registry => "Registry",
            _ => "File"
        };

        // The tags say what a location holds. Saves are what the panel is about,
        // so a configuration-only path is labelled rather than left to look like
        // somewhere progress is kept.
        var tags = location.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .ToArray();

        TagsText = tags.Length > 0 ? string.Join(", ", tags) : string.Empty;

        StatusText = location.Exists ? "Present" : "Not created yet";
    }

    /// <summary>Gets the absolute path, with every placeholder resolved.</summary>
    public string Path { get; }

    /// <summary>Gets whether it is a file, a folder or a registry key.</summary>
    public string KindLabel { get; }

    /// <summary>Gets what the manifest says this location holds.</summary>
    public string TagsText { get; }

    /// <summary>Gets a value indicating whether the location is present right now.</summary>
    public bool Exists { get; }

    /// <summary>Gets the presence of this location in words.</summary>
    public string StatusText { get; }

    /// <summary>Gets a value indicating whether there is a tag line worth drawing.</summary>
    public bool HasTags => TagsText.Length > 0;
}
