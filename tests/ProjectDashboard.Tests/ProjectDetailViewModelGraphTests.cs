using System.Text;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The commit-graph pane: the row geometry a renderer draws from, the paging that keeps
/// lanes lined up across pages, and the selection it shares with the History list. The service's
/// own lane assignment is proven in <see cref="CommitGraphServiceTests"/>.
/// </summary>
public class ProjectDetailViewModelGraphTests
{
    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo ProjectFor(string path) =>
        new() { DirectoryName = Path.GetFileName(path), DisplayName = Path.GetFileName(path), FullPath = path };

    private static async Task<TempRepo> NewRepoAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        return repo;
    }

    private static async Task CommitAsync(TempRepo repo, string subject)
    {
        repo.WriteFile(subject + ".txt", subject + "\n");
        await repo.CommitAllAsync(subject);
    }

    private static async Task<ProjectDetailViewModel> PageOnAsync(TempRepo repo)
    {
        var project = ProjectFor(repo.Path);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path, 50);
        var vm = NewVm();
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;
        return vm;
    }

    private static CommitGraphRow Find(ProjectDetailViewModel vm, string subject) =>
        vm.GraphRows.Single(r => r.Subject == subject);

    // ── Row geometry ─────────────────────────────────────────────────────────

    /// <summary>
    /// The lanes entering a row are the previous row's, and the first row of a page inherits the
    /// page's own. Threading them any other way loses every edge crossing a page's top edge.
    /// </summary>
    [Fact]
    public void ForPage_ThreadsEachRowsOutgoingLanesIntoTheNextRowsIncoming()
    {
        // Diamond m→{b,c}→a, plus an unrelated root: lane 1 carries the merge's second edge and
        // closes AT "a", so it appears in no row of a page that starts there.
        List<GraphCommit> ordered =
        [
            new() { Sha = "m", Parents = ["b", "c"] },
            new() { Sha = "c", Parents = ["a"] },
            new() { Sha = "b", Parents = ["a"] },
            new() { Sha = "a", Parents = [] },
            new() { Sha = "z", Parents = [] }
        ];
        CommitGraphService.AssignLanes(ordered);

        var head = CommitGraphRow.ForPage(CommitGraphService.BuildPage(ordered, skip: 0, take: 3));
        Assert.Empty(head[0].IncomingLanes);
        Assert.Equal(ordered[0].OpenLanes, head[1].IncomingLanes);
        Assert.Equal(ordered[1].OpenLanes, head[2].IncomingLanes);

        // The merge opens lane 1 for its second parent and keeps its own.
        Assert.Equal([1], head[0].BranchingLanes);
        Assert.Empty(head[0].MergingLanes);
        Assert.False(head[0].HasEdgeAbove);
        Assert.True(head[0].HasEdgeBelow);

        var tail = CommitGraphRow.ForPage(CommitGraphService.BuildPage(ordered, skip: 3, take: 2));
        // "a" closes both edges of the diamond: its own lane from above, and lane 1 merging in.
        Assert.Equal([0, 1], tail[0].IncomingLanes);
        Assert.Equal([1], tail[0].MergingLanes);
        Assert.True(tail[0].HasEdgeAbove);
        Assert.False(tail[0].HasEdgeBelow);
        Assert.Empty(tail[0].PassThroughLanes);
    }

    [Fact]
    public void ForPage_MarksALaneThatCrossesARowWithoutTouchingIt()
    {
        List<GraphCommit> ordered =
        [
            new() { Sha = "m", Parents = ["b", "c"] },
            new() { Sha = "c", Parents = ["a"] },
            new() { Sha = "b", Parents = ["a"] },
            new() { Sha = "a", Parents = [] }
        ];
        CommitGraphService.AssignLanes(ordered);
        var rows = CommitGraphRow.ForPage(CommitGraphService.BuildPage(ordered, skip: 0, take: 4));

        // "c" sits on lane 1; lane 0 (waiting for "b") crosses its row untouched.
        var c = rows.Single(r => r.Sha == "c");
        Assert.Equal(1, c.Lane);
        Assert.Equal([0], c.PassThroughLanes);
    }

    // ── The pane ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenCommitGraph_DrawsEveryLocalCommitWithItsDecorations()
    {
        using var repo = await NewRepoAsync("graph-open");
        await CommitAsync(repo, "A");
        await repo.GitAsync("tag", "-a", "v1", "-m", "release one");
        await repo.GitAsync("switch", "-c", "side");
        await CommitAsync(repo, "C");
        await repo.GitAsync("switch", "main");
        await CommitAsync(repo, "B");
        await repo.GitAsync("merge", "--no-ff", "-m", "M", "side");

        var vm = await PageOnAsync(repo);
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);

        Assert.True(vm.CommitGraphVisible);
        Assert.Equal(4, vm.GraphRows.Count);
        Assert.False(vm.GraphEmpty);
        Assert.Equal("", vm.GraphErrorText);
        Assert.False(vm.GraphHasMore);

        var merge = Find(vm, "M");
        Assert.True(merge.IsMerge);
        Assert.Single(merge.BranchingLanes);
        Assert.Equal(2, vm.GraphLaneCount);
        Assert.Equal(2 * ProjectDetailViewModel.GraphLaneWidth, vm.GraphLaneColumnWidth);

        Assert.Contains(new GraphRef(GraphRefKind.Tag, "v1"), Find(vm, "A").Refs);
        Assert.True(Find(vm, "A").IsRoot);
        Assert.False(vm.SafetyOverlayHidden);
    }

    [Fact]
    public async Task OpenCommitGraph_PreselectsTheCommitTheHistoryListIsOn()
    {
        using var repo = await NewRepoAsync("graph-preselect");
        await CommitAsync(repo, "A");
        await CommitAsync(repo, "B");

        var vm = await PageOnAsync(repo);
        vm.SelectedCommit = vm.Commits.Last();
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedGraphRow);
        Assert.Equal("A", vm.SelectedGraphRow!.Subject);
    }

    [Fact]
    public async Task SelectingAGraphRow_MovesTheHistorySelectionToTheSameCommit()
    {
        using var repo = await NewRepoAsync("graph-sync");
        await CommitAsync(repo, "A");
        await CommitAsync(repo, "B");

        var vm = await PageOnAsync(repo);
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);

        vm.SelectedGraphRow = Find(vm, "A");
        Assert.NotNull(vm.SelectedCommit);
        Assert.Equal(Find(vm, "A").Sha, vm.SelectedCommit!.Ref);
    }

    /// <summary>
    /// The graph walks every local branch; the History list walks HEAD. A row off that branch
    /// has no counterpart, which is stated rather than left as a selection that did not move.
    /// </summary>
    [Fact]
    public async Task SelectingACommitOffTheBranch_SaysSoInsteadOfMovingTheHistorySelection()
    {
        using var repo = await NewRepoAsync("graph-offbranch");
        await CommitAsync(repo, "A");
        await repo.GitAsync("switch", "-c", "side");
        await CommitAsync(repo, "S");
        await repo.GitAsync("switch", "main");

        var vm = await PageOnAsync(repo);
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Commits, c => c.Message == "S");

        var offBranch = Find(vm, "S");
        vm.SelectedGraphRow = offBranch;

        Assert.Null(vm.SelectedCommit);
        Assert.Contains(offBranch.ShortSha, vm.GraphStatusText);
    }

    [Fact]
    public async Task ARepositoryWithNoCommits_ShowsTheEmptyStateAndNoError()
    {
        using var repo = await NewRepoAsync("graph-empty");
        var vm = await PageOnAsync(repo);

        await vm.OpenCommitGraphCommand.ExecuteAsync(null);

        Assert.True(vm.GraphEmpty);
        Assert.Equal("", vm.GraphErrorText);
        Assert.Empty(vm.GraphRows);
    }

    /// <summary>A failed walk and an empty repository must not look the same to a reader.</summary>
    [Fact]
    public async Task AFailedWalk_IsReportedRatherThanDrawnAsAnEmptyGraph()
    {
        var broken = TestEnv.NewDir("graph-broken");
        await File.WriteAllTextAsync(Path.Combine(broken, ".git"), "gitdir: ./nowhere\n");

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(broken));
        await vm.WorkingStateRefresh;
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);

        Assert.NotEqual("", vm.GraphErrorText);
        Assert.False(vm.GraphEmpty);
        Assert.Empty(vm.GraphRows);
        Assert.False(vm.GraphHasMore);
    }

    /// <summary>
    /// A second page must continue the first without repeating or skipping a row, and every row
    /// must keep the lane the first page would have given it.
    /// </summary>
    [Fact]
    public async Task LoadMoreGraph_AppendsThePageAfterTheOneAlreadyDrawn()
    {
        const int total = ProjectDetailViewModel.GraphPageSize + 40;
        using var repo = await NewRepoAsync("graph-page");
        await ImportLinearHistoryAsync(repo, total);

        var vm = await PageOnAsync(repo);
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);
        Assert.Equal(ProjectDetailViewModel.GraphPageSize, vm.GraphRows.Count);
        Assert.True(vm.GraphHasMore);
        Assert.True(vm.LoadMoreGraphCommand.CanExecute(null));

        await vm.LoadMoreGraphCommand.ExecuteAsync(null);
        await vm.GraphRefresh;

        Assert.Equal(total, vm.GraphRows.Count);
        Assert.False(vm.GraphHasMore);
        Assert.Equal(total, vm.GraphRows.Select(r => r.Sha).Distinct().Count());

        var walk = (await repo.GitAsync("rev-list", "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim());
        Assert.Equal(walk, vm.GraphRows.Select(r => r.Sha));
        Assert.All(vm.GraphRows, r => Assert.Equal(0, r.Lane));
    }

    [Fact]
    public async Task AProjectSwitch_ClosesTheGraph()
    {
        using var repo = await NewRepoAsync("graph-switch-a");
        await CommitAsync(repo, "A");
        using var other = await TempRepo.CreateWithCommitAsync("graph-switch-b");

        var vm = await PageOnAsync(repo);
        await vm.OpenCommitGraphCommand.ExecuteAsync(null);
        Assert.True(vm.CommitGraphVisible);

        await vm.SetProjectAsync(ProjectFor(other.Path));
        await vm.WorkingStateRefresh;

        Assert.False(vm.CommitGraphVisible);
        Assert.Empty(vm.GraphRows);
        Assert.Equal(0, vm.GraphLaneCount);
    }

    /// <summary>Builds <paramref name="count"/> linear commits on main in one fast-import stream.</summary>
    private static async Task ImportLinearHistoryAsync(TempRepo repo, int count)
    {
        var stream = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            var blobMark = i * 2 - 1;
            var commitMark = i * 2;
            var content = $"revision {i}\n";
            stream.Append($"blob\nmark :{blobMark}\ndata {content.Length}\n{content}");
            var message = $"commit {count - i + 1}\n";
            stream.Append($"commit refs/heads/main\nmark :{commitMark}\n");
            stream.Append($"author Fixture <fixture@example.invalid> {1700000000 + i} +0000\n");
            stream.Append($"committer Fixture <fixture@example.invalid> {1700000000 + i} +0000\n");
            stream.Append($"data {message.Length}\n{message}");
            if (i > 1) stream.Append($"from :{(i - 1) * 2}\n");
            stream.Append($"M 100644 :{blobMark} file.txt\n\n");
        }
        await Git.RunWithStdinAsync(repo.Path, stream.ToString(), "fast-import", "--quiet");
    }
}
