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

        var remotes = await _git.GetRemotesAsync(repo.Path);
        Assert.Equal(2, remotes.Count);
        var origin = remotes.Single(r => r.Name == "origin");
        Assert.Equal("https://example.test/a.git", origin.FetchUrl);
        Assert.Equal("https://example.test/a.git", origin.PushUrl);

        Assert.True((await _git.RenameRemoteAsync(repo.Path, "backup", "mirror")).Success);
        remotes = await _git.GetRemotesAsync(repo.Path);
        Assert.Contains(remotes, r => r.Name == "mirror");
        Assert.DoesNotContain(remotes, r => r.Name == "backup");

        Assert.True((await _git.SetRemoteUrlAsync(repo.Path, "origin", "https://example.test/c.git")).Success);
        origin = (await _git.GetRemotesAsync(repo.Path)).Single(r => r.Name == "origin");
        Assert.Equal("https://example.test/c.git", origin.FetchUrl);

        Assert.True((await _git.RemoveRemoteAsync(repo.Path, "mirror")).Success);
        remotes = await _git.GetRemotesAsync(repo.Path);
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

        var origin = Assert.Single(await _git.GetRemotesAsync(repo.Path));
        Assert.Equal("https://example.test/fetch.git", origin.FetchUrl);
        Assert.Equal("https://example.test/push.git", origin.PushUrl);
    }

    [Fact]
    public async Task GetRemotes_NoRemotes_IsEmpty()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-none");
        Assert.Empty(await _git.GetRemotesAsync(repo.Path));
        // A non-repo directory fails `git remote -v`; degrade to empty, never throw.
        Assert.Empty(await _git.GetRemotesAsync(TestEnv.NewDir("not-a-repo")));
    }
}
