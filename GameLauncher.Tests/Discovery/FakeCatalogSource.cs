using System.Net.Http;
using System.Runtime.CompilerServices;
using GameLauncher.Desktop.Services.Discovery;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// A catalogue source backed by an in-memory list, so the pipeline can be tested
/// without a network or a site to be polite to.
/// </summary>
internal sealed class FakeCatalogSource(string key = "fake", int rank = 0) : ICatalogSource
{
    private readonly List<SourceListing> _items = [];

    public string Key { get; } = key;

    public string DisplayName => Key;

    public int Rank { get; } = rank;

    public bool IsAvailable { get; set; } = true;

    /// <summary>No spacing: these tests must not spend real seconds being polite.</summary>
    public SourceThrottle Throttle { get; set; } = new(4, TimeSpan.Zero);

    private int _fetchCount;

    /// <summary>
    /// How many times <see cref="FetchAsync"/> was actually called.
    /// </summary>
    /// <remarks>
    /// Incremented atomically: the pipeline fetches a batch in parallel, and a
    /// plain increment would lose counts under contention — making this
    /// instrument report fewer fetches than happened, which is the one direction
    /// that turns a real regression into a passing test.
    /// </remarks>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>How many times <see cref="EnumerateAsync"/> was called.</summary>
    public int EnumerateCount { get; private set; }

    /// <summary>Items whose fetch should return null, simulating a broken parse.</summary>
    public HashSet<string> FailingItems { get; } = new(StringComparer.Ordinal);

    /// <summary>Items whose fetch should throw, simulating a transport failure.</summary>
    public HashSet<string> ThrowingItems { get; } = new(StringComparer.Ordinal);

    /// <summary>The last options the pipeline enumerated with.</summary>
    public SourceEnumerationOptions? LastOptions { get; private set; }

    /// <summary>Raised after each reference is yielded, so a test can cancel mid-pass.</summary>
    public Action<int>? OnYielded { get; set; }

    public FakeCatalogSource Add(string title, int? year, Action<SourceListingBuilder>? configure = null)
    {
        var builder = new SourceListingBuilder(Key, title, year);
        configure?.Invoke(builder);
        _items.Add(builder.Build());

        return this;
    }

    /// <summary>Replaces an item's data, as a site would when it edits a page.</summary>
    public void Replace(string sourceItemId, Func<SourceListing, SourceListing> update)
    {
        var index = _items.FindIndex(item =>
            string.Equals(item.SourceItemId, sourceItemId, StringComparison.Ordinal));

        _items[index] = update(_items[index]);
    }

    public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnumerateCount++;
        LastOptions = options;

        var start = 0;

        // The cursor is an index here. A real source's is opaque, which is why the
        // pipeline never interprets it.
        if (options.Cursor is not null && int.TryParse(options.Cursor, out var resume))
        {
            start = resume;
        }

        for (var index = start; index < _items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.MaxItems > 0 && index - start >= options.MaxItems)
            {
                yield break;
            }

            var item = _items[index];

            yield return new SourceListingRef(
                Key, item.SourceItemId, item.Title, item.SourceUpdatedAt, (index + 1).ToString());

            OnYielded?.Invoke(index);

            await Task.Yield();
        }
    }

    public Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fetchCount);

        if (ThrowingItems.Contains(reference.SourceItemId))
        {
            throw new HttpRequestException("simulated transport failure");
        }

        if (FailingItems.Contains(reference.SourceItemId))
        {
            return Task.FromResult<SourceListing?>(null);
        }

        return Task.FromResult<SourceListing?>(
            _items.FirstOrDefault(item =>
                string.Equals(item.SourceItemId, reference.SourceItemId, StringComparison.Ordinal)));
    }
}

/// <summary>Builds a source observation without repeating the required fields.</summary>
internal sealed class SourceListingBuilder(string sourceKey, string title, int? year)
{
    public string? Description { get; set; }

    public string? Developer { get; set; }

    public string? Publisher { get; set; }

    public IReadOnlyList<string> Genres { get; set; } = [];

    public IReadOnlyList<string> Platforms { get; set; } = [];

    public IReadOnlyList<ListingDownloadRef> Downloads { get; set; } = [];

    public IReadOnlyList<ListingImageRef> Images { get; set; } = [];

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? ItemId { get; set; }

    public SourceListing Build() => new()
    {
        SourceKey = sourceKey,
        SourceItemId = ItemId ?? title.ToLowerInvariant().Replace(' ', '-'),
        SourceUrl = new Uri("https://fake.test/" + Uri.EscapeDataString(title)),
        Title = title,
        Year = year,
        Description = Description,
        Developer = Developer,
        Publisher = Publisher,
        Genres = Genres,
        Platforms = Platforms,
        Downloads = Downloads,
        Images = Images,
        SourceUpdatedAt = UpdatedAt,
        RawPayload = $$"""{"title":"{{title}}"}"""
    };
}
