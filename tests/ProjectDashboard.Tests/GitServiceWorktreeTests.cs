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
}
