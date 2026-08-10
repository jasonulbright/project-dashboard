using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Depth and facets on the Actions tab's run list, driven through the page seam so every outcome —
/// a full window, a short one, a failed page, a facet change — is reachable without gh. The claim
/// under test throughout is the one the Issues and Pull Requests lists already make: the surface
/// never says more than the read established, and a read that never completed neither empties the
/// list nor reports a total.
/// </summary>
public class ProjectDetailViewModelWorkflowRunDepthTests
{
    private static ProjectInfo RemoteProject(string prefix = "gh-runs")
    {
        var dir = TestEnv.NewDir(prefix);
        var project = new ProjectInfo { DirectoryName = prefix, DisplayName = prefix, FullPath = dir };
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    private static List<WorkflowRun> Runs(int count, string name = "CI") =>
        [.. Enumerable.Range(1, count).Select(n => new WorkflowRun
        {
            Id = 4_000_000_000L + n, Name = name, DisplayTitle = $"run {n}", Branch = "main",
            Status = "completed", Conclusion = "success"
        })];

    private static GitHubService.WorkflowRunPage Page(int count, int limit, string name = "CI") =>
        new(Runs(count, name), count >= limit, limit);

    /// <summary>
    /// Answers the run read from a supplied function and records the query each one carried. The
    /// issue and pull-request seams are answered too: applying a project starts both, and a live
    /// read would reach a service this view model was not given.
    /// </summary>
    private class RunViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        public List<GitHubService.WorkflowRunQuery> Reads { get; } = [];

        /// <summary>The page for read number <c>n</c> (zero-based), or null for a failed read.</summary>
        public Func<GitHubService.WorkflowRunQuery, int, GitHubService.WorkflowRunPage?> Answer { get; set; } =
            (query, _) => Page(0, query.Limit);

        /// <summary>What gh said about a failed read; "" is a failure that said nothing.</summary>
        public string ReadError { get; set; } = "";

        /// <summary>Hand-completed reads, in call order, for a read left in flight.</summary>
        public Queue<TaskCompletionSource<GitHubService.ListRead<GitHubService.WorkflowRunPage>>> Gates { get; } = [];

        internal override Task<GitHubService.ListRead<GitHubService.WorkflowRunPage>> FetchWorkflowRunPageAsync(
            string slug, GitHubService.WorkflowRunQuery query)
        {
            Reads.Add(query);
            if (Gates.Count > 0) return Gates.Dequeue().Task;
            var page = Answer(query, Reads.Count - 1);
            return Task.FromResult(new GitHubService.ListRead<GitHubService.WorkflowRunPage>(
                page, page is null ? ReadError : ""));
        }

        internal override Task<GitHubService.ListRead<GitHubService.IssuePage>> FetchIssuePageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.IssuePage>(
                new GitHubService.IssuePage([], false, query.Limit), ""));

        internal override Task<GitHubService.ListRead<GitHubService.PullRequestPage>> FetchPullRequestPageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.PullRequestPage>(
                new GitHubService.PullRequestPage([], false, query.Limit), ""));
    }

    private static async Task<RunViewModel> LoadedAsync(RunViewModel vm)
    {
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        await vm.WorkflowRunsPageLoad;
        return vm;
    }

    // ── What the footer may claim ───────────────────────────────────────────────

    [Fact]
    public async Task AFullWindow_IsDisclosedAsOneRatherThanAsATotal()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });

        Assert.True(vm.WorkflowRunsHasMore);
        Assert.Equal("Showing the first 30 runs — there may be more.", vm.WorkflowRunsFooterText);
    }

    [Fact]
    public async Task AWindowThatCameBackShort_IsTheWholeAnswer()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(4, q.Limit) });

        Assert.False(vm.WorkflowRunsHasMore);
        Assert.Equal("All 4 runs shown.", vm.WorkflowRunsFooterText);
    }

    /// <summary>The empty-state line already names the facets; a footer would say it twice.</summary>
    [Fact]
    public async Task AnEmptyList_LeavesTheFooterToTheEmptyStateLine()
    {
        var vm = await LoadedAsync(new RunViewModel());

        Assert.Equal("", vm.WorkflowRunsFooterText);
        Assert.Equal("No workflow runs.", vm.WorkflowRunsEmptyText);
    }

    [Theory]
    [InlineData(null, null, null, "No workflow runs.")]
    [InlineData("CI", null, null, "No runs of “CI”.")]
    [InlineData(null, "main", null, "No runs on main.")]
    [InlineData(null, null, "failure", "No runs with status failed.")]
    [InlineData("CI", "main", "in_progress", "No runs of “CI” on main with status running.")]
    public void TheEmptyStateLine_NamesWhatProducedTheEmptiness(
        string? workflow, string? branch, string? status, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.WorkflowRunsEmptyTextFor(
            new GitHubService.WorkflowRunQuery(workflow, branch, status)));

    [Theory]
    [InlineData(7, false, "All 7 runs of “CI” with status failed shown.")]
    [InlineData(1, false, "All 1 run of “CI” with status failed shown.")]
    [InlineData(30, true, "Showing the first 30 runs of “CI” with status failed — there may be more.")]
    public void TheFooter_NamesTheFacetsTheCountBelongsTo(int shown, bool mayHaveMore, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.WorkflowRunsFooterTextFor(
            shown, mayHaveMore, new GitHubService.WorkflowRunQuery("CI", Status: "failure")));

    // ── Paging ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// gh has no cursor on the run list, so a page is read by asking for the window again with a
    /// page added to it. A read that asked only for the new rows could not be proved to continue
    /// the list already on screen.
    /// </summary>
    [Fact]
    public async Task LoadMore_AsksForTheWholeLargerWindow()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });

        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);

        Assert.Equal([30, 60], vm.Reads.Select(q => q.Limit));
        Assert.Equal(60, vm.WorkflowRuns.Count);
        Assert.Equal("Showing the first 60 runs — there may be more.", vm.WorkflowRunsFooterText);
    }

    /// <summary>
    /// A deeper read of an unchanged repository repeats the loaded rows and adds behind them, so
    /// the rows on screen are extended rather than replaced — which is what keeps the reader's
    /// place in the list.
    /// </summary>
    [Fact]
    public async Task LoadMore_ExtendsTheRowsOnScreenWhenTheyAreStillItsHead()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });
        var shown = vm.WorkflowRuns;

        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);

        Assert.Same(shown, vm.WorkflowRuns);
    }

    [Fact]
    public async Task LoadMore_IsOfferedOnlyWhileAWindowMayHaveMoreBehindIt()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, i) => Page(i == 0 ? q.Limit : 31, q.Limit) });
        Assert.True(vm.LoadMoreWorkflowRunsCommand.CanExecute(null));

        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);

        Assert.False(vm.WorkflowRunsHasMore);
        Assert.False(vm.LoadMoreWorkflowRunsCommand.CanExecute(null));
    }

    /// <summary>A second click on a read already in flight is the same question, not a second gh.</summary>
    [Fact]
    public async Task LoadMoreTwice_WhileTheFirstIsInFlight_ReadsOnce()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });
        var gate = new TaskCompletionSource<GitHubService.ListRead<GitHubService.WorkflowRunPage>>();
        vm.Gates.Enqueue(gate);

        var first = vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);
        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);
        gate.SetResult(new GitHubService.ListRead<GitHubService.WorkflowRunPage>(Page(60, 60), ""));
        await first;

        Assert.Equal(2, vm.Reads.Count);
    }

    // ── Facets ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFacetChange_ReadsTheFirstWindowAgainRatherThanTheDepthPagedInto()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });
        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);

        vm.SelectedWorkflowRunStatus = WorkflowRunStatus.Failed;
        await vm.WorkflowRunsPageLoad;

        Assert.Equal([30, 60, 30], vm.Reads.Select(q => q.Limit));
        Assert.Equal("failure", vm.Reads[^1].Status);
    }

    [Fact]
    public async Task ABranchFilter_ReachesGhRatherThanTheLoadedRows()
    {
        var vm = await LoadedAsync(new RunViewModel());

        vm.WorkflowRunsBranchText = "  release  ";
        await vm.ApplyWorkflowRunFiltersCommand.ExecuteAsync(null);

        Assert.Equal("release", vm.Reads[^1].Branch);
    }

    /// <summary>
    /// A facet moved while a read is in flight is a different question and is answered once that
    /// read lands. Dropped, the picker would name a filter the list on screen was never read under.
    /// </summary>
    [Fact]
    public async Task AFacetChangedMidRead_IsAnsweredWhenThatReadLands()
    {
        var vm = await LoadedAsync(new RunViewModel());
        var gate = new TaskCompletionSource<GitHubService.ListRead<GitHubService.WorkflowRunPage>>();
        vm.Gates.Enqueue(gate);
        var inFlight = vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        vm.SelectedWorkflowRunStatus = WorkflowRunStatus.Queued;
        gate.SetResult(new GitHubService.ListRead<GitHubService.WorkflowRunPage>(Page(2, 30), ""));
        await inFlight;
        await vm.WorkflowRunsPageLoad;

        Assert.Equal("queued", vm.Reads[^1].Status);
    }

    // ── The workflow picker ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheWorkflowPicker_ListsTheWorkflowsTheLoadedRunsName()
    {
        var vm = await LoadedAsync(new RunViewModel
        {
            Answer = (_, _) => new GitHubService.WorkflowRunPage(
                [.. Runs(1, "release"), .. Runs(1, "CI"), .. Runs(1, "CI")], false, 30)
        });

        Assert.Equal(["Any workflow", "CI", "release"], vm.WorkflowChoices.Select(c => c.Label));
    }

    /// <summary>
    /// The picker is built from the runs on screen, so a workflow whose runs are all behind the
    /// window is missing from it. A picker silently missing one reads as a workflow the repository
    /// does not define, so the surface says where its rows came from while more may exist.
    /// </summary>
    [Fact]
    public async Task TheWorkflowPicker_SaysWhereItsRowsCameFromWhileTheWindowMayHaveMore()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });

        Assert.Equal(ProjectDetailViewModel.WorkflowsFromLoadedRuns, vm.WorkflowFilterNotice);
    }

    [Fact]
    public async Task TheWorkflowPicker_SaysNothingOnceTheLoadedRunsAreTheWholeHistory()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(2, q.Limit) });

        Assert.Equal("", vm.WorkflowFilterNotice);
    }

    /// <summary>
    /// A read filtered to one workflow names only that one. Narrowing the picker to what came back
    /// would strand the reader on a filter they could no longer leave by name.
    /// </summary>
    [Fact]
    public async Task TheWorkflowPicker_KeepsASelectionTheFilteredRowsNoLongerName()
    {
        var vm = await LoadedAsync(new RunViewModel
        {
            Answer = (q, i) => Page(2, q.Limit, i == 0 ? "CI" : "release")
        });
        vm.SelectedWorkflow = vm.WorkflowChoices.First(c => c.Name == "CI");
        await vm.WorkflowRunsPageLoad;

        Assert.Equal("CI", vm.SelectedWorkflow.Name);
        Assert.Contains(vm.WorkflowChoices, c => c.Name == "CI");
        Assert.Equal("CI", vm.Reads[^1].Workflow);
    }

    // ── Failure and project switch ──────────────────────────────────────────────

    /// <summary>
    /// Replacing the rows with nothing would report a repository whose runs were removed, which a
    /// read that never completed cannot say.
    /// </summary>
    [Fact]
    public async Task AFailedPage_LeavesTheRowsStandingAndSaysWhatGhSaid()
    {
        var vm = await LoadedAsync(new RunViewModel
        {
            Answer = (q, i) => i == 0 ? Page(q.Limit, q.Limit) : null,
            ReadError = "HTTP 403: rate limited"
        });

        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);

        Assert.Equal(30, vm.WorkflowRuns.Count);
        Assert.Contains("Couldn't load workflow runs", vm.WorkflowRunsError);
        Assert.Contains("HTTP 403: rate limited", vm.WorkflowRunsError);
    }

    [Fact]
    public async Task AProjectSwitchMidRead_DropsThePageRatherThanWritingItToTheNewProject()
    {
        var vm = await LoadedAsync(new RunViewModel());
        var gate = new TaskCompletionSource<GitHubService.ListRead<GitHubService.WorkflowRunPage>>();
        vm.Gates.Enqueue(gate);
        var inFlight = vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        await vm.SetProjectAsync(RemoteProject("gh-runs-next"));
        gate.SetResult(new GitHubService.ListRead<GitHubService.WorkflowRunPage>(Page(5, 30), ""));
        await inFlight;

        Assert.Empty(vm.WorkflowRuns);
    }

    /// <summary>
    /// The depth and the facets belong to the repository they were chosen for; carried over, they
    /// would read the incoming repository under the outgoing one's question.
    /// </summary>
    [Fact]
    public async Task AProjectSwitch_ReturnsTheListToItsFirstWindowAndDefaultFacets()
    {
        var vm = await LoadedAsync(new RunViewModel { Answer = (q, _) => Page(q.Limit, q.Limit) });
        await vm.LoadMoreWorkflowRunsCommand.ExecuteAsync(null);
        vm.SelectedWorkflowRunStatus = WorkflowRunStatus.Failed;
        vm.WorkflowRunsBranchText = "release";
        await vm.WorkflowRunsPageLoad;

        await vm.SetProjectAsync(RemoteProject("gh-runs-next"));
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        await vm.WorkflowRunsPageLoad;

        Assert.Equal(WorkflowRunStatus.Any, vm.SelectedWorkflowRunStatus);
        Assert.Equal("", vm.WorkflowRunsBranchText);
        Assert.Equal(WorkflowChoice.Any, vm.SelectedWorkflow);
        Assert.Equal(30, vm.Reads[^1].Limit);
        Assert.Null(vm.Reads[^1].Status);
    }

    [Fact]
    public async Task WithoutARemote_TheTabSaysSoInsteadOfReading()
    {
        var vm = new RunViewModel();
        var project = RemoteProject();
        project.GitStatus.RemoteUrl = "";

        await vm.SetProjectAsync(project);
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Reads);
        Assert.Equal("This project has no GitHub remote.", vm.WorkflowRunsError);
    }
}
