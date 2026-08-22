using System.Collections.Concurrent;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// What went wrong on one page.
/// </summary>
/// <param name="Address">The page.</param>
/// <param name="Reason">What happened, in a few words.</param>
/// <param name="Detail">The underlying message, when there was one.</param>
public sealed record CrawlFailure(string Address, string Reason, string? Detail = null);

/// <summary>
/// What a crawl did, and what it could not do.
/// </summary>
/// <remarks>
/// <para>
/// Kept separately from the log because the interesting question after a crawl
/// is not "what happened" but "did this work" — and a crawl that fetched two
/// hundred pages and parsed a title out of none of them has to be
/// distinguishable from one that found nothing because there is nothing there.
/// The counters below are what tells those apart.
/// </para>
/// <para>
/// Safe to write to from several page workers at once, because that is how the
/// engine runs when a manifest raises the concurrency.
/// </para>
/// </remarks>
public sealed class CrawlDiagnostics
{
    /// <summary>Most failures kept for reporting.</summary>
    /// <remarks>
    /// A crawl of a redesigned site fails on every page. Keeping all of them
    /// would turn a diagnostic into a memory leak, and the tenth is as
    /// informative as the thousandth.
    /// </remarks>
    private const int MaxRecordedFailures = 25;

    private readonly ConcurrentQueue<CrawlFailure> _failures = new();

    private int _pagesFetched;
    private int _pagesFailed;
    private int _itemsFound;
    private int _itemsSkipped;
    private int _duplicatesSkipped;
    private int _linksRejected;
    private int _robotsDenied;
    private int _retries;

    /// <summary>Gets how many pages were read successfully.</summary>
    public int PagesFetched => Volatile.Read(ref _pagesFetched);

    /// <summary>Gets how many pages could not be read or parsed.</summary>
    public int PagesFailed => Volatile.Read(ref _pagesFailed);

    /// <summary>Gets how many items were yielded.</summary>
    public int ItemsFound => Volatile.Read(ref _itemsFound);

    /// <summary>Gets how many candidate items were discarded as unusable.</summary>
    public int ItemsSkipped => Volatile.Read(ref _itemsSkipped);

    /// <summary>Gets how many addresses had already been seen.</summary>
    public int DuplicatesSkipped => Volatile.Read(ref _duplicatesSkipped);

    /// <summary>Gets how many addresses the URL policy refused.</summary>
    public int LinksRejected => Volatile.Read(ref _linksRejected);

    /// <summary>Gets how many fetches the site's own rules disallowed.</summary>
    public int RobotsDenied => Volatile.Read(ref _robotsDenied);

    /// <summary>Gets how many requests were attempted more than once.</summary>
    public int Retries => Volatile.Read(ref _retries);

    /// <summary>Gets the failures worth reporting.</summary>
    public IReadOnlyList<CrawlFailure> Failures => _failures.ToArray();

    /// <summary>
    /// Gets a value indicating whether the crawl looks like it worked.
    /// </summary>
    /// <remarks>
    /// Pages read and nothing found is the shape of a site that has changed
    /// under its selectors. Reported as unhealthy so a caller can say so rather
    /// than announcing a successful import of zero games.
    /// </remarks>
    public bool LooksHealthy => PagesFetched == 0 || ItemsFound > 0;

    /// <summary>Records a page read.</summary>
    public void PageFetched() => Interlocked.Increment(ref _pagesFetched);

    /// <summary>Records an item yielded.</summary>
    public void ItemFound() => Interlocked.Increment(ref _itemsFound);

    /// <summary>Records a candidate item discarded.</summary>
    public void ItemSkipped() => Interlocked.Increment(ref _itemsSkipped);

    /// <summary>Records an address already seen.</summary>
    public void DuplicateSkipped() => Interlocked.Increment(ref _duplicatesSkipped);

    /// <summary>Records a retry.</summary>
    public void Retried() => Interlocked.Increment(ref _retries);

    /// <summary>Records an address the URL policy refused.</summary>
    /// <param name="address">The address.</param>
    /// <param name="reason">Why it was refused.</param>
    public void LinkRejected(string address, string reason)
    {
        Interlocked.Increment(ref _linksRejected);
        Record(new CrawlFailure(address, "address refused", reason));
    }

    /// <summary>Records a fetch the site's rules disallowed.</summary>
    /// <param name="address">The address.</param>
    public void DeniedByRobots(string address)
    {
        Interlocked.Increment(ref _robotsDenied);
        Record(new CrawlFailure(address, "disallowed by robots.txt"));
    }

    /// <summary>Records a page that could not be read or parsed.</summary>
    /// <param name="address">The page.</param>
    /// <param name="reason">What happened.</param>
    /// <param name="detail">The underlying message, when there was one.</param>
    public void PageFailed(string address, string reason, string? detail = null)
    {
        Interlocked.Increment(ref _pagesFailed);
        Record(new CrawlFailure(address, reason, detail));
    }

    /// <summary>
    /// Summarises the crawl in one line.
    /// </summary>
    /// <returns>Something worth putting in a log or on screen.</returns>
    public string Summarize()
    {
        var summary =
            $"{PagesFetched} page(s), {ItemsFound} item(s)";

        if (PagesFailed > 0)
        {
            summary += $", {PagesFailed} page(s) failed";
        }

        if (RobotsDenied > 0)
        {
            summary += $", {RobotsDenied} disallowed by robots.txt";
        }

        if (LinksRejected > 0)
        {
            summary += $", {LinksRejected} address(es) refused";
        }

        if (DuplicatesSkipped > 0)
        {
            summary += $", {DuplicatesSkipped} duplicate(s)";
        }

        return summary;
    }

    /// <summary>Keeps a failure, up to the cap.</summary>
    /// <param name="failure">What happened.</param>
    private void Record(CrawlFailure failure)
    {
        if (_failures.Count < MaxRecordedFailures)
        {
            _failures.Enqueue(failure);
        }
    }
}
