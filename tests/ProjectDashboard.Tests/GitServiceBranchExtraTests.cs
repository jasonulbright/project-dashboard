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
}
