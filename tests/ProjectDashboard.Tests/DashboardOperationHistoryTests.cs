using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The dashboard runs the same fetch, pull, and push the detail page does — from a card, and in
/// bulk from Sync All. A repository's ledger must not depend on which surface the reader used, or
/// the history reads as though the app never touched repositories it did.
///
/// One record per user action per repository: a card Pull fetches first, and Sync All fetches every
/// candidate, but the reader pressed one button and each repository is one unit of that work. A
/// repository Sync All's candidate filter excluded is not recorded at all — nothing was attempted
/// against it.
///
/// Discovery and the settings file live under AppPaths, so these join the serialized collection.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardOperationHistoryTests
{
    private readonly ITestOutputHelper _output;

    public DashboardOperationHistoryTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    [Fact]
    public async Task ACardFetch_IsRecordedOnceAgainstThatRepository()
    {
        var root = TestEnv.NewDir("card-fetch");
        var repo = await CloneIntoAsync(root, "worker");
        var history = NewHistory();
        var dashboard = await NewDashboardAsync(root, history, new RepoBusyRegistry());

        await dashboard.FetchProjectCommand.ExecuteAsync(Card(dashboard, repo));

        var record = Assert.Single(history.Tail(repo).Records);
        Assert.Equal(OperationCategory.Remote, record.Category);
        Assert.Equal("Fetch", record.Label);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
        _output.WriteLine($"card fetch recorded: {record.Label} · {record.Outcome}");
    }

    /// <summary>
    /// A card Pull fetches first. Two rows for one click would make the ledger describe this app's
    /// internals rather than the operation the reader asked for.
    /// </summary>
    [Fact]
    public async Task ACardPull_IsRecordedOnceNotOncePerGitCall()
    {
        var root = TestEnv.NewDir("card-pull");
        var repo = await CloneIntoAsync(root, "worker");
        var history = NewHistory();
        var dashboard = await NewDashboardAsync(root, history, new RepoBusyRegistry());

        await dashboard.PullProjectCommand.ExecuteAsync(Card(dashboard, repo));

        var record = Assert.Single(history.Tail(repo).Records);
        Assert.Equal("Pull", record.Label);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
        Assert.Contains("Fetched", record.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A card action refused before it runs is exactly the case the ledger exists to explain: the
    /// button did nothing, and the status line reporting it is gone by the next operation.
    /// </summary>
    [Fact]
    public async Task ACardActionRefusedWhileTheRepositoryIsBusy_IsRecordedAsRefused()
    {
        var root = TestEnv.NewDir("card-refused");
        var repo = await CloneIntoAsync(root, "worker");
        var history = NewHistory();
        var busy = new RepoBusyRegistry();
        var dashboard = await NewDashboardAsync(root, history, busy);

        Assert.True(busy.TryAcquire(repo, out var lease));
        using (lease) await dashboard.FetchProjectCommand.ExecuteAsync(Card(dashboard, repo));

        var record = Assert.Single(history.Tail(repo).Records);
        Assert.Equal(OperationOutcome.Refused, record.Outcome);
        Assert.Equal("Fetch", record.Label);
        Assert.NotEqual("", record.Detail);
    }

    [Fact]
    public async Task SyncAll_RecordsOnceAgainstEveryRepositoryItReached()
    {
        var root = TestEnv.NewDir("sync-all");
        var first = await CloneIntoAsync(root, "alpha");
        var second = await CloneIntoAsync(root, "beta");
        var history = NewHistory();
        var dashboard = await NewDashboardAsync(root, history, new RepoBusyRegistry());
        Assert.Equal(2, dashboard.Projects.Count);

        await dashboard.SyncAllCommand.ExecuteAsync(null);

        foreach (var repo in new[] { first, second })
        {
            var record = Assert.Single(history.Tail(repo).Records);
            Assert.Equal(OperationCategory.Remote, record.Category);
            Assert.Equal("Sync all", record.Label);
            Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
            Assert.Contains("Fetched", record.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A repository the candidate filter excluded was never attempted, so it gets no record. A
    /// refusal row per repository per sync would fill every ledger with work nobody asked of it.
    /// </summary>
    [Fact]
    public async Task SyncAll_RecordsNothingForARepositoryItNeverAttempted()
    {
        var root = TestEnv.NewDir("sync-all-dirty");
        var repo = await CloneIntoAsync(root, "worker");
        // A dirty tree keeps it out of the candidate set, which is read before any task starts.
        await File.WriteAllTextAsync(Path.Combine(repo, "uncommitted.txt"), "work in progress\n");
        var history = NewHistory();
        var dashboard = await NewDashboardAsync(root, history, new RepoBusyRegistry());

        await dashboard.SyncAllCommand.ExecuteAsync(null);

        Assert.Empty(history.Tail(repo).Records);
        Assert.Contains("no clean repos", dashboard.OpStatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A card action against a repository this app is not recording for still runs. The writer is
    /// best effort in both directions: it never fails an operation, and it never suppresses one.
    /// </summary>
    [Fact]
    public async Task ACardFetchWithAnUnwritableLedger_StillRuns()
    {
        var root = TestEnv.NewDir("card-blocked");
        var repo = await CloneIntoAsync(root, "worker");
        var ledgerRoot = TestEnv.NewDir("card-blocked-ledger");
        // A FILE where the per-repo directory belongs, so every append fails.
        await File.WriteAllTextAsync(Path.Combine(ledgerRoot, RepoKey.For(repo)), "not a directory");
        var history = new OperationHistory(ledgerRoot);
        var dashboard = await NewDashboardAsync(root, history, new RepoBusyRegistry());

        await dashboard.FetchProjectCommand.ExecuteAsync(Card(dashboard, repo));

        Assert.Contains("fetched", dashboard.OpStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(history.Tail(repo).Records);
    }

    // ── Fixture plumbing ────────────────────────────────────────────────────

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("dashboard-ledger"));

    private static ProjectInfo Card(DashboardViewModel dashboard, string repoPath) =>
        dashboard.Projects.Single(p =>
            p.FullPath.Length > 0
            && string.Equals(RepoKey.For(p.FullPath), RepoKey.For(repoPath), StringComparison.Ordinal));

    /// <summary>
    /// A clone inside the scan root, with a file-protocol origin so a fetch and a pull are real git
    /// operations that reach a remote without a network.
    /// </summary>
    private static async Task<string> CloneIntoAsync(string root, string name)
    {
        var origin = Path.Combine(TestEnv.Root, $"{name}-{Guid.NewGuid():N}"[..24] + "-origin.git");
        var seed = origin + "-seed";
        Directory.CreateDirectory(origin);
        Directory.CreateDirectory(seed);
        await Git.RunAsync(origin, "init", "--bare", "-b", "main");

        await Git.RunAsync(seed, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "one\n");
        await Git.RunAsync(seed, "add", "-A");
        await Git.RunAsync(seed, "commit", "-m", "initial");
        await Git.RunAsync(seed, "push", origin.Replace('\\', '/'), "main");
        TestEnv.TryDeleteTree(seed);

        var clone = Path.Combine(root, name);
        await Git.RunAsync(root, "clone", origin.Replace('\\', '/'), name);
        return clone;
    }

    private static async Task<DashboardViewModel> NewDashboardAsync(
        string root, OperationHistory history, RepoBusyRegistry busy)
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
            RefreshIntervalSeconds = 7200,
        });

        var gitHub = new GitHubService(settings);
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            new ProjectWatcherService(),
            busy,
            // No Application in the test host, so the default post target has no dispatcher
            // and would drop every callback the drain runs through.
            uiPost: callback => callback(),
            history: history);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        return dashboard;
    }
}
