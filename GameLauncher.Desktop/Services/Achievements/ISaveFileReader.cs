using GameLauncher.Desktop.Services.Achievements.Configuration;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// The outcome of reading a value out of a save file.
/// </summary>
/// <param name="Found">Whether a value was located.</param>
/// <param name="Value">The value as text, when found.</param>
/// <param name="Error">Why it was not found, when it was not.</param>
public sealed record SaveFileReadResult(bool Found, string? Value, string? Error)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value read.</param>
    /// <returns>A result carrying the value.</returns>
    public static SaveFileReadResult Success(string value) => new(true, value, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">Why the read failed.</param>
    /// <returns>A result carrying the reason.</returns>
    public static SaveFileReadResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Extracts a single value from a save file using a declarative rule.
/// </summary>
/// <remarks>
/// Separate from the provider so the four formats can be tested against real
/// files with no database, no achievement definitions and no engine.
/// </remarks>
public interface ISaveFileReader
{
    /// <summary>
    /// Reads one value from a save file.
    /// </summary>
    /// <param name="filePath">Absolute path to the save file.</param>
    /// <param name="format">How to parse it.</param>
    /// <param name="fieldPath">
    /// Where the value is: a dotted path for JSON, an XPath expression for XML,
    /// <c>section/key</c> for INI, or a regular expression whose first capture
    /// group holds the value.
    /// </param>
    /// <returns>The value, or the reason it could not be read.</returns>
    /// <remarks>
    /// Never throws for a missing file, a malformed document or a path that
    /// matches nothing. Those are ordinary conditions — a save file does not
    /// exist until the player saves — and are reported rather than raised.
    /// </remarks>
    SaveFileReadResult ReadValue(string filePath, SaveFileFormat format, string fieldPath);
}
