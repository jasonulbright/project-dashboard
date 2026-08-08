using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Remote management (L-02) — add/rename/set-url/remove reflected back through GetRemotes.</summary>
public class GitServiceRemotesTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task AddRenameSetUrlRemove_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes");

        Assert.True((await _git.AddRemoteAsync(repo.Path, "origin", "https://example.test/a.git")).Success);
        Assert.True((await _git.AddRemoteAsync(repo.Path, "backup", "https://example.test/b.git")).Success);

        var remotes = (await _git.GetRemotesAsync(repo.Path)).Remotes;
        Assert.Equal(2, remotes.Count);
        var origin = remotes.Single(r => r.Name == "origin");
        Assert.Equal("https://example.test/a.git", origin.FetchUrl);
        Assert.Equal("https://example.test/a.git", origin.PushUrl);

        Assert.True((await _git.RenameRemoteAsync(repo.Path, "backup", "mirror")).Success);
        remotes = (await _git.GetRemotesAsync(repo.Path)).Remotes;
        Assert.Contains(remotes, r => r.Name == "mirror");
        Assert.DoesNotContain(remotes, r => r.Name == "backup");

        Assert.True((await _git.SetRemoteUrlAsync(repo.Path, "origin", "https://example.test/c.git")).Success);
        origin = (await _git.GetRemotesAsync(repo.Path)).Remotes.Single(r => r.Name == "origin");
        Assert.Equal("https://example.test/c.git", origin.FetchUrl);

        Assert.True((await _git.RemoveRemoteAsync(repo.Path, "mirror")).Success);
        remotes = (await _git.GetRemotesAsync(repo.Path)).Remotes;
        Assert.Single(remotes);
        Assert.Equal("origin", remotes[0].Name);
    }

    [Fact]
    public async Task GetRemotes_DistinctPushUrl_IsReported()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-push");
        await _git.AddRemoteAsync(repo.Path, "origin", "https://example.test/fetch.git");
        // A separate push URL is a common triangular-workflow setup.
        await repo.GitAsync("remote", "set-url", "--push", "origin", "https://example.test/push.git");

        var origin = Assert.Single((await _git.GetRemotesAsync(repo.Path)).Remotes);
        Assert.Equal("https://example.test/fetch.git", origin.FetchUrl);
        Assert.Equal("https://example.test/push.git", origin.PushUrl);
    }

    /// <summary>
    /// A repository with nothing configured and a directory `git remote -v` refuses both leave
    /// the list empty, and the Branches tab shows its "no remotes configured" state off that
    /// list. Only the flag separates the two, so the read reports it and the surface gates the
    /// empty state on it. Neither case throws — a refusal is still a non-zero exit.
    /// </summary>
    [Fact]
    public async Task GetRemotes_SeparatesNothingConfiguredFromAReadThatFailed()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-none");

        var configured = await _git.GetRemotesAsync(repo.Path);
        Assert.Empty(configured.Remotes);
        Assert.False(configured.HasError);
        Assert.Equal("", configured.ErrorText);

        var refused = await _git.GetRemotesAsync(TestEnv.NewDir("not-a-repo"));
        Assert.Empty(refused.Remotes);
        Assert.True(refused.HasError);
        Assert.NotEqual("", refused.ErrorText);
    }

    /// <summary>The remote-branch read carries the same separation, and the same picker rests on it.</summary>
    [Fact]
    public async Task GetRemoteBranches_SeparatesNoneTrackedFromAReadThatFailed()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remote-branches-none");

        var tracked = await _git.GetRemoteBranchesAsync(repo.Path);
        Assert.Empty(tracked.Branches);
        Assert.False(tracked.HasError);

        var refused = await _git.GetRemoteBranchesAsync(TestEnv.NewDir("not-a-repo-either"));
        Assert.Empty(refused.Branches);
        Assert.True(refused.HasError);
        Assert.NotEqual("", refused.ErrorText);
    }
}
