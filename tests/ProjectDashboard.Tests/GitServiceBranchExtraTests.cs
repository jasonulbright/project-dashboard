using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Branch rename, remote-tracking checkout, remote branch delete/prune (L-03) — file:// fixtures only.</summary>
public class GitServiceBranchExtraTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task RenameBranch_ChangesTheName()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("branch-rename");
        await _git.CreateBranchAsync(repo.Path, "feature");
        await _git.SwitchBranchAsync(repo.Path, "main");

        Assert.True((await _git.RenameBranchAsync(repo.Path, "feature", "topic")).Success);

        var names = (await _git.GetBranchesAsync(repo.Path)).Select(b => b.Name).ToList();
        Assert.Contains("topic", names);
        Assert.DoesNotContain("feature", names);
    }

    [Fact]
    public async Task GetRemoteBranches_ListsTrackingRefs_WithoutHeadPointer()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("rb-seed");
        // A second branch on the origin gives the clone a remote-tracking ref to find.
        await seed.GitAsync("switch", "-c", "release");
        seed.WriteFile("r.txt", "release\n");
        await seed.CommitAllAsync("release work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "rb-clone");
        await _git.FetchAsync(clone.Path);

        var remoteBranches = await _git.GetRemoteBranchesAsync(clone.Path);
        Assert.Contains("origin/release", remoteBranches);
        Assert.Contains("origin/main", remoteBranches);
        Assert.DoesNotContain(remoteBranches, b => b.EndsWith("/HEAD"));
    }

    [Fact]
    public async Task CheckoutRemoteBranch_CreatesLocalTrackingBranch()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("crb-seed");
        await seed.GitAsync("switch", "-c", "release");
        seed.WriteFile("r.txt", "release\n");
        await seed.CommitAllAsync("release work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "crb-clone");
        await _git.FetchAsync(clone.Path);

        Assert.True((await _git.CheckoutRemoteBranchAsync(clone.Path, "origin/release")).Success);

        var state = await _git.GetWorkingStateAsync(clone.Path);
        Assert.Equal("release", state!.Branch);
        Assert.Equal("origin/release", state.Upstream);
        Assert.True(clone.FileExists("r.txt"));
    }

    [Fact]
    public async Task DeleteRemoteBranch_RemovesItFromOrigin()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("drb-seed");
        await seed.GitAsync("switch", "-c", "throwaway");
        seed.WriteFile("t.txt", "temp\n");
        await seed.CommitAllAsync("temp work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "drb-clone");

        var before = await Git.RunAsync(bare.Path, "branch", "--list");
        Assert.Contains("throwaway", before);

        Assert.True((await _git.DeleteRemoteBranchAsync(clone.Path, "origin", "throwaway")).Success);

        var after = await Git.RunAsync(bare.Path, "branch", "--list");
        Assert.DoesNotContain("throwaway", after);
    }

    [Fact]
    public async Task PruneRemote_DropsStaleTrackingRefs()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("prune-seed");
        await seed.GitAsync("switch", "-c", "gone");
        seed.WriteFile("g.txt", "gone\n");
        await seed.CommitAllAsync("gone work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "prune-clone");
        await _git.FetchAsync(clone.Path);
        Assert.Contains("origin/gone", await _git.GetRemoteBranchesAsync(clone.Path));

        // Delete the branch straight on the bare origin, leaving the clone's tracking ref stale.
        await Git.RunAsync(bare.Path, "branch", "-D", "gone");

        Assert.True((await _git.PruneRemoteAsync(clone.Path, "origin")).Success);
        Assert.DoesNotContain("origin/gone", await _git.GetRemoteBranchesAsync(clone.Path));
    }

    [Fact]
    public async Task SetAndUnsetUpstream_MoveTheLinkWithoutTouchingTheTrackingRef()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("ups-seed");
        await seed.GitAsync("switch", "-c", "release");
        seed.WriteFile("r.txt", "release\n");
        await seed.CommitAllAsync("release work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "ups-clone");
        await _git.FetchAsync(clone.Path);

        Assert.True((await _git.SetUpstreamAsync(clone.Path, "main", "origin/release")).Success);
        var moved = (await _git.GetBranchesAsync(clone.Path)).Single(b => b.Name == "main");
        Assert.Equal("origin/release", moved.Upstream);

        Assert.True((await _git.UnsetUpstreamAsync(clone.Path, "main")).Success);
        var cleared = (await _git.GetBranchesAsync(clone.Path)).Single(b => b.Name == "main");
        Assert.Equal("", cleared.Upstream);
        // Only the link went; the remote-tracking ref is still there to relink to.
        Assert.Contains("origin/release", await _git.GetRemoteBranchesAsync(clone.Path));
    }

    [Fact]
    public async Task CompareRefs_CountsEachSideOfTheDivergence()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("compare");
        await repo.GitAsync("switch", "-c", "topic");
        repo.WriteFile("t1.txt", "one\n");
        await repo.CommitAllAsync("topic one");
        repo.WriteFile("t2.txt", "two\n");
        await repo.CommitAllAsync("topic two");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("m1.txt", "main\n");
        await repo.CommitAllAsync("main one");

        var topicVsMain = await _git.CompareRefsAsync(repo.Path, "topic", "main");
        Assert.Equal(new RefComparison(2, 1), topicVsMain);

        // The reverse reading is the same measurement with the sides swapped.
        var mainVsTopic = await _git.CompareRefsAsync(repo.Path, "main", "topic");
        Assert.Equal(new RefComparison(1, 2), mainVsTopic);
    }

    [Fact]
    public async Task CompareRefs_UnknownRefIsNotMeasured_RatherThanCountedAsZero()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("compare-unknown");
        Assert.Null(await _git.CompareRefsAsync(repo.Path, "main", "no-such-branch"));
        Assert.Null(await _git.CompareRefsAsync(repo.Path, "main", ""));
    }

    /// <summary>
    /// Two histories with no common commit are measurable: the symmetric difference is each
    /// side's whole history, and rev-list returns those counts rather than failing.
    /// </summary>
    [Fact]
    public async Task CompareRefs_HistoriesWithNoCommonCommit_AreCountedInFull()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("compare-unrelated");
        repo.WriteFile("m2.txt", "main two\n");
        await repo.CommitAllAsync("main two");

        await repo.GitAsync("checkout", "--orphan", "separate");
        await repo.GitAsync("rm", "-rf", "--cached", ".");
        repo.WriteFile("o1.txt", "orphan\n");
        await repo.CommitAllAsync("orphan one");

        Assert.Equal(new RefComparison(1, 2), await _git.CompareRefsAsync(repo.Path, "separate", "main"));
    }

    [Fact]
    public async Task IsValidRemoteName_RefusesWhatWouldCollideOrBeReadAsAnOption()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remote-names");

        Assert.True(await _git.IsValidRemoteNameAsync(repo.Path, "origin"));
        Assert.True(await _git.IsValidRemoteNameAsync(repo.Path, "up-stream.2"));

        Assert.False(await _git.IsValidRemoteNameAsync(repo.Path, ""));
        Assert.False(await _git.IsValidRemoteNameAsync(repo.Path, "-force"));
        Assert.False(await _git.IsValidRemoteNameAsync(repo.Path, "team/origin"));
        Assert.False(await _git.IsValidRemoteNameAsync(repo.Path, "has space"));
        Assert.False(await _git.IsValidRemoteNameAsync(repo.Path, "two..dots"));
        Assert.False(await _git.IsValidRemoteNameAsync(repo.Path, "tilde~1"));
    }

    [Theory]
    [InlineData("https://example.test/a.git", true)]
    [InlineData("git@example.test:owner/repo.git", true)]
    [InlineData(@"C:\repos\origin", true)]
    [InlineData("", false)]
    [InlineData("--upload-pack=cmd", false)]
    [InlineData("https://example.test/a b.git", false)]
    [InlineData("https://example.test/a\nb.git", false)]
    public void IsPlausibleRemoteUrl_RefusesOnlyWhatWouldMisfire(string url, bool plausible)
        => Assert.Equal(plausible, GitService.IsPlausibleRemoteUrl(url));
}
