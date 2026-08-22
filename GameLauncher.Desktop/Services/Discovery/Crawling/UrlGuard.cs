using System.Net;
using System.Net.Sockets;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// Why an address was refused.
/// </summary>
public enum UrlRejection
{
    /// <summary>It was not refused.</summary>
    None = 0,

    /// <summary>It could not be parsed, or was not absolute.</summary>
    Malformed = 1,

    /// <summary>Its scheme is not one this launcher fetches.</summary>
    UnsupportedScheme = 2,

    /// <summary>It points at this machine or a private network.</summary>
    PrivateAddress = 3,

    /// <summary>Its host is not on the allow list.</summary>
    HostNotAllowed = 4,

    /// <summary>Its host is on the block list.</summary>
    HostBlocked = 5
}

/// <summary>
/// The outcome of checking one address.
/// </summary>
/// <param name="Address">The canonical form, when it passed.</param>
/// <param name="Rejection">Why it failed, or <see cref="UrlRejection.None"/>.</param>
/// <param name="Explanation">A sentence naming the reason, or <see langword="null"/>.</param>
public sealed record UrlVerdict(Uri? Address, UrlRejection Rejection, string? Explanation = null)
{
    /// <summary>Gets a value indicating whether the address may be used.</summary>
    public bool IsAllowed => Rejection == UrlRejection.None && Address is not null;

    /// <summary>An accepted address.</summary>
    /// <param name="address">The canonical form.</param>
    /// <returns>The verdict.</returns>
    public static UrlVerdict Allow(Uri address) => new(address, UrlRejection.None);

    /// <summary>A refused address.</summary>
    /// <param name="rejection">Why.</param>
    /// <param name="explanation">A sentence naming the reason.</param>
    /// <returns>The verdict.</returns>
    public static UrlVerdict Deny(UrlRejection rejection, string explanation) =>
        new(null, rejection, explanation);
}

/// <summary>
/// What a guard will accept.
/// </summary>
public sealed record UrlPolicy
{
    /// <summary>Schemes that may be fetched.</summary>
    public IReadOnlyList<string> Schemes { get; init; } = ["http", "https"];

    /// <summary>
    /// Hosts that may be reached, matched on suffix, or empty for any.
    /// </summary>
    /// <remarks>
    /// A crawl is given the site it was pointed at, which stops a page of links
    /// turning one configured source into a walk of the open web.
    /// </remarks>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>Hosts that may never be reached, matched on suffix.</summary>
    public IReadOnlyList<string> BlockedHosts { get; init; } = [];

    /// <summary>
    /// Whether this machine and private networks may be reached.
    /// </summary>
    /// <remarks>
    /// False everywhere but in tests and for a deliberately self-hosted
    /// repository. Crawled HTML and adapter output are untrusted input, and a
    /// link is the cheapest way to ask a program to fetch something on the far
    /// side of a firewall it happens to be inside.
    /// </remarks>
    public bool AllowPrivateAddresses { get; init; }

    /// <summary>The default: public HTTP and HTTPS only.</summary>
    public static UrlPolicy Default { get; } = new();

    /// <summary>
    /// The default, plus the magnet scheme a torrent address needs.
    /// </summary>
    /// <remarks>
    /// Separate because a magnet is handed to an external engine rather than
    /// fetched, so the host checks below do not apply to it and it has no
    /// business being accepted where an HTTP address is expected.
    /// </remarks>
    public static UrlPolicy WithMagnet { get; } = new() { Schemes = ["http", "https", "magnet"] };

    /// <summary>
    /// Narrows this policy to one site.
    /// </summary>
    /// <param name="host">The host to allow, and its subdomains.</param>
    /// <returns>A policy allowing only that host.</returns>
    public UrlPolicy ConfinedTo(string? host) =>
        string.IsNullOrWhiteSpace(host) ? this : this with { AllowedHosts = [host.Trim()] };
}

/// <summary>
/// Canonicalises and vets addresses before anything fetches them.
/// </summary>
/// <remarks>
/// <para>
/// One gate for the crawler and the sourcing adapters both, because they face
/// the same problem from the same direction: an address that came out of a web
/// page or an external program, which is to say an address a stranger chose.
/// </para>
/// <para>
/// This is not a substitute for the robots policy and does not replace it. That
/// answers "does the site permit this fetch"; this answers "is this an address
/// we are willing to fetch at all", and both have to pass.
/// </para>
/// </remarks>
public static class UrlGuard
{
    /// <summary>Hosts that always mean this machine.</summary>
    private static readonly string[] LocalHostNames = ["localhost", "localhost.localdomain", "ip6-localhost"];

    /// <summary>
    /// Resolves an address, possibly relative, into a canonical absolute one.
    /// </summary>
    /// <param name="value">The address as written.</param>
    /// <param name="baseAddress">The page it was found on, for relative forms.</param>
    /// <returns>The canonical address, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The fragment is dropped. Two links differing only after the hash are the
    /// same page, and keeping it would make a crawl visit the same document once
    /// per anchor on it.
    /// </remarks>
    public static Uri? Canonicalize(string? value, Uri? baseAddress = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        // A protocol-relative address is common in markup and resolves against
        // the page's own scheme.
        if (text.StartsWith("//", StringComparison.Ordinal) && baseAddress is not null)
        {
            text = $"{baseAddress.Scheme}:{text}";
        }

        Uri? parsed;

        if (baseAddress is not null)
        {
            if (!Uri.TryCreate(baseAddress, text, out parsed))
            {
                return null;
            }
        }
        else if (!Uri.TryCreate(text, UriKind.Absolute, out parsed))
        {
            return null;
        }

        if (!parsed.IsAbsoluteUri)
        {
            return null;
        }

        // Magnet and the like carry no path to normalise.
        if (!parsed.IsFile && parsed.Scheme is not ("http" or "https"))
        {
            return parsed;
        }

        var builder = new UriBuilder(parsed) { Fragment = string.Empty };

        // Default ports are dropped so http://x/ and http://x:80/ dedupe.
        if ((parsed.Scheme == "http" && parsed.Port == 80) ||
            (parsed.Scheme == "https" && parsed.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    /// <summary>
    /// Canonicalises an address and checks it against a policy.
    /// </summary>
    /// <param name="value">The address as written.</param>
    /// <param name="policy">What to accept.</param>
    /// <param name="baseAddress">The page it was found on, for relative forms.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public static UrlVerdict Inspect(string? value, UrlPolicy policy, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var address = Canonicalize(value, baseAddress);

        return address is null
            ? UrlVerdict.Deny(UrlRejection.Malformed, $"'{Describe(value)}' is not a usable address.")
            : Inspect(address, policy);
    }

    /// <summary>
    /// Checks an already-absolute address against a policy.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="policy">What to accept.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static UrlVerdict Inspect(Uri address, UrlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(policy);

        if (!address.IsAbsoluteUri)
        {
            return UrlVerdict.Deny(UrlRejection.Malformed, "A relative address cannot be fetched on its own.");
        }

        if (!policy.Schemes.Contains(address.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return UrlVerdict.Deny(
                UrlRejection.UnsupportedScheme,
                $"'{address.Scheme}' is not a scheme this launcher fetches.");
        }

        // A magnet names content rather than a host, so the host rules below do
        // not apply and there is nothing to resolve.
        if (address.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return UrlVerdict.Allow(address);
        }

        var host = address.Host;

        if (Matches(policy.BlockedHosts, host))
        {
            return UrlVerdict.Deny(UrlRejection.HostBlocked, $"{host} is on the blocked-host list.");
        }

        if (policy.AllowedHosts.Count > 0 && !Matches(policy.AllowedHosts, host))
        {
            return UrlVerdict.Deny(
                UrlRejection.HostNotAllowed,
                $"{host} is outside the hosts this source is allowed to reach.");
        }

        if (!policy.AllowPrivateAddresses && IsPrivate(host))
        {
            return UrlVerdict.Deny(
                UrlRejection.PrivateAddress,
                $"{host} is this machine or a private network, which is not fetched from untrusted input.");
        }

        return UrlVerdict.Allow(address);
    }

    /// <summary>
    /// Determines whether a host names this machine or a private network.
    /// </summary>
    /// <param name="host">The host portion of an address.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    /// <remarks>
    /// Literal addresses and the well-known local names, not a DNS lookup. A
    /// lookup here would add a round trip to every link on every page and still
    /// be beatable by a name that resolves differently the second time, so the
    /// honest position is that this stops the obvious cases and is not a
    /// substitute for a firewall.
    /// </remarks>
    public static bool IsPrivate(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var trimmed = host.Trim().Trim('[', ']');

        if (LocalHostNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // A name ending in .local or .internal is a local-network convention.
        if (trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out var parsed) && IsPrivate(parsed);
    }

    /// <summary>
    /// Determines whether an address is on this machine or a private network.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            // fc00::/7, the unique-local range.
            var v6 = address.GetAddressBytes();

            if ((v6[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // ::ffff:a.b.c.d carries a v4 address and must be judged as one.
            return address.IsIPv4MappedToIPv6 && IsPrivate(address.MapToIPv4());
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            // Something exotic. Treated as private, because a scheme this
            // launcher does not understand is not one to reach out over.
            return true;
        }

        var bytes = address.GetAddressBytes();

        return bytes[0] switch
        {
            0 => true,                                        // 0.0.0.0/8
            10 => true,                                       // private
            127 => true,                                      // loopback
            169 when bytes[1] == 254 => true,                 // link-local
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true, // private
            192 when bytes[1] == 168 => true,                 // private
            192 when bytes[1] == 0 && bytes[2] == 2 => true,   // documentation
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true, // carrier NAT
            198 when bytes[1] == 18 || bytes[1] == 19 => true, // benchmarking
            >= 224 => true,                                   // multicast and reserved
            _ => false
        };
    }

    /// <summary>Determines whether a host matches any suffix in a list.</summary>
    /// <param name="hosts">The suffixes.</param>
    /// <param name="host">The host to test.</param>
    /// <returns><see langword="true"/> when one matches.</returns>
    private static bool Matches(IReadOnlyList<string> hosts, string host) =>
        hosts.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            (host.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith("." + candidate.TrimStart('.'), StringComparison.OrdinalIgnoreCase)));

    /// <summary>Shortens a value for an error message.</summary>
    /// <param name="value">The value as written.</param>
    /// <returns>Something safe to put in a sentence.</returns>
    private static string Describe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(blank)";
        }

        var trimmed = value.Trim().ReplaceLineEndings(" ");

        return trimmed.Length <= 120 ? trimmed : trimmed[..117] + "...";
    }
}
