using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Default <see cref="IRelaySyncService"/>.
/// </summary>
public sealed class RelaySyncService : IRelaySyncService
{
    /// <summary>Upper bound on items handled in one pass.</summary>
    /// <remarks>
    /// Bounded so a launcher that has been offline for months does not attempt one
    /// enormous request. Whatever is left stays queued for the next pass, which
    /// runs on the next reconnect.
    /// </remarks>
    private const int BatchSize = 200;

    private readonly IRelayApiClient _api;
    private readonly ICatalogService _catalog;
    private readonly IAchievementRepository _achievements;
    private readonly ILogger<RelaySyncService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="api">Relay HTTP client.</param>
    /// <param name="catalog">Catalog identity management.</param>
    /// <param name="achievements">Achievement persistence.</param>
    /// <param name="logger">Logger for sync diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RelaySyncService(
        IRelayApiClient api,
        ICatalogService catalog,
        IAchievementRepository achievements,
        ILogger<RelaySyncService> logger)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!_api.IsConfigured)
        {
            return SyncResult.Nothing;
        }

        // Catalog first, deliberately. An unlock can only be pushed under an
        // assigned catalog identity, so promoting entries in the same pass is what
        // lets achievements earned offline sync on the very first reconnect
        // instead of waiting for a second one.
        var (promoted, catalogCompleted) =
            await PromoteCatalogEntriesAsync(cancellationToken).ConfigureAwait(false);

        if (!catalogCompleted)
        {
            return new SyncResult(promoted, 0, Completed: false);
        }

        var (pushed, unlocksCompleted) =
            await PushUnlocksAsync(cancellationToken).ConfigureAwait(false);

        var result = new SyncResult(promoted, pushed, unlocksCompleted);

        if (result.DidWork)
        {
            _logger.LogInformation(
                "Sync pass promoted {Promoted} catalog entries and pushed {Pushed} unlocks.",
                promoted, pushed);
        }

        return result;
    }

    /// <summary>Registers provisional catalog entries with the relay.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many were promoted, and whether the pass finished.</returns>
    private async Task<(int Promoted, bool Completed)> PromoteCatalogEntriesAsync(
        CancellationToken cancellationToken)
    {
        var pending = await _catalog.GetPendingRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return (0, true);
        }

        var promoted = 0;

        foreach (var entry in pending.Take(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.MatchFingerprint))
            {
                // Nothing to resolve against. The startup repair fills these in;
                // skipping keeps one broken entry from stalling the queue.
                continue;
            }

            try
            {
                var response = await _api.ResolveCatalogAsync(
                    new CatalogResolveRequest
                    {
                        Fingerprint = entry.MatchFingerprint,
                        Title = entry.CanonicalTitle
                    },
                    cancellationToken).ConfigureAwait(false);

                await _catalog.ApplyAssignedIdentityAsync(
                    entry.CatalogId,
                    response.CatalogId,
                    source: "relay",
                    response.CanonicalTitle,
                    cancellationToken).ConfigureAwait(false);

                promoted++;
            }
            catch (RelayApiException ex) when (ex.IsTransient)
            {
                // The relay went away part way through. Stop rather than hammering
                // it; everything still provisional is retried on the next pass.
                _logger.LogDebug(ex, "Catalog promotion paused; the relay is unreachable.");
                return (promoted, false);
            }
            catch (RelayApiException ex)
            {
                // Permanent for this entry only — a malformed fingerprint, say.
                // Skipped so it cannot block everything behind it.
                _logger.LogWarning(ex, "Catalog entry {CatalogId} was rejected by the relay.", entry.CatalogId);
            }
        }

        return (promoted, true);
    }

    /// <summary>Pushes queued achievement unlocks.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many were accepted, and whether the pass finished.</returns>
    private async Task<(int Pushed, bool Completed)> PushUnlocksAsync(CancellationToken cancellationToken)
    {
        var pending = await _achievements
            .GetUnsyncedUnlocksAsync(BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return (0, true);
        }

        try
        {
            var response = await _api.SyncAchievementsAsync(
                new AchievementSyncRequest
                {
                    Unlocks = pending.Select(unlock => new AchievementUnlockDto
                    {
                        CatalogId = unlock.CatalogId,
                        ApiName = unlock.ApiName,
                        UnlockedAt = unlock.UnlockedAt
                    }).ToArray()
                },
                cancellationToken).ConfigureAwait(false);

            // Stamped on a successful response, not on Accepted > 0. Accepted
            // counts what was *new to the relay*; a replayed batch legitimately
            // returns zero and must still be marked, or it would be resent forever.
            await _achievements.MarkUnlocksSyncedAsync(
                pending.Select(unlock => unlock.DefinitionId).ToArray(),
                DateTimeOffset.Now,
                cancellationToken).ConfigureAwait(false);

            return (response.Accepted, true);
        }
        catch (RelayApiException ex) when (ex.IsTransient)
        {
            _logger.LogDebug(ex, "Unlock push paused; the relay is unreachable.");
            return (0, false);
        }
        catch (RelayApiException ex)
        {
            // Not stamped: a permanent failure here is worth investigating rather
            // than silently discarding somebody's achievements.
            _logger.LogError(ex, "The relay rejected an unlock batch.");
            return (0, false);
        }
    }
}
