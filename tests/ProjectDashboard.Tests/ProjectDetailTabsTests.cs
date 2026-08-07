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
    [InlineData(Key.D8)] // 8th index (7) — no tab yet
    [InlineData(Key.D9)] // 9th index (8) — no tab yet
    [InlineData(Key.D0)] // 10th index (9) — no tab yet
    public void TabIndexForDigit_BeyondLiveTabsIsInert(Key key)
    {
        // Seven tabs today: Ctrl+8/9/0 must be inert no-ops, not out-of-range jumps.
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

    [Fact]
    public void LoadForTab_LazyTabsFetchWhenNotYetLoaded()
    {
        Assert.Equal(DetailTabLoad.Branches,
            ProjectDetailTabs.LoadForTab(DetailTab.Branches, branchesLoaded: false, stashesLoaded: false, pullRequestsLoaded: false));
        Assert.Equal(DetailTabLoad.Stashes,
            ProjectDetailTabs.LoadForTab(DetailTab.Stashes, branchesLoaded: false, stashesLoaded: false, pullRequestsLoaded: false));
        Assert.Equal(DetailTabLoad.PullRequests,
            ProjectDetailTabs.LoadForTab(DetailTab.PullRequests, branchesLoaded: false, stashesLoaded: false, pullRequestsLoaded: false));
    }

    [Fact]
    public void LoadForTab_LazyTabsStayInertOnceLoaded()
    {
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Branches, branchesLoaded: true, stashesLoaded: false, pullRequestsLoaded: false));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.Stashes, branchesLoaded: false, stashesLoaded: true, pullRequestsLoaded: false));
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(DetailTab.PullRequests, branchesLoaded: false, stashesLoaded: false, pullRequestsLoaded: true));
    }

    [Theory]
    [InlineData(DetailTab.Overview)]
    [InlineData(DetailTab.Changes)]
    [InlineData(DetailTab.History)]
    [InlineData(DetailTab.Issues)]
    public void LoadForTab_NonLazyTabsNeverFetch(DetailTab tab)
    {
        Assert.Equal(DetailTabLoad.None,
            ProjectDetailTabs.LoadForTab(tab, branchesLoaded: false, stashesLoaded: false, pullRequestsLoaded: false));
    }
}
