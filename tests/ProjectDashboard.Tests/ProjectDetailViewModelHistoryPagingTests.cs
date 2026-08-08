using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Paging the History list past its recent window (L-05), and the invariant the surgery commands
/// read it under: the list is a contiguous walk from HEAD, so a row's position IS the depth a
/// rebase would be planned over. A page that does not continue the same walk must be refused,
/// not appended — an appended gap would let a plan rewrite commits the reader never saw.
/// </summary>
public class ProjectDetailViewModelHistoryPagingTests
{
    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo ProjectFor(TempRepo repo) =>
        new()
        {
            DirectoryName = Path.GetFileName(repo.Path),
            DisplayName = Path.GetFileName(repo.Path),
            FullPath = repo.Path
        };

    /// <summary>Repo with <paramref name="count"/> linear commits on main, newest last written.</summary>
    private static async Task<TempRepo> LinearRepoAsync(string prefix, int count)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        for (var i = 1; i <= count; i++)
        {
            repo.WriteFile("f.txt", $"revision {i}\n");
            await repo.CommitAllAsync($"commit {i}");
        }
        return repo;
    }

    /// <summary>
    /// Opens the page with only the newest <paramref name="seed"/> commits loaded — the state a
    /// scan's cached window leaves behind, and the state paging continues from.
    /// </summary>
    private static async Task<ProjectDetailViewModel> PageWithSeedWindowAsync(TempRepo repo, int seed)
    {
        var project = ProjectFor(repo);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path, seed);
        var vm = NewVm();
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;
        return vm;
    }

    private static async Task<List<string>> WalkAsync(TempRepo repo) =>
        [.. (await repo.GitAsync("rev-list", "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())];

    [Fact]
    public async Task LoadOlderCommits_ContinuesTheSameWalkWithoutGapOrRepeat()
    {
        using var repo = await LinearRepoAsync("page-append", 12);
        var vm = await PageWithSeedWindowAsync(repo, 5);
        Assert.Equal(5, vm.Commits.Count);
        Assert.True(vm.HistoryHasMore);

        await vm.LoadOlderCommitsCommand.ExecuteAsync(null);
        await vm.HistoryPageLoad;

        Assert.Equal(await WalkAsync(repo), vm.Commits.Select(c => c.Ref));
        Assert.False(vm.HistoryHasMore);
        Assert.Contains("whole branch", vm.HistoryPagingStatusText);
    }

    /// <summary>
    /// Depth is the row's position plus one, and a rebase is planned over exactly that many
    /// commits back from HEAD. Every appended row must satisfy it, or a plan silently widens.
    /// </summary>
    [Fact]
    public async Task LoadOlderCommits_KeepsEveryRowsPositionEqualToItsDepthFromHead()
    {
        using var repo = await LinearRepoAsync("page-depth", 12);
        var vm = await PageWithSeedWindowAsync(repo, 5);
        await vm.LoadOlderCommitsCommand.ExecuteAsync(null);
        await vm.HistoryPageLoad;

        Assert.Equal(12, vm.Commits.Count);
        for (var index = 0; index < vm.Commits.Count; index++)
        {
            var ahead = (await repo.GitAsync("rev-list", "--count", $"{vm.Commits[index].Ref}..HEAD")).Trim();
            Assert.Equal(index.ToString(), ahead);
        }
    }

    /// <summary>
    /// A pull, a fetch-and-reset, or a commit made in a terminal moves HEAD without this page
    /// knowing. A --skip taken from the new tip lands somewhere else in the walk, so the overlap
    /// commit is what proves the continuation before anything is appended.
    /// </summary>
    [Fact]
    public async Task LoadOlderCommits_RefusesAPageFromAWalkThatMoved()
    {
        using var repo = await LinearRepoAsync("page-moved", 12);
        var vm = await PageWithSeedWindowAsync(repo, 5);
        var staleWindow = vm.Commits.Select(c => c.Ref).ToList();

        // Two commits land under the loaded window, shifting every --skip position by two.
        repo.WriteFile("f.txt", "revision 13\n");
        await repo.CommitAllAsync("commit 13");
        repo.WriteFile("f.txt", "revision 14\n");
        await repo.CommitAllAsync("commit 14");

        await vm.LoadOlderCommitsCommand.ExecuteAsync(null);
        await vm.HistoryPageLoad;

        Assert.Contains("History moved", vm.HistoryPagingStatusText);
        // Reloaded from the new tip rather than left holding a spliced walk.
        Assert.Equal(await repo.HeadShaAsync(), vm.Commits[0].Ref);
        Assert.NotEqual(staleWindow[0], vm.Commits[0].Ref);
        var walk = await WalkAsync(repo);
        Assert.Equal(walk.Take(vm.Commits.Count), vm.Commits.Select(c => c.Ref));
    }

    /// <summary>
    /// A commit reloads the list. Re-reading only the recent window would collapse it back to
    /// that page, throwing away everything the reader paged in and any selection deeper than it.
    /// The window is a depth from HEAD, so the new tip pushes the oldest row out of it.
    /// </summary>
    [Fact]
    public async Task ACommitAfterPaging_RereadsTheWindowAtItsPagedDepth()
    {
        using var repo = await LinearRepoAsync("page-keep", 12);
        var vm = await PageWithSeedWindowAsync(repo, 5);
        await vm.LoadOlderCommitsCommand.ExecuteAsync(null);
        await vm.HistoryPageLoad;
        Assert.Equal(12, vm.Commits.Count);

        var deep = vm.Commits[9];
        vm.SelectedCommit = deep;
        repo.WriteFile("f.txt", "revision 13\n");
        await vm.StageAllCommand.ExecuteAsync(null);
        vm.CommitMessage = "commit 13";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(12, vm.Commits.Count);
        Assert.Equal(await repo.HeadShaAsync(), vm.Commits[0].Ref);
        Assert.NotNull(vm.SelectedCommit);
        Assert.Equal(deep.Ref, vm.SelectedCommit!.Ref);
        // The row moved down by the new tip, and its position still equals its depth.
        Assert.Equal(10, vm.Commits.IndexOf(vm.SelectedCommit));
        Assert.True(vm.HistoryHasMore);
    }

    [Fact]
    public async Task ProjectSwitch_ResetsTheWindowToTheRecentPage()
    {
        using var deepRepo = await LinearRepoAsync("page-switch-a", 12);
        using var otherRepo = await LinearRepoAsync("page-switch-b", 3);

        var vm = await PageWithSeedWindowAsync(deepRepo, 5);
        await vm.LoadOlderCommitsCommand.ExecuteAsync(null);
        await vm.HistoryPageLoad;
        Assert.Equal(12, vm.Commits.Count);

        await vm.SetProjectAsync(ProjectFor(otherRepo));
        await vm.WorkingStateRefresh;
        Assert.Empty(vm.Commits);
        Assert.False(vm.HistoryHasMore);
        Assert.Equal("", vm.HistoryPagingStatusText);
    }

    [Fact]
    public async Task LoadOlderCommits_OnAnEmptyListReadsTheWindowFromTheTip()
    {
        using var repo = await LinearRepoAsync("page-empty-seed", 4);
        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        Assert.Empty(vm.Commits);

        // The command is gated on HasMore; the read behind it still has to cope with no anchor.
        await vm.LoadOlderCommitsCommand.ExecuteAsync(null);
        await vm.HistoryPageLoad;
        Assert.Equal(4, vm.Commits.Count);
    }

    /// <summary>
    /// A window filled to its limit cannot tell a branch of exactly that length from a longer
    /// one; anything short of the limit is the end of the branch and says so.
    /// </summary>
    [Theory]
    [InlineData(50, 50, true)]
    [InlineData(49, 50, false)]
    [InlineData(0, 50, false)]
    [InlineData(51, 50, true)]
    public void WindowMayHaveMore_IsTrueOnlyForAFullWindow(int loaded, int windowSize, bool expected)
    {
        Assert.Equal(expected, ProjectDetailViewModel.WindowMayHaveMore(loaded, windowSize));
    }
}
