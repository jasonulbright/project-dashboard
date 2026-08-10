using ProjectDashboard.Models;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// An empty card grid has five unrelated causes. Rendering the same blank panel for
/// all of them is the failure this table forecloses.
/// </summary>
public class DashboardEmptyStateTests
{
    [Fact]
    public void LoadInFlight_ShowsLoading_EvenWithNothingDiscovered()
        => Assert.Equal(DashboardContent.Loading,
            DashboardEmptyState.Select(loading: true, scanFailed: false, configuredRoots: 1, usableRoots: 1, 0, 0));

    [Fact]
    public void RetryInFlight_ShowsLoading_NotTheFailureItIsRetrying()
        => Assert.Equal(DashboardContent.Loading,
            DashboardEmptyState.Select(loading: true, scanFailed: true, configuredRoots: 1, usableRoots: 0, 0, 0));

    [Fact]
    public void FaultedScan_IsDistinctFromAnEmptyRoot()
    {
        var failed = DashboardEmptyState.Select(false, scanFailed: true, 1, 1, 0, 0);
        var empty = DashboardEmptyState.Select(false, scanFailed: false, 1, 1, 0, 0);

        Assert.Equal(DashboardContent.ScanFailed, failed);
        Assert.Equal(DashboardContent.EmptyRoot, empty);
        Assert.NotEqual(failed, empty);
    }

    [Fact]
    public void UnreadableRoots_AreDistinctFromAnEmptyRoot()
    {
        var unavailable = DashboardEmptyState.Select(false, false, configuredRoots: 2, usableRoots: 0, 0, 0);
        var empty = DashboardEmptyState.Select(false, false, configuredRoots: 2, usableRoots: 2, 0, 0);

        Assert.Equal(DashboardContent.RootsUnavailable, unavailable);
        Assert.NotEqual(unavailable, empty);
    }

    /// <summary>
    /// A first run has configured nothing; it is not the same fault as a configured folder that
    /// cannot be reached, and pointing it at "reconnect the drive" would be nonsense.
    /// </summary>
    [Fact]
    public void NoRootsConfigured_IsDistinctFromRootsThatCannotBeRead()
    {
        var none = DashboardEmptyState.Select(false, false, configuredRoots: 0, usableRoots: 0, 0, 0);
        var unavailable = DashboardEmptyState.Select(false, false, configuredRoots: 1, usableRoots: 0, 0, 0);

        Assert.Equal(DashboardContent.NoRootsConfigured, none);
        Assert.Equal(DashboardContent.RootsUnavailable, unavailable);
    }

    [Fact]
    public void FilteredToNothing_IsNotAnEmptyRoot()
        => Assert.Equal(DashboardContent.NoMatches,
            DashboardEmptyState.Select(false, false, 1, 1, discoveredCount: 12, filteredCount: 0));

    [Fact]
    public void ProjectsShowing_YieldCards()
        => Assert.Equal(DashboardContent.Cards,
            DashboardEmptyState.Select(false, false, 1, 1, discoveredCount: 12, filteredCount: 3));

    [Fact]
    public void ReloadOverARenderedGrid_KeepsTheCards()
        => Assert.Equal(DashboardContent.Cards,
            DashboardEmptyState.Select(loading: true, scanFailed: false, 1, 1, 30, 30));

    [Fact]
    public void FaultedRescanOverARenderedGrid_KeepsTheCards()
        => Assert.Equal(DashboardContent.Cards,
            DashboardEmptyState.Select(loading: false, scanFailed: true, 1, 1, 30, 30));

    [Fact]
    public void VanishedRootOverACachedList_KeepsTheCards()
        => Assert.Equal(DashboardContent.Cards,
            DashboardEmptyState.Select(loading: false, scanFailed: false, 1, 0, 30, 30));

    [Theory]
    [InlineData("ShowLoading")]
    [InlineData("ShowScanFailed")]
    [InlineData("ShowNoRootsConfigured")]
    [InlineData("ShowRootsUnavailable")]
    [InlineData("ShowEmptyRoot")]
    [InlineData("ShowNoMatches")]
    [InlineData("ShowCards")]
    public void EveryOutcome_HasABodyPanel(string flag)
        => Assert.Contains($"Binding {flag}, Converter", RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml"),
            StringComparison.Ordinal);

    [Fact]
    public void ReloadOverCards_ReportsItselfBesideTheGrid()
        => Assert.Contains("Binding ShowRefreshing, Converter", RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml"),
            StringComparison.Ordinal);

    [Fact]
    public void EveryOutcome_IsReachable()
    {
        var seen = new HashSet<DashboardContent>
        {
            DashboardEmptyState.Select(true, false, 1, 1, 0, 0),
            DashboardEmptyState.Select(false, true, 1, 1, 0, 0),
            DashboardEmptyState.Select(false, false, 0, 0, 0, 0),
            DashboardEmptyState.Select(false, false, 1, 0, 0, 0),
            DashboardEmptyState.Select(false, false, 1, 1, 0, 0),
            DashboardEmptyState.Select(false, false, 1, 1, 5, 0),
            DashboardEmptyState.Select(false, false, 1, 1, 5, 5),
        };

        Assert.Equal(Enum.GetValues<DashboardContent>().Length, seen.Count);
    }

    // ── Naming what the scan could not read ─────────────────────────────────────

    [Fact]
    public void EveryRootRead_ReportsNothing()
        => Assert.Null(DashboardEmptyState.DescribeUnavailableRoots(
            [Status(@"C:\one", RootAvailability.Available), Status(@"D:\two", RootAvailability.Available)]));

    /// <summary>
    /// The count and the names both: a reader who cannot tell WHICH folder is missing cannot
    /// tell which repositories are absent from the grid.
    /// </summary>
    [Fact]
    public void APartiallyReadScan_NamesTheFoldersItCouldNotRead()
    {
        var text = DashboardEmptyState.DescribeUnavailableRoots(
        [
            Status(@"C:\one", RootAvailability.Available),
            Status(@"D:\archive", RootAvailability.Missing),
            Status(@"E:\locked", RootAvailability.Unreadable),
        ]);

        Assert.NotNull(text);
        Assert.Contains("Scanned 1 of 3", text);
        Assert.Contains(@"D:\archive (not there)", text);
        Assert.Contains(@"E:\locked (could not be read)", text);
    }

    [Fact]
    public void ADisabledRoot_IsAChoiceAndIsNotReportedAsAFailure()
        => Assert.Null(DashboardEmptyState.DescribeUnavailableRoots(
            [Status(@"C:\one", RootAvailability.Available), Status(@"D:\off", RootAvailability.Disabled)]));

    [Fact]
    public void ATruncatedWalk_SaysSoRatherThanPresentingAFloorAsATotal()
    {
        var text = DashboardEmptyState.DescribeTruncatedRoots(
            [Status(@"C:\one", RootAvailability.Available, truncated: true)]);

        Assert.NotNull(text);
        Assert.Contains(@"C:\one", text);
        Assert.Contains("stopped early", text);
    }

    [Fact]
    public void ACompleteWalk_ReportsNoTruncation()
        => Assert.Null(DashboardEmptyState.DescribeTruncatedRoots([Status(@"C:\one", RootAvailability.Available)]));

    private static RootStatus Status(string path, RootAvailability availability, bool truncated = false) =>
        new(path, "", availability, 0, truncated, "");
}
