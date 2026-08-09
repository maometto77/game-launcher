namespace GameLauncher.Desktop.Models;

/// <summary>
/// A top-level destination in the sidebar.
/// </summary>
/// <remarks>
/// <para>
/// Five sections, each answering a different question: what do I have, what
/// exists, what is transferring, who am I playing with, and how is this
/// configured. Pages that answer the same question live inside one section as
/// sub-navigation rather than competing for a sidebar slot — a sidebar that
/// grows a row per page stops being navigation and becomes a list.
/// </para>
/// <para>
/// Renumbered when the eight original entries were consolidated. Nothing
/// persists these values — they exist only for the lifetime of a window — so
/// unlike a schema enum there is no stored data to invalidate.
/// </para>
/// </remarks>
public enum NavigationSection
{
    /// <summary>
    /// Games the user has, with their collections and achievements.
    /// </summary>
    /// <remarks>The landing section: what someone opens a launcher to reach.</remarks>
    Library = 0,

    /// <summary>Games that exist, from the discovery catalogue.</summary>
    Search = 1,

    /// <summary>The download queue.</summary>
    Downloads = 2,

    /// <summary>Friend codes, requests and presence.</summary>
    Friends = 3,

    /// <summary>Application settings.</summary>
    Settings = 4
}

/// <summary>
/// One destination inside a top-level section.
/// </summary>
/// <remarks>
/// Modelled as an object with an activation delegate rather than a second enum,
/// so a section's sub-navigation is described where the section is defined
/// instead of in a switch somewhere else that has to be kept in step with it.
/// </remarks>
public sealed class SubNavigationItem
{
    /// <summary>Stable identifier, used to restore the last choice.</summary>
    public required string Key { get; init; }

    /// <summary>What the tab says.</summary>
    public required string Label { get; init; }

    /// <summary>Shows this sub-view.</summary>
    public required Func<CancellationToken, Task> ActivateAsync { get; init; }
}
