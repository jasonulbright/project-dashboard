using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// One bulk operation at a time. A clone reads the gate before its repository fetch and
/// its target dialog, both arbitrarily long; claiming it only afterwards lets a Sync All
/// start in that window — two fan-outs writing the same project list, and whichever
/// finishes first clearing a gate the other still relies on, releasing every queued
/// re-scan into a running operation. The read and the claim are therefore one step taken
/// immediately before the work, and only the claim that took the gate may clear it.
///
/// Nothing here reaches the network: every refused operation is refused before it spawns.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardBulkOpGateTests
{
    private const string BusyNotice = "Another operation is in progress — try again in a moment.";

    public DashboardBulkOpGateTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task AClaimedGate_RefusesEverySecondBulkOpAndFreesOnceTheFirstFinishes()
    {
        var root = TestEnv.NewDir("bulk-gate");
        var discovery = NewSettingsAndDiscovery(root, out var settings);
        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        // Parked inside its refresh, this scaffold holds the gate across an await.
        var scaffold = dashboard.ScaffoldProjectAsync(Path.Combine(root, "alpha"), "alpha");
        await WaitUntil(() => discovery.Started == 1);

        // A cloud card clicked now is refused before any git runs.
        var cloudCard = new ProjectInfo
        {
            DirectoryName = "beta",
            DisplayName = "beta",
            FullPath = "",
            IsRemoteOnly = true,
            RemoteSlug = "o/beta",
        };
        await dashboard.CloneRemoteOnlyCommand.ExecuteAsync(cloudCard);
        Assert.Equal(BusyNotice, dashboard.OpStatusText);
        Assert.False(Directory.Exists(Path.Combine(root, "beta")));

        dashboard.OpStatusText = "";
        await dashboard.SyncAllCommand.ExecuteAsync(null);
        Assert.Equal(BusyNotice, dashboard.OpStatusText);

        // A second scaffold is refused whole — half a folder with no repository in it is
        // worse than a refusal.
        var refused = await dashboard.ScaffoldProjectAsync(Path.Combine(root, "gamma"), "gamma");
        Assert.False(refused.Created);
        Assert.Null(refused.Error);
        Assert.Equal(BusyNotice, dashboard.OpStatusText);
        Assert.False(Directory.Exists(Path.Combine(root, "gamma")));

        // None of the refusals cleared a gate they never took: the first op is still
        // holding it and still owns its own release.
        discovery.Release();
        var first = await scaffold;
        Assert.True(first.Created);
        Assert.Null(first.Error);
        Assert.True(Directory.Exists(Path.Combine(root, "alpha")));

        // Released by its own claim, so the next bulk op gets through.
        var next = await dashboard.ScaffoldProjectAsync(Path.Combine(root, "delta"), "delta");
        Assert.True(next.Created);
        Assert.Null(next.Error);
        Assert.True(Directory.Exists(Path.Combine(root, "delta")));
    }

    /// <summary>
    /// The gate serializes work; a report on screen is not work. Held across the results dialog
    /// it stalls every queued re-scan and every other bulk op for as long as the reader leaves
    /// the box open — the exact thing the gate's own contract forbids of a modal.
    /// </summary>
    [Fact]
    public async Task TheSyncAllReport_IsShownAfterTheGateIsReleased_SoAQueuedRescanDrainsBehindIt()
    {
        var first = TestEnv.NewDir("sync-report-first");
        var second = TestEnv.NewDir("sync-report-second");
        var settings = NewSettings(first);

        // One clean repository with a remote that cannot be reached: the fetch fails fast with
        // no network, which is what puts a line in the report the dialog exists to show.
        using var repo = await TempRepo.CreateWithCommitAsync("sync-report-repo");
        var moved = Path.Combine(first, "synced");
        CopyTree(repo.Path, moved);
        await Git.RunAsync(moved, "remote", "add", "origin", Path.Combine(first, "no-such-origin.git"));

        using var watcher = new ProjectWatcherService();
        var dashboard = new ReportingDashboard(settings, watcher, second);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        Assert.Contains(dashboard.Projects, p => p.FullPath == moved);

        await dashboard.SyncAllCommand.ExecuteAsync(null);

        Assert.Equal(1, dashboard.Reports);
        // Drained while the report stood, not after it closed.
        Assert.Equal("", dashboard.RescanStatusDuringReport);
        Assert.Equal(second, dashboard.RootDuringReport);
        Assert.NotEqual(DashboardRescan.QueuedStatus, dashboard.RescanStatusDuringReport);
    }

    /// <summary>
    /// The candidate filter reads the busy registry once, before any repository task starts, so a
    /// repository claimed after that read would otherwise be fetched into whatever claimed it.
    /// Two cards on one path make the race exact: they are both candidates, exactly one wins the
    /// lease, and the loser has to say so instead of running.
    /// </summary>
    [Fact]
    public async Task SyncAll_TakesTheRepositoryLeasePerRepo_AndReportsTheOneItCouldNotClaim()
    {
        var root = TestEnv.NewDir("sync-lease");
        var settings = NewSettings(root);
        using var repo = await TempRepo.CreateWithCommitAsync("sync-lease-repo");
        var moved = Path.Combine(root, "synced");
        CopyTree(repo.Path, moved);
        await Git.RunAsync(moved, "remote", "add", "origin", Path.Combine(root, "no-such-origin.git"));

        var registry = new RepoBusyRegistry();
        var git = new BlockingFetchGitService();
        using var watcher = new ProjectWatcherService();
        var dashboard = new LeaseProbeDashboard(settings, watcher, registry, git);

        dashboard.Projects =
        [
            NewCard("alpha", moved),
            NewCard("beta", moved),
        ];

        var sync = dashboard.SyncAllCommand.ExecuteAsync(null);
        // The loser records its skip and increments the counter without running any git, so the
        // winner can stay parked in its fetch until that has happened.
        await WaitUntil(() => dashboard.OpProgressText == "1/2");
        Assert.True(registry.IsBusy(moved), "the repository must be leased while its fetch runs");
        git.ReleaseFetch();
        await sync;

        Assert.False(registry.IsBusy(moved), "the lease must be released before the aggregate refresh");
        Assert.Contains("skipped — busy with another operation.", dashboard.LastReport, StringComparison.Ordinal);
        // Exactly one card ran: the other never reached a git call.
        Assert.Equal(1, git.FetchCount);
    }

    private static ProjectInfo NewCard(string name, string path) => new()
    {
        DirectoryName = name,
        DisplayName = name,
        FullPath = path,
        GitStatus = new GitStatus { RemoteUrl = "https://example.invalid/o/r.git" },
    };

    /// <summary>Parks every fetch until released, so the window in which one repository holds its lease is under the test's control.</summary>
    private sealed class BlockingFetchGitService : GitService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fetches;

        public int FetchCount => Volatile.Read(ref _fetches);

        public void ReleaseFetch() => _gate.TrySetResult();

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            if (!argv.Contains("fetch"))
                return await base.RunAsync(repoPath, argv, environment, ct, timeout);
            Interlocked.Increment(ref _fetches);
            await _gate.Task;
            return new ProcessResult(-1, "", "no such remote", TimedOut: false);
        }
    }

    /// <summary>Captures the results body instead of putting a message box on a test host that has no Application.</summary>
    private sealed class LeaseProbeDashboard : DashboardViewModel
    {
        public LeaseProbeDashboard(
            SettingsService settings, ProjectWatcherService watcher, RepoBusyRegistry registry, GitService git)
            : base(new ProjectDiscoveryService(git, new GitHubService(settings), settings, new ManifestStore()),
                navigationService: null!, settings, new GitHubService(settings), git, watcher,
                registry, uiPost: callback => callback())
        {
        }

        public string LastReport { get; private set; } = "";

        internal override Task ShowSyncAllResultsAsync(string body)
        {
            LastReport = body;
            return Task.CompletedTask;
        }
    }

    /// <summary>Queues a re-scan from inside the results dialog and records what it did there.</summary>
    private sealed class ReportingDashboard : DashboardViewModel
    {
        private readonly SettingsService _settings;
        private readonly string _newRoot;

        public ReportingDashboard(SettingsService settings, ProjectWatcherService watcher, string newRoot)
            : base(new ProjectDiscoveryService(new GitService(), new GitHubService(settings), settings, new ManifestStore()),
                navigationService: null!, settings, new GitHubService(settings), new GitService(), watcher,
                new RepoBusyRegistry(), uiPost: callback => callback())
        {
            _settings = settings;
            _newRoot = newRoot;
        }

        public int Reports { get; private set; }
        public string RescanStatusDuringReport { get; private set; } = "never shown";
        public string RootDuringReport { get; private set; } = "";

        internal override async Task ShowSyncAllResultsAsync(string body)
        {
            Reports++;
            var moved = _settings.Load();
            moved.ProjectsRootPath = _newRoot;
            _settings.Save(moved);
            await PendingRescan;
            RescanStatusDuringReport = RescanStatus;
            RootDuringReport = ConfiguredRootPath;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }

    private static SettingsService NewSettings(string root)
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            RefreshIntervalSeconds = 7200,
        });
        return settings;
    }

    private static GatedDiscovery NewSettingsAndDiscovery(string root, out SettingsService settings)
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
        return new GatedDiscovery(settings, new GitHubService(settings));
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
            // and would drop the drain the gate's release starts.
            uiPost: callback => callback());
    }

    /// <summary>A discovery whose force refresh parks until released.</summary>
    private sealed class GatedDiscovery(SettingsService settings, GitHubService gitHub)
        : ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore())
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void Release() => _gate.TrySetResult();

        public override async Task<List<ProjectInfo>> ForceRefreshAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _started);
            await _gate.Task;
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
