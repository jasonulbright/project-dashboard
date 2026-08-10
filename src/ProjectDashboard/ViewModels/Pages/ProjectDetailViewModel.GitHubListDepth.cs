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

    /// <summary>The milestone list this project's pickers stand on; refetched per project.</summary>
    private bool _milestonesLoaded;
    private Task<List<Milestone>?>? _milestoneFetch;

    /// <summary>
    /// The milestone read the issue list started and did not await. Held so a caller — and a
    /// headless test — can wait for the pickers to be populated instead of polling them.
    /// </summary>
    internal Task IssueMilestonesLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private GitHubListState _issuesState = GitHubListState.Open;
    [ObservableProperty] private string _issuesSearchText = "";
    [ObservableProperty] private bool _issuesHasMore;
    [ObservableProperty] private bool _issuesPaging;
    [ObservableProperty] private string _issuesFooterText = "";
    [ObservableProperty] private string _issuesEmptyText = "No open issues.";
    [ObservableProperty] private string _issuesFacetNotice = "";

    /// <summary>Every milestone this repository defines, behind the row that filters to none.</summary>
    [ObservableProperty] private ObservableCollection<MilestoneChoice> _issueMilestoneChoices = [MilestoneChoice.Any];

    [ObservableProperty] private MilestoneChoice _selectedIssueMilestone = MilestoneChoice.Any;

    /// <summary>
    /// Why the milestone pickers hold nothing but their own no-milestone row, or "" when the
    /// list is an answer. A repository defines no milestones and a milestone read that failed
    /// both leave one row on screen, and only this line tells them apart.
    /// </summary>
    [ObservableProperty] private string _issueMilestonesError = "";

    /// <summary>
    /// How far the milestone the list was read under has got, or why that cannot be said. Derived
    /// from the query that produced the page, never from the picker: a picker moved since the read
    /// would caption one milestone's rows with another's progress.
    /// </summary>
    [ObservableProperty] private string _issueMilestoneProgressText = "";

    /// <summary>The open milestones a new issue may join, behind the row that joins none.</summary>
    [ObservableProperty] private ObservableCollection<MilestoneChoice> _newIssueMilestoneChoices = [MilestoneChoice.None];

    [ObservableProperty] private MilestoneChoice _newIssueMilestone = MilestoneChoice.None;

    internal const string MilestonesUnavailable =
        "Milestones are unavailable — the read failed, so this picker lists none rather than none existing.";

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
            // The milestone list belongs to the repository it was read for, so the pickers return
            // to their no-milestone rows rather than offering another project's milestones.
            _milestonesLoaded = false;
            _milestoneFetch = null;
            IssueMilestonesLoad = Task.CompletedTask;
            IssueMilestoneChoices = [MilestoneChoice.Any];
            SelectedIssueMilestone = MilestoneChoice.Any;
            NewIssueMilestoneChoices = [MilestoneChoice.None];
            NewIssueMilestone = MilestoneChoice.None;
            IssueMilestonesError = "";
            IssueMilestoneProgressText = "";
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
    /// The milestone facet, spelled for a sentence that names it, or "" when none is in force.
    /// </summary>
    internal static string MilestoneSuffix(string? milestone) =>
        string.IsNullOrEmpty(milestone) ? "" : $" in milestone “{milestone}”";

    /// <summary>
    /// The line an empty list carries. It names the facets that produced the emptiness: a list
    /// filtered to closed rows, one filtered to a milestone nothing is in, and a repository with
    /// no issues at all are three different answers.
    /// </summary>
    internal static string ListEmptyText(GitHubListState state, string plural, bool searching,
        string? milestone = null) =>
        searching
            ? $"No {plural}{MilestoneSuffix(milestone)} match that search."
            : $"No {ListLabel(state, plural)}{MilestoneSuffix(milestone)}.";

    /// <summary>
    /// What the list can honestly say about its own depth. A read that came back full establishes
    /// only that more may exist, and saying "all N" there would report a repository's whole
    /// contents from one window of it. An empty list says nothing here — the empty-state line
    /// already names the facets — and a failed read leaves the previous line standing, because the
    /// rows it described are still the rows on screen.
    /// </summary>
    internal static string ListFooterText(int shown, bool mayHaveMore, GitHubListState state, string plural,
        string singular, bool searching, string? milestone = null)
    {
        if (shown == 0) return "";
        var noun = shown == 1 ? singular : plural;
        var label = searching ? $"matching {noun}" : ListLabel(state, noun);
        var scope = $"{label}{MilestoneSuffix(milestone)}";
        return mayHaveMore
            ? $"Showing the first {shown} {scope} — there may be more."
            : $"All {shown} {scope} shown.";
    }

    /// <summary>
    /// Says that the state picker is not in force. GitHub's search syntax carries its own state
    /// qualifier, and a search that names one overrules the picker inside gh; a picker still
    /// reading "Open" beside a list of closed rows explains nothing about either.
    /// </summary>
    internal const string SearchSetsStateNotice =
        "The search text sets the state, so the state filter is not applied.";

    /// <summary>
    /// Says that the milestone picker is not in force, on the same terms as the state one. gh
    /// turns the milestone flag into a <c>milestone:</c> qualifier, so a search carrying one of
    /// its own leaves two qualifiers that intersect to nothing while the picker still names a
    /// milestone — an empty list that would read as a milestone holding no issues.
    /// </summary>
    internal const string SearchSetsMilestoneNotice =
        "The search text sets the milestone, so the milestone filter is not applied.";

    private static string FacetNotice(string search) =>
        search.Trim().Length > 0 && GitHubService.SearchSetsState(search) ? SearchSetsStateNotice : "";

    /// <summary>
    /// The Issues notice, which carries both overruled facets. A milestone qualifier overrules
    /// nothing while the picker is on its unfiltered row, so it is only reported when a milestone
    /// is actually selected.
    /// </summary>
    private static string IssuesFacetNoticeFor(string search, bool milestoneSelected)
    {
        if (search.Trim().Length == 0) return "";
        var notices = new List<string>();
        if (GitHubService.SearchSetsState(search)) notices.Add(SearchSetsStateNotice);
        if (milestoneSelected && GitHubService.SearchSetsMilestone(search)) notices.Add(SearchSetsMilestoneNotice);
        return string.Join(" ", notices);
    }

    private GitHubService.GitHubListQuery IssuesQuery(int windowSize) =>
        new(IssuesState.Token(), NullIfBlank(IssuesSearchText), windowSize, SelectedIssueMilestone.Facet);

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

    partial void OnIssuesSearchTextChanged(string value) =>
        IssuesFacetNotice = IssuesFacetNoticeFor(value, SelectedIssueMilestone.Milestone is not null);

    partial void OnSelectedIssueMilestoneChanged(MilestoneChoice value)
    {
        IssuesFacetNotice = IssuesFacetNoticeFor(IssuesSearchText, value.Milestone is not null);
        if (_gitHubFacetsQuiet) return;
        IssuesPageLoad = ApplyIssueFiltersAsync();
    }

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

    // ── Writing a page onto a list ──────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="rows"/> into the list on screen, keeping the collection the view is
    /// bound to whenever the rows already loaded are still the head of the read.
    ///
    /// Both list reads are newest-first, so a deeper read of an unchanged repository repeats the
    /// loaded rows and adds behind them: the head matches, the delta is appended, and the reader
    /// keeps their scroll position and their place in the list. A row created or removed between
    /// the two reads shifts that head, and the result is a different list rather than a deeper view
    /// of the same one — it replaces the collection, which returns the reader to the top. That is
    /// the case where a preserved scroll offset would point at rows it no longer describes.
    /// </summary>
    private static ObservableCollection<T> MergeRows<T>(ObservableCollection<T> shown, IReadOnlyList<T> rows,
        Func<T, int> number, Func<T, T, bool> unchanged)
    {
        if (shown.Count == 0 || rows.Count < shown.Count) return new ObservableCollection<T>(rows);
        for (var i = 0; i < shown.Count; i++)
            if (number(shown[i]) != number(rows[i]))
                return new ObservableCollection<T>(rows);

        var overlap = shown.Count;
        // The rows are re-read, not only extended: a title edited or a check that finished since
        // the last read belongs on screen, and only the rows that actually changed are replaced.
        for (var i = 0; i < overlap; i++)
            if (!unchanged(shown[i], rows[i])) shown[i] = rows[i];
        for (var i = overlap; i < rows.Count; i++) shown.Add(rows[i]);
        return shown;
    }

    /// <summary>Whether two reads of one row would draw and announce the same thing.</summary>
    private static bool SameIssueRow(GitHubIssue a, GitHubIssue b) =>
        a.Number == b.Number && a.Title == b.Title && a.State == b.State &&
        a.Author == b.Author && a.Labels == b.Labels && a.UpdatedAt == b.UpdatedAt;

    private static bool SamePullRequestRow(GitHubPullRequest a, GitHubPullRequest b) =>
        a.Number == b.Number && a.Title == b.Title && a.State == b.State && a.IsDraft == b.IsDraft &&
        a.Author == b.Author && a.ChecksState == b.ChecksState && a.UpdatedAt == b.UpdatedAt;

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
                var read = new GitHubService.ListRead<GitHubService.IssuePage>(null, "");
                try
                {
                    read = await FetchIssuePageAsync(slug, query);
                }
                catch (Exception ex)
                {
                    // A read that threw and a read that answered null are the same answer to the
                    // reader: nothing was established, so nothing on screen changes but the line.
                    Log.Warn($"issue list read failed for {slug}", ex);
                }
                if (!IsCurrent(gen)) return;
                if (read.Page is not { } page)
                {
                    IssuesError = ListFetchFailed(IssuesFetchFailed, read.Error);
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

        // Started, not awaited: the milestone pickers stand beside the list rather than in front
        // of it, and holding the rows behind a second gh call would delay what was asked for.
        if (IsCurrent(gen)) IssueMilestonesLoad = EnsureMilestonesLoadedAsync();
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
        var merged = MergeRows(Issues, page.Items, i => i.Number, SameIssueRow);
        if (!ReferenceEquals(merged, Issues)) Issues = merged;
        if (keep is { } number) SelectedIssue = Issues.FirstOrDefault(i => i.Number == number);
        // A milestone the search overruled reached no gh call, so it names nothing about these rows.
        var milestone = query.Search is { } search && GitHubService.SearchSetsMilestone(search)
            ? null
            : query.Milestone;
        IssuesHasMore = page.MayHaveMore;
        IssuesEmptyText = ListEmptyText(state, IssuesNoun, searching, milestone?.Title);
        IssuesFooterText = ListFooterText(page.Items.Count, page.MayHaveMore, state, IssuesNoun, IssueNoun,
            searching, milestone?.Title);
        IssueMilestoneProgressText = MilestoneProgressText(milestone);
        // Seeds the next visit, which opens under the default facets — a page read under any other
        // facets describes a different question and would seed the list with rows the state picker
        // then names wrongly.
        if (Project is not null && !searching && milestone is null && state == GitHubListState.Open)
            Project.Issues = [.. page.Items];
    }

    /// <summary>
    /// How far the milestone a page was read under has got. The counts come from the milestone
    /// read, which answers them per milestone; a milestone the picker no longer holds, or one
    /// whose counts the read did not carry, says the counts are unavailable rather than showing a
    /// zero that would read as an empty milestone.
    /// </summary>
    private string MilestoneProgressText(MilestoneFacet? facet)
    {
        if (facet is null) return "";
        var known = IssueMilestoneChoices
            .Select(c => c.Milestone)
            .FirstOrDefault(m => m is not null && m.Number == facet.Number);
        return known is { OpenIssues: { } open, ClosedIssues: { } closed }
            ? $"Milestone “{facet.Title}”: {closed} of {open + closed} closed."
            : $"Milestone “{facet.Title}”: issue counts unavailable.";
    }

    /// <summary>
    /// Fills both milestone pickers, once per project and joining a fetch already in flight. A
    /// failed read drops the task so the next visit retries rather than caching the failure for
    /// the life of the project, and says the pickers list nothing because the read failed.
    /// </summary>
    private async Task EnsureMilestonesLoadedAsync()
    {
        if (_milestonesLoaded) return;
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;

        List<Milestone>? milestones;
        Task<List<Milestone>?>? fetch = null;
        try
        {
            fetch = _milestoneFetch ??= FetchMilestonesAsync(slug);
            milestones = await fetch;
        }
        catch (Exception ex)
        {
            // A read that threw and one that answered null establish the same nothing about which
            // milestones this repository defines.
            Log.Warn($"milestone read failed for {slug}", ex);
            milestones = null;
        }
        if (milestones is null)
        {
            if (fetch is null || ReferenceEquals(_milestoneFetch, fetch)) _milestoneFetch = null;
            if (IsCurrent(gen)) IssueMilestonesError = MilestonesUnavailable;
            return;
        }
        if (!IsCurrent(gen)) return;

        var ordered = InPickerOrder(milestones).ToList();
        var keep = SelectedIssueMilestone.Milestone?.Number;
        var choices = ordered.Select(MilestoneChoice.For).ToList();
        // Rewriting the picker's rows replaces the selected one with an equal-but-different
        // instance; the write is quiet so a list that gained no facet does not reread itself.
        _gitHubFacetsQuiet = true;
        try
        {
            IssueMilestoneChoices = [MilestoneChoice.Any, .. choices];
            SelectedIssueMilestone = IssueMilestoneChoices
                .FirstOrDefault(c => c.Milestone?.Number == keep) ?? MilestoneChoice.Any;
        }
        finally
        {
            _gitHubFacetsQuiet = false;
        }
        // A closed milestone is worth filtering to and is not worth filing new work under.
        var keepComposed = NewIssueMilestone.Milestone?.Number;
        NewIssueMilestoneChoices =
            [MilestoneChoice.None, .. ordered.Where(m => m.State != "closed").Select(MilestoneChoice.For)];
        NewIssueMilestone = NewIssueMilestoneChoices
            .FirstOrDefault(c => c.Milestone?.Number == keepComposed) ?? MilestoneChoice.None;
        IssueMilestonesError = "";
        _milestonesLoaded = true;
    }

    /// <summary>Open milestones first, then by due date, then by title — soonest work at the top.</summary>
    private static IEnumerable<Milestone> InPickerOrder(IEnumerable<Milestone> milestones) =>
        milestones
            .OrderBy(m => m.State == "closed")
            .ThenBy(m => m.DueOn ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase);

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
                var read = new GitHubService.ListRead<GitHubService.PullRequestPage>(null, "");
                try
                {
                    read = await FetchPullRequestPageAsync(slug, query);
                }
                catch (Exception ex)
                {
                    Log.Warn($"pull request list read failed for {slug}", ex);
                }
                if (!IsCurrent(gen)) return;
                if (read.Page is not { } page)
                {
                    PullRequestsError = ListFetchFailed(PullRequestsFetchFailed, read.Error);
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
        var merged = MergeRows(PullRequests, page.Items, p => p.Number, SamePullRequestRow);
        if (!ReferenceEquals(merged, PullRequests)) PullRequests = merged;
        if (keep is { } number) SelectedPullRequest = PullRequests.FirstOrDefault(p => p.Number == number);
        PullRequestsLoaded = true;
        PullRequestsHasMore = page.MayHaveMore;
        PullRequestsEmptyText = ListEmptyText(state, PullRequestsNoun, searching);
        PullRequestsFooterText =
            ListFooterText(page.Items.Count, page.MayHaveMore, state, PullRequestsNoun, PullRequestNoun, searching);
    }

    /// <summary>
    /// A failed list read, carrying what gh said about it. The search is never blamed: gh answers a
    /// query it cannot make sense of with an empty result rather than a failure, so a read that
    /// failed while a search was in force establishes nothing about that search — and a message
    /// pointing at the reader's query would send them after the wrong thing while the connection,
    /// the repository or the sign-in is what actually broke.
    /// </summary>
    internal static string ListFetchFailed(string baseMessage, string error) =>
        error.Length == 0 ? baseMessage : $"{baseMessage} The GitHub CLI reported: {error}";
}
