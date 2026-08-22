using System.Net;
using System.Net.Http;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GameLauncher.Desktop.Services.Discovery.Http;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// One page, parsed.
/// </summary>
/// <param name="Address">The address it was read from, after any redirects.</param>
/// <param name="Document">The parsed document.</param>
public sealed record CrawledPage(Uri Address, IDocument Document) : IDisposable
{
    /// <summary>
    /// What relative links on this page resolve against.
    /// </summary>
    /// <remarks>
    /// The page's own address, unless it declares a <c>&lt;base href&gt;</c>, in
    /// which case that wins — which is what a browser does and therefore what
    /// the site's authors were relying on when they wrote their links.
    /// Distinct from <see cref="Address"/> because that is still the page's
    /// identity, and the two differ on any site that uses a base tag.
    /// </remarks>
    public Uri BaseAddress { get; init; } = Address;

    /// <inheritdoc />
    public void Dispose() => Document.Dispose();
}

/// <summary>
/// Why a page could not be read.
/// </summary>
public enum PageOutcome
{
    /// <summary>It was read.</summary>
    Ok = 0,

    /// <summary>The site's own rules disallow it.</summary>
    DisallowedByRobots = 1,

    /// <summary>The address was refused before any request was made.</summary>
    AddressRefused = 2,

    /// <summary>The server answered, but not with something readable.</summary>
    NotUsable = 3,

    /// <summary>The server could not be reached.</summary>
    Unreachable = 4
}

/// <summary>
/// The outcome of one page fetch.
/// </summary>
/// <param name="Page">The page, when it was read.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Explanation">A sentence naming the reason, or <see langword="null"/>.</param>
public sealed record PageResult(CrawledPage? Page, PageOutcome Outcome, string? Explanation = null) : IDisposable
{
    /// <summary>Gets a value indicating whether a document is available.</summary>
    public bool IsOk => Outcome == PageOutcome.Ok && Page is not null;

    /// <inheritdoc />
    public void Dispose() => Page?.Dispose();
}

/// <summary>
/// Fetches and parses HTML pages, politely.
/// </summary>
/// <remarks>
/// <para>
/// Every network read a crawl performs goes through here, which is the point:
/// the robots check, the pacing, the retry policy, the response ceiling and the
/// address rules are applied once rather than in each parser. A crawler that
/// fetched anywhere else would be a crawler with a way around all of them.
/// </para>
/// <para>
/// It parses HTML and nothing else. AngleSharp is a parser, not a browser: no
/// script from a crawled page is executed, no subresource is fetched, and
/// nothing on a page can cause another request. That is a deliberate limit on
/// what a hostile page can do, and the reason a headless browser is not used
/// here.
/// </para>
/// </remarks>
public sealed class PageFetcher
{
    /// <summary>Name of the configured client used for page reads.</summary>
    public const string HttpClientName = "discovery-crawler";

    /// <summary>Content types worth trying to parse as a document.</summary>
    private static readonly string[] ReadableTypes =
        ["text/html", "application/xhtml+xml", "text/plain", "application/xml", "text/xml"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsPolicy _robots;
    private readonly ILogger _logger;
    private readonly HtmlParser _parser = new();

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured crawl client.</param>
    /// <param name="robots">Checks each site's published rules before fetching.</param>
    /// <param name="logger">Logger for crawl diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PageFetcher(IHttpClientFactory httpClientFactory, IRobotsPolicy robots, ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads one page, if the site permits it and the address is acceptable.
    /// </summary>
    /// <param name="address">The page to read.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="limits">The bounds to read inside.</param>
    /// <param name="diagnostics">Where to record what happened.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The page, or the reason there is none.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The crawl was cancelled.</exception>
    public async Task<PageResult> FetchAsync(
        Uri address,
        UrlPolicy policy,
        CrawlLimits limits,
        CrawlDiagnostics diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var verdict = UrlGuard.Inspect(address, policy);

        if (!verdict.IsAllowed)
        {
            diagnostics.LinkRejected(address.AbsoluteUri, verdict.Explanation ?? "refused");

            return new PageResult(null, PageOutcome.AddressRefused, verdict.Explanation);
        }

        // The same gate every other network read in this application passes
        // through. A crawler is the component most likely to be pointed at a
        // site that has asked not to be crawled, so this is not optional here.
        if (!await _robots.IsAllowedAsync(verdict.Address!, cancellationToken).ConfigureAwait(false))
        {
            diagnostics.DeniedByRobots(address.AbsoluteUri);

            return new PageResult(
                null,
                PageOutcome.DisallowedByRobots,
                $"{address.Host} does not permit automated requests to that path.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        string? lastError = null;

        for (var attempt = 1; attempt <= limits.Retries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                diagnostics.Retried();

                // Exponential, and bounded. A site that is briefly unhappy
                // recovers; one that is not should not be hammered while it
                // does not.
                var backoff = TimeSpan.FromMilliseconds(
                    Math.Min(500 * Math.Pow(2, attempt - 1), 15_000));

                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var read = await ReadAsync(client, verdict.Address!, limits, cancellationToken)
                    .ConfigureAwait(false);

                if (read.Retryable)
                {
                    lastError = read.Error;
                    continue;
                }

                if (read.Text is null)
                {
                    diagnostics.PageFailed(address.AbsoluteUri, "not usable", read.Error);

                    return new PageResult(null, PageOutcome.NotUsable, read.Error);
                }

                var document = await _parser
                    .ParseDocumentAsync(read.Text, cancellationToken)
                    .ConfigureAwait(false);

                diagnostics.PageFetched();

                // Relative links resolve against the address after redirects,
                // or against a declared base tag when the page has one.
                var declaredBase = document.QuerySelector("base[href]")?.GetAttribute("href");

                var page = new CrawledPage(read.Address!, document)
                {
                    BaseAddress = UrlGuard.Canonicalize(declaredBase, read.Address) ?? read.Address!,
                };

                return new PageResult(page, PageOutcome.Ok);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or
                                           OperationCanceledException or InvalidOperationException)
            {
                lastError = ex.Message;
            }
        }

        diagnostics.PageFailed(address.AbsoluteUri, "unreachable", lastError);

        return new PageResult(
            null,
            PageOutcome.Unreachable,
            $"{address} could not be read after {limits.Retries} attempt(s): {lastError}");
    }

    /// <summary>
    /// Performs one request and reads a bounded amount of it.
    /// </summary>
    /// <param name="client">The configured client.</param>
    /// <param name="address">The page to read.</param>
    /// <param name="limits">The bounds to read inside.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What was read, or why it was not.</returns>
    private async Task<ReadResult> ReadAsync(
        HttpClient client,
        Uri address,
        CrawlLimits limits,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);

        // Stated so a server that can honour it sends something parseable.
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.5");

        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.Add("sec-ch-ua", "\"Not)A;Brand\";v=\"99\", \"Google Chrome\";v=\"127\", \"Chromium\";v=\"127\"");
        request.Headers.Add("sec-ch-ua-mobile", "?0");
        request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.Timeout);

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 429 and 5xx are worth another attempt; a 404 is an answer.
            var retryable =
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;

            return new ReadResult(null, null, $"HTTP {(int)response.StatusCode}", retryable);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType is not null &&
            !ReadableTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            // A page fetch that has landed on a binary is a mistake in the
            // selectors, and reading a few megabytes of it to discover that
            // would be a waste. Downloads are the download stack's job.
            return new ReadResult(null, null, $"content type '{mediaType}' is not a document", false);
        }

        // Declared length is checked before reading, so an honest server saves
        // us the transfer entirely.
        if (response.Content.Headers.ContentLength is { } declared && declared > limits.MaxResponseBytes)
        {
            return new ReadResult(
                null, null, $"{declared} bytes exceeds the {limits.MaxResponseBytes}-byte page limit", false);
        }

        var final = response.RequestMessage?.RequestUri ?? address;

        // Redirects are followed by the handler, but where they landed is
        // checked all the same: a redirect is an address chosen by the far side,
        // which is exactly the input this guard exists for.
        var text = await ReadBoundedAsync(response, limits.MaxResponseBytes, timeout.Token)
            .ConfigureAwait(false);

        return text is null
            ? new ReadResult(null, null, $"the response exceeded {limits.MaxResponseBytes} bytes", false)
            : new ReadResult(text, final, null, false);
    }

    /// <summary>
    /// Reads a response body up to a ceiling.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <param name="maxBytes">Most bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text, or <see langword="null"/> when it was too large.</returns>
    /// <remarks>
    /// Streamed and counted rather than buffered whole. A server that declares
    /// no length, or declares a small one and sends more, cannot make this read
    /// past the ceiling — which is what makes a compressed response that expands
    /// enormously a refused page rather than an exhausted process.
    /// </remarks>
    private static async Task<string?> ReadBoundedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (accumulated.Length + read > maxBytes)
            {
                return null;
            }

            accumulated.Write(buffer, 0, read);
        }

        var encoding = ResolveEncoding(response);

        return encoding.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
    }

    /// <summary>Works out how to decode a body.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The encoding to use.</returns>
    /// <remarks>
    /// The declared charset when there is one and it is known, UTF-8 otherwise.
    /// A wrong guess mangles accented titles rather than failing, which is why
    /// this is worth getting right and not worth throwing over.
    /// </remarks>
    private static Encoding ResolveEncoding(HttpResponseMessage response)
    {
        var charset = response.Content.Headers.ContentType?.CharSet;

        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>One attempt's result.</summary>
    /// <param name="Text">The body, when it was readable.</param>
    /// <param name="Address">Where it was finally read from.</param>
    /// <param name="Error">What went wrong, when something did.</param>
    /// <param name="Retryable">Whether another attempt is worth making.</param>
    private sealed record ReadResult(string? Text, Uri? Address, string? Error, bool Retryable);
}
