using System.Globalization;
using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Presents one achievement — locked, unlocked, hidden or partially progressed —
/// for display.
/// </summary>
/// <remarks>
/// <para>
/// Concealment of hidden achievements is resolved here rather than in the view.
/// A template that merely declines to draw the title still has the real text
/// bound into the visual tree, where automation, a tooltip or a copy command can
/// reach it. Substituting the text at this boundary means the secret never
/// arrives at the interface at all.
/// </para>
/// <para>
/// Holds no evaluation logic. It is a projection of rows the caller has already
/// read.
/// </para>
/// </remarks>
public sealed class AchievementItemViewModel
{
    /// <summary>Title shown in place of a hidden achievement's real name.</summary>
    public const string ConcealedTitle = "Hidden achievement";

    /// <summary>Description shown in place of a hidden achievement's real one.</summary>
    public const string ConcealedDescription = "Revealed when you unlock it.";

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="definition">The achievement definition.</param>
    /// <param name="unlockedAt">When it was unlocked, or <see langword="null"/> if still locked.</param>
    /// <param name="progressValue">
    /// Progress recorded towards <see cref="AchievementDefinition.ProgressTarget"/>,
    /// or <see langword="null"/> when none has been recorded.
    /// </param>
    /// <param name="isProviderAvailable">
    /// Whether a provider matching the definition's key is installed. A definition
    /// whose provider is missing is shown as inert rather than merely unearned.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public AchievementItemViewModel(
        AchievementDefinition definition,
        DateTimeOffset? unlockedAt,
        double? progressValue = null,
        bool isProviderAvailable = true)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        UnlockedAt = unlockedAt;
        ProgressValue = progressValue;
        IsProviderAvailable = isProviderAvailable;

        UnlockedText = unlockedAt is { } stamp
            ? $"Unlocked {RelativeTimeConverter.Format(stamp)}"
            : "Locked";
    }

    /// <summary>Gets the underlying definition.</summary>
    public AchievementDefinition Definition { get; }

    /// <summary>Gets the definition's identifier.</summary>
    public int Id => Definition.Id;

    /// <summary>Gets the achievement's authored display name.</summary>
    /// <remarks>
    /// The real title regardless of concealment, so lists can be sorted
    /// consistently. Views bind <see cref="DisplayTitle"/>.
    /// </remarks>
    public string Title => Definition.Title;

    /// <summary>Gets how the achievement is evaluated.</summary>
    public AchievementKind Kind => Definition.Kind;

    /// <summary>Gets the key of the provider that evaluates this achievement.</summary>
    public string ProviderKey => Definition.ProviderKey;

    /// <summary>Gets when the achievement was unlocked, or <see langword="null"/>.</summary>
    public DateTimeOffset? UnlockedAt { get; }

    /// <summary>Gets a value indicating whether the achievement has been earned.</summary>
    public bool IsUnlocked => UnlockedAt is not null;

    /// <summary>Gets text describing the unlock state.</summary>
    public string UnlockedText { get; }

    /// <summary>Gets a value indicating whether a provider for this definition is installed.</summary>
    public bool IsProviderAvailable { get; }

    /// <summary>
    /// Gets a value indicating whether this achievement's details are currently
    /// concealed.
    /// </summary>
    /// <remarks>
    /// Concealment ends at the moment of unlocking and never resumes: an earned
    /// achievement is something the user is entitled to read.
    /// </remarks>
    public bool IsConcealed => Definition.IsHidden && !IsUnlocked;

    /// <summary>Gets the title to display, concealed while hidden and locked.</summary>
    public string DisplayTitle => IsConcealed ? ConcealedTitle : Definition.Title;

    /// <summary>Gets the description to display, concealed while hidden and locked.</summary>
    public string DisplayDescription => IsConcealed ? ConcealedDescription : Definition.Description;

    /// <summary>
    /// Gets the icon to display, or <see langword="null"/> for the placeholder.
    /// </summary>
    /// <remarks>
    /// Suppressed while concealed. An achievement's artwork routinely gives away
    /// what it is for, so showing it would defeat hiding the text.
    /// </remarks>
    public string? DisplayIconPath => IsConcealed ? null : Definition.IconPath;

    /// <summary>Gets the recorded progress value, or <see langword="null"/> when none.</summary>
    public double? ProgressValue { get; }

    /// <summary>
    /// Gets a value indicating whether a progress bar should be shown.
    /// </summary>
    /// <remarks>
    /// Suppressed once unlocked — a full bar beside an earned achievement is
    /// noise — and while concealed, because "3 of 50" discloses both the goal and
    /// how close the player is to something they are not meant to see yet.
    /// </remarks>
    public bool HasProgress => !IsUnlocked && !IsConcealed && Definition.IsProgressive;

    /// <summary>Gets progress towards the target as a percentage, clamped to 0–100.</summary>
    public double ProgressPercent
    {
        get
        {
            if (Definition.ProgressTarget is not { } target || target <= 0)
            {
                return 0;
            }

            return Math.Clamp((ProgressValue ?? 0) / target * 100d, 0d, 100d);
        }
    }

    /// <summary>Gets progress as text, such as <c>3 / 10</c>.</summary>
    public string ProgressText => HasProgress
        ? $"{Format(ProgressValue ?? 0)} / {Format(Definition.ProgressTarget ?? 0)}"
        : string.Empty;

    /// <summary>Gets a short label naming how this achievement is evaluated.</summary>
    public string KindLabel => Kind switch
    {
        AchievementKind.Meta => "Meta",
        AchievementKind.SaveFile => "Save file",
        AchievementKind.Memory => "Memory",
        _ => Kind.ToString()
    };

    /// <summary>
    /// Gets a message explaining why this achievement cannot be evaluated, or
    /// <see langword="null"/> when it can.
    /// </summary>
    public string? ProviderWarning => IsProviderAvailable
        ? null
        : $"No '{ProviderKey}' provider is installed, so this achievement is never evaluated.";

    /// <summary>
    /// Formats a progress number without trailing zeroes.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The value as display text.</returns>
    /// <remarks>
    /// Progress is stored as a double because stats can be fractional, but most
    /// are whole counts and "7 / 10" reads better than "7.00 / 10.00".
    /// </remarks>
    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);
}
