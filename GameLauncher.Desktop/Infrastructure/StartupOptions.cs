namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Options parsed from the process command line.
/// </summary>
public sealed record StartupOptions
{
    /// <summary>Command-line switch that requests sample data.</summary>
    public const string SeedSampleDataSwitch = "--seed-sample-data";

    /// <summary>Command-line switch that redirects all writable state.</summary>
    public const string StateDirectorySwitch = "--state-dir";

    /// <summary>
    /// Whether the library should be populated with sample data when it is
    /// empty.
    /// </summary>
    /// <remarks>
    /// Opt-in rather than automatic. Sample entries point at executables that do
    /// not exist, so seeding somebody's real library would leave them cleaning
    /// up rows by hand.
    /// </remarks>
    public bool SeedSampleData { get; init; }

    /// <summary>
    /// Folder to keep the database, settings, artwork and logs in, or
    /// <see langword="null"/> to use the default under the local application
    /// data folder.
    /// </summary>
    /// <remarks>
    /// Exists so the application can be exercised against a throwaway library
    /// rather than the one somebody actually uses. Testing against real state is
    /// how a real library gets damaged, and there was no way to avoid it before
    /// this switch.
    /// </remarks>
    public string? StateDirectory { get; init; }

    /// <summary>
    /// Parses startup options from raw command-line arguments.
    /// </summary>
    /// <param name="args">Arguments as supplied by WPF, excluding the executable name.</param>
    /// <returns>The parsed options. Unrecognised arguments are ignored.</returns>
    /// <remarks>
    /// Accepts <c>--state-dir C:\path</c> and <c>--state-dir=C:\path</c>, because
    /// both forms are what people actually type. A relative path is rejected by
    /// <see cref="AppPaths"/> rather than silently resolved against whatever the
    /// working directory happens to be.
    /// </remarks>
    public static StartupOptions Parse(IEnumerable<string>? args)
    {
        var arguments = args?.ToArray() ?? [];

        var seed = arguments.Any(argument =>
            string.Equals(argument, SeedSampleDataSwitch, StringComparison.OrdinalIgnoreCase));

        return new StartupOptions
        {
            SeedSampleData = seed,
            StateDirectory = ParseStateDirectory(arguments)
        };
    }

    /// <summary>Reads the state directory out of the argument list.</summary>
    /// <param name="arguments">Raw arguments.</param>
    /// <returns>The requested directory, or <see langword="null"/> when absent.</returns>
    private static string? ParseStateDirectory(string[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];

            if (argument.StartsWith($"{StateDirectorySwitch}=", StringComparison.OrdinalIgnoreCase))
            {
                var inline = argument[(StateDirectorySwitch.Length + 1)..].Trim('"');
                return string.IsNullOrWhiteSpace(inline) ? null : inline;
            }

            if (string.Equals(argument, StateDirectorySwitch, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Length)
            {
                var next = arguments[index + 1].Trim('"');
                return string.IsNullOrWhiteSpace(next) ? null : next;
            }
        }

        return null;
    }
}
