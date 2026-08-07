using ProjectDashboard.Models;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Pinning must lift cards to the front WITHOUT disturbing the order the active sort
/// produces on either side of the partition — a pin is a promotion, not an extra sort key.
/// </summary>
public class DashboardOrderingTests
{
    private static ProjectInfo Project(
        string name,
        string? path = null,
        string status = "active",
        string category = "Tools",
        bool dirty = false,
        DateTimeOffset? lastCommit = null) =>
        new()
        {
            DisplayName = name,
            DirectoryName = name,
            FullPath = path ?? $@"C:\projects\{name}",
            Manifest = new ProjectManifest { Status = status, Category = category },
            GitStatus = new GitStatus { IsDirty = dirty, LastCommitDate = lastCommit },
        };

    private static HashSet<string> Pins(params string[] paths) => DashboardOrdering.KeySet(paths);

    private static string[] Names(IEnumerable<ProjectInfo> projects) =>
        projects.Select(p => p.DisplayName).ToArray();

    [Theory]
    [InlineData("Name")]
    [InlineData("Last Commit")]
    [InlineData("Status")]
    [InlineData("Dirty First")]
    [InlineData("Category")]
    [InlineData("Something Unknown")]
    public void PinnedProjects_ComeFirst_InEverySortMode(string sort)
    {
        var projects = new[]
        {
            Project("alpha", status: "active", category: "Apps", dirty: true,
                lastCommit: DateTimeOffset.Parse("2026-01-05T00:00:00Z")),
            Project("bravo", status: "maintenance", category: "Tools",
                lastCommit: DateTimeOffset.Parse("2026-03-05T00:00:00Z")),
            Project("charlie", status: "archived", category: "Zebra", dirty: true,
                lastCommit: DateTimeOffset.Parse("2026-02-05T00:00:00Z")),
        };

        var ordered = DashboardOrdering.Apply(projects, sort, Pins(@"C:\projects\charlie")).ToList();

        Assert.Equal("charlie", ordered[0].DisplayName);
        Assert.Equal(3, ordered.Count);
    }

    [Fact]
    public void UnpinnedTail_KeepsTheActiveSortOrder()
    {
        var projects = new[]
        {
            Project("alpha", lastCommit: DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            Project("bravo", lastCommit: DateTimeOffset.Parse("2026-05-01T00:00:00Z")),
            Project("charlie", lastCommit: DateTimeOffset.Parse("2026-03-01T00:00:00Z")),
            Project("delta", lastCommit: DateTimeOffset.Parse("2026-04-01T00:00:00Z")),
        };

        var ordered = DashboardOrdering.Apply(projects, "Last Commit", Pins(@"C:\projects\alpha"));

        Assert.Equal(["alpha", "bravo", "delta", "charlie"], Names(ordered));
    }

    [Fact]
    public void MultiplePins_KeepTheActiveSortOrderAmongThemselves()
    {
        var projects = new[]
        {
            Project("alpha"),
            Project("bravo"),
            Project("charlie"),
            Project("delta"),
        };

        var ordered = DashboardOrdering.Apply(
            projects, "Name", Pins(@"C:\projects\delta", @"C:\projects\bravo"));

        Assert.Equal(["bravo", "delta", "alpha", "charlie"], Names(ordered));
    }

    [Fact]
    public void NoPins_LeavesTheSortUntouched()
    {
        var projects = new[] { Project("charlie"), Project("alpha"), Project("bravo") };

        Assert.Equal(["alpha", "bravo", "charlie"],
            Names(DashboardOrdering.Apply(projects, "Name", Pins())));
    }

    [Fact]
    public void DirtyFirstSort_StillPutsAPinnedCleanRepoAheadOfDirtyOnes()
    {
        var projects = new[]
        {
            Project("alpha", dirty: true),
            Project("bravo", dirty: true),
            Project("clean-pinned"),
        };

        var ordered = DashboardOrdering.Apply(projects, "Dirty First", Pins(@"C:\projects\clean-pinned"));

        Assert.Equal(["clean-pinned", "alpha", "bravo"], Names(ordered));
    }

    [Fact]
    public void RepoKey_IgnoresTrailingSeparatorsAndCase()
    {
        var pins = Pins(@"C:\projects\alpha\");
        Assert.True(DashboardOrdering.IsPinned(Project("alpha", @"C:\PROJECTS\Alpha"), pins));
    }

    [Fact]
    public void RemoteOnlyCard_WithNoPath_IsNeverPinned()
    {
        var cloud = new ProjectInfo { DisplayName = "cloud", IsRemoteOnly = true, FullPath = "" };
        Assert.False(DashboardOrdering.IsPinned(cloud, Pins(@"C:\projects\alpha")));
    }

    [Fact]
    public void KeySet_DropsBlankEntries()
    {
        Assert.Empty(DashboardOrdering.KeySet(["", "   "]));
    }
}
