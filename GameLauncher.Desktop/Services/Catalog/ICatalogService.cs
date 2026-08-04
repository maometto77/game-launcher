using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Catalog;

/// <summary>
/// Assigns and maintains shared catalog identity for local games.
/// </summary>
/// <remarks>
/// The launcher must work fully offline, so identity is minted locally and
/// reconciled later rather than requiring a relay round trip before a game can be
/// added. This service owns that lifecycle; the relay client will call
/// <see cref="ApplyAssignedIdentityAsync"/> once it exists.
/// </remarks>
public interface ICatalogService
{
    /// <summary>
    /// Computes the deterministic signature used to recognise the same title
    /// across machines and users.
    /// </summary>
    /// <param name="title">The title the game is known by locally.</param>
    /// <param name="executable">Metadata read from the game's executable, if available.</param>
    /// <returns>A lowercase hexadecimal fingerprint.</returns>
    string ComputeFingerprint(string title, ExecutableInfo? executable);

    /// <summary>
    /// Returns the catalog entry for a game, creating a provisional one if
    /// nothing matches.
    /// </summary>
    /// <param name="title">The title the game is known by locally.</param>
    /// <param name="executable">Metadata read from the game's executable, if available.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The matched or newly created entry.</returns>
    Task<CatalogEntry> EnsureEntryAsync(
        string title,
        ExecutableInfo? executable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an identity a relay assigned to a provisional entry, merging if
    /// the assigned identity is already present locally.
    /// </summary>
    /// <param name="provisionalCatalogId">The locally minted identity being retired.</param>
    /// <param name="assignedCatalogId">The identity the relay assigned.</param>
    /// <param name="source">The relay that assigned it.</param>
    /// <param name="canonicalTitle">The relay's canonical title.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The surviving catalog identity.</returns>
    /// <remarks>
    /// Exists now, ahead of the relay, because it is the step that determines
    /// whether the schema actually supports promotion — and it is far cheaper to
    /// discover a flaw in that before there is data to migrate.
    /// </remarks>
    Task<string> ApplyAssignedIdentityAsync(
        string provisionalCatalogId,
        string assignedCatalogId,
        string source,
        string canonicalTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the entries still awaiting a server-assigned identity.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Entries to register the next time a relay is reachable.</returns>
    Task<IReadOnlyList<CatalogEntry>> GetPendingRegistrationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a catalog identity to the entry that currently represents it,
    /// following any merge redirects.
    /// </summary>
    /// <param name="catalogId">Any catalog identity, current or superseded.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The canonical entry, or <see langword="null"/> when unknown.</returns>
    /// <remarks>
    /// Callers holding a stored identity should resolve before use. Identities are
    /// immutable, so a stored one stays valid indefinitely — but the title it
    /// belongs to may since have been merged into another.
    /// </remarks>
    Task<CatalogEntry?> ResolveAsync(string catalogId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that another fingerprint belongs to a known title.
    /// </summary>
    /// <param name="catalogId">The entry the fingerprint belongs to.</param>
    /// <param name="fingerprint">The fingerprint to bind.</param>
    /// <param name="source">The authority recording the alias.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> when a new alias was recorded.</returns>
    /// <remarks>
    /// How a relay teaches this client that a re-release, a launcher executable
    /// and the game's own binary are all the same title. A fingerprint already
    /// bound elsewhere is left alone; rebinding is a merge, not an alias.
    /// </remarks>
    Task<bool> RegisterAliasAsync(
        string catalogId,
        string fingerprint,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a fingerprint and alias to any catalog entry that lacks one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the repair.</param>
    /// <returns>The number of entries repaired.</returns>
    /// <remarks>
    /// <para>
    /// Entries created by the schema v3 backfill carry an empty fingerprint: that
    /// migration ran in SQL, which cannot hash normalised metadata, and had no
    /// access to the executables anyway. Such an entry can never be matched by
    /// fingerprint, so re-adding the same game would silently create a second
    /// catalog entry for one title.
    /// </para>
    /// <para>
    /// This repairs them in code, where the executable can actually be inspected.
    /// It is idempotent and cheap once there is nothing left to fix, so it runs at
    /// every startup rather than being tied to a particular schema version.
    /// </para>
    /// </remarks>
    Task<int> RepairMissingFingerprintsAsync(CancellationToken cancellationToken = default);
}
