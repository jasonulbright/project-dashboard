using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Interactive Issues and Pull Requests surfaces: on-demand detail fetch plus the
/// mutating actions (comment, close/reopen, label, assign, create, merge, checkout,
/// review). Every mutation runs through the generation-owned busy gate so a slow gh
/// call started on one project can never write into the project switched to, and
/// surfaces its outcome through <see cref="GitHubStatusText"/>. A null detail fetch
/// is a failure (surfaced as an error line), never rendered as an empty success.
/// </summary>
public partial class ProjectDetailViewModel
{
    // ── Shared status line for GitHub actions (toast/status pattern) ────────────
    [ObservableProperty] private string _gitHubStatusText = "";

    // Labels defined on the repo, fetched once per project for the add/remove editor.
    [ObservableProperty] private ObservableCollection<string> _availableLabelNames = [];
    private bool _labelsLoaded;

    // ── Issues detail + compose ─────────────────────────────────────────────────
    [ObservableProperty] private GitHubIssue? _selectedIssue;
    [ObservableProperty] private IssueDetail? _issueDetail;
    [ObservableProperty] private bool _issueDetailLoading;
    [ObservableProperty] private string _issueDetailError = "";
    [ObservableProperty] private ObservableCollection<string> _issueLabels = [];

    [ObservableProperty] private bool _issueComposeVisible;
    [ObservableProperty] private string _newIssueTitle = "";
    [ObservableProperty] private string _newIssueBody = "";
    [ObservableProperty] private string _newIssueLabels = "";

    [ObservableProperty] private string _issueCommentDraft = "";
    [ObservableProperty] private string _issueAssignee = "";
    [ObservableProperty] private string? _selectedLabelToAdd;
    [ObservableProperty] private string? _selectedLabelToRemove;

    // ── Pull request detail + compose ───────────────────────────────────────────
    [ObservableProperty] private GitHubPullRequest? _selectedPullRequest;
    [ObservableProperty] private PullRequestDetail? _pullRequestDetail;
    [ObservableProperty] private bool _pullRequestDetailLoading;
    [ObservableProperty] private string _pullRequestDetailError = "";

    [ObservableProperty] private bool _pullRequestComposeVisible;
    [ObservableProperty] private string _newPrTitle = "";
    [ObservableProperty] private string _newPrBody = "";
    [ObservableProperty] private string _newPrBase = "";
    [ObservableProperty] private bool _newPrDraft;

    [ObservableProperty] private string _prCommentDraft = "";
    [ObservableProperty] private MergeStrategy _selectedMergeStrategy = MergeStrategy.Squash;
    [ObservableProperty] private bool _mergeDeleteBranch;
    [ObservableProperty] private string _reviewBody = "";

    /// <summary>Enum-only pickers; the exact gh token is derived, never typed.</summary>
    public static IReadOnlyList<MergeStrategy> MergeStrategies { get; } = Enum.GetValues<MergeStrategy>();

    private string Slug => Project?.GitHubSlug ?? "";

    /// <summary>Resets every interactive Issues/PR field so nothing leaks across a project switch.</summary>
    private void ResetGitHubState()
    {
        GitHubStatusText = "";
        AvailableLabelNames = [];
        _labelsLoaded = false;

        SelectedIssue = null;
        IssueDetail = null;
        IssueDetailLoading = false;
        IssueDetailError = "";
        IssueLabels = [];
        IssueComposeVisible = false;
        NewIssueTitle = "";
        NewIssueBody = "";
        NewIssueLabels = "";
        IssueCommentDraft = "";
        IssueAssignee = "";
        SelectedLabelToAdd = null;
        SelectedLabelToRemove = null;

        SelectedPullRequest = null;
        PullRequestDetail = null;
        PullRequestDetailLoading = false;
        PullRequestDetailError = "";
        PullRequestComposeVisible = false;
        NewPrTitle = "";
        NewPrBody = "";
        NewPrBase = "";
        NewPrDraft = false;
        PrCommentDraft = "";
        SelectedMergeStrategy = MergeStrategy.Squash;
        MergeDeleteBranch = false;
        ReviewBody = "";
    }

    // ── Issue detail load ───────────────────────────────────────────────────────

    partial void OnSelectedIssueChanged(GitHubIssue? value)
    {
        IssueDetail = null;
        IssueDetailError = "";
        IssueLabels = [];
        if (value is not null) _ = LoadIssueDetailAsync(value);
    }

    private async Task LoadIssueDetailAsync(GitHubIssue issue)
    {
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;
        IssueDetailLoading = true;
        IssueDetailError = "";
        try
        {
            var detail = await _gitHubService.GetIssueDetailAsync(slug, issue.Number);
            // Selection or project changed mid-await — drop this result.
            if (!IsCurrent(gen) || !ReferenceEquals(SelectedIssue, issue)) return;
            if (detail is null)
            {
                // Null = fetch failed; never render it as an empty issue.
                IssueDetailError = "Couldn't load this issue. Check that the GitHub CLI is installed and signed in.";
                return;
            }
            IssueDetail = detail;
            IssueLabels = new ObservableCollection<string>(SplitLabels(detail.Labels));
        }
        finally
        {
            if (IsCurrent(gen)) IssueDetailLoading = false;
        }
    }

    private async Task ReloadIssueDetailAsync()
    {
        if (SelectedIssue is not null) await LoadIssueDetailAsync(SelectedIssue);
    }

    private async Task ReloadIssueListAsync()
    {
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;
        var issues = await _gitHubService.GetIssuesAsync(slug, "open");
        if (!IsCurrent(gen)) return;
        Issues = new ObservableCollection<GitHubIssue>(issues);
        if (Project is not null) Project.Issues = issues;
    }

    [RelayCommand]
    private async Task RefreshIssues() => await ReloadIssueListAsync();

    private async Task EnsureLabelsLoadedAsync()
    {
        if (_labelsLoaded) return;
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;
        var labels = await _gitHubService.GetLabelsAsync(slug);
        if (!IsCurrent(gen) || labels is null) return; // null = fetch failed; retry next open
        AvailableLabelNames = new ObservableCollection<string>(labels.Select(l => l.Name));
        _labelsLoaded = true;
    }

    // ── Issue actions ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowNewIssue()
    {
        NewIssueTitle = "";
        NewIssueBody = "";
        NewIssueLabels = "";
        IssueComposeVisible = true;
        await EnsureLabelsLoadedAsync();
    }

    [RelayCommand]
    private void CancelNewIssue() => IssueComposeVisible = false;

    [RelayCommand]
    private async Task SubmitNewIssue()
    {
        var slug = Slug;
        var title = NewIssueTitle.Trim();
        if (slug.Length == 0 || title.Length == 0 || IsBusy)
        {
            if (title.Length == 0) GitHubStatusText = "Enter an issue title first.";
            return;
        }
        var body = NewIssueBody;
        var labels = SplitLabels(NewIssueLabels);
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.CreateIssueAsync(slug, title, body, labels), "Create issue");
        if (ok && IsCurrent(gen))
        {
            IssueComposeVisible = false;
            NewIssueTitle = "";
            NewIssueBody = "";
            NewIssueLabels = "";
            await ReloadIssueListAsync();
        }
    }

    [RelayCommand]
    private async Task CommentIssue()
    {
        var slug = Slug;
        var issue = SelectedIssue;
        var body = IssueCommentDraft.Trim();
        if (slug.Length == 0 || issue is null || body.Length == 0 || IsBusy) return;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.CommentIssueAsync(slug, issue.Number, body),
            $"Comment on #{issue.Number}");
        if (ok && IsCurrent(gen))
        {
            IssueCommentDraft = "";
            await ReloadIssueDetailAsync();
        }
    }

    [RelayCommand]
    private async Task CloseIssue()
    {
        var slug = Slug;
        var issue = SelectedIssue;
        if (slug.Length == 0 || issue is null || IsBusy) return;
        if (!await ConfirmAsync("Close issue?", $"Close issue #{issue.Number} — {issue.Title}?", "Close")) return;
        var ok = await RunGitHubOp(() => _gitHubService.CloseIssueAsync(slug, issue.Number), $"Close #{issue.Number}");
        if (ok)
        {
            await ReloadIssueDetailAsync();
            await ReloadIssueListAsync();
        }
    }

    [RelayCommand]
    private async Task ReopenIssue()
    {
        var slug = Slug;
        var issue = SelectedIssue;
        if (slug.Length == 0 || issue is null || IsBusy) return;
        var ok = await RunGitHubOp(() => _gitHubService.ReopenIssueAsync(slug, issue.Number), $"Reopen #{issue.Number}");
        if (ok)
        {
            await ReloadIssueDetailAsync();
            await ReloadIssueListAsync();
        }
    }

    [RelayCommand]
    private async Task AddIssueLabel()
    {
        var slug = Slug;
        var issue = SelectedIssue;
        var label = SelectedLabelToAdd;
        if (slug.Length == 0 || issue is null || string.IsNullOrWhiteSpace(label) || IsBusy) return;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.EditIssueLabelsAsync(slug, issue.Number, [label], []),
            $"Add label to #{issue.Number}");
        if (ok && IsCurrent(gen))
        {
            SelectedLabelToAdd = null;
            await ReloadIssueDetailAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveIssueLabel()
    {
        var slug = Slug;
        var issue = SelectedIssue;
        var label = SelectedLabelToRemove;
        if (slug.Length == 0 || issue is null || string.IsNullOrWhiteSpace(label) || IsBusy) return;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.EditIssueLabelsAsync(slug, issue.Number, [], [label]),
            $"Remove label from #{issue.Number}");
        if (ok && IsCurrent(gen))
        {
            SelectedLabelToRemove = null;
            await ReloadIssueDetailAsync();
        }
    }

    [RelayCommand]
    private async Task AssignIssue()
    {
        var slug = Slug;
        var issue = SelectedIssue;
        var assignee = IssueAssignee.Trim();
        if (slug.Length == 0 || issue is null || assignee.Length == 0 || IsBusy) return;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.AssignIssueAsync(slug, issue.Number, assignee),
            $"Assign #{issue.Number}");
        if (ok && IsCurrent(gen))
        {
            IssueAssignee = "";
            await ReloadIssueDetailAsync();
        }
    }

    // ── Pull request detail load ────────────────────────────────────────────────

    partial void OnSelectedPullRequestChanged(GitHubPullRequest? value)
    {
        PullRequestDetail = null;
        PullRequestDetailError = "";
        if (value is not null) _ = LoadPullRequestDetailAsync(value);
    }

    private async Task LoadPullRequestDetailAsync(GitHubPullRequest pr)
    {
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;
        PullRequestDetailLoading = true;
        PullRequestDetailError = "";
        try
        {
            var detail = await _gitHubService.GetPullRequestDetailAsync(slug, pr.Number);
            if (!IsCurrent(gen) || !ReferenceEquals(SelectedPullRequest, pr)) return;
            if (detail is null)
            {
                PullRequestDetailError = "Couldn't load this pull request. Check that the GitHub CLI is installed and signed in.";
                return;
            }
            PullRequestDetail = detail;
        }
        finally
        {
            if (IsCurrent(gen)) PullRequestDetailLoading = false;
        }
    }

    private async Task ReloadPullRequestDetailAsync()
    {
        if (SelectedPullRequest is not null) await LoadPullRequestDetailAsync(SelectedPullRequest);
    }

    // ── Pull request actions ────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowNewPr()
    {
        NewPrTitle = "";
        NewPrBody = "";
        NewPrBase = "";
        NewPrDraft = false;
        PullRequestComposeVisible = true;
    }

    [RelayCommand]
    private void CancelNewPr() => PullRequestComposeVisible = false;

    [RelayCommand]
    private async Task SubmitNewPr()
    {
        var repo = RepoPath;
        var title = NewPrTitle.Trim();
        if (repo.Length == 0 || title.Length == 0 || IsBusy)
        {
            if (title.Length == 0) GitHubStatusText = "Enter a pull request title first.";
            return;
        }
        var body = NewPrBody;
        var baseBranch = string.IsNullOrWhiteSpace(NewPrBase) ? null : NewPrBase.Trim();
        var draft = NewPrDraft;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.CreatePullRequestAsync(repo, title, body, baseBranch, draft),
            "Create pull request");
        if (ok && IsCurrent(gen))
        {
            PullRequestComposeVisible = false;
            NewPrTitle = "";
            NewPrBody = "";
            NewPrBase = "";
            NewPrDraft = false;
            await LoadPullRequests();
        }
    }

    [RelayCommand]
    private async Task CommentPr()
    {
        var slug = Slug;
        var pr = SelectedPullRequest;
        var body = PrCommentDraft.Trim();
        if (slug.Length == 0 || pr is null || body.Length == 0 || IsBusy) return;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.CommentPullRequestAsync(slug, pr.Number, body),
            $"Comment on #{pr.Number}");
        if (ok && IsCurrent(gen))
        {
            PrCommentDraft = "";
            await ReloadPullRequestDetailAsync();
        }
    }

    [RelayCommand]
    private async Task ClosePr()
    {
        var slug = Slug;
        var pr = SelectedPullRequest;
        if (slug.Length == 0 || pr is null || IsBusy) return;
        if (!await ConfirmAsync("Close pull request?", $"Close pull request #{pr.Number} — {pr.Title}?", "Close")) return;
        var ok = await RunGitHubOp(() => _gitHubService.ClosePullRequestAsync(slug, pr.Number), $"Close #{pr.Number}");
        if (ok)
        {
            await ReloadPullRequestDetailAsync();
            await LoadPullRequests();
        }
    }

    [RelayCommand]
    private async Task MergePr()
    {
        var slug = Slug;
        var pr = SelectedPullRequest;
        var detail = PullRequestDetail;
        if (slug.Length == 0 || pr is null || IsBusy) return;

        var strategy = SelectedMergeStrategy;
        var token = strategy.Token(); // enum → exact gh token; BuildMergeArgs can't see a bad value
        var branch = detail?.HeadRef ?? "";
        var branchNote = branch.Length > 0 ? $" ({branch})" : "";
        var deleteNote = MergeDeleteBranch ? "\n\nThe head branch will be deleted." : "";
        var confirmed = await ConfirmAsync("Merge pull request?",
            $"{strategy} pull request #{pr.Number}{branchNote} into the base branch?{deleteNote}\n\nThis pushes to the remote and cannot be undone here.",
            $"{strategy}");
        if (!confirmed) return;

        var deleteBranch = MergeDeleteBranch;
        var ok = await RunGitHubOp(() => _gitHubService.MergePullRequestAsync(slug, pr.Number, token, deleteBranch),
            $"Merge #{pr.Number}");
        if (ok)
        {
            await ReloadPullRequestDetailAsync();
            await LoadPullRequests();
        }
    }

    [RelayCommand]
    private async Task CheckoutPr()
    {
        var repo = RepoPath;
        var pr = SelectedPullRequest;
        if (repo.Length == 0 || pr is null || IsBusy) return;
        var ok = await RunGitHubOp(() => _gitHubService.CheckoutPullRequestAsync(repo, pr.Number),
            $"Checkout #{pr.Number}");
        if (ok) await SafeRefreshWorkingStateAsync();
    }

    [RelayCommand]
    private async Task MarkPrReady()
    {
        var slug = Slug;
        var pr = SelectedPullRequest;
        if (slug.Length == 0 || pr is null || IsBusy) return;
        var ok = await RunGitHubOp(() => _gitHubService.MarkPullRequestReadyAsync(slug, pr.Number),
            $"Mark #{pr.Number} ready");
        if (ok)
        {
            await ReloadPullRequestDetailAsync();
            await LoadPullRequests();
        }
    }

    [RelayCommand]
    private async Task ReviewPr(ReviewAction action)
    {
        var slug = Slug;
        var pr = SelectedPullRequest;
        if (slug.Length == 0 || pr is null || IsBusy) return;

        var token = action.Token(); // enum → exact gh token; BuildReviewArgs can't see a bad value
        var body = ReviewBody.Trim();
        // request-changes with no body is a gh error — surface it before spawning.
        if (action == ReviewAction.RequestChanges && body.Length == 0)
        {
            GitHubStatusText = "Request changes needs a comment explaining what to change.";
            return;
        }
        var gen = _generation;
        // A caller can't approve their own PR; that returns a failed ProcessResult,
        // surfaced as a normal failure toast rather than a crash.
        var ok = await RunGitHubOp(() => _gitHubService.ReviewPullRequestAsync(slug, pr.Number, token, body),
            $"Review #{pr.Number}");
        if (ok && IsCurrent(gen))
        {
            ReviewBody = "";
            await ReloadPullRequestDetailAsync();
        }
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────────

    /// <summary>Comma-separated label names → trimmed, non-empty list.</summary>
    internal static List<string> SplitLabels(string labels) =>
        labels.Split(',').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// Runs a mutating gh op through the same generation-owned busy gate the git ops
    /// use: only the generation that acquired IsBusy releases it, so a stale op that
    /// finishes after a project switch neither reopens the new project's gate nor
    /// writes a status onto it. Never throws — the service returns a failed
    /// ProcessResult (including GitHub's own-PR self-approve refusal), toasted here.
    /// </summary>
    private async Task<bool> RunGitHubOp(Func<Task<ProcessResult>> op, string label)
    {
        if (IsBusy) return false;
        var gen = _generation;
        IsBusy = true;
        GitHubStatusText = $"{label}…";
        try
        {
            var result = await op();
            if (!IsCurrent(gen)) return false; // switched projects mid-op — drop the UI write
            GitHubStatusText = result.Success ? $"{label} done." : $"{label} failed: {result.FirstError}";
            return result.Success;
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} failed", ex);
            if (IsCurrent(gen)) GitHubStatusText = $"{label} failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (IsCurrent(gen)) IsBusy = false;
        }
    }
}
