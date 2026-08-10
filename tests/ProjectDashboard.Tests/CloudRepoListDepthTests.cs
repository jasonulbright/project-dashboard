using ProjectDashboard.Models;
using System.IO;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Tests;

/// <summary>
/// What the account's repository list is allowed to claim. One capped read feeds both the Cloud
/// cards and the clone picker, so a read that came back full has left repositories out of the grid
/// and out of the picker — and the only thing that keeps either honest is saying so.
/// </summary>
public class CloudRepoListDepthTests
{
    private static GitHubService.RemoteRepoPage Page(int count, int limit) =>
        new([.. Enumerable.Range(1, count).Select(n => new RemoteRepo { NameWithOwner = $"bob/r{n}" })],
            count >= limit, limit);

    private static GitHubService.ListRead<GitHubService.RemoteRepoPage> Read(
        GitHubService.RemoteRepoPage? page, string error = "") => new(page, error);

    /// <summary>Answers the account read without gh, so both outcomes and the cap are reachable.</summary>
    private sealed class StubDiscovery(GitHubService.ListRead<GitHubService.RemoteRepoPage> read)
        : ProjectDiscoveryService(new GitService(), new GitHubService(new SettingsService()),
            new SettingsService(), new ManifestStore())
    {
        protected override Task<GitHubService.ListRead<GitHubService.RemoteRepoPage>> ReadAccountReposAsync(
            CancellationToken ct) => Task.FromResult(read);
    }

    private static async Task<StubDiscovery> ScannedAsync(
        GitHubService.ListRead<GitHubService.RemoteRepoPage> read, List<ProjectInfo>? local = null)
    {
        var discovery = new StubDiscovery(read);
        await discovery.AppendRemoteOnlyAsync(local ?? [], CancellationToken.None);
        return discovery;
    }

    // ── The service ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheAccountRead_AsksForTheDocumentedWindow()
        => Assert.Equal(
            ["repo", "list", "--json", "nameWithOwner,description,visibility,updatedAt", "--limit", "200"],
            GitHubService.BuildUserRepoListArgs(GitHubService.UserRepoLimit));

    /// <summary>
    /// A response nothing could be read from is a failed read. Read as an empty list, it would
    /// report an account that owns no repositories — and the grid would drop every Cloud card it
    /// had on the strength of it.
    /// </summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"message":"Bad credentials"}""")]
    public void AnUnreadableResponse_IsAFailedRead_NotAnAccountWithNoRepositories(string json)
        => Assert.Null(GitHubService.ParseUserRepos(json));

    [Fact]
    public void TheRepositories_ComeBackNewestActivityFirstWithVisibilityLowercased()
    {
        var repos = GitHubService.ParseUserRepos("""
            [{"nameWithOwner":"bob/old","visibility":"PUBLIC","updatedAt":"2026-01-01T00:00:00Z"},
             {"nameWithOwner":"bob/new","visibility":"Private","updatedAt":"2026-08-01T00:00:00Z"}]
            """);

        Assert.NotNull(repos);
        Assert.Equal(["bob/new", "bob/old"], repos.Select(r => r.NameWithOwner));
        Assert.Equal(["private", "public"], repos.Select(r => r.Visibility));
    }

    // ── Cloud discovery ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AFullRead_RecordsThatTheCloudCardsStopShort()
    {
        var discovery = await ScannedAsync(Read(Page(200, 200)));

        Assert.True(discovery.RemoteListStoppedShort);
    }

    [Fact]
    public async Task AReadThatCameBackShort_IsTheWholeAccount()
    {
        var discovery = await ScannedAsync(Read(Page(12, 200)));

        Assert.False(discovery.RemoteListStoppedShort);
    }

    /// <summary>
    /// A failed read establishes nothing about the account, least of all that a cap hid something.
    /// It also adds no cards, which is what keeps a signed-out gh from emptying the Cloud filter.
    /// </summary>
    [Fact]
    public async Task AFailedRead_ClaimsNeitherACapNorAnEmptyAccount()
    {
        var local = new List<ProjectInfo>();
        var discovery = await ScannedAsync(Read(null, "gh: not logged in"), local);

        Assert.False(discovery.RemoteListStoppedShort);
        Assert.Empty(local);
    }

    [Fact]
    public async Task TheCardsTheReadDidReturn_AreStillAdded()
    {
        var local = new List<ProjectInfo>();
        await ScannedAsync(Read(Page(200, 200)), local);

        Assert.Equal(200, local.Count);
        Assert.All(local, p => Assert.True(p.IsRemoteOnly));
    }

    // ── What the surfaces say ───────────────────────────────────────────────────

    /// <summary>
    /// The banner beside a grid that still has cards in it. A Cloud count read from a capped list
    /// is a partial scan presented as a complete one, which is what that banner exists to prevent.
    /// </summary>
    [Fact]
    public void TheCloudCapNotice_NamesTheWindowAndTheWayPastIt()
    {
        Assert.Contains(GitHubService.UserRepoLimit.ToString(), DashboardViewModel.CloudListCappedNotice);
        Assert.Contains("URL", DashboardViewModel.CloudListCappedNotice);
    }

    /// <summary>
    /// The whole point of recording the cap: a reader looking at a Cloud count has to be able to
    /// see that it is not the whole account. Driven through the real refresh path, so the fact
    /// reaching the banner is what is proven rather than the sentence existing.
    /// </summary>
    [Fact]
    public async Task ACappedCloudList_ReachesTheBannerBesideTheGrid()
    {
        var root = TestEnv.NewDir("cloud-cap-banner");
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            // gh pointed at a nonexistent executable: the scan stays local and spawns no network.
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            RefreshIntervalSeconds = 7200,
        });
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, new CappedDiscovery(settings));
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        await dashboard.ForceRefreshCommand.ExecuteAsync(null);

        Assert.True(dashboard.RootIssueVisible);
        Assert.Contains(DashboardViewModel.CloudListCappedNotice, dashboard.RootIssueText);
    }

    /// <summary>A scan whose account read came back full, without one running.</summary>
    private sealed class CappedDiscovery(SettingsService settings)
        : ProjectDiscoveryService(new GitService(), new GitHubService(settings), settings, new ManifestStore())
    {
        public override Task<List<ProjectInfo>> ForceRefreshAllAsync(CancellationToken ct = default)
        {
            RemoteListStoppedShort = true;
            return Task.FromResult(new List<ProjectInfo>());
        }
    }

    private static DashboardViewModel NewDashboard(
        SettingsService settings, ProjectWatcherService watcher, ProjectDiscoveryService discovery) =>
        new(discovery, navigationService: null!, settings, new GitHubService(settings), new GitService(),
            watcher, new RepoBusyRegistry(),
            // No Application in the test host, so the default post target has no dispatcher.
            uiPost: callback => callback());

    [Fact]
    public void TheClonePicker_SaysNothingWhenItListsTheWholeAccount()
        => Assert.Equal("", DashboardViewModel.ClonePickerNotice(Read(Page(12, 200))));

    [Fact]
    public void TheClonePicker_DisclosesAWindowThatCameBackFull()
    {
        var notice = DashboardViewModel.ClonePickerNotice(Read(Page(200, 200)));

        Assert.Contains("200", notice);
        Assert.Contains("owner/repo", notice);
    }

    /// <summary>
    /// An empty picker after a failed read reads as an account that owns nothing, and the reader
    /// would go looking for the repository rather than for the sign-in.
    /// </summary>
    [Fact]
    public void TheClonePicker_NamesAFailedReadRatherThanShowingAnEmptyList()
    {
        var notice = DashboardViewModel.ClonePickerNotice(Read(null, "gh: not logged in"));

        Assert.Contains("couldn't be read", notice);
        Assert.Contains("gh: not logged in", notice);
    }

    [Fact]
    public void TheClonePicker_StillExplainsAFailureThatSaidNothing()
        => Assert.Contains("couldn't be read", DashboardViewModel.ClonePickerNotice(Read(null)));
}
