using System.Text;

namespace GameLauncher.Desktop.Services.Saves;

/// <summary>
/// Puts a path into one canonical form so two spellings of the same location
/// compare equal.
/// </summary>
/// <remarks>
/// <para>
/// The problem this exists for is not hypothetical. A folder called
/// <c>Café</c> can be stored on disk as <c>Caf</c> + <c>é</c> (form C, one code
/// point) or as <c>Cafe</c> + a combining acute (form D, two). Windows creates
/// the first, several installers and archive extractors create the second, and
/// the two strings are not equal in .NET even though they name the same
/// directory. A scan that compares raw strings therefore reports the save as
/// changed on every single pass, for every game with an accent in its path —
/// which is a large fraction of anything non-English.
/// </para>
/// <para>
/// Form C is the right target rather than form D: it is what Windows itself
/// produces, what NTFS stores for a newly created name, and the shorter of the
/// two. Normalising towards it means the common case is already canonical and
/// costs nothing.
/// </para>
/// <para>
/// Pure and allocation-light: no file system access, no probing, no clock. It
/// answers "are these the same name" and nothing else — in particular it does
/// not resolve links, relative segments or case, because those need the disk and
/// a wrong answer there would be worse than no answer.
/// </para>
/// </remarks>
public static class SavePathNormalizer
{
    /// <summary>
    /// Compares paths after normalising both.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, matching Windows. Two paths differing only in case name
    /// the same file here, and treating them as distinct would double every
    /// entry in a scan.
    /// </remarks>
    public static IEqualityComparer<string> Comparer { get; } = new NormalizedPathComparer();

    /// <summary>
    /// Normalises a path.
    /// </summary>
    /// <param name="path">The path as it was read or built.</param>
    /// <returns>
    /// The canonical form, or the input unchanged when it is null or blank.
    /// </returns>
    /// <remarks>
    /// Three steps, in order: separators to the platform's own, runs of
    /// separators collapsed, and the whole string to Unicode form C. A trailing
    /// separator is dropped so a directory named with one matches the same
    /// directory named without.
    /// </remarks>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        var builder = new StringBuilder(path.Length);

        var lastWasSeparator = false;

        foreach (var character in path)
        {
            var isSeparator = character is '/' or '\\';

            if (isSeparator)
            {
                // A leading double separator is a UNC prefix and survives; every
                // other run collapses to one.
                if (lastWasSeparator && builder.Length > 1)
                {
                    continue;
                }

                builder.Append(separator);
                lastWasSeparator = true;
                continue;
            }

            builder.Append(character);
            lastWasSeparator = false;
        }

        // A root such as "C:\" keeps its separator; anything longer loses a
        // trailing one, so "…/Saves" and "…/Saves/" are the same place.
        while (builder.Length > 3 && builder[^1] == separator)
        {
            builder.Length--;
        }

        var collapsed = builder.ToString();

        return collapsed.IsNormalized(NormalizationForm.FormC)
            ? collapsed
            : collapsed.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Determines whether two paths name the same location.
    /// </summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public static bool AreSame(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Compares paths by their normalised form.</summary>
    private sealed class NormalizedPathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => AreSame(x, y);

        public int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));
    }
}
