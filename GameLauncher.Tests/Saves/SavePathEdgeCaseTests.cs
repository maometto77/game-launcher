using System.Text;
using GameLauncher.Desktop.Services.Saves;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Saves;

/// <summary>
/// Covers the two save-path gaps the Hydra audit named: an account placeholder
/// that used to make a rule unusable, and paths that compared unequal to
/// themselves.
/// </summary>
public sealed class SavePathEdgeCaseTests
{
    /// <summary>"Café" with a precomposed é — one code point, as Windows writes it.</summary>
    private const string ComposedName = "Caf\u00e9 Noir";

    /// <summary>The same name with a combining acute — two code points.</summary>
    private const string DecomposedName = "Cafe\u0301 Noir";

    [Fact]
    public void The_two_spellings_of_an_accented_name_are_the_same_path()
    {
        // The bug this closes: these are different strings and the same folder.
        // Compared raw, every scan reports the save as modified.
        Assert.NotEqual(ComposedName, DecomposedName);

        Assert.True(SavePathNormalizer.AreSame(
            $@"C:\Games\{ComposedName}\save.dat",
            $@"C:\Games\{DecomposedName}\save.dat"));
    }

    [Fact]
    public void Normalising_produces_form_c()
    {
        var normalised = SavePathNormalizer.Normalize($@"C:\Games\{DecomposedName}");

        Assert.True(normalised.IsNormalized(NormalizationForm.FormC));
        Assert.Contains(ComposedName, normalised, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Games\Doom\", @"C:\Games\Doom")]
    [InlineData(@"C:\Games//Doom", @"C:\Games\Doom")]
    [InlineData(@"C:/Games/Doom", @"C:\Games\Doom")]
    public void Separator_and_trailing_slash_differences_collapse(string left, string right) =>
        Assert.True(SavePathNormalizer.AreSame(left, right));

    [Fact]
    public void A_root_keeps_its_separator()
    {
        // "C:\" is a directory; "C:" is a drive-relative reference to whatever
        // that drive's current directory happens to be, which is a different and
        // much worse thing to hand to a file API.
        Assert.Equal(@"C:\", SavePathNormalizer.Normalize(@"C:\"));
    }

    [Fact]
    public void The_comparer_can_key_a_set()
    {
        var seen = new HashSet<string>(SavePathNormalizer.Comparer)
        {
            $@"C:\Games\{ComposedName}"
        };

        Assert.False(seen.Add($@"C:\Games\{DecomposedName}\"));
        Assert.False(seen.Add($@"c:\games\{ComposedName}"));
        Assert.Single(seen);
    }

    [Fact]
    public void An_account_folder_is_found_on_disk()
    {
        // The rule from the audit: <winAppData>/Sekiro/<storeUserId>/S0000.sl2.
        // Before this, the placeholder made the whole rule unusable.
        using var temp = new TempDirectory();

        var game = Path.Combine(temp.Path, "Sekiro");

        Directory.CreateDirectory(Path.Combine(game, "76561198000000001"));

        var found = StoreUserIdProbe.Discover(Path.Combine(game, "<storeUserId>", "S0000.sl2"));

        Assert.Equal("76561198000000001", Assert.Single(found));
    }

    [Fact]
    public void Every_account_is_returned_rather_than_the_first()
    {
        // Two accounts on one machine is two real save folders. Picking one would
        // silently hide the other's progress.
        using var temp = new TempDirectory();

        Directory.CreateDirectory(Path.Combine(temp.Path, "76561198000000001"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "76561198000000002"));

        var found = StoreUserIdProbe.Discover(Path.Combine(temp.Path, "<storeUserId>", "save.dat"));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Folders_that_sit_beside_accounts_are_not_mistaken_for_them()
    {
        using var temp = new TempDirectory();

        foreach (var name in new[] { "76561198000000001", "common", "config", "remote", "7" })
        {
            Directory.CreateDirectory(Path.Combine(temp.Path, name));
        }

        var found = StoreUserIdProbe.Discover(Path.Combine(temp.Path, "<storeUserId>"));

        // "7" is a slot number, not an account; the named folders are Steam's own.
        Assert.Equal("76561198000000001", Assert.Single(found));
    }

    [Theory]
    [InlineData("76561198000000001", true)]  // Steam64
    [InlineData("123456789", true)]          // Steam32 account id
    [InlineData("a1b2c3d4e5f6", true)]       // emulator profile token
    [InlineData("12345", false)]             // too short to be an account
    [InlineData("Documents", false)]         // a word, not an id
    [InlineData("config", false)]            // named exclusion
    public void An_id_is_recognised_by_its_shape(string name, bool expected) =>
        Assert.Equal(expected, StoreUserIdProbe.LooksLikeAccountId(name));

    [Fact]
    public void A_numeric_account_sorts_ahead_of_a_profile_token()
    {
        using var temp = new TempDirectory();

        Directory.CreateDirectory(Path.Combine(temp.Path, "offlineprofile1"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "76561198000000001"));

        var found = StoreUserIdProbe.Discover(Path.Combine(temp.Path, "<storeUserId>"));

        // A person with both is far more likely to care about the store account.
        Assert.Equal("76561198000000001", found[0]);
    }

    [Fact]
    public void A_rule_with_no_account_on_disk_still_resolves_to_nothing()
    {
        using var temp = new TempDirectory();

        Assert.Empty(StoreUserIdProbe.Discover(Path.Combine(temp.Path, "<storeUserId>", "save.dat")));

        // And the expander still refuses the raw rule, so a half-expanded path
        // never reaches a file API.
        Assert.Null(LudusaviPathExpander.Expand("<winAppData>/Sekiro/<storeUserId>/S0000.sl2", null));
    }

    [Fact]
    public void Supplying_an_account_expands_the_rule()
    {
        var expanded = LudusaviPathExpander.Expand(
            "<winAppData>/Sekiro/<storeUserId>/S0000.sl2", null, null, "76561198000000001");

        Assert.NotNull(expanded);
        Assert.Contains("76561198000000001", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("<storeUserId>", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void The_partial_expansion_keeps_only_the_account_placeholder()
    {
        var partial = LudusaviPathExpander.ExpandRetainingStoreUserId(
            "<winAppData>/Sekiro/<storeUserId>/S0000.sl2", null);

        Assert.NotNull(partial);
        Assert.Contains(StoreUserIdProbe.Placeholder, partial, StringComparison.Ordinal);
        Assert.DoesNotContain("<winAppData>", partial, StringComparison.Ordinal);

        // A different unresolvable placeholder still fails the whole thing, so
        // the probe is never handed a path with a hole somewhere else in it.
        Assert.Null(LudusaviPathExpander.ExpandRetainingStoreUserId(
            "<base>/saves/<storeUserId>", installDirectory: null));
    }

    [Fact]
    public void An_expanded_path_is_already_in_form_c()
    {
        var expanded = LudusaviPathExpander.Expand($"<home>/{DecomposedName}/save.dat", null);

        Assert.NotNull(expanded);
        Assert.True(expanded.IsNormalized(NormalizationForm.FormC));
    }
}
