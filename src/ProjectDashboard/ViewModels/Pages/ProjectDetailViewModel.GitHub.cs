using System.Diagnostics.CodeAnalysis;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

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
    private Task<List<Label>?>? _labelFetch;

    /// <summary>
    /// Why the Issues list is not an answer, or "" when it is one. An empty list under an
    /// empty notice claims the repository has no open issues; a repository that was never
    /// queried — no GitHub remote — has established nothing of the sort. Carried by the
    /// Pull Requests surface too, on the same terms.
    /// </summary>
    [ObservableProperty] private string _issuesError = "";
    [ObservableProperty] private string _pullRequestsError = "";

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

    /// <summary>Shown instead of a silent return when a command needs a slug and there is none.</summary>
    private const string NoRemoteStatus = "This project has no GitHub remote.";

    /// <summary>
    /// Whether a command that needs a repository can proceed, writing why it cannot when
    /// it cannot. An absent remote is a state the reader can see and act on, so it is
    /// never a silent return. The busy gate is deliberately not folded in: the operation
    /// holding it already names itself on this same status line, and overwriting that
    /// with a refusal would hide the operation actually running. That silence only holds
    /// while every command tests the busy gate before it reaches any check that writes —
    /// this one included.
    /// </summary>
    private bool HasGitHubRemote(string slug)
    {
        if (slug.Length > 0) return true;
        GitHubStatusText = NoRemoteStatus;
        return false;
    }

    /// <summary>
    /// <see cref="HasGitHubRemote"/> plus the row the command acts on.
    /// <paramref name="selectionNoun"/> completes "Select … first." and names the surface
    /// the reader has to pick from, since one status line serves every GitHub command.
    /// </summary>
    private bool HasGitHubTarget<T>(string slug, [NotNullWhen(true)] T? selection, string selectionNoun)
        where T : class
    {
        if (!HasGitHubRemote(slug)) return false;
        if (selection is not null) return true;
        GitHubStatusText = $"Select {selectionNoun} first.";
        return false;
    }

    /// <summary>Resets every interactive Issues/PR field so nothing leaks across a project switch.</summary>
    private void ResetGitHubState()
    {
        GitHubStatusText = "";
        IssuesError = "";
        PullRequestsError = "";
        AvailableLabelNames = [];
        _labelsLoaded = false;
        // The in-flight fetch belongs to the project being left; the new one refetches.
        _labelFetch = null;

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

        ResetGitHubListDepth();
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
            var detail = await FetchIssueDetailAsync(slug, issue.Number);
            // Selection or project changed mid-await — drop this result.
            if (!IsCurrent(gen) || !ReferenceEquals(SelectedIssue, issue)) return;
            if (detail is null)
            {
                // Null = fetch failed; never render it as an empty issue.
                IssueDetailError = "Couldn't load this issue. Check that the GitHub CLI is installed and signed in.";
                return;
            }
            IssueDetail = detail;
            // From the API list, not a re-split of the joined display string: a label
            // name containing a comma would come back as two names that match nothing.
            IssueLabels = new ObservableCollection<string>(detail.LabelNames);
        }
        finally
        {
            if (IsCurrent(gen)) IssueDetailLoading = false;
        }

        // Reached only once a detail is on screen. The pane's label picker binds
        // AvailableLabelNames and a project switch clears them, so without this the
        // picker stays empty and Add label has nothing to send. Runs after the
        // detail spinner is released — the body does not wait on the label list.
        await EnsureLabelsLoadedAsync();
    }

    private async Task ReloadIssueDetailAsync()
    {
        if (SelectedIssue is not null) await LoadIssueDetailAsync(SelectedIssue);
    }

    /// <summary>
    /// Re-reads the issue list at the depth and facets it is already showing, so a refresh — and
    /// the reload every issue mutation ends with — does not undo the window the reader paged into.
    /// </summary>
    private Task ReloadIssueListAsync()
    {
        IssuesPageLoad = LoadIssuePageAsync(_issuesWindowSize);
        return IssuesPageLoad;
    }

    [RelayCommand]
    private async Task RefreshIssues() => await ReloadIssueListAsync();

    private async Task EnsureLabelsLoadedAsync()
    {
        if (_labelsLoaded) return;
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;
        // _labelsLoaded is only set after the await, so every issue selected before the
        // first fetch returns would start another gh label list for the same repo.
        // Joining the in-flight one keeps it at a single process per project.
        var fetch = _labelFetch ??= FetchLabelsAsync(slug);
        var labels = await fetch;
        if (labels is null)
        {
            // null = fetch failed. Dropping the task lets the next open retry rather
            // than caching the failure for the life of the project.
            if (ReferenceEquals(_labelFetch, fetch)) _labelFetch = null;
            return;
        }
        if (!IsCurrent(gen)) return;
        AvailableLabelNames = new ObservableCollection<string>(labels.Select(l => l.Name));
        _labelsLoaded = true;
    }

    /// <summary>
    /// The two remote reads the issue detail pane depends on. Both go through the
    /// service in the app; as overridable members the pane's state transitions can be
    /// driven without spawning gh.
    /// </summary>
    internal virtual Task<IssueDetail?> FetchIssueDetailAsync(string slug, int number)
        => _gitHubService.GetIssueDetailAsync(slug, number);

    internal virtual Task<List<Label>?> FetchLabelsAsync(string slug)
        => _gitHubService.GetLabelsAsync(slug);

    /// <summary>
    /// The two list reads the Issues and Pull Requests tabs stand on. Null is a failed read,
    /// never rendered as an empty repository, and the page carries whether the window it filled
    /// may have rows behind it. Overridable on the same terms as the other remote reads, so both
    /// outcomes and every facet are reachable without gh.
    /// </summary>
    internal virtual Task<GitHubService.ListRead<GitHubService.IssuePage>> FetchIssuePageAsync(
        string slug, GitHubService.GitHubListQuery query)
        => _gitHubService.GetIssuePageAsync(slug, query);

    internal virtual Task<GitHubService.ListRead<GitHubService.PullRequestPage>> FetchPullRequestPageAsync(
        string slug, GitHubService.GitHubListQuery query)
        => _gitHubService.GetPullRequestPageAsync(slug, query);

    /// <summary>Shown wherever a list read failed, so a failure never reads as an empty repository.</summary>
    private const string IssuesFetchFailed =
        "Couldn't load issues. Check that the GitHub CLI is installed and signed in.";

    private const string PullRequestsFetchFailed =
        "Couldn't load pull requests. Check that the GitHub CLI is installed and signed in.";

    // ── Issue actions ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowNewIssue()
    {
        if (!HasGitHubRemote(Slug)) return;
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
        if (IsBusy) return;
        var slug = Slug;
        var title = NewIssueTitle.Trim();
        if (title.Length == 0)
        {
            GitHubStatusText = "Enter an issue title first.";
            return;
        }
        if (!HasGitHubRemote(slug)) return;
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
        if (IsBusy) return;
        var slug = Slug;
        var issue = SelectedIssue;
        var body = IssueCommentDraft.Trim();
        if (!HasGitHubTarget(slug, issue, "an issue")) return;
        if (body.Length == 0)
        {
            GitHubStatusText = "Enter a comment first.";
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var issue = SelectedIssue;
        if (!HasGitHubTarget(slug, issue, "an issue")) return;
        // Held across the dialog: the captured slug keeps the write on the right repo,
        // but a project switched to while the dialog is open must not inherit this
        // command's busy gate or its status line.
        var gen = _generation;
        if (!await ConfirmAsync("Close issue?", $"Close issue #{issue.Number} — {issue.Title}?", "Close")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Close issue");
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var issue = SelectedIssue;
        if (!HasGitHubTarget(slug, issue, "an issue")) return;
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
        if (IsBusy) return;
        var slug = Slug;
        var issue = SelectedIssue;
        var label = SelectedLabelToAdd;
        if (!HasGitHubTarget(slug, issue, "an issue")) return;
        if (string.IsNullOrWhiteSpace(label))
        {
            GitHubStatusText = "Pick a label to add first.";
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var issue = SelectedIssue;
        var label = SelectedLabelToRemove;
        if (!HasGitHubTarget(slug, issue, "an issue")) return;
        if (string.IsNullOrWhiteSpace(label))
        {
            GitHubStatusText = "Pick a label to remove first.";
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var issue = SelectedIssue;
        var assignee = IssueAssignee.Trim();
        if (!HasGitHubTarget(slug, issue, "an issue")) return;
        if (assignee.Length == 0)
        {
            GitHubStatusText = "Enter a username to assign first.";
            return;
        }
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
    private async Task ShowNewPr()
    {
        if (!HasGitHubRemote(Slug)) return;
        NewPrTitle = "";
        NewPrBody = "";
        NewPrBase = "";
        NewPrDraft = false;
        PullRequestComposeVisible = true;
        // The form names the head branch, and submit pins that branch — refresh it so
        // a checkout made since the page loaded is what the reader sees.
        await SafeRefreshWorkingStateAsync();
    }

    /// <summary>
    /// The branch the compose form displays, or "" on a detached HEAD (no branch to
    /// open a pull request from). <see cref="BranchLabel"/> is this value's display form.
    /// </summary>
    private string ComposeHeadBranch => WorkingState is { Detached: false } state ? state.Branch : "";

    [RelayCommand]
    private void CancelNewPr() => PullRequestComposeVisible = false;

    [RelayCommand]
    private async Task SubmitNewPr()
    {
        if (IsBusy) return;
        var repo = RepoPath;
        var title = NewPrTitle.Trim();
        if (title.Length == 0)
        {
            GitHubStatusText = "Enter a pull request title first.";
            return;
        }
        if (!HasGitHubRemote(Slug)) return;
        if (repo.Length == 0) return;
        // Without an explicit head, gh reads the checkout at spawn time — which the
        // app's own Open in Terminal makes easy to change while the form is open.
        var head = ComposeHeadBranch;
        if (head.Length == 0)
        {
            GitHubStatusText = "Check out a branch before opening a pull request.";
            return;
        }
        var body = NewPrBody;
        var baseBranch = string.IsNullOrWhiteSpace(NewPrBase) ? null : NewPrBase.Trim();
        var draft = NewPrDraft;
        var gen = _generation;
        var ok = await RunGitHubOp(
            () => _gitHubService.CreatePullRequestAsync(repo, title, body, baseBranch, draft, head),
            $"Create pull request from {head}");
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
        if (IsBusy) return;
        var slug = Slug;
        var pr = SelectedPullRequest;
        var body = PrCommentDraft.Trim();
        if (!HasGitHubTarget(slug, pr, "a pull request")) return;
        if (body.Length == 0)
        {
            GitHubStatusText = "Enter a comment first.";
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var pr = SelectedPullRequest;
        if (!HasGitHubTarget(slug, pr, "a pull request")) return;
        var gen = _generation;
        if (!await ConfirmAsync("Close pull request?", $"Close pull request #{pr.Number} — {pr.Title}?", "Close")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Close pull request");
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var pr = SelectedPullRequest;
        var detail = PullRequestDetail;
        if (!HasGitHubTarget(slug, pr, "a pull request")) return;

        var strategy = SelectedMergeStrategy;
        var token = strategy.Token(); // enum → exact gh token; BuildMergeArgs can't see a bad value
        // Read once: the confirm text and the command must describe the same merge even
        // if the checkbox is toggled while the dialog is open.
        var deleteBranch = MergeDeleteBranch;
        var branch = detail?.HeadRef ?? "";
        var branchNote = branch.Length > 0 ? $" ({branch})" : "";
        var deleteNote = deleteBranch ? "\n\nThe head branch will be deleted." : "";
        var gen = _generation;
        var confirmed = await ConfirmAsync("Merge pull request?",
            $"{strategy} pull request #{pr.Number}{branchNote} into the base branch?{deleteNote}\n\nThis pushes to the remote and cannot be undone here.",
            $"{strategy}");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Merge");
            return;
        }

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
        if (IsBusy) return;
        var repo = RepoPath;
        var pr = SelectedPullRequest;
        // Checkout runs in the clone, so the repository path is what it needs; the slug
        // still gates it, because a clone with no GitHub remote has no pull request.
        if (!HasGitHubTarget(Slug, pr, "a pull request") || repo.Length == 0) return;

        var head = PullRequestDetail?.HeadRef ?? "";
        var target = head.Length > 0 ? head : "the pull request's head branch";
        var gen = _generation;
        if (!await ConfirmAsync("Check out pull request?",
                $"Switch this working copy to {target} for pull request #{pr.Number}?\n\n" +
                $"The current branch ({BranchLabel}) is left as it is.",
                "Check out")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Checkout");
            return;
        }

        var ok = await RunGitHubRepoOp(() => _gitHubService.CheckoutPullRequestAsync(repo, pr.Number),
            $"Checkout #{pr.Number} ({target})", repo);
        if (ok) await SafeRefreshWorkingStateAsync();
    }

    [RelayCommand]
    private async Task MarkPrReady()
    {
        if (IsBusy) return;
        var slug = Slug;
        var pr = SelectedPullRequest;
        if (!HasGitHubTarget(slug, pr, "a pull request")) return;
        var gen = _generation;
        if (!await ConfirmAsync("Mark ready for review?",
                $"Mark pull request #{pr.Number} — {pr.Title} — ready for review?\n\n" +
                "This starts the required checks and notifies the code owners. There is no convert-to-draft here.",
                "Mark ready")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Mark ready");
            return;
        }
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
        if (IsBusy) return;
        var slug = Slug;
        var pr = SelectedPullRequest;
        if (!HasGitHubTarget(slug, pr, "a pull request")) return;

        var token = action.Token(); // enum → exact gh token; BuildReviewArgs can't see a bad value
        var body = ReviewBody.Trim();
        // request-changes with no body is a gh error — surface it before spawning.
        if (action == ReviewAction.RequestChanges && body.Length == 0)
        {
            GitHubStatusText = "Request changes needs a comment explaining what to change.";
            return;
        }
        var gen = _generation;
        // Approve and request-changes are public, permanently attributed verdicts with
        // no un-review here, and one body box feeds all three — confirm names both.
        if (action is ReviewAction.Approve or ReviewAction.RequestChanges
            && !await ConfirmAsync("Submit review?", ReviewConfirmMessage(action, pr.Number, body),
                ReviewVerdictLabel(action))) return;
        // Only the confirmed verdicts await anything before this, so only they can
        // arrive here on a moved generation.
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Review");
            return;
        }
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

    /// <summary>Confirm-button wording for a review verdict.</summary>
    internal static string ReviewVerdictLabel(ReviewAction action) => action switch
    {
        ReviewAction.Approve => "Approve",
        ReviewAction.RequestChanges => "Request changes",
        _ => "Comment",
    };

    /// <summary>
    /// Names the verdict and the body it will carry. One body box serves all three
    /// verdicts, so text typed for a comment must not reach GitHub as an approval
    /// without the reader having seen both together.
    /// </summary>
    internal static string ReviewConfirmMessage(ReviewAction action, int number, string body)
    {
        var verdict = action switch
        {
            ReviewAction.Approve => $"Approve pull request #{number}",
            ReviewAction.RequestChanges => $"Request changes on pull request #{number}",
            _ => $"Comment on pull request #{number}",
        };
        var bodyNote = body.Length == 0
            ? "The review comment box is empty; the verdict is submitted on its own."
            : $"It will carry this comment: “{Excerpt(body)}”";
        return $"{verdict}?\n\n{bodyNote}\n\n" +
               "The review is posted publicly under your account and cannot be withdrawn from here.";
    }

    /// <summary>First line, truncated: the confirm names the body without becoming a wall of text.</summary>
    private static string Excerpt(string text)
    {
        var firstLine = text.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        var multiline = firstLine.Length < text.Trim().Length;
        return firstLine.Length > 100 ? firstLine[..100] + "…" : multiline ? firstLine + " …" : firstLine;
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
        => await RunGitHubOpResult(op, label) is { Success: true };

    /// <summary>
    /// <see cref="RunGitHubOp"/> for a gh command that writes this repository rather than only
    /// its GitHub side. The page's own busy flag ends at the page; the repository lease is the
    /// gate that holds across them, and a checkout moving HEAD and the working tree inside a
    /// rewrite's swap is what it exists to stop. Remote-only gh mutations stay on the light gate,
    /// because nothing they touch is on disk.
    /// </summary>
    private async Task<bool> RunGitHubRepoOp(Func<Task<ProcessResult>> op, string label, string repo)
        => await RunGitHubRepoOpResult(op, label, repo) is { Success: true };

    /// <summary>
    /// <see cref="RunGitHubRepoOp"/> for callers that must read the failure itself. Null means no
    /// result belongs to this caller: the lease was held, the page was busy, the op threw, or the
    /// project changed while it ran.
    /// </summary>
    private async Task<ProcessResult?> RunGitHubRepoOpResult(Func<Task<ProcessResult>> op, string label, string repo)
    {
        if (IsBusy || repo.Length == 0) return null;
        if (!_busyRegistry.TryAcquire(repo, out var lease))
        {
            GitHubStatusText = $"{label} refused: another operation is running on this repository.";
            // A button that did nothing is exactly what a history has to explain, so the
            // invisible gate is recorded rather than returned silently.
            RecordGitHubOp(repo, label, OperationOutcome.Refused, GitHubStatusText,
                DateTimeOffset.UtcNow, _generation);
            return null;
        }
        using (lease)
            return await RunGitHubOpResult(op, label, repo);
    }

    /// <summary>
    /// One ledger row for a gh mutation, under the repository it acted on. A row needs a
    /// repository to be keyed under, so a caller with no local clone records nothing rather than
    /// filing the operation against an empty path.
    /// </summary>
    private void RecordGitHubOp(string repo, string label, OperationOutcome outcome, string detail,
        DateTimeOffset started, int gen)
    {
        if (repo.Length == 0) return;
        var written = _history.Append(OperationRecord.For(
            repo, OperationCategory.GitHub, label, outcome, detail, started));
        _lastOperationRecordId = written.Id;
        var failed = outcome is OperationOutcome.Failed or OperationOutcome.Unknown;
        if (failed) _failedOperationRecordId = written.Id;
        // Raised and lowered by every outcome: an affordance left standing beside a later success
        // would open the history at a failure that has since been dealt with.
        if (IsCurrent(gen)) OperationHistoryHintVisible = failed;
    }

    /// <summary>
    /// <see cref="RunGitHubOp"/> for callers that must read the failure itself — the
    /// missing-scope refusal a repo delete has to tell apart from any other failure.
    /// Null means no result belongs to this caller: the gate was already held, the op
    /// threw, or the project changed while it ran.
    /// </summary>
    /// <param name="recordRepo">
    /// The repository a ledger row for this op belongs under, or "" to record nothing. Repo-admin
    /// and repo-touching gh mutations are recorded; the read-adjacent issue and pull-request
    /// mutations are not, because the surfaces they change are re-read in front of the reader.
    /// </param>
    private async Task<ProcessResult?> RunGitHubOpResult(Func<Task<ProcessResult>> op, string label,
        string recordRepo = "")
    {
        if (IsBusy) return null;
        var gen = _generation;
        var started = DateTimeOffset.UtcNow;
        var holder = new object();
        IsBusy = true;
        _busyGateHolder = holder;
        GitHubStatusText = $"{label}…";
        try
        {
            var result = await op();
            RecordGitHubOp(recordRepo, label,
                result.Success ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                result.Success ? "" : result.FirstError, started, gen);
            if (!IsCurrent(gen)) return null; // switched projects mid-op — drop the UI write
            GitHubStatusText = result.Success ? $"{label} done." : $"{label} failed: {result.FirstError}";
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} failed", ex);
            RecordGitHubOp(recordRepo, label, OperationOutcome.Failed, ex.Message, started, gen);
            if (IsCurrent(gen)) GitHubStatusText = $"{label} failed: {ex.Message}";
            return null;
        }
        finally
        {
            if (ReferenceEquals(_busyGateHolder, holder))
            {
                _busyGateHolder = null;
                if (IsCurrent(gen)) IsBusy = false;
            }
        }
    }
}
