using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Views;

/// <summary>
/// Covers the shell's two-level navigation: five sidebar sections, each holding
/// the pages that answer the same question.
/// </summary>
/// <remarks>
/// The pages themselves are covered by <see cref="DialogSmokeTests"/>. What
/// matters here is the movement between them — which is where a user notices a
/// launcher that forgets what they were doing.
/// </remarks>
public sealed class ShellNavigationTests
{
    [Fact]
    public async Task The_landing_section_is_the_library()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.InitializeAsync();

        // What someone opens a launcher to reach.
        Assert.Equal(NavigationSection.Library, shell.ActiveSection);
        Assert.IsType<HomeViewModel>(shell.CurrentPage);
        Assert.False(shell.HasError, shell.ErrorMessage);
    }

    [Fact]
    public async Task Every_section_maps_to_a_page()
    {
        using var host = new TestAppHost();
        var shell = host.Resolve<MainWindowViewModel>();

        foreach (var section in Enum.GetValues<NavigationSection>())
        {
            await shell.NavigateCommand.ExecuteAsync(section);

            Assert.Equal(section, shell.ActiveSection);
            Assert.NotNull(shell.CurrentPage);
            Assert.NotEmpty(shell.SubSections);
        }

        Assert.False(shell.HasError, shell.ErrorMessage);
    }

    [Fact]
    public async Task The_library_gathers_the_pages_that_used_to_own_a_sidebar_row()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);

        Assert.Equal(
            ["Overview", "Installed games", "Collections", "Achievements"],
            shell.SubSections.Select(tab => tab.Label));

        Assert.True(shell.HasSubSections);
    }

    [Fact]
    public async Task A_section_with_a_single_page_draws_no_tab_strip()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Downloads);

        // One tab that cannot be switched away from is decoration, not
        // navigation — the entry still exists, the strip just stays hidden.
        Assert.Single(shell.SubSections);
        Assert.False(shell.HasSubSections);
    }

    [Fact]
    public async Task Choosing_a_tab_opens_it()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);

        await SelectAsync(shell, "collections");

        Assert.IsType<CollectionsViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task Returning_to_a_section_reopens_the_tab_it_was_left_on()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);
        await SelectAsync(shell, "achievements");

        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Downloads);
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);

        // Not Overview: a user who was working through their achievements and
        // glanced at the download queue has not asked to start again.
        Assert.Equal("achievements", shell.SelectedSubSection?.Key);
        Assert.IsType<AchievementsViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task Leaving_a_section_and_coming_back_keeps_the_same_page_instance()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Search);

        var discover = shell.CurrentPage;
        Assert.IsType<DiscoverViewModel>(discover);

        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Downloads);
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Search);

        // The same object, so the search text, facet filters and scroll position
        // are all still there. A fresh instance would look identical in a
        // screenshot and be infuriating to use.
        Assert.Same(discover, shell.CurrentPage);
    }

    [Fact]
    public async Task Tabs_within_a_section_keep_their_pages_too()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);

        await SelectAsync(shell, "games");
        var library = shell.CurrentPage;

        await SelectAsync(shell, "collections");
        await SelectAsync(shell, "games");

        Assert.Same(library, shell.CurrentPage);
    }

    [Fact]
    public async Task Moving_sideways_never_puts_anything_on_the_back_stack()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);
        await SelectAsync(shell, "games");
        await SelectAsync(shell, "collections");
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Downloads);

        // Back is for coming out of a game page, not for retracing the sidebar.
        // Offering it here would also let the content disagree with the tab
        // strip, which would still be showing the section the user had chosen.
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public async Task Switching_section_drops_a_drill_down_from_the_back_stack()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);

        await DrillInAsync(host);
        Assert.True(shell.CanGoBack);

        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Downloads);

        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public async Task Switching_tab_drops_a_drill_down_from_the_back_stack()
    {
        using var host = new TestAppHost();

        var shell = host.Resolve<MainWindowViewModel>();
        await shell.NavigateCommand.ExecuteAsync(NavigationSection.Library);

        await DrillInAsync(host);
        Assert.True(shell.CanGoBack);

        await SelectAsync(shell, "collections");

        Assert.False(shell.CanGoBack);
    }

    /// <summary>
    /// Pushes a page onto the back stack the way opening a game from the library
    /// does.
    /// </summary>
    /// <param name="host">The container under test.</param>
    /// <returns>A task that completes once the pushed page is active.</returns>
    private static Task DrillInAsync(TestAppHost host) =>
        host.Resolve<INavigationService>().NavigateToAsync<SettingsViewModel>();

    /// <summary>
    /// Picks a tab the way the tab strip does, and waits for the page it opens.
    /// </summary>
    /// <param name="shell">The shell view model under test.</param>
    /// <param name="key">Key of the tab to select.</param>
    /// <returns>A task that completes once the sub-view has loaded.</returns>
    /// <remarks>
    /// Setting the property is the real path: the strip binds
    /// <c>SelectedItem</c> to it. The command it starts is what makes the
    /// navigation awaitable — a property setter cannot be.
    /// </remarks>
    private static async Task SelectAsync(MainWindowViewModel shell, string key)
    {
        shell.SelectedSubSection = shell.SubSections.Single(tab => tab.Key == key);

        if (shell.SelectSubSectionCommand.ExecutionTask is { } running)
        {
            await running;
        }
    }
}
