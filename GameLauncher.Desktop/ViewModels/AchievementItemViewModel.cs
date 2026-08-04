using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Presents one achievement, locked or unlocked, for display.
/// </summary>
public sealed class AchievementItemViewModel
{
    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="definition">The achievement definition.</param>
    /// <param name="unlockedAt">When it was unlocked, or <see langword="null"/> if still locked.</param>
    /// <param name="gameTitle">Owning game title, shown on the library-wide page.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public AchievementItemViewModel(
        AchievementDefinition definition,
        DateTimeOffset? unlockedAt,
        string? gameTitle = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        UnlockedAt = unlockedAt;
        GameTitle = gameTitle;

        UnlockedText = unlockedAt is { } stamp
            ? $"Unlocked {RelativeTimeConverter.Format(stamp)}"
            : "Locked";
    }

    /// <summary>Gets the underlying definition.</summary>
    public AchievementDefinition Definition { get; }

    /// <summary>Gets the definition's identifier.</summary>
    public int Id => Definition.Id;

    /// <summary>Gets the achievement's display name.</summary>
    public string Title => Definition.Title;

    /// <summary>Gets the achievement's description.</summary>
    public string Description => Definition.Description;

    /// <summary>Gets the achievement's icon path, or <see langword="null"/> for the default.</summary>
    public string? IconPath => Definition.IconPath;

    /// <summary>Gets how the achievement is evaluated.</summary>
    public AchievementKind Kind => Definition.Kind;

    /// <summary>Gets the owning game's title, when shown outside that game's page.</summary>
    public string? GameTitle { get; }

    /// <summary>Gets when the achievement was unlocked, or <see langword="null"/>.</summary>
    public DateTimeOffset? UnlockedAt { get; }

    /// <summary>Gets a value indicating whether the achievement has been earned.</summary>
    public bool IsUnlocked => UnlockedAt is not null;

    /// <summary>Gets text describing the unlock state.</summary>
    public string UnlockedText { get; }

    /// <summary>Gets a short label naming how this achievement is evaluated.</summary>
    public string KindLabel => Kind switch
    {
        AchievementKind.Meta => "Meta",
        AchievementKind.SaveFile => "Save file",
        AchievementKind.Memory => "Memory",
        _ => Kind.ToString()
    };
}
