using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// An empty card grid has four unrelated causes. Rendering the same blank panel for
/// all of them is the failure this table forecloses.
/// </summary>
public class DashboardEmptyStateTests
{
    [Fact]
    public void LoadInFlight_ShowsLoading_EvenWithNothingDiscovered()
        => Assert.Equal(DashboardContent.Loading,
            DashboardEmptyState.Select(loading: true, scanFailed: false, rootExists: true, 0, 0));

    [Fact]
    public void RetryInFlight_ShowsLoading_NotTheFailureItIsRetrying()
        => Assert.Equal(DashboardContent.Loading,
            DashboardEmptyState.Select(loading: true, scanFailed: true, rootExists: false, 0, 0));

    [Fact]
    public void FaultedScan_IsDistinctFromAnEmptyRoot()
    {
        var failed = DashboardEmptyState.Select(false, scanFailed: true, rootExists: true, 0, 0);
        var empty = DashboardEmptyState.Select(false, scanFailed: false, rootExists: true, 0, 0);

        Assert.Equal(DashboardContent.ScanFailed, failed);
        Assert.Equal(DashboardContent.EmptyRoot, empty);
        Assert.NotEqual(failed, empty);
    }

    [Fact]
    public void MissingRoot_IsDistinctFromAnEmptyRoot()
    {
        var missing = DashboardEmptyState.Select(false, false, rootExists: false, 0, 0);
        var empty = DashboardEmptyState.Select(false, false, rootExists: true, 0, 0);

        Assert.Equal(DashboardContent.RootMissing, missing);
        Assert.NotEqual(missing, empty);
    }

    [Fact]
    public void FilteredToNothing_IsNotAnEmptyRoot()
        => Assert.Equal(DashboardContent.NoMatches,
            DashboardEmptyState.Select(false, false, true, discoveredCount: 12, filteredCount: 0));

    [Fact]
    public void ProjectsShowing_YieldCards()
        => Assert.Equal(DashboardContent.Cards,
            DashboardEmptyState.Select(false, false, true, discoveredCount: 12, filteredCount: 3));

    [Fact]
    public void EveryOutcome_IsReachable()
    {
        var seen = new HashSet<DashboardContent>
        {
            DashboardEmptyState.Select(true, false, true, 0, 0),
            DashboardEmptyState.Select(false, true, true, 0, 0),
            DashboardEmptyState.Select(false, false, false, 0, 0),
            DashboardEmptyState.Select(false, false, true, 0, 0),
            DashboardEmptyState.Select(false, false, true, 5, 0),
            DashboardEmptyState.Select(false, false, true, 5, 5),
        };

        Assert.Equal(Enum.GetValues<DashboardContent>().Length, seen.Count);
    }
}
