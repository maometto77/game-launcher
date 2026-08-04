namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Options parsed from the process command line.
/// </summary>
public sealed record StartupOptions
{
    /// <summary>Command-line switch that requests sample data.</summary>
    public const string SeedSampleDataSwitch = "--seed-sample-data";

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
    /// Parses startup options from raw command-line arguments.
    /// </summary>
    /// <param name="args">Arguments as supplied by WPF, excluding the executable name.</param>
    /// <returns>The parsed options. Unrecognised arguments are ignored.</returns>
    public static StartupOptions Parse(IEnumerable<string>? args)
    {
        var arguments = args?.ToArray() ?? [];

        return new StartupOptions
        {
            SeedSampleData = arguments.Any(argument =>
                string.Equals(argument, SeedSampleDataSwitch, StringComparison.OrdinalIgnoreCase))
        };
    }
}
