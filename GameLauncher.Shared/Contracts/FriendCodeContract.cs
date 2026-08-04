using System.Text.RegularExpressions;

namespace GameLauncher.Shared.Contracts;

/// <summary>
/// Defines the on-the-wire shape of a friend code.
/// </summary>
/// <remarks>
/// <para>
/// A friend code is the public identity of a user: it is the only identifier
/// exchanged between people, and it is safe to share. It is deliberately not a
/// secret — the paired auth token is.
/// </para>
/// <para>
/// The format is <c>GL-XXXXX-XXXXX</c>, where each <c>X</c> is a Crockford
/// Base32 symbol. That alphabet omits <c>I</c>, <c>L</c>, <c>O</c> and
/// <c>U</c>, which removes the usual transcription ambiguities (1/I/L, 0/O)
/// when somebody reads a code aloud or copies it by hand. Ten symbols carry
/// fifty bits of entropy, which is far more than enough to make guessing a
/// stranger's code impractical.
/// </para>
/// <para>
/// This type holds format constants and validation only. Generation lives with
/// the side that owns identity creation, so that this assembly stays free of
/// behaviour.
/// </para>
/// </remarks>
public static class FriendCodeContract
{
    /// <summary>The fixed prefix every friend code carries.</summary>
    public const string Prefix = "GL-";

    /// <summary>The symbol alphabet used for the random portion of a code (Crockford Base32).</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Number of random symbols in each of the two groups.</summary>
    public const int GroupLength = 5;

    /// <summary>Total length of a well-formed friend code, including prefix and separators.</summary>
    public const int TotalLength = 14;

    /// <summary>Regular expression source matching a well-formed friend code.</summary>
    public const string Pattern = "^GL-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$";

    private static readonly Regex Validator = new(
        Pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Determines whether <paramref name="value"/> is a well-formed friend code.
    /// </summary>
    /// <param name="value">Candidate friend code; may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the value matches <see cref="Pattern"/> exactly;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Validation is case-sensitive by design. Codes are always produced in
    /// upper case, so callers should normalise user input with
    /// <see cref="Normalize"/> before validating.
    /// </remarks>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Validator.IsMatch(value);

    /// <summary>
    /// Normalises user-entered text into the canonical friend code form.
    /// </summary>
    /// <param name="value">Raw user input; may be <see langword="null"/>.</param>
    /// <returns>
    /// The trimmed, upper-cased value, or an empty string when
    /// <paramref name="value"/> is <see langword="null"/> or blank.
    /// </returns>
    /// <remarks>
    /// This only fixes casing and surrounding whitespace. It deliberately does
    /// not attempt to repair a malformed code by inserting separators, because
    /// silently reshaping input risks turning a typo into a valid code that
    /// belongs to somebody else.
    /// </remarks>
    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
}
