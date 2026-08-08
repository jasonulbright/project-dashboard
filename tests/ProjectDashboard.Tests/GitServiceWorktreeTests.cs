using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Worktree list/add/remove and porcelain parsing (L-08).</summary>
public class GitServiceWorktreeTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task AddListRemove_Worktree_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt");
        var linkedPath = Path.Combine(TestEnv.NewDir("wt-parent"), "linked");

        var initial = await _git.GetWorktreesAsync(repo.Path);
        Assert.Single(initial);
        Assert.Equal("main", initial[0].Branch);   // primary worktree carries its branch

        Assert.True((await _git.AddWorktreeAsync(repo.Path, linkedPath, "side")).Success);

        var list = await _git.GetWorktreesAsync(repo.Path);
        Assert.Equal(2, list.Count);
        var linked = list.Single(w => w.Branch == "side");
        Assert.False(linked.IsBare);
        Assert.False(linked.IsDetached);
        Assert.NotEqual("", linked.HeadSha);

        Assert.True((await _git.RemoveWorktreeAsync(repo.Path, linkedPath)).Success);
        Assert.Single(await _git.GetWorktreesAsync(repo.Path));
    }

    [Fact]
    public void ParseWorktreePorcelain_HandlesBranchDetachedBareLocked()
    {
        const string porcelain =
            "worktree /repos/main\n" +
            "HEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repos/detached\n" +
            "HEAD bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n" +
            "detached\n" +
            "\n" +
            "worktree /repos/bare\n" +
            "bare\n" +
            "\n" +
            "worktree /repos/locked\n" +
            "HEAD cccccccccccccccccccccccccccccccccccccccc\n" +
            "branch refs/heads/side\n" +
            "locked\n";

        var entries = GitService.ParseWorktreePorcelain(porcelain);
        Assert.Equal(4, entries.Count);

        Assert.Equal("main", entries[0].Branch);
        Assert.False(entries[0].IsDetached);

        Assert.True(entries[1].IsDetached);
        Assert.Null(entries[1].Branch);

        Assert.True(entries[2].IsBare);
        Assert.Equal("", entries[2].HeadSha);

        Assert.Equal("side", entries[3].Branch);
        Assert.True(entries[3].IsLocked);
    }

    [Fact]
    public void ParseWorktreePorcelain_MarksOnlyTheFirstBlockAsTheMainWorktree()
    {
        const string porcelain =
            "worktree /repos/main\n" +
            "HEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repos/linked\n" +
            "HEAD bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n" +
            "branch refs/heads/side\n";

        var entries = GitService.ParseWorktreePorcelain(porcelain);
        Assert.True(entries[0].IsMain);
        Assert.False(entries[1].IsMain);
    }

    [Fact]
    public void ParseWorktreePorcelain_CarriesTheReasonGitGivesForAPrunableEntry()
    {
        const string porcelain =
            "worktree /repos/main\n" +
            "HEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repos/vanished\n" +
            "HEAD bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n" +
            "branch refs/heads/side\n" +
            "prunable gitdir file points to non-existent location\n";

        var entries = GitService.ParseWorktreePorcelain(porcelain);
        Assert.False(entries[0].IsPrunable);
        Assert.Equal("", entries[0].PrunableReason);
        Assert.True(entries[1].IsPrunable);
        Assert.Equal("gitdir file points to non-existent location", entries[1].PrunableReason);
    }

    [Fact]
    public async Task GetWorktrees_ListsTheMainWorktreeFirst_EvenWhenReadFromALinkedOne()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-main");
        var linkedPath = Path.Combine(TestEnv.NewDir("wt-main-parent"), "linked");
        Assert.True((await _git.AddWorktreeAsync(repo.Path, linkedPath, "side")).Success);

        // The app itself runs from a linked worktree in development; the listing it renders there
        // must still name the container repository rather than call the worktree its own main.
        var fromLinked = await _git.GetWorktreesAsync(linkedPath);

        Assert.Equal(2, fromLinked.Count);
        Assert.True(fromLinked[0].IsMain);
        Assert.Equal("main", fromLinked[0].Branch);
        Assert.False(fromLinked[1].IsMain);
        Assert.Equal("side", fromLinked[1].Branch);

        await _git.RemoveWorktreeAsync(repo.Path, linkedPath);
    }

    [Fact]
    public async Task PruneWorktrees_ClearsTheEntryWhoseTreeIsGone()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-prune");
        var linkedPath = Path.Combine(TestEnv.NewDir("wt-prune-parent"), "linked");
        Assert.True((await _git.AddWorktreeAsync(repo.Path, linkedPath, "side")).Success);

        // Deleting the tree behind git's back is exactly the state prune exists for.
        TestEnv.TryDeleteTree(linkedPath);

        var stale = await _git.GetWorktreesAsync(repo.Path);
        Assert.True(stale.Single(w => !w.IsMain).IsPrunable);

        Assert.True((await _git.PruneWorktreesAsync(repo.Path)).Success);

        var pruned = await _git.GetWorktreesAsync(repo.Path);
        Assert.True(Assert.Single(pruned).IsMain);
    }
}
