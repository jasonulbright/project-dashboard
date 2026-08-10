using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The hidden-project badge. Deriving its count means reading the settings file and
/// probing every excluded directory twice on disk, and it is bound twice on the summary
/// bar, which re-reads on every notification — and the file watcher notifies on every
/// save in every repository. Done in the property getter that is disk work on the UI
/// thread at typing speed, so the count is computed off-thread and served from a field.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardHiddenCountTests
{
    public DashboardHiddenCountTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task TheCount_IsServedFromTheLastRecount_NotProbedOnEveryRead()
    {
        var root = TestEnv.NewDir("hidden-count");
        await InitRepoAsync(Path.Combine(root, "alpha"));
        await InitRepoAsync(Path.Combine(root, "beta"));
        // An excluded name with nothing behind it is not a hidden project.
        Directory.CreateDirectory(Path.Combine(root, "gamma"));

        var settings = new SettingsService();
        settings.Save(BaseSettings(root, "alpha", "beta", "gamma"));

        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        await WaitUntil(() => dashboard.HiddenCount == 2);

        // Disk moves under it with nothing notifying anyone. A getter that probed per read
        // would report the change immediately — that reporting IS the per-read disk work.
        Directory.Delete(Path.Combine(root, "beta", ".git"), recursive: true);
        Assert.Equal(2, dashboard.HiddenCount);
        Assert.Equal(2, dashboard.HiddenCount);

        // A settings write is one of the two things that recounts it.
        var unhidden = settings.Load();
        unhidden.ProjectRoots[0].ExcludedDirectories = ["alpha"];
        settings.Save(unhidden);

        await WaitUntil(() => dashboard.HiddenCount == 1);
    }

    /// <summary>
    /// A bare exclusion name hides that name at every depth, so the hidden set has to be found
    /// the same way the scan finds repositories. Probing only the root's own children leaves a
    /// nested repository absent from the grid AND absent from the Hidden view, which is not
    /// hiding it — it is losing it.
    /// </summary>
    [Fact]
    public async Task ARepositoryHiddenBelowTheTopLevel_IsStillListedAsHidden()
    {
        var root = TestEnv.NewDir("hidden-nested");
        await InitRepoAsync(Path.Combine(root, "group", "docs"));
        await InitRepoAsync(Path.Combine(root, "group", "keeper"));

        var settings = BaseSettings(root, "docs");
        settings.ProjectRoots = [new ProjectRoot { Path = root, ExcludedDirectories = ["docs"], MaxDepth = 3 }];

        var hidden = RepositoryWalk.Run(settings.ProjectRoots[0], CancellationToken.None).Excluded;

        Assert.Equal(RepoPaths.Normalize(Path.Combine(root, "group", "docs")), Assert.Single(hidden));
    }

    /// <summary>The count itself, without a view model in the way.</summary>
    [Fact]
    public async Task OnlyExcludedDirectoriesThatAreRepositories_AreCounted()
    {
        var root = TestEnv.NewDir("hidden-count-pure");
        await InitRepoAsync(Path.Combine(root, "alpha"));
        await InitRepoAsync(Path.Combine(root, "beta"));
        Directory.CreateDirectory(Path.Combine(root, "plain"));

        Assert.Equal(2, HiddenCountFor(BaseSettings(root, "alpha", "beta", "plain", "never-created")));
        Assert.Equal(0, HiddenCountFor(BaseSettings(root)));
    }

    /// <summary>The hidden set the scan's own walk produces, without a view model in the way.</summary>
    private static int HiddenCountFor(AppSettings settings) =>
        ProjectRootSettings.Scannable(settings)
            .Sum(root => RepositoryWalk.Run(root, CancellationToken.None).Excluded.Count);

    /// <summary>Counts the directory walks a scan runs, so the watcher path can be shown to run none.</summary>
    private sealed class CountingDiscovery(SettingsService settings, GitHubService gitHub)
        : ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore())
    {
        private int _walks;

        public int Walks => Volatile.Read(ref _walks);

        protected override RootWalkResult WalkRoot(ProjectRoot root, CancellationToken ct)
        {
            Interlocked.Increment(ref _walks);
            return base.WalkRoot(root, ct);
        }
    }

    /// <summary>
    /// The badge is read by the summary bar on every notification, and the file watcher notifies
    /// on every save in every repository. Deriving the hidden set there is a directory walk per
    /// event, stacked and uncancelled during exactly the busy periods the debounce exists for —
    /// so the watcher path runs none, and a rescan is what refreshes the count.
    /// </summary>
    [Fact]
    public async Task WatcherSignals_RunNoDirectoryWalks_WhileARescanStillRefreshesTheCount()
    {
        var root = TestEnv.NewDir("hidden-walk-cost");
        await InitRepoAsync(Path.Combine(root, "alpha"));
        await InitRepoAsync(Path.Combine(root, "beta"));

        var settings = new SettingsService();
        settings.Save(BaseSettings(root, "alpha"));

        var gitHub = new GitHubService(settings);
        var discovery = new CountingDiscovery(settings, gitHub);
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        await WaitUntil(() => dashboard.HiddenCount == 1);

        var afterScan = discovery.Walks;
        Assert.True(afterScan > 0, "the scan itself must walk");

        // Ten signals naming the visible repository, each awaited to completion: every one drives
        // a card refresh AND the summary notification, which is where the walk used to be.
        for (var i = 0; i < 10; i++)
        {
            dashboard.OnRepoDirsChanged([Path.Combine(root, "beta")]);
            await dashboard.WatcherRefresh;
        }

        Assert.Equal(afterScan, discovery.Walks);
        Assert.Equal(1, dashboard.HiddenCount);

        // A rescan is what moves it: hiding the second repository too.
        var hideBoth = settings.Load();
        hideBoth.ProjectRoots[0].ExcludedDirectories = ["alpha", "beta"];
        settings.Save(hideBoth);

        await WaitUntil(() => dashboard.HiddenCount == 2);
        Assert.True(discovery.Walks > afterScan, "the rescan must walk again");
    }

    private static AppSettings BaseSettings(string root, params string[] excluded) => new()
    {
        ProjectsRootPath = root,
        ExcludedDirectories = excluded,
        // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
        GhPath = Path.Combine(root, "no-such-gh.exe"),
        EnableGitHubDiscovery = false,
        RefreshIntervalSeconds = 7200,
    };

    private static async Task InitRepoAsync(string path)
    {
        Directory.CreateDirectory(path);
        await Git.RunAsync(path, "init", "-b", "main");
    }

    private static DashboardViewModel NewDashboard(
        SettingsService settings, ProjectWatcherService watcher, ProjectDiscoveryService? discovery = null)
    {
        var gitHub = new GitHubService(settings);
        return new DashboardViewModel(
            discovery ?? new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            watcher,
            new RepoBusyRegistry(),
            // No Application in the test host, so the default post target has no dispatcher
            // and would drop the recount's publication.
            uiPost: callback => callback());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the awaited condition never became true");
            await Task.Delay(15);
        }
    }
}
