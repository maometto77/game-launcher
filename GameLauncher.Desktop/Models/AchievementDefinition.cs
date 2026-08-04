namespace GameLauncher.Desktop.Models;

/// <summary>
/// The definition of an achievement: what it is called and what has to be true
/// for it to unlock.
/// </summary>
/// <remarks>
/// Definitions are separated from unlocks so that editing a rule never destroys
/// the record of somebody having already earned it. See
/// <see cref="AchievementUnlock"/>.
/// </remarks>
public sealed class AchievementDefinition
{
    /// <summary>Auto-incrementing primary key. Zero for a definition not yet persisted.</summary>
    /// <remarks>Local to this database; never transmitted. See <see cref="GlobalKey"/>.</remarks>
    public int Id { get; set; }

    /// <summary>
    /// Stable, human-readable handle for this achievement within its game, such
    /// as <c>ACH_FINISH_CAMPAIGN</c>.
    /// </summary>
    /// <remarks>
    /// The name a shared or authored achievement set refers to. Unique per game,
    /// case-insensitively; library-wide achievements share a single namespace.
    /// </remarks>
    public string ApiName { get; set; } = string.Empty;

    /// <summary>
    /// Stable machine-independent identity, as 32 lowercase hexadecimal
    /// characters.
    /// </summary>
    /// <remarks>
    /// <see cref="ApiName"/> is what a human authors against; this is what
    /// synchronisation keys on. Both exist because two people can legitimately
    /// author different achievements with the same api name for the same game,
    /// and a merge has to be able to tell them apart.
    /// </remarks>
    public string GlobalKey { get; set; } = string.Empty;

    /// <summary>
    /// The catalog entry this achievement belongs to, or <see langword="null"/>
    /// for a library-wide achievement not tied to any single title.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership hangs off the shared catalog identity rather than a local game
    /// row, which is what allows one authored achievement set to apply to every
    /// user who owns the title. It also means achievements outlive the
    /// installation: removing a game from the library no longer erases what was
    /// earned in it.
    /// </para>
    /// <para>
    /// Library-wide meta achievements — total playtime across everything, number
    /// of games owned — legitimately belong to no title, which is why this is
    /// nullable rather than pointing at a synthetic entry.
    /// </para>
    /// </remarks>
    public string? CatalogId { get; set; }

    /// <summary>Display name of the achievement.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Description explaining how the achievement is earned.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Absolute path to the achievement icon, or <see langword="null"/> for the default icon.</summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// Coarse category, used for grouping and filtering in the interface.
    /// </summary>
    /// <remarks>
    /// Not what the engine dispatches on — see <see cref="ProviderKey"/>. Kept
    /// because it is a useful way to sort a long achievement list, and it stays
    /// accurate for the three built-in providers.
    /// </remarks>
    public AchievementKind Kind { get; set; }

    /// <summary>
    /// Key of the provider that evaluates this achievement.
    /// </summary>
    /// <remarks>
    /// The engine's dispatch key. A string rather than an enum member so that
    /// adding a provider is a registration rather than an edit to the core model.
    /// An unrecognised key is left alone rather than guessed at, so a definition
    /// authored for a provider that is not installed is inert instead of being
    /// evaluated by the wrong one.
    /// </remarks>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Kind-specific trigger configuration, stored as JSON.
    /// </summary>
    /// <remarks>
    /// Held as raw text rather than parsed columns because the three kinds have
    /// entirely disjoint configuration shapes. Storing them as JSON keeps one
    /// table instead of three sparse ones, and lets a new evaluator kind be
    /// added without a schema migration. Each evaluator deserialises this into
    /// its own strongly-typed configuration record and validates it there.
    /// </remarks>
    public string TriggerConfigJson { get; set; } = "{}";

    /// <summary>
    /// Whether the achievement's description is concealed until it is earned.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>Manual display position within its game's achievement list.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The value <see cref="StatApiName"/> must reach for this achievement to
    /// unlock, or <see langword="null"/> for an all-or-nothing achievement.
    /// </summary>
    /// <remarks>
    /// Present so an achievement can render as "47 / 100" rather than merely
    /// locked. Stored as a double because stats may be fractional (hours played,
    /// distance travelled), and narrowing to an integer here would make those
    /// inexpressible.
    /// </remarks>
    public double? ProgressTarget { get; set; }

    /// <summary>
    /// The stat this achievement is measured against, or <see langword="null"/>
    /// when it is driven directly by a trigger.
    /// </summary>
    /// <remarks>
    /// References <c>GameStatDefinition.ApiName</c> rather than its primary key,
    /// so an authored achievement set can name a stat without knowing what row
    /// identifier it will be given on import.
    /// </remarks>
    public string? StatApiName { get; set; }

    /// <summary>When this definition was last modified locally.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Gets a value indicating whether this achievement is evaluated
    /// automatically from local data with no per-game configuration.
    /// </summary>
    public bool IsAutomatic => Kind == AchievementKind.Meta;

    /// <summary>
    /// Gets a value indicating whether this achievement reports partial progress.
    /// </summary>
    public bool IsProgressive => ProgressTarget is > 0;
}
