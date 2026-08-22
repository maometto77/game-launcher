namespace GameLauncher.Desktop.Services.Discovery;

/// <summary>
/// Decides whether a published checksum is one the download stack can verify.
/// </summary>
/// <remarks>
/// <para>
/// Every source that can carry a checksum needs this same judgement — a feed
/// field, a shared catalogue document, a page a crawler read, a program a
/// manifest nominated — and they had all better agree, because they feed one
/// verification path.
/// </para>
/// <para>
/// A value that is not a digest is worse than an absent one. "Not computed",
/// "see below" and a sentence explaining where the checksums are kept are all
/// common where a checksum belongs; recorded as one, each would fail every
/// transfer with a mismatch that is really a typo on somebody's release page.
/// So anything unrecognised is dropped, and the file transfers unverified in
/// the ordinary way.
/// </para>
/// <para>
/// The accepted lengths are exactly those the download service can verify, and
/// which field a digest is mapped to is only a label: the algorithm is inferred
/// from its length when the transfer is checked. That is why a publisher's
/// single <c>checksum</c> field can be mapped to any of the three.
/// </para>
/// </remarks>
public static class HexDigest
{
    /// <summary>Lengths of the digests the verification path recognises.</summary>
    private static readonly int[] Lengths = [32, 40, 64, 128];

    /// <summary>
    /// Keeps a published value only if it is syntactically a digest.
    /// </summary>
    /// <param name="value">What the source published, in whatever form.</param>
    /// <returns>The digest in lower case, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Three conventions are unwrapped rather than rejected, because real
    /// repositories publish all of them: an algorithm prefix (<c>sha256:abc…</c>,
    /// as Zenodo writes it), surrounding whitespace, and the file name printed
    /// after the digest that <c>sha256sum</c> output carries. Dropping a
    /// perfectly good checksum over its punctuation would lose verification the
    /// launcher otherwise gets for free.
    /// </remarks>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // 'sha256:abc…' and 'SHA-1: abc…' are both common ways to print one.
        var separator = trimmed.LastIndexOf(':');

        if (separator >= 0 && separator < trimmed.Length - 1)
        {
            trimmed = trimmed[(separator + 1)..].Trim();
        }

        // 'abc…  doom.zip' is what sha256sum writes.
        var space = trimmed.IndexOfAny([' ', '\t']);

        if (space > 0)
        {
            trimmed = trimmed[..space];
        }

        trimmed = trimmed.Trim().ToLowerInvariant();

        return Lengths.Contains(trimmed.Length) && trimmed.All(Uri.IsHexDigit)
            ? trimmed
            : null;
    }
}
