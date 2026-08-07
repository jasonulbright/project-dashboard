using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Remote flows against file:// bare origins created per test — no network, no gh.
/// </summary>
public class GitServiceRemoteTests
{
    private readonly GitService _git = new();

    /// <summary>Bare file:// origin seeded with one commit on main.</summary>
    private sealed class Origin : IDisposable
    {
        public required TempRepo Seed { get; init; }
        public required TempRepo Bare { get; init; }
        public string Url => Bare.FileUrl;

        public static async Task<Origin> CreateAsync()
        {
            var seed = await TempRepo.CreateWithCommitAsync("seed");
            var bare = await TempRepo.CreateBareFromAsync(seed);
            return new Origin { Seed = seed, Bare = bare };
        }

        public async Task<string> HeadShaAsync(string rev = "HEAD") =>
            (await Git.RunAsync(Bare.Path, "rev-parse", rev)).Trim();

        public void Dispose()
        {
            Seed.Dispose();
            Bare.Dispose();
        }
    }

    [Fact]
    public async Task Clone_FromBareRepo_ProducesWorkingCheckout()
    {
        using var origin = await Origin.CreateAsync();
        var targetParent = TestEnv.NewDir("clone-target");

        var error = await _git.CloneAsync(origin.Url, targetParent);

        Assert.Null(error);
        var clonedPath = Path.Combine(targetParent, "remote");
        Assert.True(GitService.IsGitRepo(clonedPath));
        Assert.Equal("line one\n", File.ReadAllText(Path.Combine(clonedPath, "file.txt")));

        var state = await _git.GetWorkingStateAsync(clonedPath);
        Assert.NotNull(state);
        Assert.Equal("main", state.Branch);
        Assert.False(state.IsDirty);
        Assert.True(state.HasUpstream);
    }

    [Fact]
    public async Task Clone_FromMissingSource_ReturnsError()
    {
        var targetParent = TestEnv.NewDir("clone-fail");
        var missing = Path.Combine(TestEnv.Root, "does-not-exist.git");

        var error = await _git.CloneAsync(new Uri(missing).AbsoluteUri, targetParent);

        Assert.NotNull(error);
        Assert.NotEqual("", error.Trim());
    }

    [Fact]
    public async Task Pull_FastForwardsWhenBehind()
    {
        using var origin = await Origin.CreateAsync();
        using var ahead = await TempRepo.CloneFromAsync(origin.Bare, "ahead");
        using var behind = await TempRepo.CloneFromAsync(origin.Bare, "behind");

        ahead.WriteFile("advance.txt", "pushed from ahead\n");
        await ahead.CommitAllAsync("advance origin");
        await ahead.GitAsync("push");

        Assert.True((await _git.FetchAsync(behind.Path)).Success);
        var state = await _git.GetWorkingStateAsync(behind.Path);
        Assert.Equal(1, state!.Behind);
        Assert.Equal(0, state.Ahead);

        var pull = await _git.PullAsync(behind.Path);

        Assert.True(pull.Success);
        Assert.Equal(await ahead.HeadShaAsync(), await behind.HeadShaAsync());
        Assert.True(behind.FileExists("advance.txt"));
    }

    [Fact]
    public async Task Pull_FfOnly_RefusesDivergedBranch()
    {
        using var origin = await Origin.CreateAsync();
        using var remoteSide = await TempRepo.CloneFromAsync(origin.Bare, "remote-side");
        using var local = await TempRepo.CloneFromAsync(origin.Bare, "local");

        remoteSide.WriteFile("remote.txt", "remote change\n");
        await remoteSide.CommitAllAsync("remote commit");
        await remoteSide.GitAsync("push");

        local.WriteFile("local.txt", "local change\n");
        await local.CommitAllAsync("local commit");
        var headBefore = await local.HeadShaAsync();

        var pull = await _git.PullAsync(local.Path);

        // --ff-only must fail loudly on divergence and leave HEAD untouched.
        Assert.False(pull.Success);
        Assert.Equal(headBefore, await local.HeadShaAsync());
        var state = await _git.GetWorkingStateAsync(local.Path);
        Assert.Equal(1, state!.Ahead);
        Assert.Equal(1, state.Behind);
    }

    [Fact]
    public async Task Push_NonFastForward_IsRefusedAndOriginUnchanged()
    {
        using var origin = await Origin.CreateAsync();
        using var remoteSide = await TempRepo.CloneFromAsync(origin.Bare, "remote-side");
        using var local = await TempRepo.CloneFromAsync(origin.Bare, "local");

        remoteSide.WriteFile("remote.txt", "remote change\n");
        await remoteSide.CommitAllAsync("remote commit");
        await remoteSide.GitAsync("push");
        var originHead = await origin.HeadShaAsync();

        local.WriteFile("local.txt", "local change\n");
        await local.CommitAllAsync("local commit");

        var push = await _git.PushAsync(local.Path);

        Assert.False(push.Success);
        Assert.Equal(originHead, await origin.HeadShaAsync());
    }

    [Fact]
    public async Task Push_FastForward_AdvancesOrigin()
    {
        using var origin = await Origin.CreateAsync();
        using var clone = await TempRepo.CloneFromAsync(origin.Bare, "push-ok");

        clone.WriteFile("new.txt", "content\n");
        await clone.CommitAllAsync("pushable commit");

        var push = await _git.PushAsync(clone.Path);

        Assert.True(push.Success);
        Assert.Equal(await clone.HeadShaAsync(), await origin.HeadShaAsync());
    }

    [Fact]
    public async Task Status_RenamedRemote_StillCarriesRemoteUrl()
    {
        using var origin = await Origin.CreateAsync();
        using var clone = await TempRepo.CloneFromAsync(origin.Bare, "renamed");
        await clone.GitAsync("remote", "rename", "origin", "github");

        var status = await _git.GetStatusAsync(clone.Path);

        Assert.False(status.HasError);
        Assert.Equal(origin.Url, status.RemoteUrl);
    }

    [Fact]
    public async Task Status_OriginPresent_ReadsOriginUrl()
    {
        using var origin = await Origin.CreateAsync();
        using var clone = await TempRepo.CloneFromAsync(origin.Bare, "origin-url");

        var status = await _git.GetStatusAsync(clone.Path);

        Assert.Equal(origin.Url, status.RemoteUrl);
    }

    [Fact]
    public async Task Status_MultipleRemotes_PrefersOrigin()
    {
        using var origin = await Origin.CreateAsync();
        using var other = await Origin.CreateAsync();
        using var clone = await TempRepo.CloneFromAsync(origin.Bare, "multi-remote");
        await clone.GitAsync("remote", "add", "alpha", other.Url);

        var status = await _git.GetStatusAsync(clone.Path);

        Assert.Equal(origin.Url, status.RemoteUrl);
    }

    [Fact]
    public async Task Status_NoRemote_LeavesRemoteUrlEmpty()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("no-remote");

        var status = await _git.GetStatusAsync(repo.Path);

        Assert.False(status.HasError);
        Assert.Equal("", status.RemoteUrl);
    }

    [Fact]
    public async Task Status_FetchOnlyOrigin_DoesNotInventRemoteUrl()
    {
        // A fetch-only stanza (remote.origin.fetch set, url unset) makes `git
        // remote` list origin while `git remote get-url origin` exits 0 echoing
        // the literal name "origin" — which must never surface as a RemoteUrl.
        using var repo = await TempRepo.CreateWithCommitAsync("fetch-only-origin");
        await repo.GitAsync("config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");

        Assert.Null(await _git.ResolveDefaultRemoteAsync(repo.Path));
        var status = await _git.GetStatusAsync(repo.Path);

        Assert.False(status.HasError);
        Assert.Equal("", status.RemoteUrl);
    }

    [Fact]
    public async Task Status_UrlLessOrigin_YieldsToRemoteWithUrl()
    {
        using var origin = await Origin.CreateAsync();
        using var other = await Origin.CreateAsync();
        using var clone = await TempRepo.CloneFromAsync(origin.Bare, "urlless-origin");
        await clone.GitAsync("remote", "add", "backup", other.Url);
        await clone.GitAsync("config", "--unset", "remote.origin.url");

        Assert.Equal("backup", await _git.ResolveDefaultRemoteAsync(clone.Path));
        var status = await _git.GetStatusAsync(clone.Path);

        Assert.False(status.HasError);
        Assert.Equal(other.Url, status.RemoteUrl);
    }

    [Fact]
    public async Task Push_NewBranchWithoutUpstream_SetsUpstreamOnOrigin()
    {
        using var origin = await Origin.CreateAsync();
        using var clone = await TempRepo.CloneFromAsync(origin.Bare, "auto-upstream");

        await clone.GitAsync("switch", "-c", "topic");
        clone.WriteFile("topic.txt", "topic work\n");
        await clone.CommitAllAsync("topic commit");

        var push = await _git.PushAsync(clone.Path);

        Assert.True(push.Success);
        var upstream = (await clone.GitAsync("rev-parse", "--abbrev-ref", "topic@{upstream}")).Trim();
        Assert.Equal("origin/topic", upstream);
        Assert.Equal(await clone.HeadShaAsync(), await origin.HeadShaAsync("refs/heads/topic"));
    }
}
