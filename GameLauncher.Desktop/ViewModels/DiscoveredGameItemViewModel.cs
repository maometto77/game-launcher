using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One selectable row in the folder scan results.
/// </summary>
public sealed partial class DiscoveredGameItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="discovered">The candidate this row represents.</param>
    /// <param name="icon">
    /// The executable's icon, already frozen, or <see langword="null"/> when it
    /// has none.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="discovered"/> is <see langword="null"/>.</exception>
    public DiscoveredGameItemViewModel(DiscoveredGame discovered, ImageSource? icon)
    {
        Discovered = discovered ?? throw new ArgumentNullException(nameof(discovered));
        Icon = icon;

        // Likely games start ticked and everything else starts clear, so the
        // common case is "press Add" while nothing is ever added without the user
        // having seen it listed.
        _isSelected = discovered.IsLikelyGame;

        SizeText = ByteSizeConverter.Format(discovered.Executable.FileSizeBytes);
    }

    /// <summary>Gets the underlying scan result.</summary>
    public DiscoveredGame Discovered { get; }

    /// <summary>Gets the executable's icon, or <see langword="null"/>.</summary>
    public ImageSource? Icon { get; }

    /// <summary>Gets the suggested display title.</summary>
    public string Title => Discovered.SuggestedTitle;

    /// <summary>Gets the executable's absolute path.</summary>
    public string ExecutablePath => Discovered.ExecutablePath;

    /// <summary>Gets the formatted executable size.</summary>
    public string SizeText { get; }

    /// <summary>Gets the note explaining how this candidate was ranked, if any.</summary>
    public string? Note => Discovered.Note;

    /// <summary>Gets a value indicating whether this executable is already in the library.</summary>
    public bool IsAlreadyInLibrary => Discovered.IsAlreadyInLibrary;

    /// <summary>
    /// Gets a value indicating whether this row can be selected.
    /// </summary>
    /// <remarks>
    /// Rows already in the library are shown so the user can see the scan found
    /// them, but cannot be ticked: adding one again would create a duplicate
    /// entry pointing at the same executable.
    /// </remarks>
    public bool IsSelectable => !Discovered.IsAlreadyInLibrary;
}
