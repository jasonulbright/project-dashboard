using System.Windows.Input;
using ProjectDashboard.Models;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Pure tab-routing logic for the detail page: the Ctrl+digit → index mapping and
/// the enum-keyed lazy-load dispatch. Driving the live TabControl needs an STA
/// host the rest of the suite avoids, so the routing is factored into
/// <see cref="ProjectDetailTabs"/> and exercised directly here.
/// </summary>
public class ProjectDetailTabsTests
{
    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D2, 1)]
    [InlineData(Key.D7, 6)]
    [InlineData(Key.D9, 8)]
    [InlineData(Key.D0, 9)]
    public void TabIndexForDigit_MapsDigitsWithZeroAsTenth(Key key, int expected)
    {
        Assert.Equal(expected, ProjectDetailTabs.TabIndexForDigit(key, tabCount: 10));
    }

    [Theory]
    [InlineData(Key.D8)] // 8th index (7) — beyond a seven-tab page
    [InlineData(Key.D9)] // 9th index (8) — beyond a seven-tab page
    [InlineData(Key.D0)] // 10th index (9) — beyond a seven-tab page
    public void TabIndexForDigit_BeyondLiveTabsIsInert(Key key)
    {
        // Digits past the live tab count must be inert no-ops, not out-of-range jumps.
        Assert.Null(ProjectDetailTabs.TabIndexForDigit(key, tabCount: 7));
    }

    [Fact]
    public void TabIndexForDigit_FirstSevenUnchangedAtSevenTabs()
    {
        for (var i = 0; i < 7; i++)
            Assert.Equal(i, ProjectDetailTabs.TabIndexForDigit(Key.D1 + i, tabCount: 7));
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Enter)]
    [InlineData(Key.NumPad1)]
    public void TabIndexForDigit_NonDigitKeysAreInert(Key key)
    {
        Assert.Null(ProjectDetailTabs.TabIndexForDigit(key, tabCount: 10));
    }

    /// <summary>Nothing fetched yet — the state every project switch resets to.</summary>
    private static DetailTabLoadState Nothing => new(false, false, false, false, false, false, false);

    [Theory]
    [InlineData(DetailTab.Branches, DetailTabLoad.Branches)]
    [InlineData(DetailTab.Stashes, DetailTabLoad.Stashes)]
    [InlineData(DetailTab.PullRequests, DetailTabLoad.PullRequests)]
    [InlineData(DetailTab.Actions, DetailTabLoad.WorkflowRuns)]
    [InlineData(DetailTab.Releases, DetailTabLoad.Releases)]
    [InlineData(DetailTab.Repo, DetailTabLoad.RepoTab)]
    [InlineData(DetailTab.Internals, DetailTabLoad.Internals)]
    public void LoadForTab_LazyTabsFetchWhenNotYetLoaded(DetailTab tab, DetailTabLoad expected)
    {
        Assert.Equal(expected, ProjectDetailTabs.LoadForTab(tab, Nothing));
    }

    [Fact]
    public void LoadForTab_LazyTabsStayInertOnceLoaded()
    {
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Branches, Nothing with { Branches = true }));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Stashes, Nothing with { Stashes = true }));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.PullRequests, Nothing with { PullRequests = true }));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Actions, Nothing with { WorkflowRuns = true }));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Releases, Nothing with { Releases = true }));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Repo, Nothing with { RepoTab = true }));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Internals, Nothing with { Internals = true }));
    }

    /// <summary>
    /// The lazy surfaces differ only by identically typed flags: a transposed pair
    /// would route one tab's load to another and show the wrong repository data.
    /// </summary>
    [Fact]
    public void LoadForTab_EachTabReadsOnlyItsOwnFlag()
    {
        Assert.Equal(DetailTabLoad.WorkflowRuns,
            ProjectDetailTabs.LoadForTab(DetailTab.Actions,
                new DetailTabLoadState(true, true, true, false, true, true, true)));
        Assert.Equal(DetailTabLoad.Releases,
            ProjectDetailTabs.LoadForTab(DetailTab.Releases,
                new DetailTabLoadState(true, true, true, true, false, true, true)));
        Assert.Equal(DetailTabLoad.RepoTab,
            ProjectDetailTabs.LoadForTab(DetailTab.Repo,
                new DetailTabLoadState(true, true, true, true, true, false, true)));
        Assert.Equal(DetailTabLoad.Internals,
            ProjectDetailTabs.LoadForTab(DetailTab.Internals,
                new DetailTabLoadState(true, true, true, true, true, true, false)));
    }

    [Theory]
    [InlineData(DetailTab.Overview)]
    [InlineData(DetailTab.Changes)]
    [InlineData(DetailTab.History)]
    [InlineData(DetailTab.Issues)]
    public void LoadForTab_NonLazyTabsNeverFetch(DetailTab tab)
    {
        Assert.Equal(DetailTabLoad.None, ProjectDetailTabs.LoadForTab(tab, Nothing));
    }

    [Theory]
    [InlineData(DetailTab.Overview, 0)]
    [InlineData(DetailTab.History, 1)]
    [InlineData(DetailTab.Repo, 2)]
    public void IndexOfTab_FindsTheTabByItsTag(DetailTab tab, int expected)
    {
        IEnumerable<DetailTab?> tags = [DetailTab.Overview, DetailTab.History, DetailTab.Repo];

        Assert.Equal(expected, ProjectDetailTabs.IndexOfTab(tags, tab));
    }

    /// <summary>
    /// A deep link to a surface this page does not host must leave the selection alone
    /// rather than land on whatever occupies that position.
    /// </summary>
    [Fact]
    public void IndexOfTab_UnhostedTagSelectsNothing()
    {
        Assert.Null(ProjectDetailTabs.IndexOfTab([DetailTab.Overview, DetailTab.Changes], DetailTab.Repo));
        Assert.Null(ProjectDetailTabs.IndexOfTab([], DetailTab.Overview));
    }

    /// <summary>An untagged tab is not a match for any surface, and never shifts the count.</summary>
    [Fact]
    public void IndexOfTab_SkipsUntaggedTabs()
    {
        Assert.Equal(2, ProjectDetailTabs.IndexOfTab([null, null, DetailTab.Issues], DetailTab.Issues));
    }
}
