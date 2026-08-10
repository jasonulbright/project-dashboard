using System.ComponentModel;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Depth and facets for the Issues and Pull Requests lists.
///
/// gh's issue and pull-request list commands expose no cursor: <c>--limit</c> is the only depth
/// control either of them has. A page is therefore read by re-asking for a larger window and
/// replacing the list, which cannot interleave a stale page into a list built from an older one.
///
/// The window is a claim about what was read, never about what the repository holds: a read that
/// came back full says only that more may be behind it, and the footer says exactly that until a
/// larger read answers it. State and search are gh's to apply — a facet applied here would answer
/// from whatever the last window happened to hold.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Rows the first read of either list asks for.</summary>
    internal const int GitHubListWindow = 100;

    /// <summary>Rows added to the window per "load more" click.</summary>
    internal const int GitHubListPageSize = 100;

    /// <summary>The load-more button's own label; the count and the window come from one place.</summary>
    public static string GitHubListLoadMoreLabel => $"Load {GitHubListPageSize} more";

    /// <summary>Enum-only picker; the exact gh token is derived, never typed.</summary>
    public static IReadOnlyList<GitHubListState> GitHubListStates { get; } = Enum.GetValues<GitHubListState>();

    private int _issuesWindowSize = GitHubListWindow;
    private int _pullRequestsWindowSize = GitHubListWindow;

    /// <summary>
    /// Held while a project switch rewrites the facets. A facet write reloads its list, and the
    /// reload a reset triggers would read the incoming repository under the outgoing one's window.
    /// </summary>
    private bool _gitHubFacetsQuiet;

    /// <summary>A facet changed while that list's read was in flight; it is answered when it lands.</summary>
    private bool _issuesFacetsPending;
    private bool _pullRequestsFacetsPending;

    [ObservableProperty] private GitHubListState _issuesState = GitHubListState.Open;
    [ObservableProperty] private string _issuesSearchText = "";
    [ObservableProperty] private bool _issuesHasMore;
    [ObservableProperty] private bool _issuesPaging;
    [ObservableProperty] private string _issuesFooterText = "";
    [ObservableProperty] private string _issuesEmptyText = "No open issues.";
    [ObservableProperty] private string _issuesFacetNotice = "";

    [ObservableProperty] private GitHubListState _pullRequestsState = GitHubListState.Open;
    [ObservableProperty] private string _pullRequestsSearchText = "";
    [ObservableProperty] private bool _pullRequestsHasMore;
    [ObservableProperty] private bool _pullRequestsPaging;
    [ObservableProperty] private string _pullRequestsFooterText = "";
    [ObservableProperty] private string _pullRequestsEmptyText = "No open pull requests.";
    [ObservableProperty] private string _pullRequestsFacetNotice = "";

    /// <summary>
    /// The list read this command started and did not await. Held so a caller — and a headless
    /// test — can wait for the page instead of polling the properties it writes.
    /// </summary>
    internal Task IssuesPageLoad { get; private set; } = Task.CompletedTask;

    internal Task PullRequestsPageLoad { get; private set; } = Task.CompletedTask;

    private bool CanLoadMoreIssues() => !IssuesPaging && IssuesHasMore && Slug.Length > 0;

    private bool CanLoadMorePullRequests() => !PullRequestsPaging && PullRequestsHasMore && Slug.Length > 0;

    private void HandleGitHubListDepthPropertyChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IssuesPaging):
            case nameof(IssuesHasMore):
            case nameof(PullRequestsPaging):
            case nameof(PullRequestsHasMore):
            case nameof(Project):
                LoadMoreIssuesCommand.NotifyCanExecuteChanged();
                LoadMorePullRequestsCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>
    /// Returns both lists to their first window with the default facets. The depth and the facets
    /// belong to the repository they were chosen for, so neither survives a project switch.
    /// </summary>
    private void ResetGitHubListDepth()
    {
        _gitHubFacetsQuiet = true;
        try
        {
            _issuesWindowSize = GitHubListWindow;
            _pullRequestsWindowSize = GitHubListWindow;
            _issuesFacetsPending = false;
            _pullRequestsFacetsPending = false;
            IssuesState = GitHubListState.Open;
            IssuesSearchText = "";
            IssuesHasMore = false;
            IssuesPaging = false;
            IssuesFooterText = "";
            IssuesFacetNotice = "";
            IssuesEmptyText = ListEmptyText(GitHubListState.Open, IssuesNoun, searching: false);
            PullRequestsState = GitHubListState.Open;
            PullRequestsSearchText = "";
            PullRequestsHasMore = false;
            PullRequestsPaging = false;
            PullRequestsFooterText = "";
            PullRequestsFacetNotice = "";
            PullRequestsEmptyText = ListEmptyText(GitHubListState.Open, PullRequestsNoun, searching: false);
        }
        finally
        {
            _gitHubFacetsQuiet = false;
        }
    }

    private const string IssuesNoun = "issues";
    private const string IssueNoun = "issue";
    private const string PullRequestsNoun = "pull requests";
    private const string PullRequestNoun = "pull request";

    /// <summary>What a surface calls the rows one state selects.</summary>
    internal static string ListLabel(GitHubListState state, string plural) =>
        state == GitHubListState.All ? plural : $"{state.Token()} {plural}";

    /// <summary>
    /// The line an empty list carries. It names the facets that produced the emptiness: a list
    /// filtered to closed rows and a repository with no issues at all are different answers.
    /// </summary>
    internal static string ListEmptyText(GitHubListState state, string plural, bool searching) =>
        searching ? $"No {plural} match that search." : $"No {ListLabel(state, plural)}.";

    /// <summary>
    /// What the list can honestly say about its own depth. A read that came back full establishes
    /// only that more may exist, and saying "all N" there would report a repository's whole
    /// contents from one window of it. An empty list says nothing here — the empty-state line
    /// already names the facets — and a failed read leaves the previous line standing, because the
    /// rows it described are still the rows on screen.
    /// </summary>
    internal static string ListFooterText(int shown, bool mayHaveMore, GitHubListState state, string plural,
        string singular, bool searching)
    {
        if (shown == 0) return "";
        var noun = shown == 1 ? singular : plural;
        var label = searching ? $"matching {noun}" : ListLabel(state, noun);
        return mayHaveMore
            ? $"Showing the first {shown} {label} — there may be more."
            : $"All {shown} {label} shown.";
    }

    /// <summary>
    /// Says that the state picker is not in force. GitHub's search syntax carries its own state
    /// qualifier, and a search that names one overrules the picker inside gh; a picker still
    /// reading "Open" beside a list of closed rows explains nothing about either.
    /// </summary>
    internal const string SearchSetsStateNotice =
        "The search text sets the state, so the state filter is not applied.";

    private static string FacetNotice(string search) =>
        search.Trim().Length > 0 && GitHubService.SearchSetsState(search) ? SearchSetsStateNotice : "";

    private GitHubService.GitHubListQuery IssuesQuery(int windowSize) =>
        new(IssuesState.Token(), NullIfBlank(IssuesSearchText), windowSize);

    private GitHubService.GitHubListQuery PullRequestsQuery(int windowSize) =>
        new(PullRequestsState.Token(), NullIfBlank(PullRequestsSearchText), windowSize);

    private static string? NullIfBlank(string text) => text.Trim().Length == 0 ? null : text.Trim();

    // ── Facet changes ───────────────────────────────────────────────────────────

    // The picker moves before the read it starts lands. Every line describing the list is written
    // by the page that arrives, so until it does, the lines keep describing the rows on screen.
    partial void OnIssuesStateChanged(GitHubListState value)
    {
        if (_gitHubFacetsQuiet) return;
        IssuesPageLoad = ApplyIssueFiltersAsync();
    }

    partial void OnIssuesSearchTextChanged(string value) => IssuesFacetNotice = FacetNotice(value);

    partial void OnPullRequestsStateChanged(GitHubListState value)
    {
        if (_gitHubFacetsQuiet) return;
        PullRequestsPageLoad = ApplyPullRequestFiltersAsync();
    }

    partial void OnPullRequestsSearchTextChanged(string value) => PullRequestsFacetNotice = FacetNotice(value);

    /// <summary>
    /// Applies the facets from the first window. A changed facet is a different question, so the
    /// depth paged into under the previous one is not carried over to it.
    /// </summary>
    [RelayCommand]
    private Task ApplyIssueFilters()
    {
        IssuesPageLoad = ApplyIssueFiltersAsync();
        return IssuesPageLoad;
    }

    /// <summary>
    /// The wait for the facets to be in force. A read already in flight absorbs them and answers
    /// them before it returns, so that read — not a call that only queued them — is what a caller
    /// waits on.
    /// </summary>
    private Task ApplyIssueFiltersAsync()
    {
        var inFlight = IssuesPaging ? IssuesPageLoad : null;
        var read = LoadIssuePageAsync(GitHubListWindow, facetChange: true);
        return inFlight ?? read;
    }

    [RelayCommand]
    private Task ApplyPullRequestFilters()
    {
        PullRequestsPageLoad = ApplyPullRequestFiltersAsync();
        return PullRequestsPageLoad;
    }

    private Task ApplyPullRequestFiltersAsync()
    {
        var inFlight = PullRequestsPaging ? PullRequestsPageLoad : null;
        var read = LoadPullRequestPageAsync(GitHubListWindow, facetChange: true);
        return inFlight ?? read;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreIssues))]
    private Task LoadMoreIssues()
    {
        IssuesPageLoad = LoadIssuePageAsync(_issuesWindowSize + GitHubListPageSize);
        return IssuesPageLoad;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMorePullRequests))]
    private Task LoadMorePullRequests()
    {
        PullRequestsPageLoad = LoadPullRequestPageAsync(_pullRequestsWindowSize + GitHubListPageSize);
        return PullRequestsPageLoad;
    }

    // ── The reads ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the issue list at <paramref name="windowSize"/> rows and replaces what is on screen
    /// with it. A failed read leaves the rows standing and sets the error line: replacing them
    /// with nothing would report a repository emptied, which a read that never completed cannot
    /// say.
    ///
    /// A repeated click while a read is in flight is the same question and is dropped rather than
    /// spawning a second gh. A facet changed while one is in flight is a different question, so it
    /// is held and answered from the first window as soon as that read is done — dropped, the
    /// picker would name a state the list on screen was never read under.
    /// </summary>
    private async Task LoadIssuePageAsync(int windowSize, bool facetChange = false)
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            IssuesError = NoRemoteStatus;
            return;
        }
        if (IssuesPaging)
        {
            if (facetChange) _issuesFacetsPending = true;
            return;
        }

        var gen = _generation;
        var window = windowSize;
        IssuesPaging = true;
        try
        {
            do
            {
                _issuesFacetsPending = false;
                var query = IssuesQuery(window);
                GitHubService.IssuePage? page = null;
                try
                {
                    page = await FetchIssuePageAsync(slug, query);
                }
                catch (Exception ex)
                {
                    // A read that threw and a read that answered null are the same answer to the
                    // reader: nothing was established, so nothing on screen changes but the line.
                    Log.Warn($"issue list read failed for {slug}", ex);
                }
                if (!IsCurrent(gen)) return;
                if (page is null)
                {
                    IssuesError = ListFetchFailed(IssuesFetchFailed, query.Search);
                }
                else
                {
                    IssuesError = "";
                    _issuesWindowSize = window;
                    ApplyIssuePage(page, query);
                }
                window = GitHubListWindow;
            } while (_issuesFacetsPending && IsCurrent(gen));
        }
        finally
        {
            if (IsCurrent(gen)) IssuesPaging = false;
        }
    }

    /// <summary>
    /// Writes a page onto the Issues surface. Every line describing it is derived from the query
    /// that produced it, never from the pickers: a facet changed while the read was in flight is
    /// answered by a further read, and until that one lands the rows on screen are the earlier
    /// query's — labelling them from the live picker would caption open rows as closed ones for a
    /// whole gh call.
    /// </summary>
    private void ApplyIssuePage(GitHubService.IssuePage page, GitHubService.GitHubListQuery query)
    {
        var searching = query.Search is not null;
        var state = GitHubActionTokens.ParseListState(query.State);
        // A selection held by reference would keep the detail pane on a row the list no longer
        // holds, and a facet that dropped the row must clear it.
        var keep = SelectedIssue?.Number;
        Issues = new ObservableCollection<GitHubIssue>(page.Items);
        if (keep is { } number) SelectedIssue = Issues.FirstOrDefault(i => i.Number == number);
        IssuesHasMore = page.MayHaveMore;
        IssuesEmptyText = ListEmptyText(state, IssuesNoun, searching);
        IssuesFooterText = ListFooterText(page.Items.Count, page.MayHaveMore, state, IssuesNoun, IssueNoun, searching);
        // Seeds the next visit, which opens under the default facets — a page read under any other
        // facets describes a different question and would seed the list with rows the state picker
        // then names wrongly.
        if (Project is not null && !searching && state == GitHubListState.Open)
            Project.Issues = [.. page.Items];
    }

    /// <summary><see cref="LoadIssuePageAsync"/> for the pull-request list, on the same terms.</summary>
    private async Task LoadPullRequestPageAsync(int windowSize, bool facetChange = false)
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            PullRequestsError = NoRemoteStatus;
            return;
        }
        if (PullRequestsPaging)
        {
            if (facetChange) _pullRequestsFacetsPending = true;
            return;
        }

        var gen = _generation;
        var window = windowSize;
        PullRequestsPaging = true;
        try
        {
            do
            {
                _pullRequestsFacetsPending = false;
                var query = PullRequestsQuery(window);
                GitHubService.PullRequestPage? page = null;
                try
                {
                    page = await FetchPullRequestPageAsync(slug, query);
                }
                catch (Exception ex)
                {
                    Log.Warn($"pull request list read failed for {slug}", ex);
                }
                if (!IsCurrent(gen)) return;
                if (page is null)
                {
                    PullRequestsError = ListFetchFailed(PullRequestsFetchFailed, query.Search);
                }
                else
                {
                    PullRequestsError = "";
                    _pullRequestsWindowSize = window;
                    ApplyPullRequestPage(page, query);
                }
                window = GitHubListWindow;
            } while (_pullRequestsFacetsPending && IsCurrent(gen));
        }
        finally
        {
            if (IsCurrent(gen)) PullRequestsPaging = false;
        }
    }

    /// <summary><see cref="ApplyIssuePage"/> for the pull-request surface, on the same terms.</summary>
    private void ApplyPullRequestPage(GitHubService.PullRequestPage page, GitHubService.GitHubListQuery query)
    {
        var searching = query.Search is not null;
        var state = GitHubActionTokens.ParseListState(query.State);
        var keep = SelectedPullRequest?.Number;
        PullRequests = new ObservableCollection<GitHubPullRequest>(page.Items);
        if (keep is { } number) SelectedPullRequest = PullRequests.FirstOrDefault(p => p.Number == number);
        PullRequestsLoaded = true;
        PullRequestsHasMore = page.MayHaveMore;
        PullRequestsEmptyText = ListEmptyText(state, PullRequestsNoun, searching);
        PullRequestsFooterText =
            ListFooterText(page.Items.Count, page.MayHaveMore, state, PullRequestsNoun, PullRequestNoun, searching);
    }

    /// <summary>
    /// A failed list read names the search when one was in force. GitHub rejects a query it cannot
    /// parse, and a message about the CLI's sign-in state would send the reader after the wrong
    /// thing entirely.
    /// </summary>
    internal static string ListFetchFailed(string baseMessage, string? search) =>
        search is null ? baseMessage : $"{baseMessage} The search text must be valid GitHub search syntax.";
}
