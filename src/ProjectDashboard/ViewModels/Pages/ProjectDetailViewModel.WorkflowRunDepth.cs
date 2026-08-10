using System.ComponentModel;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Depth and facets for the Actions tab's run list.
///
/// <c>gh run list</c> exposes no cursor, so depth is reached the way the Issues and Pull Requests
/// lists reach it: re-ask for a larger window and merge the result over the rows already on
/// screen. Workflow, branch and status are gh's to apply — a facet applied to a loaded window
/// would answer from the runs that window happened to hold rather than from the repository.
///
/// The window is a claim about what was read, never about how much history the repository has.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Runs the first read of the Actions tab asks for.</summary>
    internal const int WorkflowRunWindow = 30;

    /// <summary>Runs added to the window per "load more" click.</summary>
    internal const int WorkflowRunPageSize = 30;

    /// <summary>The load-more button's own label; the count and the window come from one place.</summary>
    public static string WorkflowRunLoadMoreLabel => $"Load {WorkflowRunPageSize} more";

    /// <summary>Enum-only picker; the exact gh token is derived, never typed.</summary>
    public static IReadOnlyList<WorkflowRunStatus> WorkflowRunStatuses { get; } =
        Enum.GetValues<WorkflowRunStatus>();

    private int _workflowRunsWindowSize = WorkflowRunWindow;

    /// <summary>Held while a project switch rewrites the facets, on the same terms as the lists.</summary>
    private bool _workflowRunFacetsQuiet;

    /// <summary>A facet changed while the read was in flight; it is answered when that read lands.</summary>
    private bool _workflowRunFacetsPending;

    [ObservableProperty] private bool _workflowRunsHasMore;
    [ObservableProperty] private bool _workflowRunsPaging;
    [ObservableProperty] private string _workflowRunsFooterText = "";
    [ObservableProperty] private string _workflowRunsEmptyText = NoRunsText;
    [ObservableProperty] private string _workflowRunsBranchText = "";
    [ObservableProperty] private WorkflowRunStatus _selectedWorkflowRunStatus = WorkflowRunStatus.Any;

    /// <summary>The workflows the loaded runs name, behind the row that filters to none.</summary>
    [ObservableProperty] private ObservableCollection<WorkflowChoice> _workflowChoices = [WorkflowChoice.Any];

    [ObservableProperty] private WorkflowChoice _selectedWorkflow = WorkflowChoice.Any;

    /// <summary>
    /// Says where the workflow picker's rows came from, or "" once the loaded runs are the whole
    /// history. The picker is built from the runs on screen, so a workflow whose last run sits
    /// behind the window is missing from it — and a picker silently missing a workflow reads as a
    /// workflow the repository does not define.
    /// </summary>
    [ObservableProperty] private string _workflowFilterNotice = "";

    internal const string WorkflowsFromLoadedRuns =
        "The workflow filter lists the workflows named by the runs loaded so far; " +
        "a workflow whose runs are all older than these is not among them.";

    internal const string NoRunsText = "No workflow runs.";

    /// <summary>
    /// The run read this command started and did not await. Held so a caller — and a headless
    /// test — can wait for the page instead of polling the properties it writes.
    /// </summary>
    internal Task WorkflowRunsPageLoad { get; private set; } = Task.CompletedTask;

    private bool CanLoadMoreWorkflowRuns() => !WorkflowRunsPaging && WorkflowRunsHasMore && Slug.Length > 0;

    private void HandleWorkflowRunDepthPropertyChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkflowRunsPaging):
            case nameof(WorkflowRunsHasMore):
            case nameof(Project):
                LoadMoreWorkflowRunsCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>
    /// Returns the run list to its first window with the default facets. Both belong to the
    /// repository they were chosen for, so neither survives a project switch.
    /// </summary>
    private void ResetWorkflowRunDepth()
    {
        _workflowRunFacetsQuiet = true;
        try
        {
            _workflowRunsWindowSize = WorkflowRunWindow;
            _workflowRunFacetsPending = false;
            WorkflowRunsHasMore = false;
            WorkflowRunsPaging = false;
            WorkflowRunsFooterText = "";
            WorkflowRunsEmptyText = NoRunsText;
            WorkflowRunsBranchText = "";
            SelectedWorkflowRunStatus = WorkflowRunStatus.Any;
            WorkflowChoices = [WorkflowChoice.Any];
            SelectedWorkflow = WorkflowChoice.Any;
            WorkflowFilterNotice = "";
        }
        finally
        {
            _workflowRunFacetsQuiet = false;
        }
    }

    // ── The sentences the surface says about itself ─────────────────────────────

    /// <summary>
    /// The facets a read carried, spelled for a sentence that names them. Every one is taken from
    /// the query that produced the page rather than from the pickers, which the reader may have
    /// moved on since.
    /// </summary>
    internal static string WorkflowRunScope(GitHubService.WorkflowRunQuery query)
    {
        var scope = "";
        if (query.Workflow is { Length: > 0 } workflow) scope += $" of “{workflow}”";
        if (query.Branch is { Length: > 0 } branch) scope += $" on {branch}";
        var status = GitHubActionTokens.ParseRunStatus(query.Status);
        if (status != WorkflowRunStatus.Any) scope += $" with status {status.ToString().ToLowerInvariant()}";
        return scope;
    }

    /// <summary>
    /// The line an empty run list carries. It names the facets that produced the emptiness: a
    /// repository whose failed runs are none and one with no runs at all are different answers.
    /// </summary>
    internal static string WorkflowRunsEmptyTextFor(GitHubService.WorkflowRunQuery query)
    {
        var scope = WorkflowRunScope(query);
        return scope.Length == 0 ? NoRunsText : $"No runs{scope}.";
    }

    /// <summary>
    /// What the run list can honestly say about its own depth, on the terms
    /// <see cref="ListFooterText"/> sets: a window that came back full establishes only that more
    /// may be behind it, and an empty list says nothing here because the empty line already has.
    /// </summary>
    internal static string WorkflowRunsFooterTextFor(int shown, bool mayHaveMore,
        GitHubService.WorkflowRunQuery query)
    {
        if (shown == 0) return "";
        var scope = $"{(shown == 1 ? "run" : "runs")}{WorkflowRunScope(query)}";
        return mayHaveMore
            ? $"Showing the first {shown} {scope} — there may be more."
            : $"All {shown} {scope} shown.";
    }

    private GitHubService.WorkflowRunQuery WorkflowRunsQuery(int windowSize) =>
        new(SelectedWorkflow.Name, NullIfBlank(WorkflowRunsBranchText), SelectedWorkflowRunStatus.Token(),
            windowSize);

    // ── Facet changes ───────────────────────────────────────────────────────────

    partial void OnSelectedWorkflowChanged(WorkflowChoice value)
    {
        if (_workflowRunFacetsQuiet) return;
        WorkflowRunsPageLoad = ApplyWorkflowRunFiltersAsync();
    }

    partial void OnSelectedWorkflowRunStatusChanged(WorkflowRunStatus value)
    {
        if (_workflowRunFacetsQuiet) return;
        WorkflowRunsPageLoad = ApplyWorkflowRunFiltersAsync();
    }

    /// <summary>
    /// Applies the facets from the first window. A changed facet is a different question, so the
    /// depth paged into under the previous one is not carried over to it.
    /// </summary>
    [RelayCommand]
    private Task ApplyWorkflowRunFilters()
    {
        WorkflowRunsPageLoad = ApplyWorkflowRunFiltersAsync();
        return WorkflowRunsPageLoad;
    }

    /// <summary>
    /// The wait for the facets to be in force. A read already in flight absorbs them and answers
    /// them before it returns, so that read — not a call that only queued them — is what a caller
    /// waits on.
    /// </summary>
    private Task ApplyWorkflowRunFiltersAsync()
    {
        var inFlight = WorkflowRunsPaging ? WorkflowRunsPageLoad : null;
        var read = LoadWorkflowRunPageAsync(WorkflowRunWindow, facetChange: true);
        return inFlight ?? read;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreWorkflowRuns))]
    private Task LoadMoreWorkflowRuns()
    {
        WorkflowRunsPageLoad = LoadWorkflowRunPageAsync(_workflowRunsWindowSize + WorkflowRunPageSize);
        return WorkflowRunsPageLoad;
    }

    /// <summary>Whether two reads of one row would draw and announce the same thing.</summary>
    private static bool SameWorkflowRunRow(WorkflowRun a, WorkflowRun b) =>
        a.Id == b.Id && a.Name == b.Name && a.DisplayTitle == b.DisplayTitle && a.Branch == b.Branch &&
        a.Status == b.Status && a.Conclusion == b.Conclusion && a.UpdatedAt == b.UpdatedAt;

    /// <summary>
    /// Reads the run list at <paramref name="windowSize"/> rows and writes it onto the tab, on the
    /// same terms as <see cref="LoadIssuePageAsync"/>: a failed read leaves the rows standing and
    /// sets the error line, a repeated click while a read is in flight is dropped rather than
    /// spawning a second gh, and a facet changed mid-read is answered from the first window as
    /// soon as that read is done.
    /// </summary>
    private async Task LoadWorkflowRunPageAsync(int windowSize, bool facetChange = false)
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            WorkflowRunsError = NoRemoteStatus;
            return;
        }
        if (WorkflowRunsPaging)
        {
            if (facetChange) _workflowRunFacetsPending = true;
            return;
        }

        var gen = _generation;
        var window = windowSize;
        WorkflowRunsPaging = true;
        try
        {
            do
            {
                _workflowRunFacetsPending = false;
                var query = WorkflowRunsQuery(window);
                var read = new GitHubService.ListRead<GitHubService.WorkflowRunPage>(null, "");
                try
                {
                    read = await FetchWorkflowRunPageAsync(slug, query);
                }
                catch (Exception ex)
                {
                    // A read that threw and a read that answered null are the same answer to the
                    // reader: nothing was established, so nothing on screen changes but the line.
                    Log.Warn($"workflow run list read failed for {slug}", ex);
                }
                if (!IsCurrent(gen)) return;
                if (read.Page is not { } page)
                {
                    WorkflowRunsError = ListFetchFailed(WorkflowRunsFetchFailed, read.Error);
                }
                else
                {
                    WorkflowRunsError = "";
                    _workflowRunsWindowSize = window;
                    ApplyWorkflowRunPage(page, query);
                }
                window = WorkflowRunWindow;
            } while (_workflowRunFacetsPending && IsCurrent(gen));
        }
        finally
        {
            if (IsCurrent(gen)) WorkflowRunsPaging = false;
        }
    }

    /// <summary>
    /// Writes a page onto the Actions tab. Every line describing it is derived from the query that
    /// produced it rather than from the pickers, so rows read under one facet are never captioned
    /// with another.
    /// </summary>
    private void ApplyWorkflowRunPage(GitHubService.WorkflowRunPage page,
        GitHubService.WorkflowRunQuery query)
    {
        // Rebuilt rows are new instances, and a selection held by reference would blank the jobs
        // pane on every read.
        var keepId = SelectedWorkflowRun?.Id;
        var merged = MergeRows(WorkflowRuns, page.Items, r => r.Id, SameWorkflowRunRow);
        if (!ReferenceEquals(merged, WorkflowRuns)) WorkflowRuns = merged;
        if (keepId is { } id) SelectedWorkflowRun = WorkflowRuns.FirstOrDefault(r => r.Id == id);
        WorkflowRunsLoaded = true;
        WorkflowRunsHasMore = page.MayHaveMore;
        WorkflowRunsEmptyText = WorkflowRunsEmptyTextFor(query);
        WorkflowRunsFooterText = WorkflowRunsFooterTextFor(page.Items.Count, page.MayHaveMore, query);
        ApplyWorkflowChoices(page);
    }

    /// <summary>
    /// Rebuilds the workflow picker from the runs on screen, keeping the selection when the
    /// workflow it names is still among them. A read filtered to one workflow names only that one,
    /// so the rows it produced are not allowed to narrow the picker that produced them.
    /// </summary>
    private void ApplyWorkflowChoices(GitHubService.WorkflowRunPage page)
    {
        WorkflowFilterNotice = page.MayHaveMore ? WorkflowsFromLoadedRuns : "";
        var keep = SelectedWorkflow.Name;
        var names = page.Items
            .Select(r => r.Name)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keep is { } selected && !names.Contains(selected, StringComparer.Ordinal)) names.Add(selected);

        // Rewriting the rows replaces the selected one with an equal-but-different instance; the
        // write is quiet so a picker that gained no facet does not reread the list.
        _workflowRunFacetsQuiet = true;
        try
        {
            WorkflowChoices = [WorkflowChoice.Any, .. names.Select(n => new WorkflowChoice(n))];
            SelectedWorkflow = WorkflowChoices.FirstOrDefault(c => c.Name == keep) ?? WorkflowChoice.Any;
        }
        finally
        {
            _workflowRunFacetsQuiet = false;
        }
    }

    private const string WorkflowRunsFetchFailed =
        "Couldn't load workflow runs. Check that the GitHub CLI is installed and signed in.";
}
