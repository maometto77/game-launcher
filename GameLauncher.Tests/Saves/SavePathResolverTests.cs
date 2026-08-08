using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Saves;
using GameLauncher.Tests.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Saves;

/// <summary>
/// Covers the Ludusavi manifest resolver: placeholder expansion, platform and
/// tag filtering, and lookup by title or Steam id.
/// </summary>
public sealed class SavePathResolverTests
{
    /// <summary>
    /// A manifest in the real schema, small enough to reason about.
    /// </summary>
    private const string Manifest = """
        An Example Game:
          files:
            <base>/saves:
              tags:
                - save
            <base>/settings.json:
              tags:
                - config
            <winAppData>/ExampleGame/profile.sav:
              when:
                - os: windows
              tags:
                - save
            <xdgData>/example/save.dat:
              when:
                - os: linux
              tags:
                - save
          installDir:
            AnExampleGame: {}
          steam:
            id: 4242

        Oregon Trail, The:
          files:
            <winDocuments>/OregonTrail/save.dat:
              tags:
                - save

        Config Only Game:
          files:
            <base>/settings.ini:
              tags:
                - config

        Untagged Game:
          files:
            <base>/data.bin: {}
        """;

    [Fact]
    public async Task A_game_is_found_by_title_and_its_paths_are_expanded()
    {
        using var fixture = new ResolverFixture(Manifest);

        var install = fixture.CreateInstall("AnExampleGame", "saves");

        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "An Example Game",
            InstallDirectory = install
        });

        Assert.True(result.Found);
        Assert.Equal("An Example Game", result.MatchedTitle);

        var saves = result.Locations.Single(location => location.Path.EndsWith("saves", StringComparison.Ordinal));

        Assert.True(Path.IsPathRooted(saves.Path));
        Assert.DoesNotContain('<', saves.Path);
        Assert.Contains("save", saves.Tags);
    }

    [Fact]
    public async Task A_steam_id_is_preferred_over_the_title()
    {
        using var fixture = new ResolverFixture(Manifest);

        // The title is deliberately wrong: the id must win, because it
        // identifies a game exactly while a title has to be matched.
        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "Something Else Entirely",
            SteamAppId = 4242,
            IncludeMissing = true
        });

        Assert.Equal("An Example Game", result.MatchedTitle);
    }

    [Fact]
    public async Task A_catalogue_style_title_still_matches()
    {
        using var fixture = new ResolverFixture(Manifest);

        // The same normalisation the discovery catalogue uses, so the launcher's
        // display title reaches the manifest's catalogue-style one.
        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "The Oregon Trail",
            IncludeMissing = true
        });

        Assert.Equal("Oregon Trail, The", result.MatchedTitle);
    }

    [Fact]
    public async Task A_game_the_manifest_does_not_cover_is_an_ordinary_outcome()
    {
        using var fixture = new ResolverFixture(Manifest);

        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery { Title = "Not In The Manifest" });

        Assert.False(result.Found);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task Config_only_entries_are_not_indexed()
    {
        using var fixture = new ResolverFixture(Manifest);

        // Keeping them would roughly double the index to describe files a save
        // feature does not want.
        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "Config Only Game",
            IncludeMissing = true
        });

        Assert.False(result.Found);
    }

    [Fact]
    public async Task An_untagged_entry_is_treated_as_a_save()
    {
        using var fixture = new ResolverFixture(Manifest);

        var install = fixture.CreateInstall("UntaggedGame");

        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "Untagged Game",
            InstallDirectory = install,
            IncludeMissing = true
        });

        Assert.True(result.Found);
        Assert.Single(result.Locations);
    }

    [Fact]
    public async Task Entries_for_another_platform_are_dropped()
    {
        using var fixture = new ResolverFixture(Manifest);

        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "An Example Game",
            InstallDirectory = fixture.CreateInstall("AnExampleGame"),
            IncludeMissing = true
        });

        var paths = result.Locations.Select(location => location.Path).ToArray();

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(paths, path => path.Contains("ExampleGame", StringComparison.Ordinal));
            Assert.DoesNotContain(paths, path => path.Contains("example/save.dat", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(paths, path => path.Contains("ExampleGame", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Only_locations_that_exist_are_returned_by_default()
    {
        using var fixture = new ResolverFixture(Manifest);

        // The install has no "saves" folder, so nothing should come back.
        var install = fixture.CreateInstall("AnExampleGame");

        var strict = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "An Example Game",
            InstallDirectory = install
        });

        var lenient = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "An Example Game",
            InstallDirectory = install,
            IncludeMissing = true
        });

        Assert.DoesNotContain(strict.Locations, location => !location.Exists);
        Assert.Contains(lenient.Locations, location => !location.Exists);
    }

    [Fact]
    public async Task A_path_relative_to_an_install_is_skipped_when_none_is_known()
    {
        using var fixture = new ResolverFixture(Manifest);

        // Without an install directory, <base> cannot be resolved. Returning a
        // literal "<base>/saves" would be a directory nobody has.
        var result = await fixture.Resolver.ResolveAsync(new SavePathQuery
        {
            Title = "An Example Game",
            IncludeMissing = true
        });

        Assert.DoesNotContain(result.Locations, location => location.Path.Contains('<', StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_leaves_the_resolver_unavailable()
    {
        using var fixture = new ResolverFixture("this: is: not: valid: yaml: [");

        // No manifest means nothing is known, which is not an error.
        Assert.False(await fixture.Resolver.IsAvailableAsync());
        Assert.False((await fixture.Resolver.ResolveAsync(new SavePathQuery { Title = "Anything" })).Found);
    }

    [Theory]
    [InlineData("<base>/saves", true)]
    [InlineData("<home>/docs", true)]
    [InlineData("<storeUserId>/saves", false)]
    [InlineData("<somethingUnknown>/x", false)]
    public void Only_fully_resolvable_paths_are_returned(string template, bool expected)
    {
        var expanded = LudusaviPathExpander.Expand(template, @"C:\Games\Example");

        Assert.Equal(expected, expanded is not null);
    }

    [Fact]
    public void Base_root_and_game_are_derived_from_the_install_directory()
    {
        var expanded = LudusaviPathExpander.Expand("<root>/<game>/saves", @"C:\Games\Example");

        Assert.NotNull(expanded);
        Assert.EndsWith(Path.Combine("Games", "Example", "saves"), expanded);
    }

    /// <summary>A resolver over a manifest written into a throwaway directory.</summary>
    private sealed class ResolverFixture : IDisposable
    {
        private readonly string _root;

        public ResolverFixture(string manifest)
        {
            _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // Written directly into the cache so no download is attempted: these
            // tests must never reach the network.
            File.WriteAllText(Path.Combine(_root, "ludusavi-manifest.yaml"), manifest);

            Resolver = new LudusaviSavePathResolver(
                new StubHttpClientFactory(),
                new AppPaths(_root),
                NullLogger<LudusaviSavePathResolver>.Instance);
        }

        public LudusaviSavePathResolver Resolver { get; }

        /// <summary>Creates an install directory, optionally with sub-folders.</summary>
        public string CreateInstall(string name, params string[] children)
        {
            var install = Path.Combine(_root, "games", name);
            Directory.CreateDirectory(install);

            foreach (var child in children)
            {
                Directory.CreateDirectory(Path.Combine(install, child));
            }

            return install;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is not worth failing a passing test over.
            }
        }
    }
}
