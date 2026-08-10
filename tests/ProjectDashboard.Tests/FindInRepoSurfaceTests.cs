using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Find in one repository, and the portfolio fan-out it shares a service with. The two differ in
/// breadth and in nothing else: the same scopes, the same bounds, and the same rule that a scope
/// is never carried from one open to the next.
/// </summary>
[Collection("app-data-sandbox")]
public class FindInRepoSurfaceTests
{
    public FindInRepoSurfaceTests() => TestSandbox.ResetDataDir();

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    /// <summary>A repository with one tracked, one untracked and one ignored file, each with its own term.</summary>
    private static async Task<TempRepo> ThreeKindsRepoAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile(".gitignore", "*.hidden\n");
        repo.WriteFile("src/alpha.cs", "needlealpha lives here\n");
        await repo.CommitAllAsync("fixture");
        repo.WriteFile("beta.txt", "needlebeta lives here\n");
        repo.WriteFile("gamma.hidden", "needlegamma lives here\n");
        return repo;
    }

    private static async Task<ProjectDetailViewModel> OpenOnAsync(TempRepo repo)
    {
        var vm = new UnhurriedViewModel();
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        return vm;
    }

    /// <summary>
    /// A budget no fixture here can exhaust. What these tests assert is which files a scope read
    /// and what the rows say about them; the widest scope's shipped budget is four git spawns wide,
    /// and on a loaded machine four spawns alone outlast it — a result asserted against that clock
    /// measures the machine.
    /// </summary>
    private static readonly TimeSpan Unhurried = TimeSpan.FromMinutes(2);

    private class UnhurriedViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        internal override RepoSearchService NewSearchService(GitService git, RepoBusyRegistry busy) =>
            new(git, busy, Unhurried, Unhurried);
    }

    [Fact]
    public async Task ThePaneFindsAContentMatchInThisRepository()
    {
        using var repo = await ThreeKindsRepoAsync("find-content");
        var vm = await OpenOnAsync(repo);

        vm.OpenFindCommand.Execute(null);
        vm.FindTerm = "needlealpha";
        await vm.RunFindCommand.ExecuteAsync(null);

        var hit = Assert.Single(vm.FindHits);
        Assert.Equal("src/alpha.cs", hit.FilePath);
        Assert.Equal(SearchFileScope.Tracked, hit.FileScope);
        Assert.Equal(repo.Path, hit.RepoPath);
        Assert.False(vm.FindEmpty);
    }

    /// <summary>
    /// The pane's breadth is one repository. Every other repository the app knows about is out of
    /// its reach, and the line it prints says so rather than counting repositories.
    /// </summary>
    [Fact]
    public async Task ThePaneSearchesThisRepositoryAndNoOther()
    {
        using var mine = await ThreeKindsRepoAsync("find-mine");
        using var other = await ThreeKindsRepoAsync("find-other");
        var vm = await OpenOnAsync(mine);

        vm.OpenFindCommand.Execute(null);
        vm.FindTerm = "needlealpha";
        await vm.RunFindCommand.ExecuteAsync(null);

        Assert.All(vm.FindHits, h => Assert.Equal(mine.Path, h.RepoPath));
        Assert.DoesNotContain(vm.FindHits, h => h.RepoPath == other.Path);
        Assert.StartsWith("This repository searched", vm.FindStatusText);
    }

    /// <summary>
    /// Widening the scope re-reads the working tree. Relabelling the rows already on screen would
    /// leave a tracked-only answer sitting under a heading that claims it read ignored files too.
    /// </summary>
    [Fact]
    public async Task WideningTheScope_ReadsTheFilesTheNarrowerOneDidNot()
    {
        using var repo = await ThreeKindsRepoAsync("find-widen");
        var vm = await OpenOnAsync(repo);

        vm.OpenFindCommand.Execute(null);
        vm.FindTerm = "needlegamma";
        await vm.RunFindCommand.ExecuteAsync(null);
        Assert.Empty(vm.FindHits);
        Assert.True(vm.FindEmpty);

        await vm.SetFindScopeCommand.ExecuteAsync(nameof(SearchContentScope.WithUntracked));
        Assert.Empty(vm.FindHits);

        await vm.SetFindScopeCommand.ExecuteAsync(nameof(SearchContentScope.Everything));

        var hit = Assert.Single(vm.FindHits);
        Assert.Equal("gamma.hidden", hit.FilePath);
        Assert.Equal(SearchFileScope.Ignored, hit.FileScope);
        Assert.Equal("ignored", hit.ScopeLabel);
    }

    /// <summary>The widest scope states its cost while it is in force, and takes the notice back down.</summary>
    [Fact]
    public async Task OnlyTheWidestScope_StatesItsCost()
    {
        using var repo = await ThreeKindsRepoAsync("find-notice");
        var vm = await OpenOnAsync(repo);

        vm.OpenFindCommand.Execute(null);
        Assert.Equal("", vm.FindScopeNoticeText);

        await vm.SetFindScopeCommand.ExecuteAsync(nameof(SearchContentScope.Everything));
        Assert.Equal(SearchScopeCopy.EverythingNotice, vm.FindScopeNoticeText);

        await vm.SetFindScopeCommand.ExecuteAsync(nameof(SearchContentScope.Tracked));
        Assert.Equal("", vm.FindScopeNoticeText);
    }

    /// <summary>
    /// The rule the widest scope's cost rests on, at the surface that ships it: a pane reopened
    /// after a wide search is back on tracked content, and the term it held is gone with it.
    /// </summary>
    [Fact]
    public async Task ReopeningThePane_IsBackOnTrackedContent()
    {
        using var repo = await ThreeKindsRepoAsync("find-reset");
        var vm = await OpenOnAsync(repo);

        vm.OpenFindCommand.Execute(null);
        await vm.SetFindScopeCommand.ExecuteAsync(nameof(SearchContentScope.Everything));
        vm.FindTerm = "needlealpha";
        await vm.RunFindCommand.ExecuteAsync(null);
        Assert.True(vm.FindScopeIsEverything);

        vm.CloseFindCommand.Execute(null);
        vm.OpenFindCommand.Execute(null);

        Assert.True(vm.FindScopeIsTracked);
        Assert.False(vm.FindScopeIsEverything);
        Assert.Equal("", vm.FindTerm);
        Assert.Empty(vm.FindHits);
    }

    /// <summary>A term shorter than the floor spawns no git and says why rather than reporting nothing found.</summary>
    [Fact]
    public async Task ATermShorterThanTheFloor_SaysSoRatherThanReportingNoMatches()
    {
        using var repo = await ThreeKindsRepoAsync("find-short");
        var vm = await OpenOnAsync(repo);

        vm.OpenFindCommand.Execute(null);
        vm.FindTerm = "n";
        await vm.RunFindCommand.ExecuteAsync(null);

        Assert.Empty(vm.FindHits);
        Assert.False(vm.FindEmpty);
        Assert.Contains("at least", vm.FindStatusText);
    }

    /// <summary>
    /// The pane's rows describe the repository it was opened on. Left standing across a project
    /// switch they would name files of a repository the page no longer shows.
    /// </summary>
    [Fact]
    public async Task AProjectSwitch_ClosesThePane()
    {
        using var first = await ThreeKindsRepoAsync("find-switch-a");
        using var second = await ThreeKindsRepoAsync("find-switch-b");
        var vm = await OpenOnAsync(first);

        vm.OpenFindCommand.Execute(null);
        vm.FindTerm = "needlealpha";
        await vm.RunFindCommand.ExecuteAsync(null);
        Assert.True(vm.FindVisible);

        await vm.SetProjectAsync(ProjectFor(second));

        Assert.False(vm.FindVisible);
        Assert.Empty(vm.FindHits);
    }

    /// <summary>
    /// The pane refuses to open behind another full-page pane, which covers it: a scrim stops the
    /// mouse and no keystroke, and Ctrl+F is a keystroke.
    /// </summary>
    [Fact]
    public async Task ThePaneRefusesToOpenUnderAnotherPane()
    {
        using var repo = await ThreeKindsRepoAsync("find-under");
        var vm = await OpenOnAsync(repo);

        await vm.OpenFileHistoryCommand.ExecuteAsync("src/alpha.cs");
        Assert.True(vm.FileHistoryVisible);

        vm.OpenFindCommand.Execute(null);

        Assert.False(vm.FindVisible);
    }

    /// <summary>
    /// One row's jump has to work for every file the widest scope can return. An ignored file has
    /// no history and an untracked one has no blame, so the jump is the file itself in Explorer,
    /// at its absolute path.
    /// </summary>
    [Fact]
    public async Task ARowsJumpNamesTheFileOnDisk_ForAnIgnoredFileToo()
    {
        using var repo = await ThreeKindsRepoAsync("find-reveal");
        var vm = new RecordingRevealViewModel();
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        vm.OpenFindCommand.Execute(null);
        await vm.SetFindScopeCommand.ExecuteAsync(nameof(SearchContentScope.Everything));
        vm.FindTerm = "needlegamma";
        await vm.RunFindCommand.ExecuteAsync(null);

        vm.RevealFindHitCommand.Execute(Assert.Single(vm.FindHits));

        Assert.Equal(Path.Combine(repo.Path, "gamma.hidden"), vm.Revealed);
        Assert.Contains("gamma.hidden", vm.FindStatusText);
    }

    /// <summary>
    /// Two read-only panes added on separate lanes, each covering the whole page. The single
    /// visibility union is what keeps one from opening over the other, and it is a line both lanes
    /// appended to — a resolution that kept one flag and dropped the other leaves the dropped
    /// pane's scrim over a live surface, which stops a mouse and no keystroke. Asserted in both
    /// directions, because a union missing either flag fails only one of them.
    /// </summary>
    [Fact]
    public async Task TheFindPaneAndTheRunLogPane_EachRefuseToOpenOverTheOther()
    {
        using var repo = await ThreeKindsRepoAsync("find-vs-log");
        var vm = new LogPaneViewModel();
        await vm.SetProjectAsync(RemoteProjectFor(repo));
        await vm.IssuesPageLoad;
        vm.SelectedWorkflowRun = new WorkflowRun { Id = 42, Name = "build", Status = "completed" };

        await vm.OpenWorkflowLogCommand.ExecuteAsync(null);
        await vm.WorkflowLogLoad;
        Assert.True(vm.WorkflowLogVisible);
        Assert.False(vm.SafetyOverlayHidden);

        vm.OpenFindCommand.Execute(null);
        Assert.False(vm.FindVisible);

        vm.CloseWorkflowLogCommand.Execute(null);
        Assert.True(vm.SafetyOverlayHidden);

        vm.OpenFindCommand.Execute(null);
        Assert.True(vm.FindVisible);
        Assert.False(vm.SafetyOverlayHidden);

        await vm.OpenWorkflowLogCommand.ExecuteAsync(null);

        Assert.False(vm.WorkflowLogVisible);
        Assert.True(vm.FindVisible);
    }

    private static ProjectInfo RemoteProjectFor(TempRepo repo)
    {
        var project = ProjectFor(repo);
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    /// <summary>Answers the run-log read and the list reads, so the pane opens without gh.</summary>
    private sealed class LogPaneViewModel : UnhurriedViewModel
    {
        internal override Task<WorkflowRunLog?> FetchWorkflowRunLogAsync(string slug, long runId)
            => Task.FromResult<WorkflowRunLog?>(new WorkflowRunLog("one\ntwo\n", Truncated: false, Cap: 2_000_000));

        internal override Task<GitHubService.ListRead<GitHubService.IssuePage>> FetchIssuePageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.IssuePage>(
                new GitHubService.IssuePage([], false, query.Limit), ""));

        internal override Task<GitHubService.ListRead<GitHubService.PullRequestPage>> FetchPullRequestPageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.PullRequestPage>(
                new GitHubService.PullRequestPage([], false, query.Limit), ""));

        internal override Task<List<Milestone>?> FetchMilestonesAsync(string slug)
            => Task.FromResult<List<Milestone>?>([]);
    }

    /// <summary>Overrides the shell seam so the suite spawns no explorer.</summary>
    private sealed class RecordingRevealViewModel : UnhurriedViewModel
    {
        public string Revealed { get; private set; } = "";

        internal override string? RevealInShell(string path)
        {
            Revealed = path;
            return null;
        }
    }

    // ── Portfolio breadth ───────────────────────────────────────────────────────

    /// <summary>
    /// The portfolio fan-out takes the discovered list, so it covers every configured root without
    /// knowing there is more than one. A target list built from a single root would leave the
    /// repositories under every other one unsearchable with no refusal to show for it.
    /// </summary>
    [Fact]
    public async Task ThePortfolioFanOut_CoversEveryConfiguredRoot()
    {
        var first = TestEnv.NewDir("search-root-a");
        var second = TestEnv.NewDir("search-root-b");
        await InitRepoAsync(Path.Combine(first, "alpha"));
        await InitRepoAsync(Path.Combine(second, "beta"));

        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectRoots =
            [
                new ProjectRoot { Path = first },
                new ProjectRoot { Path = second },
            ],
            GhPath = Path.Combine(first, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            RefreshIntervalSeconds = 7200,
        });

        var gitHub = new GitHubService(settings);
        using var watcher = new ProjectWatcherService();
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            watcher,
            new RepoBusyRegistry(),
            uiPost: callback => callback());

        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var targets = dashboard.SearchTargets();

        Assert.Contains(targets, t => t.Path == Path.Combine(first, "alpha"));
        Assert.Contains(targets, t => t.Path == Path.Combine(second, "beta"));
    }

    private static async Task InitRepoAsync(string path)
    {
        Directory.CreateDirectory(path);
        await Git.RunAsync(path, "init", "-b", "main");
    }
}
