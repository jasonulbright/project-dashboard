using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A refresh request answers with a scan that started after it. Single-flight alone
/// hands a mid-flight requester the results of a scan that read the disk before the
/// request, so the change they pressed refresh to see is missing from the answer; the
/// re-run latch turns that into one more pass. The watcher's full-refresh signal gets
/// the same treatment from the other side: it names no repositories and the watcher
/// has already cleared its pending set, so a refusal that dropped it would lose the
/// overflow it reports outright.
///
/// Nothing here reaches the network or a real repository: the fan-out is parked at a
/// substituted discovery.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardRefreshCoalescingTests
{
    public DashboardRefreshCoalescingTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task ARefreshRequestedMidScan_RunsAgainInsteadOfCoalescingOntoTheScanItOvertook()
    {
        var root = TestEnv.NewDir("refresh-latch");
        var discovery = NewSettingsAndDiscovery(root, out var settings);
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var first = dashboard.ForceRefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => discovery.Started == 1);

        // F5 while that scan is parked: its results predate this request.
        var second = dashboard.ForceRefreshCommand.ExecuteAsync(null);

        discovery.ReleaseOne();
        await WaitUntil(() => discovery.Started == 2);
        discovery.ReleaseOne();

        await first;
        await second;
        Assert.Equal(2, discovery.Started);

        // The latch arms one re-run, not a standing one: an uncontested refresh is a
        // single scan.
        var third = dashboard.ForceRefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => discovery.Started == 3);
        discovery.ReleaseOne();
        await third;
        Assert.Equal(3, discovery.Started);
    }

    [Fact]
    public async Task SeveralRefreshesDuringOneScan_CollapseIntoASingleReRun()
    {
        var root = TestEnv.NewDir("refresh-latch-burst");
        var discovery = NewSettingsAndDiscovery(root, out var settings);
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var first = dashboard.ForceRefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => discovery.Started == 1);

        var queued = new[]
        {
            dashboard.ForceRefreshCommand.ExecuteAsync(null),
            dashboard.ForceRefreshCommand.ExecuteAsync(null),
            dashboard.ForceRefreshCommand.ExecuteAsync(null),
        };

        discovery.ReleaseOne();
        await WaitUntil(() => discovery.Started == 2);
        discovery.ReleaseOne();

        await first;
        await Task.WhenAll(queued);
        Assert.Equal(2, discovery.Started);
    }

    [Fact]
    public async Task TheWatchersFullRefreshSignal_QueuesWhenAScanRefusesItRatherThanBeingDropped()
    {
        var root = TestEnv.NewDir("watcher-requeue");
        var discovery = NewSettingsAndDiscovery(root, out var settings);
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var scan = dashboard.ForceRefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => discovery.Started == 1);

        // Buffer overflow: the watcher lost events and can name no repositories.
        dashboard.OnRepoDirsChanged([]);

        Assert.True(dashboard.RescanQueued);
        Assert.Equal(DashboardRescan.QueuedStatus, dashboard.RescanStatus);

        discovery.ReleaseOne();
        await WaitUntil(() => discovery.Started == 2);
        discovery.ReleaseOne();
        await scan;

        await WaitUntil(() => !dashboard.RescanQueued && dashboard.RescanStatus.Length == 0);
        Assert.Equal(2, discovery.Started);
    }

    [Fact]
    public async Task APerRepoWatcherSignalRefusedMidScan_QueuesNoFullRescan()
    {
        var root = TestEnv.NewDir("watcher-per-repo");
        var discovery = NewSettingsAndDiscovery(root, out var settings);
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var scan = dashboard.ForceRefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => discovery.Started == 1);

        // A named repository is replayable from disk and the running scan reads it; a
        // full re-scan per save would fan git out over every repository instead.
        dashboard.OnRepoDirsChanged([Path.Combine(root, "alpha")]);

        Assert.False(dashboard.RescanQueued);

        discovery.ReleaseOne();
        await scan;
        Assert.Equal(1, discovery.Started);
    }

    private static ParkedDiscovery NewSettingsAndDiscovery(string root, out SettingsService settings)
    {
        settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            RefreshIntervalSeconds = 7200,
        });
        return new ParkedDiscovery(settings, new GitHubService(settings));
    }

    private static DashboardViewModel NewDashboard(
        SettingsService settings, ProjectWatcherService watcher, ProjectDiscoveryService discovery)
    {
        var gitHub = new GitHubService(settings);
        return new DashboardViewModel(
            discovery,
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            watcher,
            new RepoBusyRegistry(),
            // No Application in the test host, so the default post target has no dispatcher
            // and would drop every callback the drain and the watcher handler run through.
            uiPost: callback => callback());
    }

    /// <summary>A discovery whose every force refresh parks until it is released.</summary>
    private sealed class ParkedDiscovery(SettingsService settings, GitHubService gitHub)
        : ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore())
    {
        private readonly SemaphoreSlim _releases = new(0);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void ReleaseOne() => _releases.Release();

        public override async Task<List<ProjectInfo>> ForceRefreshAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _started);
            await _releases.WaitAsync(ct);
            return await base.ForceRefreshAllAsync(ct);
        }
    }

    /// <summary>Polls until the condition holds; a scan starts on a continuation, not inline.</summary>
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
