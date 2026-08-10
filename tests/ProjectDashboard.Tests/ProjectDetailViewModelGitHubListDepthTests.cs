using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Depth and facets on the Issues and Pull Requests lists, driven through the two page seams so
/// every outcome — a full window, a short one, a failed page, a facet change — is reachable
/// without gh. The claim under test throughout is that the surface never says more than the read
/// established: a window that came back full is disclosed as one, and a read that never completed
/// neither empties the list nor reports a total.
/// </summary>
public class ProjectDetailViewModelGitHubListDepthTests
{
    private static ProjectInfo LocalProject(string prefix = "gh-depth")
    {
        var dir = TestEnv.NewDir(prefix);
        return new ProjectInfo { DirectoryName = prefix, DisplayName = prefix, FullPath = dir };
    }

    private static ProjectInfo RemoteProject(string prefix = "gh-depth")
    {
        var project = LocalProject(prefix);
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    private static List<GitHubIssue> NumberedIssues(params int[] numbers) =>
        [.. numbers.Select(n => new GitHubIssue { Number = n, Title = $"issue {n}", State = "open" })];

    private static List<GitHubIssue> IssueRows(int count) =>
        NumberedIssues([.. Enumerable.Range(1, count)]);

    private static GitHubService.IssuePage IssuePage(int count, int limit) =>
        new(IssueRows(count), count >= limit, limit);

    private static GitHubService.PullRequestPage PullRequestPage(int count, int limit) =>
        new([.. Enumerable.Range(1, count).Select(n => new GitHubPullRequest { Number = n, Title = $"pr {n}" })],
            count >= limit, limit);

    /// <summary>
    /// Answers both list reads from a supplied function and records the query each one carried.
    /// Answers synchronously by default, so a fire-and-forget load has finished by the time the
    /// call that started it returns.
    /// </summary>
    private class ListViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        public List<GitHubService.GitHubListQuery> IssueReads { get; } = [];
        public List<GitHubService.GitHubListQuery> PullRequestReads { get; } = [];

        /// <summary>The page for read number <c>n</c> (zero-based), or null for a failed read.</summary>
        public Func<GitHubService.GitHubListQuery, int, GitHubService.IssuePage?> IssueAnswer { get; set; } =
            (query, _) => IssuePage(0, query.Limit);

        public Func<GitHubService.GitHubListQuery, int, GitHubService.PullRequestPage?> PullRequestAnswer { get; set; } =
            (query, _) => PullRequestPage(0, query.Limit);

        /// <summary>
        /// Hand-completed reads, in call order, for the cases that need a read left in flight while
        /// the test does something else.
        /// </summary>
        public Queue<TaskCompletionSource<GitHubService.IssuePage?>> IssueGates { get; } = [];

        internal override Task<GitHubService.IssuePage?> FetchIssuePageAsync(
            string slug, GitHubService.GitHubListQuery query)
        {
            IssueReads.Add(query);
            return IssueGates.Count > 0
                ? IssueGates.Dequeue().Task
                : Task.FromResult(IssueAnswer(query, IssueReads.Count - 1));
        }

        internal override Task<GitHubService.PullRequestPage?> FetchPullRequestPageAsync(
            string slug, GitHubService.GitHubListQuery query)
        {
            PullRequestReads.Add(query);
            return Task.FromResult(PullRequestAnswer(query, PullRequestReads.Count - 1));
        }
    }

    // ── What the footer may claim ───────────────────────────────────────────────

    [Fact]
    public async Task AFullWindow_IsDisclosedAsOneRatherThanAsATotal()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };

        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        Assert.True(vm.IssuesHasMore);
        Assert.Equal("Showing the first 100 open issues — there may be more.", vm.IssuesFooterText);
    }

    [Fact]
    public async Task AWindowThatCameBackShort_IsTheWholeAnswer()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(3, query.Limit) };

        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        Assert.False(vm.IssuesHasMore);
        Assert.Equal("All 3 open issues shown.", vm.IssuesFooterText);
    }

    /// <summary>The empty-state line already names the facets; a footer would say it twice.</summary>
    [Fact]
    public async Task AnEmptyList_LeavesTheFooterToTheEmptyStateLine()
    {
        var vm = new ListViewModel();

        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        Assert.Equal("", vm.IssuesFooterText);
        Assert.Equal("No open issues.", vm.IssuesEmptyText);
    }

    [Theory]
    [InlineData(GitHubListState.Open, false, "No open issues.")]
    [InlineData(GitHubListState.Closed, false, "No closed issues.")]
    [InlineData(GitHubListState.All, false, "No issues.")]
    [InlineData(GitHubListState.Closed, true, "No issues match that search.")]
    public void TheEmptyStateLine_NamesWhatProducedTheEmptiness(
        GitHubListState state, bool searching, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.ListEmptyText(state, "issues", searching));

    [Theory]
    [InlineData(5, false, "All 5 closed pull requests shown.")]
    [InlineData(1, false, "All 1 closed pull request shown.")]
    [InlineData(100, true, "Showing the first 100 closed pull requests — there may be more.")]
    public void TheFooter_NamesTheFacetsTheCountBelongsTo(int shown, bool mayHaveMore, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.ListFooterText(
            shown, mayHaveMore, GitHubListState.Closed, "pull requests", "pull request", false));

    // ── Paging ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// gh has no cursor on either list command, so a page is read by asking for the window again
    /// with a page added to it. A read that asked only for the new rows could not be proved to
    /// continue the list already on screen.
    /// </summary>
    [Fact]
    public async Task LoadMore_AsksForTheWholeLargerWindow()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);

        Assert.Equal([100, 200], vm.IssueReads.Select(q => q.Limit));
        Assert.Equal(200, vm.Issues.Count);
        Assert.Equal("Showing the first 200 open issues — there may be more.", vm.IssuesFooterText);
    }

    [Fact]
    public async Task LoadMore_IsOfferedOnlyWhileAWindowMayHaveMoreBehindIt()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        Assert.True(vm.LoadMoreIssuesCommand.CanExecute(null));

        vm.IssueAnswer = (query, _) => IssuePage(120, query.Limit);
        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);

        Assert.False(vm.LoadMoreIssuesCommand.CanExecute(null));
        Assert.Equal("All 120 open issues shown.", vm.IssuesFooterText);
    }

    /// <summary>A second click while a page is in flight is a no-op, not a second gh spawn.</summary>
    [Fact]
    public async Task ASecondLoadMore_WhileOneIsInFlight_ReadsOnce()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        var gate = new TaskCompletionSource<GitHubService.IssuePage?>();
        vm.IssueGates.Enqueue(gate);
        vm.LoadMoreIssuesCommand.Execute(null);
        vm.LoadMoreIssuesCommand.Execute(null);
        gate.SetResult(IssuePage(200, 200));
        await vm.IssuesPageLoad;

        Assert.Equal([100, 200], vm.IssueReads.Select(q => q.Limit));
    }

    /// <summary>
    /// A refresh re-reads the window the reader paged to. Collapsing back to the first window
    /// would undo the paging on every issue mutation, each of which ends in a reload.
    /// </summary>
    [Fact]
    public async Task ARefresh_RereadsTheWindowTheReaderPagedTo()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);

        await vm.RefreshIssuesCommand.ExecuteAsync(null);

        Assert.Equal(200, vm.IssueReads[^1].Limit);
    }

    // ── Failure ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A failed page is not an emptied repository. The rows already read stay on screen, and the
    /// footer keeps describing them, because they are still what the reader is looking at.
    /// </summary>
    [Fact]
    public async Task AFailedPage_LeavesTheRowsAndTheirFooterStanding()
    {
        var vm = new ListViewModel { IssueAnswer = (query, index) => index == 0 ? IssuePage(query.Limit, query.Limit) : null };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        var footer = vm.IssuesFooterText;

        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't load issues", vm.IssuesError);
        Assert.Equal(100, vm.Issues.Count);
        Assert.Equal(footer, vm.IssuesFooterText);
        Assert.True(vm.IssuesHasMore);
    }

    [Fact]
    public async Task AFailedPage_ReleasesTheGateSoTheNextClickCanRetry()
    {
        var vm = new ListViewModel { IssueAnswer = (query, index) => index == 0 ? IssuePage(query.Limit, query.Limit) : null };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);

        Assert.False(vm.IssuesPaging);
        Assert.True(vm.LoadMoreIssuesCommand.CanExecute(null));
    }

    /// <summary>
    /// A read that threw establishes no more than one that answered null. Left to escape, it would
    /// also unwind the re-entry gate through an exception path and leave the list unloadable.
    /// </summary>
    [Fact]
    public async Task AReadThatThrew_IsReportedLikeAFailedOneAndReleasesTheGate()
    {
        var vm = new ThrowingSecondRead();
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        await vm.RefreshIssuesCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't load issues", vm.IssuesError);
        Assert.Equal(100, vm.Issues.Count);
        Assert.False(vm.IssuesPaging);
    }

    private sealed class ThrowingSecondRead : ListViewModel
    {
        private int _reads;

        internal override Task<GitHubService.IssuePage?> FetchIssuePageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => _reads++ == 0
                ? Task.FromResult<GitHubService.IssuePage?>(IssuePage(query.Limit, query.Limit))
                : throw new InvalidOperationException("gh vanished mid-read");
    }

    /// <summary>
    /// A read whose search GitHub rejected fails for a reason the CLI's sign-in state has nothing
    /// to do with, and a message naming only the CLI sends the reader after the wrong thing.
    /// </summary>
    [Fact]
    public async Task AFailedSearch_NamesTheSearchRatherThanOnlyTheCli()
    {
        var vm = new ListViewModel { IssueAnswer = (_, _) => null };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        vm.IssuesSearchText = "label:\"unclosed";
        await vm.ApplyIssueFiltersCommand.ExecuteAsync(null);

        Assert.Contains("GitHub search syntax", vm.IssuesError);
    }

    // ── Facets ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStateChange_ReadsTheNewStateFromTheFirstWindow()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);

        vm.IssuesState = GitHubListState.Closed;
        await vm.IssuesPageLoad;

        Assert.Equal("closed", vm.IssueReads[^1].State);
        // A changed facet is a different question; the depth paged into under the previous one
        // does not carry over to it.
        Assert.Equal(100, vm.IssueReads[^1].Limit);
        Assert.Equal("Showing the first 100 closed issues — there may be more.", vm.IssuesFooterText);
    }

    [Fact]
    public async Task AStateChangeThatDropsTheSelectedIssue_ClearsTheDetailPane()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => new GitHubService.IssuePage(NumberedIssues(1, 2), false, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        vm.SelectedIssue = vm.Issues[0];

        vm.IssueAnswer = (query, _) => new GitHubService.IssuePage(NumberedIssues(7), false, query.Limit);
        vm.IssuesState = GitHubListState.Closed;
        await vm.IssuesPageLoad;

        Assert.Null(vm.SelectedIssue);
        Assert.Null(vm.IssueDetail);
    }

    /// <summary>A row the reload still lists keeps the pane on the issue the reader was reading.</summary>
    [Fact]
    public async Task ARefreshThatStillListsTheSelectedIssue_KeepsThePaneOnIt()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => new GitHubService.IssuePage(NumberedIssues(1, 2), false, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        vm.SelectedIssue = vm.Issues[1];

        await vm.RefreshIssuesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.SelectedIssue?.Number);
    }

    /// <summary>
    /// A facet changed while a read is in flight is a different question, not a repeat of the one
    /// running. Dropped by the re-entry gate, the picker would name a state the rows on screen were
    /// never read under.
    /// </summary>
    [Fact]
    public async Task AStateChangedWhileAReadIsInFlight_IsAnsweredWhenThatReadLands()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(2, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        var gate = new TaskCompletionSource<GitHubService.IssuePage?>();
        vm.IssueGates.Enqueue(gate);
        vm.RefreshIssuesCommand.Execute(null);   // left in flight, not awaited
        vm.IssuesState = GitHubListState.Closed;
        var applied = vm.IssuesPageLoad;
        gate.SetResult(IssuePage(2, 100));
        await applied;

        Assert.Equal(["open", "open", "closed"], vm.IssueReads.Select(q => q.State));
        Assert.Equal("All 2 closed issues shown.", vm.IssuesFooterText);
    }

    /// <summary>
    /// A facet changed mid-read is answered by a further read, and until that one lands the rows on
    /// screen belong to the earlier query. Labelled from the picker instead, three open issues
    /// render under closed-issue copy for as long as a gh call takes.
    /// </summary>
    [Fact]
    public async Task ThePageThatLandsDuringAFacetChange_IsLabelledByTheQueryThatProducedIt()
    {
        var vm = new ListViewModel();
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        var openRead = new TaskCompletionSource<GitHubService.IssuePage?>();
        var closedRead = new TaskCompletionSource<GitHubService.IssuePage?>();
        vm.IssueGates.Enqueue(openRead);
        vm.IssueGates.Enqueue(closedRead);
        vm.RefreshIssuesCommand.Execute(null);       // open read, left in flight
        vm.IssuesState = GitHubListState.Closed;     // queued behind it
        var applied = vm.IssuesPageLoad;

        openRead.SetResult(new GitHubService.IssuePage(NumberedIssues(1, 2, 3), false, 100));

        // The frame between the two reads: three open issues, and nothing on screen calls them closed.
        Assert.Equal("All 3 open issues shown.", vm.IssuesFooterText);
        Assert.Equal("No open issues.", vm.IssuesEmptyText);

        closedRead.SetResult(new GitHubService.IssuePage([], false, 100));
        await applied;

        Assert.Equal("", vm.IssuesFooterText);
        Assert.Equal("No closed issues.", vm.IssuesEmptyText);
    }

    /// <summary>
    /// The seed the next visit opens with is read under the default facets. A page read under any
    /// other facets would seed a list the state picker then describes wrongly.
    /// </summary>
    [Fact]
    public async Task OnlyADefaultFacetPage_SeedsTheProjectForTheNextVisit()
    {
        var project = RemoteProject();
        var vm = new ListViewModel { IssueAnswer = (query, _) => new GitHubService.IssuePage(NumberedIssues(1, 2), false, query.Limit) };
        await vm.SetProjectAsync(project);
        await vm.IssuesPageLoad;
        Assert.Equal(2, project.Issues.Count);

        vm.IssueAnswer = (query, _) => new GitHubService.IssuePage(NumberedIssues(9), false, query.Limit);
        vm.IssuesState = GitHubListState.Closed;
        await vm.IssuesPageLoad;

        Assert.Equal([1, 2], project.Issues.Select(i => i.Number));
    }

    [Fact]
    public async Task TheSearchText_TravelsToGhRatherThanFilteringWhatCameBack()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(2, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        vm.IssuesSearchText = "  crash in:title  ";
        await vm.ApplyIssueFiltersCommand.ExecuteAsync(null);

        Assert.Equal("crash in:title", vm.IssueReads[^1].Search);
        Assert.Equal("All 2 matching issues shown.", vm.IssuesFooterText);
    }

    /// <summary>
    /// A search naming a state overrules the picker at the gh boundary, so the picker's value
    /// stops describing the list. Left unsaid, the two disagree on screen with nothing to
    /// explain it.
    /// </summary>
    [Fact]
    public async Task ASearchThatNamesAState_SaysThePickerIsNotInForce()
    {
        var vm = new ListViewModel();
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        vm.IssuesSearchText = "crash is:closed";
        Assert.Equal(ProjectDetailViewModel.SearchSetsStateNotice, vm.IssuesFacetNotice);

        vm.IssuesSearchText = "crash";
        Assert.Equal("", vm.IssuesFacetNotice);
    }

    // ── Project switch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AProjectSwitch_ResetsTheFacetsAndTheWindow()
    {
        var vm = new ListViewModel { IssueAnswer = (query, _) => IssuePage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject("gh-depth-a"));
        await vm.IssuesPageLoad;
        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);
        vm.IssuesSearchText = "crash";
        vm.IssuesState = GitHubListState.All;
        await vm.IssuesPageLoad;

        await vm.SetProjectAsync(RemoteProject("gh-depth-b"));
        await vm.IssuesPageLoad;

        Assert.Equal(GitHubListState.Open, vm.IssuesState);
        Assert.Equal("", vm.IssuesSearchText);
        Assert.Equal("", vm.IssuesFacetNotice);
        Assert.Equal("open", vm.IssueReads[^1].State);
        Assert.Null(vm.IssueReads[^1].Search);
        Assert.Equal(100, vm.IssueReads[^1].Limit);
    }

    /// <summary>
    /// A page that arrives after the reader has moved on belongs to the repository it was read
    /// for. Written through, it would show one repository's issues under another's name.
    /// </summary>
    [Fact]
    public async Task APageLandingAfterAProjectSwitch_IsDropped()
    {
        var vm = new ListViewModel();
        var gate = new TaskCompletionSource<GitHubService.IssuePage?>();
        vm.IssueGates.Enqueue(gate);
        await vm.SetProjectAsync(RemoteProject("gh-depth-stale"));

        await vm.SetProjectAsync(RemoteProject("gh-depth-fresh"));
        await vm.IssuesPageLoad;
        gate.SetResult(new GitHubService.IssuePage(NumberedIssues(41), false, 100));

        Assert.Empty(vm.Issues);
        Assert.Equal("", vm.IssuesFooterText);
        Assert.False(vm.IssuesHasMore);
    }

    // ── Pull requests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePullRequestList_PagesAndDisclosesOnTheSameTerms()
    {
        var vm = new ListViewModel { PullRequestAnswer = (query, _) => PullRequestPage(query.Limit, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadPullRequestsCommand.ExecuteAsync(null);

        Assert.Equal("Showing the first 100 open pull requests — there may be more.", vm.PullRequestsFooterText);

        await vm.LoadMorePullRequestsCommand.ExecuteAsync(null);

        Assert.Equal([100, 200], vm.PullRequestReads.Select(q => q.Limit));
        Assert.Equal(200, vm.PullRequests.Count);
    }

    [Fact]
    public async Task APullRequestStateChange_ReadsTheNewStateFromTheFirstWindow()
    {
        var vm = new ListViewModel { PullRequestAnswer = (query, _) => PullRequestPage(4, query.Limit) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadPullRequestsCommand.ExecuteAsync(null);

        vm.PullRequestsState = GitHubListState.All;
        await vm.PullRequestsPageLoad;

        Assert.Equal("all", vm.PullRequestReads[^1].State);
        Assert.Equal("All 4 pull requests shown.", vm.PullRequestsFooterText);
    }

    /// <summary>
    /// Marked loaded, the next visit to the tab would skip its own read and show an empty list as
    /// though the repository had no pull requests.
    /// </summary>
    [Fact]
    public async Task AFailedFirstPullRequestRead_LeavesTheTabUnloaded()
    {
        var vm = new ListViewModel { PullRequestAnswer = (_, _) => null };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadPullRequestsCommand.ExecuteAsync(null);

        Assert.False(vm.PullRequestsLoaded);
        Assert.Contains("Couldn't load pull requests", vm.PullRequestsError);
        Assert.Equal("", vm.PullRequestsFooterText);
    }

    [Fact]
    public async Task AFailedPullRequestPage_LeavesTheRowsStanding()
    {
        var vm = new ListViewModel
        {
            PullRequestAnswer = (query, index) => index == 0 ? PullRequestPage(query.Limit, query.Limit) : null
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadPullRequestsCommand.ExecuteAsync(null);

        await vm.LoadMorePullRequestsCommand.ExecuteAsync(null);

        Assert.Equal(100, vm.PullRequests.Count);
        Assert.True(vm.PullRequestsLoaded);
        Assert.Contains("Couldn't load pull requests", vm.PullRequestsError);
    }

    // ── No remote ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutARemote_NeitherListReadsNorClaimsADepth()
    {
        var vm = new ListViewModel();

        await vm.SetProjectAsync(LocalProject("gh-depth-local"));
        await vm.LoadPullRequestsCommand.ExecuteAsync(null);

        Assert.Empty(vm.IssueReads);
        Assert.Empty(vm.PullRequestReads);
        Assert.False(vm.LoadMoreIssuesCommand.CanExecute(null));
        Assert.Equal("", vm.IssuesFooterText);
        Assert.Equal("This project has no GitHub remote.", vm.IssuesError);
    }
}
