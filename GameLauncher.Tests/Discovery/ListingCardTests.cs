using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery.Images;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Discovery.Sources;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers what a catalogue card shows: which sources describe the game, whether
/// its saves are known, and what its button says as the queue works through it.
/// </summary>
public sealed class ListingCardTests
{
    [Fact]
    public async Task A_card_shows_every_source_that_describes_the_game()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom")]);

        await AddSourceAsync(repository, "lst_1", InternetArchiveCatalogSource.SourceKey);
        await AddSourceAsync(repository, "lst_1", MyAbandonwareCatalogSource.SourceKey);

        var reloaded = await repository.GetAsync("lst_1");
        var card = new ListingItemViewModel(reloaded!, host.Resolve<IListingImageCache>());

        // Both, not just the one that won the merge: a card showing where a game
        // can be found has to show all of them.
        Assert.Equal(2, card.SourceBadges.Count);
        Assert.Contains(card.SourceBadges, badge => badge.Label == "Archive.org");
        Assert.True(card.HasSourceBadges);

        // The Archive can supply the file, so its badge is not the quiet kind.
        var archive = card.SourceBadges.Single(badge => badge.Label == "Archive.org");

        Assert.False(archive.IsMetadataOnly);
        Assert.True(archive.CanInstallFrom);

        // A pressed badge is the whole instruction: which source, and for which
        // listing. Without both it would have to reach back up the visual tree
        // for the other half of what it means.
        Assert.Equal(InternetArchiveCatalogSource.SourceKey, archive.SourceKey);
        Assert.Equal("lst_1", archive.ListingId);
    }

    [Fact]
    public async Task The_metadata_only_source_is_labelled_as_such()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom")]);
        await AddSourceAsync(repository, "lst_1", MyAbandonwareCatalogSource.SourceKey);

        var reloaded = await repository.GetAsync("lst_1");
        var card = new ListingItemViewModel(reloaded!, host.Resolve<IListingImageCache>());

        // A badge implying the game can be fetched from a site whose own rules
        // disallow it would be misleading — in its wording and in how it is drawn.
        var badge = card.SourceBadges.Single();

        Assert.Contains("metadata", badge.Label, StringComparison.OrdinalIgnoreCase);
        Assert.True(badge.IsMetadataOnly);

        // And its badge is not a way to start a download that cannot happen.
        Assert.False(badge.CanInstallFrom);
    }

    [Fact]
    public void A_card_with_no_source_rows_draws_no_badges()
    {
        using var host = new TestAppHost();

        var card = new ListingItemViewModel(
            Listing("lst_1", "Doom"), host.Resolve<IListingImageCache>());

        Assert.False(card.HasSourceBadges);
        Assert.Empty(card.SourceBadges);
    }

    [Fact]
    public void The_save_badge_is_off_until_the_manifest_says_otherwise()
    {
        using var host = new TestAppHost();

        var card = new ListingItemViewModel(
            Listing("lst_1", "Doom"), host.Resolve<IListingImageCache>());

        Assert.False(card.HasKnownSaves);

        card.ApplySaveState(known: true);
        Assert.True(card.HasKnownSaves);
    }

    [Fact]
    public void The_button_reports_the_queues_state_rather_than_its_own()
    {
        using var host = new TestAppHost();

        var card = new ListingItemViewModel(
            Listing("lst_1", "Doom"), host.Resolve<IListingImageCache>());

        card.ApplyQueueState(null);
        Assert.Equal("Install", card.ActionText);
        Assert.True(card.IsActionEnabled);
        Assert.False(card.IsQueued);

        card.ApplyQueueState(Job(DownloadPhase.Queued));
        Assert.Equal("Queued", card.ActionText);
        Assert.False(card.IsActionEnabled);
        Assert.True(card.IsQueued);

        // A running download shows its own progress on the button, so the card
        // is useful without leaving the catalogue.
        card.ApplyQueueState(Job(DownloadPhase.Downloading, received: 250, total: 1000));
        Assert.Equal("25%", card.ActionText);

        card.ApplyQueueState(Job(DownloadPhase.ReadyToInstall));
        Assert.Equal("Finish install", card.ActionText);
        Assert.True(card.IsActionEnabled);

        card.ApplyQueueState(Job(DownloadPhase.Completed));
        Assert.Equal("Installed", card.ActionText);
        Assert.False(card.IsActionEnabled);
        Assert.True(card.IsInstalled);

        card.ApplyQueueState(Job(DownloadPhase.Failed));
        Assert.Equal("Retry", card.ActionText);
        Assert.True(card.IsActionEnabled);
    }

    [Fact]
    public void A_listing_nothing_can_supply_cannot_be_installed_from_the_card()
    {
        using var host = new TestAppHost();

        var listing = Listing("lst_1", "Oregon Trail");
        listing.IsDownloadable = false;

        var card = new ListingItemViewModel(listing, host.Resolve<IListingImageCache>());

        card.ApplyQueueState(null);

        Assert.False(card.IsActionEnabled);
    }

    private static DownloadJob Job(DownloadPhase phase, long received = 0, long? total = null) => new()
    {
        JobId = "job_1",
        ListingId = "lst_1",
        Title = "Doom",
        Phase = phase,
        BytesReceived = received,
        TotalBytes = total
    };

    private static CatalogListing Listing(string id, string title) => new()
    {
        ListingId = id,
        Title = title,
        SortTitle = TitleNormalizer.ToSortTitle(title),
        Year = 1993,
        MatchKey = TitleNormalizer.ComputeMatchKey(title, 1993),
        PrimarySourceKey = InternetArchiveCatalogSource.SourceKey,
        ContentHash = id,
        IsDownloadable = true
    };

    private static Task AddSourceAsync(
        ICatalogListingRepository repository,
        string listingId,
        string sourceKey) =>
        repository.UpsertSourceAsync(new ListingSourceRecord
        {
            ListingId = listingId,
            SourceKey = sourceKey,
            SourceItemId = $"{sourceKey}-item",
            SourceUrl = $"https://{sourceKey}.test/item",
            NormalizedJson = "{}",
            FetchedAt = DateTimeOffset.Now,
            SourceContentHash = sourceKey
        });
}
