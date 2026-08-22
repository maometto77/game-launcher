using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Http;

/// <summary>
/// Decides whether a site permits automated access to a path.
/// </summary>
/// <remarks>
/// A site's <c>robots.txt</c> is the one machine-readable statement it makes
/// about what crawlers may do. Honouring it is not optional here: it is what
/// separates importing a catalogue from taking whatever a server will serve.
/// </remarks>
public interface IRobotsPolicy
{
    /// <summary>
    /// Determines whether a path may be fetched.
    /// </summary>
    /// <param name="address">The address being considered.</param>
    /// <param name="cancellationToken">Cancels the rules fetch.</param>
    /// <returns><see langword="true"/> when the site permits it.</returns>
    /// <remarks>
    /// Rules are fetched once per host and cached. A host whose rules cannot be
    /// read is treated as permitting access, which matches the convention: an
    /// absent <c>robots.txt</c> means no restrictions, and refusing to proceed
    /// on a transient error would make the crawler unusable rather than polite.
    /// </remarks>
    Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the crawl delay a site asks for, if any.
    /// </summary>
    /// <param name="address">Any address on the host.</param>
    /// <param name="cancellationToken">Cancels the rules fetch.</param>
    /// <returns>The requested delay, or <see langword="null"/> when none is stated.</returns>
    Task<TimeSpan?> GetCrawlDelayAsync(Uri address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IRobotsPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than taken from a package. The subset that matters is
/// <c>User-agent</c>, <c>Disallow</c>, <c>Allow</c> and <c>Crawl-delay</c>, with
/// longest-match-wins between the last two — a hundred lines against a
/// dependency, in a project whose rules say not to add one without cause.
/// </para>
/// <para>
/// Matching follows the convention every major crawler uses: the most specific
/// rule wins, and <c>Allow</c> beats <c>Disallow</c> at equal length.
/// </para>
/// </remarks>
public sealed class RobotsPolicy : IRobotsPolicy
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used to fetch rules.</summary>
    public const string HttpClientName = "discovery-robots";

    /// <summary>How long a host's rules are trusted before being fetched again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RobotsPolicy> _logger;

    private readonly Dictionary<string, CachedRules> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the client used to fetch rules.</param>
    /// <param name="logger">Logger for policy diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RobotsPolicy(IHttpClientFactory httpClientFactory, ILogger<RobotsPolicy> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var rules = await GetRulesAsync(address, cancellationToken).ConfigureAwait(false);

        return rules.IsAllowed(address.AbsolutePath);
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> GetCrawlDelayAsync(
        Uri address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var rules = await GetRulesAsync(address, cancellationToken).ConfigureAwait(false);

        return rules.CrawlDelay;
    }

    /// <summary>Parses a robots.txt body into rules for the given agent.</summary>
    /// <param name="content">The file's contents.</param>
    /// <returns>The parsed rules.</returns>
    /// <remarks>
    /// Only the wildcard group is read. Claiming a named identity to obtain
    /// looser rules than the wildcard group grants would defeat the point of
    /// asking.
    /// </remarks>
    public static RobotRules Parse(string? content)
    {
        var rules = new RobotRules();

        if (string.IsNullOrWhiteSpace(content))
        {
            return rules;
        }

        var inWildcardGroup = false;
        var sawAnyAgent = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw;
            var comment = line.IndexOf('#');

            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            var field = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (field.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                // Consecutive User-agent lines share one group of rules, so the
                // flag is only reset when a group's directives have begun.
                if (sawAnyAgent && rules.HasDirectives)
                {
                    inWildcardGroup = false;
                    rules.EndGroup();
                }

                sawAnyAgent = true;

                if (value == "*")
                {
                    inWildcardGroup = true;
                }

                continue;
            }

            if (!inWildcardGroup)
            {
                continue;
            }

            if (field.Equals("disallow", StringComparison.OrdinalIgnoreCase))
            {
                rules.AddDisallow(value);
            }
            else if (field.Equals("allow", StringComparison.OrdinalIgnoreCase))
            {
                rules.AddAllow(value);
            }
            else if (field.Equals("crawl-delay", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(value, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
                     seconds is > 0 and < 3600)
            {
                rules.CrawlDelay = TimeSpan.FromSeconds(seconds);
            }
        }

        return rules;
    }

    /// <summary>Fetches and caches a host's rules.</summary>
    /// <param name="address">Any address on the host.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The host's rules.</returns>
    private async Task<RobotRules> GetRulesAsync(Uri address, CancellationToken cancellationToken)
    {
        var host = address.GetLeftPart(UriPartial.Authority);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cache.TryGetValue(host, out var cached) &&
                DateTimeOffset.UtcNow - cached.FetchedAt < CacheLifetime)
            {
                return cached.Rules;
            }

            var rules = await FetchAsync(host, cancellationToken).ConfigureAwait(false);

            _cache[host] = new CachedRules(rules, DateTimeOffset.UtcNow);

            return rules;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fetches one host's robots.txt.</summary>
    /// <param name="host">Scheme and authority of the host.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The parsed rules, permissive when they cannot be read.</returns>
    private async Task<RobotRules> FetchAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            using var response = await client
                .GetAsync($"{host}/robots.txt", cancellationToken)
                .ConfigureAwait(false);

            // No rules published means no restrictions, which is the convention.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new RobotRules();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("{Host} returned {Status} for robots.txt.", host, response.StatusCode);
                return new RobotRules();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rules = Parse(content);

            _logger.LogInformation(
                "Read {Count} rule(s) from {Host}/robots.txt{Delay}.",
                rules.RuleCount,
                host,
                rules.CrawlDelay is { } delay ? $", crawl delay {delay.TotalSeconds:0.#}s" : string.Empty);

            return rules;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read robots.txt for {Host}.", host);
            return new RobotRules();
        }
    }

    /// <summary>One host's rules and when they were read.</summary>
    /// <param name="Rules">The rules.</param>
    /// <param name="FetchedAt">When they were read.</param>
    private sealed record CachedRules(RobotRules Rules, DateTimeOffset FetchedAt);
}

/// <summary>
/// The wildcard group's rules from one <c>robots.txt</c>.
/// </summary>
public sealed class RobotRules
{
    private readonly List<string> _disallow = [];
    private readonly List<string> _allow = [];
    private bool _groupClosed;

    /// <summary>Gets the crawl delay the site asks for, if any.</summary>
    public TimeSpan? CrawlDelay { get; internal set; }

    /// <summary>Gets how many path rules were read.</summary>
    public int RuleCount => _disallow.Count + _allow.Count;

    /// <summary>Gets a value indicating whether the current group has any directives.</summary>
    internal bool HasDirectives => RuleCount > 0;

    /// <summary>Marks the wildcard group as complete, so later groups are ignored.</summary>
    internal void EndGroup() => _groupClosed = true;

    /// <summary>Records a disallowed prefix.</summary>
    /// <param name="path">The prefix, or empty to disallow nothing.</param>
    internal void AddDisallow(string path)
    {
        // An empty Disallow means "nothing is disallowed" and is not a rule.
        if (!_groupClosed && path.Length > 0)
        {
            _disallow.Add(Normalize(path));
        }
    }

    /// <summary>Records an explicitly allowed prefix.</summary>
    /// <param name="path">The prefix.</param>
    internal void AddAllow(string path)
    {
        if (!_groupClosed && path.Length > 0)
        {
            _allow.Add(Normalize(path));
        }
    }

    /// <summary>
    /// Determines whether a path may be fetched.
    /// </summary>
    /// <param name="path">The path, beginning with a slash.</param>
    /// <returns><see langword="true"/> when nothing forbids it.</returns>
    /// <remarks>
    /// Longest match wins, and an equally specific <c>Allow</c> beats a
    /// <c>Disallow</c> — the convention every major crawler follows, and the one
    /// a site author writing an exception expects.
    /// </remarks>
    public bool IsAllowed(string path)
    {
        var candidate = string.IsNullOrEmpty(path) ? "/" : path;

        var longestDisallow = _disallow
            .Where(rule => Matches(rule, candidate))
            .Select(rule => rule.Length)
            .DefaultIfEmpty(-1)
            .Max();

        if (longestDisallow < 0)
        {
            return true;
        }

        var longestAllow = _allow
            .Where(rule => Matches(rule, candidate))
            .Select(rule => rule.Length)
            .DefaultIfEmpty(-1)
            .Max();

        return longestAllow >= longestDisallow;
    }

    /// <summary>Strips a trailing wildcard, which only means "anything after this".</summary>
    /// <param name="path">The rule as written.</param>
    /// <returns>The comparable prefix.</returns>
    private static string Normalize(string path) =>
        path.EndsWith('*') ? path.TrimEnd('*') : path;

    /// <summary>Determines whether a rule applies to a path.</summary>
    /// <param name="rule">The rule prefix, possibly containing wildcards or an end anchor.</param>
    /// <param name="path">The path being tested.</param>
    /// <returns><see langword="true"/> when the rule matches.</returns>
    private static bool Matches(string rule, string path)
    {
        if (rule.Length == 0)
        {
            return false;
        }

        // An interior '*' or a '$' anchor is rare enough that a segment walk is
        // clearer than a translated regular expression.
        if (!rule.Contains('*', StringComparison.Ordinal) && !rule.EndsWith('$'))
        {
            return path.StartsWith(rule, StringComparison.OrdinalIgnoreCase);
        }

        var anchored = rule.EndsWith('$');
        var pattern = anchored ? rule[..^1] : rule;
        var segments = pattern.Split('*');
        var position = 0;

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];

            if (segment.Length == 0)
            {
                continue;
            }

            if (index == 0)
            {
                if (!path.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                position = segment.Length;
                continue;
            }

            var found = path.IndexOf(segment, position, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return false;
            }

            position = found + segment.Length;
        }

        return !anchored || position == path.Length;
    }
}
