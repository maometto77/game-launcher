using System.Security.Cryptography;
using System.Text;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Catalog;

/// <summary>
/// Default <see cref="ICatalogService"/>.
/// </summary>
public sealed class CatalogService : ICatalogService
{
    private readonly ICatalogRepository _catalog;
    private readonly IGameRepository _games;
    private readonly IExecutableInspector _inspector;
    private readonly ILogger<CatalogService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="catalog">Catalog persistence.</param>
    /// <param name="games">Used to find the executable behind an entry when repairing fingerprints.</param>
    /// <param name="inspector">Reads executable metadata for fingerprinting.</param>
    /// <param name="logger">Logger for catalog diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CatalogService(
        ICatalogRepository catalog,
        IGameRepository games,
        IExecutableInspector inspector,
        ILogger<CatalogService> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Built only from values the publisher put in the binary — product name,
    /// company, executable file name — and never from anything machine-specific.
    /// Install path, file size and modification time are all deliberately
    /// excluded: two people who installed the same game to different drives, or
    /// who are on different patch levels, must still produce the same
    /// fingerprint or the catalog fragments into one entry per user.
    /// </para>
    /// <para>
    /// The local title is used only when the executable carries no product name,
    /// because titles are user-editable and therefore not stable.
    /// </para>
    /// </remarks>
    public string ComputeFingerprint(string title, ExecutableInfo? executable)
    {
        var product = Normalise(executable?.ProductName)
                      ?? Normalise(executable?.FileDescription)
                      ?? Normalise(title)
                      ?? string.Empty;

        var company = Normalise(executable?.CompanyName) ?? string.Empty;
        var fileName = Normalise(Path.GetFileNameWithoutExtension(executable?.FileName)) ?? string.Empty;

        var material = string.Join('|', product, company, fileName);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        // Half the digest is ample: this is a lookup key, not a security
        // boundary, and 128 bits makes accidental collision irrelevant.
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <inheritdoc />
    public async Task<CatalogEntry> EnsureEntryAsync(
        string title,
        ExecutableInfo? executable,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = ComputeFingerprint(title, executable);

        var existing = await _catalog.FindByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug(
                "Matched {Title} to existing catalog entry {CatalogId}.", title, existing.CatalogId);
            return existing;
        }

        var now = DateTimeOffset.Now;

        var entry = new CatalogEntry
        {
            // Provisional until a relay assigns a real identity. The prefix makes
            // a locally-minted id obvious at a glance in logs and in the database.
            CatalogId = CatalogEntry.ProvisionalPrefix + Guid.NewGuid().ToString("N"),
            Source = CatalogEntry.LocalSource,
            IsProvisional = true,
            CanonicalTitle = title,
            MatchFingerprint = fingerprint,
            CreatedAt = now,
            UpdatedAt = now,
            SyncedAt = null
        };

        await _catalog.AddAsync(entry, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created provisional catalog entry {CatalogId} for {Title}.", entry.CatalogId, title);

        return entry;
    }

    /// <inheritdoc />
    public async Task<string> ApplyAssignedIdentityAsync(
        string provisionalCatalogId,
        string assignedCatalogId,
        string source,
        string canonicalTitle,
        CancellationToken cancellationToken = default)
    {
        var promoted = await _catalog
            .PromoteAsync(provisionalCatalogId, assignedCatalogId, source, canonicalTitle, cancellationToken)
            .ConfigureAwait(false);

        if (promoted)
        {
            return assignedCatalogId;
        }

        // The assigned identity already exists locally, so the two entries are
        // the same title arrived at by different routes. Fold the provisional one
        // into it; the surviving identity is the assigned one either way.
        await _catalog
            .MergeIntoAsync(provisionalCatalogId, assignedCatalogId, cancellationToken)
            .ConfigureAwait(false);

        // Resolved rather than returned directly: the assigned entry may itself
        // have been merged into a third since this client last synchronised.
        var canonical = await _catalog
            .ResolveCanonicalAsync(assignedCatalogId, cancellationToken)
            .ConfigureAwait(false);

        return canonical?.CatalogId ?? assignedCatalogId;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CatalogEntry>> GetPendingRegistrationsAsync(
        CancellationToken cancellationToken = default) =>
        _catalog.GetProvisionalAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CatalogEntry?> ResolveAsync(string catalogId, CancellationToken cancellationToken = default) =>
        _catalog.ResolveCanonicalAsync(catalogId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RegisterAliasAsync(
        string catalogId,
        string fingerprint,
        string source,
        CancellationToken cancellationToken = default) =>
        _catalog.AddAliasAsync(fingerprint, catalogId, source, cancellationToken);

    /// <inheritdoc />
    public async Task<int> RepairMissingFingerprintsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _catalog.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var broken = entries
            .Where(entry => !entry.IsSuperseded && string.IsNullOrWhiteSpace(entry.MatchFingerprint))
            .ToList();

        if (broken.Count == 0)
        {
            return 0;
        }

        var games = await _games.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var gamesByCatalog = games
            .Where(game => !string.IsNullOrWhiteSpace(game.CatalogId))
            .GroupBy(game => game.CatalogId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var repaired = 0;

        foreach (var entry in broken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExecutableInfo? executable = null;

            if (gamesByCatalog.TryGetValue(entry.CatalogId, out var game) &&
                !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                File.Exists(game.ExecutablePath))
            {
                try
                {
                    executable = await _inspector
                        .InspectAsync(game.ExecutablePath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Falls back to a title-only fingerprint, which is exactly what
                    // a game added without a readable executable would have got.
                    _logger.LogDebug(ex, "Could not inspect {Path} while repairing catalog fingerprints.",
                        game.ExecutablePath);
                }
            }

            var fingerprint = ComputeFingerprint(entry.CanonicalTitle, executable);

            // A fingerprint already bound elsewhere means another entry legitimately
            // owns it. Leaving this one unfingerprinted is better than stealing the
            // binding, which would silently reassign somebody's achievements.
            if (!await _catalog
                    .AddAliasAsync(fingerprint, entry.CatalogId, entry.Source, cancellationToken)
                    .ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Fingerprint for catalog entry {CatalogId} ({Title}) is already bound elsewhere; " +
                    "it may be a duplicate of another entry.",
                    entry.CatalogId, entry.CanonicalTitle);
                continue;
            }

            entry.MatchFingerprint = fingerprint;
            await _catalog.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
            repaired++;
        }

        if (repaired > 0)
        {
            _logger.LogInformation(
                "Repaired {Count} catalog entr{Suffix} that had no fingerprint.",
                repaired, repaired == 1 ? "y" : "ies");
        }

        return repaired;
    }

    /// <summary>
    /// Reduces a value to comparable form: lower case, letters and digits only.
    /// </summary>
    /// <param name="value">Raw text.</param>
    /// <returns>The normalised value, or <see langword="null"/> when nothing is left.</returns>
    /// <remarks>
    /// Punctuation and spacing vary between how a publisher writes a name in the
    /// binary and how a store writes it — "Hollow Signal", "Hollow-Signal",
    /// "HollowSignal" are one game. Stripping everything but letters and digits
    /// collapses those to a single value.
    /// </remarks>
    private static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
