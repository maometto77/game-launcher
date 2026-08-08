using System.Text.RegularExpressions;

namespace GameLauncher.Desktop.Services.Saves;

/// <summary>
/// Turns a Ludusavi manifest path into an absolute path on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Manifest paths are written against placeholders — <c>&lt;base&gt;</c>,
/// <c>&lt;winAppData&gt;</c>, <c>&lt;home&gt;</c> — so one entry describes a game
/// on every machine and every platform. Expanding them is this class's whole
/// job, and it is pure: no file system access, no network, no manifest.
/// </para>
/// <para>
/// A path with a placeholder that cannot be resolved here returns
/// <see langword="null"/> rather than a half-expanded string. A literal
/// <c>&lt;base&gt;</c> left in a path would be a directory nobody has, and
/// acting on it would mean creating or searching nonsense.
/// </para>
/// </remarks>
public static partial class LudusaviPathExpander
{
    /// <summary>
    /// Expands a manifest path.
    /// </summary>
    /// <param name="template">The path as the manifest writes it.</param>
    /// <param name="installDirectory">Where the game is installed, or <see langword="null"/>.</param>
    /// <param name="storeGameId">Store identifier, for <c>&lt;storeGameId&gt;</c>.</param>
    /// <returns>An absolute path, or <see langword="null"/> when it cannot be resolved.</returns>
    public static string? Expand(string template, string? installDirectory, string? storeGameId = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var value = template.Replace('\\', '/');

        // <base> is <root>/<game>, so it has to go first or the pieces of it
        // would be substituted separately and produce the wrong path.
        if (installDirectory is { Length: > 0 })
        {
            var normalisedInstall = installDirectory.TrimEnd('/', '\\').Replace('\\', '/');
            var parent = Path.GetDirectoryName(normalisedInstall)?.Replace('\\', '/');
            var leaf = Path.GetFileName(normalisedInstall);

            value = value
                .Replace("<base>", normalisedInstall, StringComparison.Ordinal)
                .Replace("<game>", leaf, StringComparison.Ordinal);

            if (parent is { Length: > 0 })
            {
                value = value.Replace("<root>", parent, StringComparison.Ordinal);
            }
        }

        value = value
            .Replace("<home>", Folder(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal)
            .Replace("<osUserName>", Environment.UserName, StringComparison.Ordinal)
            .Replace("<winAppData>", Folder(Environment.SpecialFolder.ApplicationData), StringComparison.Ordinal)
            .Replace(
                "<winLocalAppData>",
                Folder(Environment.SpecialFolder.LocalApplicationData),
                StringComparison.Ordinal)
            .Replace(
                "<winLocalAppDataLow>",
                Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")
                    .Replace('\\', '/'),
                StringComparison.Ordinal)
            .Replace(
                "<winDocuments>",
                Folder(Environment.SpecialFolder.MyDocuments),
                StringComparison.Ordinal)
            .Replace("<winPublic>", EnvironmentPath("PUBLIC"), StringComparison.Ordinal)
            .Replace(
                "<winProgramData>",
                Folder(Environment.SpecialFolder.CommonApplicationData),
                StringComparison.Ordinal)
            .Replace("<winDir>", EnvironmentPath("WINDIR"), StringComparison.Ordinal)
            .Replace("<xdgData>", XdgPath("XDG_DATA_HOME", ".local/share"), StringComparison.Ordinal)
            .Replace("<xdgConfig>", XdgPath("XDG_CONFIG_HOME", ".config"), StringComparison.Ordinal);

        if (storeGameId is { Length: > 0 })
        {
            value = value.Replace("<storeGameId>", storeGameId, StringComparison.Ordinal);
        }

        // Anything left is a placeholder this machine cannot answer —
        // <storeUserId> without a store account, or <base> with no install
        // directory. A path containing one is unusable, not nearly usable.
        if (UnresolvedPlaceholder().IsMatch(value))
        {
            return null;
        }

        // The caller may also have written a native environment variable, which
        // the task's own examples use.
        value = Environment.ExpandEnvironmentVariables(value);

        if (value.Contains('%', StringComparison.Ordinal) &&
            EnvironmentVariablePattern().IsMatch(value))
        {
            return null;
        }

        return NormaliseSeparators(value);
    }

    /// <summary>Reads a special folder, tolerating one that is not defined.</summary>
    /// <param name="folder">The folder to read.</param>
    /// <returns>Its path with forward separators, or an empty string.</returns>
    private static string Folder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder).Replace('\\', '/');

    /// <summary>Reads an environment variable as a path.</summary>
    /// <param name="name">The variable to read.</param>
    /// <returns>Its value with forward separators, or an empty string.</returns>
    private static string EnvironmentPath(string name) =>
        (Environment.GetEnvironmentVariable(name) ?? string.Empty).Replace('\\', '/');

    /// <summary>Reads an XDG base directory, falling back to its documented default.</summary>
    /// <param name="variable">The XDG variable.</param>
    /// <param name="relativeDefault">Path under the home directory to use when it is unset.</param>
    /// <returns>The resolved directory.</returns>
    private static string XdgPath(string variable, string relativeDefault)
    {
        var configured = Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrWhiteSpace(configured)
            ? $"{Folder(Environment.SpecialFolder.UserProfile)}/{relativeDefault}"
            : configured.Replace('\\', '/');
    }

    /// <summary>Collapses duplicate separators and applies the platform's own.</summary>
    /// <param name="value">The expanded path.</param>
    /// <returns>A tidy path.</returns>
    private static string NormaliseSeparators(string value)
    {
        var collapsed = DuplicateSeparators().Replace(value, "/");

        return OperatingSystem.IsWindows() ? collapsed.Replace('/', '\\') : collapsed;
    }

    /// <summary>Matches any remaining angle-bracket placeholder.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"<[A-Za-z]+>", RegexOptions.CultureInvariant)]
    private static partial Regex UnresolvedPlaceholder();

    /// <summary>Matches an unexpanded Windows environment variable.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariablePattern();

    /// <summary>Matches runs of forward slashes that are not a UNC prefix.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"(?<!^)/{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateSeparators();
}
