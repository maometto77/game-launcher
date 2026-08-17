using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Discovery.Images;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Downloads;
using GameLauncher.Desktop.Services.Saves;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the discovery catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Reads and presents. It never imports anything itself — refreshing is the
/// background service's job and installing is the install service's, and both
/// are reached through their interfaces rather than reimplemented here.
/// </para>
/// <para>
/// Results are paged, never loaded whole. A catalogue of several thousand
/// listings in one observable collection would make every keystroke in the
/// search box rebuild the entire visual tree.
/// </para>
/// </remarks>
public sealed partial class DiscoverViewModel : ViewModelBase
{
    /// <summary>How many tiles are fetched at a time.</summary>
    private const int PageSize = 60;

    private readonly ICatalogListingRepository _listings;
    private readonly IListingImageCache _images;
    private readonly IListingInstallService _install;
    private readonly IDownloadQueue _queue;
    private readonly ISavePathResolver _saves;
    private readonly ICatalogImportService _import;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DiscoverViewModel> _logger;

    /// <summary>Cancels an in-flight query when the filter changes underneath it.</summary>
    private CancellationTokenSource? _queryCancellation;

    [ObservableProperty]
    private ObservableCollection<ListingItemViewModel> _listingsView = [];

    [ObservableProperty]
    private ObservableCollection<string> _genres = [];

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string? _selectedGenre;

    [ObservableProperty]
    private bool _downloadableOnly = true;

    [ObservableProperty]
    private CatalogListingSort _sort = CatalogListingSort.Relevance;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isCatalogEmpty;

    [ObservableProperty]
    private bool _isDiscoveryEnabled;

    [ObservableProperty]
    private string? _statusMessage;

    private int _total;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="listings">Supplies the catalogue.</param>
    /// <param name="images">Resolves cover art on demand.</param>
    /// <param name="install">Reads a listing's mirrors for the details shown on a card.</param>
    /// <param name="queue">Runs the download and reports what each card's button should say.</param>
    /// <param name="saves">Consulted, without loading, for the save badge.</param>
    /// <param name="import">Consulted for whether a refresh is running, and to start one.</param>
    /// <param name="settings">Supplies whether discovery is switched on.</param>
    /// <param name="dialogs">Confirmation and error prompts.</param>
    /// <param name="dispatcher">Marshals import notifications onto the interface thread.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DiscoverViewModel(
        ICatalogListingRepository listings,
        IListingImageCache images,
        IListingInstallService install,
        IDownloadQueue queue,
        ISavePathResolver saves,
        ICatalogImportService import,
        ISettingsService settings,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger<DiscoverViewModel> logger)
    {
        _listings = listings ?? throw new ArgumentNullException(nameof(listings));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        IsDiscoveryEnabled = _settings.Current.DiscoveryEnabled;

        _import.CatalogUpdated += OnCatalogUpdated;
        _queue.JobChanged += OnQueueChanged;

        await LoadFacetsAsync(cancellationToken).ConfigureAwait(true);
        await QueryAsync(reset: true, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        _import.CatalogUpdated -= OnCatalogUpdated;
        _queue.JobChanged -= OnQueueChanged;

        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;

        return Task.CompletedTask;
    }

    /// <summary>Re-runs the query when the search text changes.</summary>
    /// <param name="value">The new search text.</param>
    partial void OnSearchTextChanged(string? value) => _ = QueryAsync(reset: true, CancellationToken.None);

    /// <summary>Re-runs the query when the genre filter changes.</summary>
    /// <param name="value">The newly selected genre.</param>
    partial void OnSelectedGenreChanged(string? value) => _ = QueryAsync(reset: true, CancellationToken.None);

    /// <summary>Re-runs the query when the downloadable filter changes.</summary>
    /// <param name="value">Whether to show only installable listings.</param>
    partial void OnDownloadableOnlyChanged(bool value) => _ = QueryAsync(reset: true, CancellationToken.None);

    /// <summary>Re-runs the query when the sort order changes.</summary>
    /// <param name="value">The new order.</param>
    partial void OnSortChanged(CatalogListingSort value) => _ = QueryAsync(reset: true, CancellationToken.None);

    /// <summary>Loads the next page of results.</summary>
    /// <returns>A task that completes when the page has been appended.</returns>
    [RelayCommand]
    private Task LoadMoreAsync() => QueryAsync(reset: false, CancellationToken.None);

    /// <summary>Clears every filter.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        SelectedGenre = null;
        SearchText = null;
    }

    /// <summary>
    /// Adds a listing to the download queue.
    /// </summary>
    /// <param name="item">The card whose button was pressed.</param>
    /// <remarks>
    /// Hands the work to the queue and returns. The card then reports the
    /// queue's state rather than its own, so the button says the same thing
    /// whether the user stays on this page, navigates away, or comes back.
    /// </remarks>
    [RelayCommand]
    private void Install(ListingItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        ClearError();

        // A finished download waiting for its executable is completed on the
        // Downloads page, where the choice can actually be presented.
        if (item.Listing.IsDownloadable is false && !item.IsQueued)
        {
            SetErrorMessage($"'{item.Title}' is listed but no source offers a download for it.");
            return;
        }

        var job = _queue.Enqueue(item.ListingId, item.Title);

        item.ApplyQueueState(job);

        StatusMessage = $"'{item.Title}' added to the download queue.";
    }

    /// <summary>
    /// Starts a catalogue refresh by hand.
    /// </summary>
    /// <returns>A task that completes when the refresh finishes.</returns>
    /// <remarks>
    /// The same pass the background service runs. Offered because waiting up to
    /// a day to see a change is a poor way to find out whether a source works.
    /// </remarks>
    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (_import.IsRunning)
        {
            StatusMessage = "A catalogue refresh is already running.";
            return;
        }

        IsBusy = true;
        ClearError();

        try
        {
            var progress = new Progress<ImportProgress>(update => StatusMessage = update.Message);
            var result = await _import.RunAsync(new ImportRunOptions(), progress).ConfigureAwait(true);

            StatusMessage = result.HasChanges
                ? $"Added {result.ListingsAdded} and updated {result.ItemsChanged - result.ListingsAdded}."
                : "The catalogue is already up to date.";

            await LoadFacetsAsync(CancellationToken.None).ConfigureAwait(true);
            await QueryAsync(reset: true, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A manual catalogue refresh failed.");
            SetErrorMessage($"The refresh failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Loads the facet values available for filtering.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    private async Task LoadFacetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var facets = await _listings.GetFacetsAsync(cancellationToken).ConfigureAwait(true);

            Genres = new ObservableCollection<string>(facets.Genres.Select(facet => facet.Name));
        }
        catch (Exception ex)
        {
            // Facets are a convenience. Losing them should not stop the page.
            _logger.LogWarning(ex, "Loading catalogue facets failed.");
        }
    }

    /// <summary>
    /// Runs the current query.
    /// </summary>
    /// <param name="reset">Whether to start a new result set or append a page.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    private async Task QueryAsync(bool reset, CancellationToken cancellationToken)
    {
        // Each keystroke supersedes the query before it. Without this, a slow
        // query for "d" can land after the one for "doom" and replace it.
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = _queryCancellation.Token;

        try
        {
            var query = new CatalogListingQuery
            {
                SearchText = SearchText,
                Genre = SelectedGenre,
                DownloadableOnly = DownloadableOnly,
                Sort = Sort,
                Skip = reset ? 0 : ListingsView.Count,
                Take = PageSize
            };

            var page = await _listings.QueryAsync(query, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (reset)
            {
                ListingsView = [];
            }

            foreach (var listing in page.Items)
            {
                var item = new ListingItemViewModel(listing, _images);

                item.ApplyQueueState(FindJob(listing.ListingId));

                ListingsView.Add(item);
            }

            _total = page.TotalCount;
            HasMore = ListingsView.Count < _total;
            IsCatalogEmpty = _total == 0 && string.IsNullOrWhiteSpace(SearchText) && SelectedGenre is null;

            SummaryText = _total == 0
                ? "Nothing matches."
                : $"Showing {ListingsView.Count} of {_total:N0}.";

            // Covers and save badges are resolved after the cards exist, so the
            // grid appears at once and fills in rather than waiting on either.
            _ = LoadCoversAsync(page.Items.Count, token);
            _ = LoadSaveBadgesAsync(page.Items.Count, token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Querying the catalogue failed.");
            SetErrorMessage($"The catalogue could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches covers for the tiles just added.
    /// </summary>
    /// <param name="added">How many tiles were appended.</param>
    /// <param name="cancellationToken">Cancels the fetches.</param>
    private async Task LoadCoversAsync(int added, CancellationToken cancellationToken)
    {
        var tiles = ListingsView.Skip(Math.Max(0, ListingsView.Count - added)).ToArray();

        foreach (var tile in tiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await tile.LoadCoverAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Marks the cards whose game the save manifest already covers.
    /// </summary>
    /// <param name="added">How many cards were appended.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <remarks>
    /// Skipped entirely unless the manifest is already loaded. Browsing a
    /// catalogue must never trigger a sixteen-megabyte download to decide
    /// whether to draw a badge.
    /// </remarks>
    private async Task LoadSaveBadgesAsync(int added, CancellationToken cancellationToken)
    {
        if (!_saves.IsLoaded)
        {
            return;
        }

        var cards = ListingsView.Skip(Math.Max(0, ListingsView.Count - added)).ToArray();

        foreach (var card in cards)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var result = await _saves
                    .ResolveAsync(new SavePathQuery { Title = card.Title, IncludeMissing = true },
                        cancellationToken)
                    .ConfigureAwait(true);

                card.ApplySaveState(result.Found);
            }
            catch (Exception ex)
            {
                // A badge is decoration. Losing one must not disturb the page.
                _logger.LogDebug(ex, "Resolving saves for '{Title}' failed.", card.Title);
            }
        }
    }

    /// <summary>Finds the queue's job for a listing, if it has one.</summary>
    /// <param name="listingId">The listing to look for.</param>
    /// <returns>The job, or <see langword="null"/>.</returns>
    private DownloadJob? FindJob(string listingId) =>
        _queue.Jobs.FirstOrDefault(job =>
            string.Equals(job.ListingId, listingId, StringComparison.Ordinal));

    /// <summary>
    /// Updates the card whose game the queue just reported on.
    /// </summary>
    /// <param name="sender">The queue.</param>
    /// <param name="e">The job that changed.</param>
    private void OnQueueChanged(object? sender, DownloadJobEventArgs e) =>
        _dispatcher.Invoke(() =>
        {
            var card = ListingsView.FirstOrDefault(item =>
                string.Equals(item.ListingId, e.Job.ListingId, StringComparison.Ordinal));

            card?.ApplyQueueState(e.Job);
        });

    /// <summary>
    /// Reports that a background refresh changed the catalogue.
    /// </summary>
    /// <param name="sender">The import service.</param>
    /// <param name="e">What changed.</param>
    /// <remarks>
    /// Raised on whichever thread the import ran on, so it is marshalled. The
    /// page deliberately does not reload itself: replacing what someone is
    /// reading is worse than telling them there is more and letting them ask.
    /// </remarks>
    private void OnCatalogUpdated(object? sender, CatalogUpdatedEventArgs e) =>
        _dispatcher.Invoke(() => StatusMessage = e.ListingsAdded > 0
            ? $"{e.ListingsAdded:N0} new game{(e.ListingsAdded == 1 ? string.Empty : "s")} found. Refresh to see them."
            : "The catalogue was updated in the background.");
}
