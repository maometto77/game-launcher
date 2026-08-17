using System.Text.RegularExpressions;

namespace GameLauncher.Desktop.Services.Saves;

/// <summary>
/// Finds the account folders a <c>&lt;storeUserId&gt;</c> rule stands for.
/// </summary>
/// <remarks>
/// <para>
/// Manifest rules such as <c>&lt;winAppData&gt;/Sekiro/&lt;storeUserId&gt;/S0000.sl2</c>
/// describe a path that only exists once a particular account has played the
/// game. Ludusavi resolves it from a signed-in store session; this launcher has
/// no store session, so it reads the answer off the disk instead — the folder is
/// right there, named after the account that created it.
/// </para>
/// <para>
/// Returning every match rather than one matters. A machine with two Steam
/// accounts, or a game played under both a store account and an offline profile,
/// has two save folders and both are real. Picking one would silently hide the
/// other's progress, which is the failure a save feature can least afford.
/// </para>
/// </remarks>
public static partial class StoreUserIdProbe
{
    /// <summary>The placeholder this probe answers.</summary>
    public const string Placeholder = "<storeUserId>";

    /// <summary>
    /// Most account folders to return for one rule.
    /// </summary>
    /// <remarks>
    /// A guard against a rule whose parent happens to be a directory with
    /// thousands of children. Nobody has sixteen store accounts, so a rule
    /// matching more than this is matching the wrong thing.
    /// </remarks>
    private const int MaxMatches = 16;

    /// <summary>
    /// Folder names that sit alongside account folders and are not accounts.
    /// </summary>
    /// <remarks>
    /// Steam's own <c>userdata</c> tree keeps these next to the numeric account
    /// directories; an emulator's profile root often keeps a config folder the
    /// same way. Excluded by name because they would otherwise pass the shape
    /// test that catches alphanumeric profile ids.
    /// </remarks>
    private static readonly string[] NotAnAccount =
    [
        "common", "config", "settings", "remote", "backup", "backups",
        "temp", "cache", "logs", "shared", "default", "profiles", "saves", "save"
    ];

    /// <summary>
    /// Lists the account ids present for a rule.
    /// </summary>
    /// <param name="expandedTemplate">
    /// The rule with every other placeholder already expanded, still containing
    /// <see cref="Placeholder"/>.
    /// </param>
    /// <returns>
    /// Ids found on disk, ordered so the most likely account comes first. Empty
    /// when the parent directory does not exist or holds nothing plausible.
    /// </returns>
    /// <remarks>
    /// Only the one directory the placeholder sits in is listed — never a
    /// recursive walk. A rule points at a known place, and searching beyond it
    /// would be guessing.
    /// </remarks>
    public static IReadOnlyList<string> Discover(string? expandedTemplate)
    {
        if (string.IsNullOrWhiteSpace(expandedTemplate))
        {
            return [];
        }

        var marker = expandedTemplate.IndexOf(Placeholder, StringComparison.Ordinal);

        if (marker < 0)
        {
            return [];
        }

        var parent = expandedTemplate[..marker].TrimEnd('/', '\\');

        if (parent.Length == 0)
        {
            return [];
        }

        string[] candidates;

        try
        {
            if (!Directory.Exists(parent))
            {
                return [];
            }

            candidates = Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder is the same answer as an absent one: nothing
            // can be said about this rule, and saying so beats throwing out of a
            // lookup that is only ever advisory.
            return [];
        }

        return candidates
            .Select(Path.GetFileName)
            .Where(name => name is { Length: > 0 } && LooksLikeAccountId(name))
            .Select(name => name!)

            // Numeric ids first, longest first: a 17-digit Steam64 is a stronger
            // signal than a short emulator profile name, and a person with both
            // is far more likely to care about the store account's saves.
            .OrderByDescending(IsNumeric)
            .ThenByDescending(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxMatches)
            .ToArray();
    }

    /// <summary>
    /// Decides whether a folder name could be a store or profile account id.
    /// </summary>
    /// <param name="name">The folder's name.</param>
    /// <returns><see langword="true"/> when it has the shape of an id.</returns>
    /// <remarks>
    /// <para>
    /// Three shapes are accepted. A Steam 64-bit id is 17 digits beginning
    /// <c>7656119</c>. A Steam 32-bit account id — what <c>userdata</c> folders
    /// are named with — is a shorter run of digits. An emulator profile is
    /// usually hexadecimal or a long alphanumeric token.
    /// </para>
    /// <para>
    /// Deliberately shape-based rather than a list of known ids. The point is to
    /// recognise an account folder on a machine this code has never seen, and any
    /// list would be out of date the first time an emulator changed its naming.
    /// </para>
    /// </remarks>
    public static bool LooksLikeAccountId(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            NotAnAccount.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsNumeric(name))
        {
            // Wide enough for a Steam32 account id at the bottom and a Steam64 at
            // the top. A one- or two-digit folder is a slot number, not an
            // account.
            return name.Length is >= 6 and <= 20;
        }

        // An alphanumeric profile token: long enough not to be a word, and made
        // only of characters an id is made of.
        return name.Length is >= 8 and <= 64 && ProfileToken().IsMatch(name);
    }

    /// <summary>Determines whether a name is all digits.</summary>
    /// <param name="name">The name to test.</param>
    /// <returns><see langword="true"/> when every character is a digit.</returns>
    private static bool IsNumeric(string name) => name.All(char.IsAsciiDigit);

    /// <summary>Matches an alphanumeric profile identifier.</summary>
    /// <returns>The compiled pattern.</returns>
    /// <remarks>
    /// At least one digit is required, which is what separates a profile token
    /// from an ordinary word like <c>Documents</c> that happens to be long.
    /// </remarks>
    [GeneratedRegex("^(?=[A-Za-z0-9_-]*[0-9])[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileToken();
}
