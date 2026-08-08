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
        Assert.Null(await dashboard.ScaffoldProjectAsync(Path.Combine(root, "gamma"), "gamma"));
        Assert.Equal(BusyNotice, dashboard.OpStatusText);
        Assert.False(Directory.Exists(Path.Combine(root, "gamma")));

        // None of the refusals cleared a gate they never took: the first op is still
        // holding it and still owns its own release.
        discovery.Release();
        Assert.Null(await scaffold);
        Assert.True(Directory.Exists(Path.Combine(root, "alpha")));

        // Released by its own claim, so the next bulk op gets through.
        Assert.Null(await dashboard.ScaffoldProjectAsync(Path.Combine(root, "delta"), "delta"));
        Assert.True(Directory.Exists(Path.Combine(root, "delta")));
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
