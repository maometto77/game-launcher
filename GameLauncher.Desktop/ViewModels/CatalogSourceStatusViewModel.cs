using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One catalogue source, as a row on the settings page.
/// </summary>
/// <remarks>
/// <para>
/// Answers the two questions a person actually has when Discover is empty: is
/// this source switched on, and did it last do anything. Both are otherwise
/// only visible in the log, which is a poor place to keep the answer to "why is
/// nothing here".
/// </para>
/// <para>
/// Deliberately shallow. It reports availability and the last pass, not
/// selectors, cursors or parse counts — those belong in the log, and putting
/// them on a settings page would bury the two facts that matter.
/// </para>
/// </remarks>
public sealed class CatalogSourceStatusViewModel
{
    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="source">The source being described.</param>
    /// <param name="lastRun">Its most recent import pass, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public CatalogSourceStatusViewModel(ICatalogSource source, CatalogImportRun? lastRun)
    {
        ArgumentNullException.ThrowIfNull(source);

        Key = source.Key;
        DisplayName = source.DisplayName;
        IsAvailable = source.IsAvailable;

        StatusText = IsAvailable ? "Ready" : "Not configured";

        LastImportText = lastRun switch
        {
            null => "Never imported",

            // An unfinished run is one that was interrupted, which is worth
            // distinguishing from one that ran and found nothing: the first
            // will resume, the second will not.
            { CompletedAt: null } => "Interrupted — will resume",

            { ItemsSeen: 0 } => $"Last run {Describe(lastRun.StartedAt)}, nothing found",
            _ => $"Last run {Describe(lastRun.StartedAt)}, {lastRun.ItemsSeen} item(s)"
        };

        // A pass that read items and stored none is the shape of a source whose
        // site changed underneath it. Saying so here is the difference between
        // a person editing a selector and a person wondering why Discover is
        // empty.
        NeedsAttention =
            IsAvailable &&
            lastRun is { CompletedAt: not null, ItemsSeen: > 0, ItemsChanged: 0, ListingsAdded: 0 };
    }

    /// <summary>Gets the source's dispatch key.</summary>
    public string Key { get; }

    /// <summary>Gets the name to show.</summary>
    public string DisplayName { get; }

    /// <summary>Gets a value indicating whether the source can currently be used.</summary>
    public bool IsAvailable { get; }

    /// <summary>Gets whether it is ready, in a word.</summary>
    public string StatusText { get; }

    /// <summary>Gets when it last ran, in words.</summary>
    public string LastImportText { get; }

    /// <summary>Gets a value indicating whether the last pass looks wrong.</summary>
    public bool NeedsAttention { get; }

    /// <summary>Describes an instant the way a person would.</summary>
    /// <param name="moment">When it happened.</param>
    /// <returns>Something readable.</returns>
    private static string Describe(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.Now - moment;

        return elapsed switch
        {
            { TotalMinutes: < 2 } => "just now",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} minutes ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} hours ago",
            { TotalDays: < 30 } => $"{(int)elapsed.TotalDays} days ago",
            _ => moment.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture)
        };
    }
}
