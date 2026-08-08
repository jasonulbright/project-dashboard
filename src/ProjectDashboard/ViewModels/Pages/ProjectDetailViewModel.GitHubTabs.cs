using System.Diagnostics;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The Actions, Releases and Repo surfaces: workflow runs and their jobs, releases and
/// their assets, repository settings, this repository's unread notifications, and the
/// danger zone. Every fetch is nullable — null is a failed fetch surfaced as an error
/// line, never rendered as an empty list — and every mutation runs through the same
/// generation-owned busy gate as the Issues/PR surface, so a slow gh call started on
/// one project can never write into the project switched to. Outward-facing changes
/// (visibility, repository delete) take a typed repository-name confirmation.
/// </summary>
public partial class ProjectDetailViewModel
{
    // ── Actions tab ─────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<WorkflowRun> _workflowRuns = [];
    [ObservableProperty] private bool _workflowRunsLoaded;
    [ObservableProperty] private string _workflowRunsError = "";
    [ObservableProperty] private WorkflowRun? _selectedWorkflowRun;
    [ObservableProperty] private ObservableCollection<WorkflowJob> _workflowJobs = [];
    [ObservableProperty] private bool _workflowJobsLoading;
    [ObservableProperty] private string _workflowJobsError = "";
    [ObservableProperty] private bool _rerunFailedJobsOnly;

    // ── Releases tab ────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<Release> _releases = [];
    [ObservableProperty] private bool _releasesLoaded;
    [ObservableProperty] private string _releasesError = "";
    [ObservableProperty] private Release? _selectedRelease;
    [ObservableProperty] private bool _releaseComposeVisible;
    [ObservableProperty] private ObservableCollection<string> _availableTagNames = [];
    [ObservableProperty] private string? _newReleaseTag;
    [ObservableProperty] private string _newReleaseTitle = "";
    [ObservableProperty] private string _newReleaseBody = "";
    [ObservableProperty] private bool _newReleaseDraft;
    [ObservableProperty] private bool _newReleasePrerelease;

    // ── Repo tab: settings ──────────────────────────────────────────────────────
    [ObservableProperty] private RepoSettings? _repoSettings;
    [ObservableProperty] private bool _repoSettingsLoading;
    [ObservableProperty] private string _repoSettingsError = "";
    [ObservableProperty] private string _repoDescriptionDraft = "";
    [ObservableProperty] private string _repoHomepageDraft = "";
    [ObservableProperty] private string _repoTopicsDraft = "";
    [ObservableProperty] private string _repoDefaultBranchDraft = "";
    [ObservableProperty] private RepoVisibility _selectedRepoVisibility = RepoVisibility.Private;
    [ObservableProperty] private bool _repoIssuesEnabled;
    [ObservableProperty] private bool _repoWikiEnabled;
    [ObservableProperty] private bool _repoProjectsEnabled;

    // ── Repo tab: notifications (G-12) ──────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<GitHubNotification> _notifications = [];
    [ObservableProperty] private bool _notificationsLoading;
    [ObservableProperty] private string _notificationsError = "";

    // ── Repo tab: danger zone (G-09) ────────────────────────────────────────────
    [ObservableProperty] private bool _dangerZoneEnabled;
    [ObservableProperty] private string _repoDeleteNotice = "";
    [ObservableProperty] private bool _deleteScopeHintVisible;

    /// <summary>Enum-only picker; the exact gh token is derived, never typed.</summary>
    public static IReadOnlyList<RepoVisibility> RepoVisibilities { get; } = Enum.GetValues<RepoVisibility>();

    /// <summary>Shown instead of a silent return when the danger zone is switched off.</summary>
    internal const string DangerZoneOffNotice =
        "Repository delete is off. Turn on the danger zone in Settings first.";

    /// <summary>Resets every Actions/Releases/Repo field so nothing leaks across a project switch.</summary>
    private void ResetGitHubTabState()
    {
        WorkflowRuns = [];
        WorkflowRunsLoaded = false;
        WorkflowRunsError = "";
        SelectedWorkflowRun = null;
        WorkflowJobs = [];
        WorkflowJobsLoading = false;
        WorkflowJobsError = "";
        RerunFailedJobsOnly = false;

        Releases = [];
        ReleasesLoaded = false;
        ReleasesError = "";
        SelectedRelease = null;
        ReleaseComposeVisible = false;
        AvailableTagNames = [];
        NewReleaseTag = null;
        NewReleaseTitle = "";
        NewReleaseBody = "";
        NewReleaseDraft = false;
        NewReleasePrerelease = false;

        RepoSettings = null;
        RepoSettingsLoaded = false;
        RepoSettingsLoading = false;
        RepoSettingsError = "";
        RepoDescriptionDraft = "";
        RepoHomepageDraft = "";
        RepoTopicsDraft = "";
        RepoDefaultBranchDraft = "";
        SelectedRepoVisibility = RepoVisibility.Private;
        RepoIssuesEnabled = false;
        RepoWikiEnabled = false;
        RepoProjectsEnabled = false;

        Notifications = [];
        NotificationsLoading = false;
        NotificationsError = "";

        RepoDeleteNotice = "";
        DeleteScopeHintVisible = false;
        RefreshDangerZoneGate();
    }

    /// <summary>Real "loaded" flag — a repo with no readable settings is not a repo already loaded.</summary>
    [ObservableProperty] private bool _repoSettingsLoaded;

    /// <summary>
    /// Re-reads the danger-zone opt-in. The setting is edited on another page, so the
    /// gate is re-read whenever this page applies a project rather than cached at
    /// construction.
    /// </summary>
    internal void RefreshDangerZoneGate() => DangerZoneEnabled = ReadDangerZoneEnabled();

    internal virtual bool ReadDangerZoneEnabled() => _settingsService?.Load().DangerZoneEnabled ?? false;

    // ── Remote reads (overridable so the surfaces are drivable without gh) ───────

    internal virtual Task<List<WorkflowRun>?> FetchWorkflowRunsAsync(string slug)
        => _gitHubService.GetWorkflowRunsAsync(slug);

    internal virtual Task<List<WorkflowJob>?> FetchWorkflowJobsAsync(string slug, long runId)
        => _gitHubService.GetWorkflowRunJobsAsync(slug, runId);

    internal virtual Task<List<Release>?> FetchReleasesAsync(string slug)
        => _gitHubService.GetReleasesAsync(slug);

    internal virtual Task<RepoSettings?> FetchRepoSettingsAsync(string slug)
        => _gitHubService.GetRepoSettingsAsync(slug);

    internal virtual Task<List<GitHubNotification>?> FetchNotificationsAsync(string slug)
        => _gitHubService.GetNotificationsAsync(slug);

    /// <summary>
    /// The one mutation whose failure the caller inspects and whose success rewrites the
    /// card. Overridable so both outcomes are reachable without deleting a repository.
    /// </summary>
    internal virtual Task<ProcessResult> DeleteRepoRemoteAsync(string slug)
        => _gitHubService.DeleteRepoAsync(slug);

    /// <summary>Typed confirmation text, or null when the prompt was cancelled.</summary>
    internal virtual Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
        => Views.Windows.TextPromptWindow.ShowAsync(title, message, confirmLabel);

    /// <summary>Destination chosen by the reader, or null when the save dialog was cancelled.</summary>
    internal virtual Task<string?> PromptForSavePathAsync(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save release asset",
            FileName = suggestedName,
            Filter = "All files (*.*)|*.*",
            OverwritePrompt = true
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    // ── Actions tab ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadWorkflowRuns()
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            WorkflowRunsError = NoRemoteStatus;
            return;
        }
        var gen = _generation;
        var runs = await FetchWorkflowRunsAsync(slug);
        if (!IsCurrent(gen)) return;
        if (runs is null)
        {
            // A failed fetch leaves the tab unloaded: marked loaded, the next visit skips
            // its own load and an empty list reads as "this repository has no runs".
            WorkflowRunsError = "Couldn't load workflow runs. Check that the GitHub CLI is installed and signed in.";
            return;
        }
        WorkflowRunsError = "";
        var keepId = SelectedWorkflowRun?.Id;
        WorkflowRuns = new ObservableCollection<WorkflowRun>(runs);
        WorkflowRunsLoaded = true;
        // Rebuilt list, new instances: without this a refresh blanks the jobs pane.
        if (keepId is { } id)
            SelectedWorkflowRun = WorkflowRuns.FirstOrDefault(r => r.Id == id);
    }

    partial void OnSelectedWorkflowRunChanged(WorkflowRun? value)
    {
        WorkflowJobs = [];
        WorkflowJobsError = "";
        if (value is not null) _ = LoadWorkflowJobsAsync(value);
    }

    private async Task LoadWorkflowJobsAsync(WorkflowRun run)
    {
        var slug = Slug;
        if (slug.Length == 0) return;
        var gen = _generation;
        WorkflowJobsLoading = true;
        try
        {
            var jobs = await FetchWorkflowJobsAsync(slug, run.Id);
            if (!IsCurrent(gen) || !ReferenceEquals(SelectedWorkflowRun, run)) return;
            if (jobs is null)
            {
                WorkflowJobsError = "Couldn't load this run's jobs.";
                return;
            }
            WorkflowJobs = new ObservableCollection<WorkflowJob>(jobs);
        }
        finally
        {
            if (IsCurrent(gen)) WorkflowJobsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RerunWorkflowRun()
    {
        var slug = Slug;
        var run = SelectedWorkflowRun;
        if (slug.Length == 0 || run is null || IsBusy) return;
        var failedOnly = RerunFailedJobsOnly;
        var gen = _generation;
        var scope = failedOnly ? "the failed jobs of" : "every job in";
        if (!await ConfirmAsync("Re-run workflow?",
                $"Re-run {scope} {run.Name} on {run.Branch}?\n\n" +
                "This starts a new run on GitHub and consumes Actions minutes.",
                "Re-run")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Re-run");
            return;
        }
        var ok = await RunGitHubOp(() => _gitHubService.RerunWorkflowAsync(slug, run.Id, failedOnly),
            $"Re-run {run.Name}");
        if (ok && IsCurrent(gen)) await LoadWorkflowRuns();
    }

    [RelayCommand]
    private async Task CancelWorkflowRun()
    {
        var slug = Slug;
        var run = SelectedWorkflowRun;
        if (slug.Length == 0 || run is null || IsBusy) return;
        if (run.IsCompleted)
        {
            GitHubStatusText = "That run has already finished — there is nothing to cancel.";
            return;
        }
        var gen = _generation;
        if (!await ConfirmAsync("Cancel workflow run?",
                $"Cancel the running {run.Name} on {run.Branch}?\n\n" +
                "Jobs still in flight are stopped where they are.",
                "Cancel run")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Cancel run");
            return;
        }
        var ok = await RunGitHubOp(() => _gitHubService.CancelWorkflowRunAsync(slug, run.Id),
            $"Cancel {run.Name}");
        if (ok && IsCurrent(gen)) await LoadWorkflowRuns();
    }

    [RelayCommand]
    private void OpenWorkflowRun(WorkflowRun? run) => OpenExternal(run?.Url ?? "");

    // ── Releases tab ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadReleases()
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            ReleasesError = NoRemoteStatus;
            return;
        }
        var gen = _generation;
        var releases = await FetchReleasesAsync(slug);
        if (!IsCurrent(gen)) return;
        if (releases is null)
        {
            ReleasesError = "Couldn't load releases. Check that the GitHub CLI is installed and signed in.";
            return;
        }
        ReleasesError = "";
        var keepTag = SelectedRelease?.TagName;
        Releases = new ObservableCollection<Release>(releases);
        ReleasesLoaded = true;
        if (keepTag is not null)
            SelectedRelease = Releases.FirstOrDefault(r => r.TagName == keepTag);
    }

    [RelayCommand]
    private async Task ShowNewRelease()
    {
        if (Slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        NewReleaseTag = null;
        NewReleaseTitle = "";
        NewReleaseBody = "";
        NewReleaseDraft = false;
        NewReleasePrerelease = false;
        ReleaseComposeVisible = true;
        await LoadReleaseTagsAsync();
    }

    [RelayCommand]
    private void CancelNewRelease() => ReleaseComposeVisible = false;

    /// <summary>
    /// The repository's own tags. A release is cut from a tag that already exists, so
    /// the picker is the tag list and never a free-text box that would have gh create
    /// a tag as a side effect of publishing.
    /// </summary>
    private async Task LoadReleaseTagsAsync()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;
        var tags = await _gitService.GetTagsAsync(repo);
        if (!IsCurrent(gen)) return;
        AvailableTagNames = new ObservableCollection<string>(tags.Select(t => t.Name));
    }

    [RelayCommand]
    private async Task SubmitNewRelease()
    {
        var repo = RepoPath;
        var tag = NewReleaseTag?.Trim() ?? "";
        var title = NewReleaseTitle.Trim();
        if (Slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        if (tag.Length == 0)
        {
            GitHubStatusText = "Pick an existing tag to release from.";
            return;
        }
        if (!AvailableTagNames.Contains(tag))
        {
            GitHubStatusText = $"{tag} isn't a tag in this repository — refresh the tag list.";
            return;
        }
        if (title.Length == 0)
        {
            GitHubStatusText = "Enter a release title first.";
            return;
        }
        if (repo.Length == 0 || IsBusy) return;

        var body = NewReleaseBody;
        var draft = NewReleaseDraft;
        var prerelease = NewReleasePrerelease;
        var gen = _generation;
        var ok = await RunGitHubOp(
            () => _gitHubService.CreateReleaseAsync(repo, tag, title, body, draft, prerelease),
            $"Create release {tag}");
        if (ok && IsCurrent(gen))
        {
            ReleaseComposeVisible = false;
            NewReleaseTag = null;
            NewReleaseTitle = "";
            NewReleaseBody = "";
            NewReleaseDraft = false;
            NewReleasePrerelease = false;
            await LoadReleases();
        }
    }

    [RelayCommand]
    private async Task DeleteRelease()
    {
        var slug = Slug;
        var release = SelectedRelease;
        if (slug.Length == 0 || release is null || IsBusy) return;
        // Read before the dialog: the confirmation names this release's published state,
        // and the command below decides on the same reading.
        var tag = release.TagName;
        var published = !release.IsDraft;
        var gen = _generation;
        if (!await ConfirmAsync("Delete release?", ReleaseDeleteMessage(tag, published), "Delete release")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Delete release");
            return;
        }
        // A draft still goes through the service's draft probe: nothing is lost by
        // verifying, and a mislabelled row cannot delete a published release unasked.
        var ok = await RunGitHubOp(() => _gitHubService.DeleteReleaseAsync(slug, tag, allowNonDraft: published),
            $"Delete release {tag}");
        if (ok && IsCurrent(gen))
        {
            SelectedRelease = null;
            await LoadReleases();
        }
    }

    /// <summary>
    /// Names the release and whether it is published. Deleting a published release
    /// removes it from everyone who can see the repository; deleting a draft removes
    /// only unpublished notes. The tag itself survives either way.
    /// </summary>
    internal static string ReleaseDeleteMessage(string tag, bool published) =>
        published
            ? $"Delete the published release {tag}?\n\n" +
              "It disappears for everyone who can see the repository, and its assets go with it. " +
              "The git tag stays. This cannot be undone."
            : $"Delete the draft release {tag}?\n\nThe git tag stays. This cannot be undone.";

    [RelayCommand]
    private async Task DownloadReleaseAsset(ReleaseAsset? asset)
    {
        var slug = Slug;
        var release = SelectedRelease;
        if (slug.Length == 0 || release is null || asset is null || IsBusy) return;

        var tag = release.TagName;
        var gen = _generation;
        var destination = await PromptForSavePathAsync(asset.Name);
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Download");
            return;
        }
        await RunGitHubOp(
            () => _gitHubService.DownloadReleaseAssetAsync(slug, tag, asset.Name, destination),
            $"Download {asset.Name}");
    }

    [RelayCommand]
    private void OpenRelease(Release? release) => OpenExternal(release?.Url ?? "");

    // ── Repo tab ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadRepoTab()
    {
        await LoadRepoSettings();
        await LoadNotifications();
    }

    [RelayCommand]
    private async Task LoadRepoSettings()
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            RepoSettingsError = NoRemoteStatus;
            return;
        }
        var gen = _generation;
        RepoSettingsLoading = true;
        try
        {
            var settings = await FetchRepoSettingsAsync(slug);
            if (!IsCurrent(gen)) return;
            if (settings is null)
            {
                RepoSettingsError = "Couldn't load repository settings. Check that the GitHub CLI is installed and signed in.";
                return;
            }
            RepoSettingsError = "";
            RepoSettings = settings;
            RepoSettingsLoaded = true;
            ApplyRepoSettingsToDrafts(settings);
        }
        finally
        {
            if (IsCurrent(gen)) RepoSettingsLoading = false;
        }
    }

    /// <summary>
    /// Seeds the editors from what the remote actually reports. A feature flag the
    /// response omitted stays off in the editor but is excluded from the save, so an
    /// unread flag is never written back as a deliberate "off".
    /// </summary>
    private void ApplyRepoSettingsToDrafts(RepoSettings settings)
    {
        RepoDescriptionDraft = settings.Description;
        RepoHomepageDraft = settings.Homepage;
        RepoTopicsDraft = settings.TopicsText;
        RepoDefaultBranchDraft = settings.DefaultBranch;
        SelectedRepoVisibility = GitHubActionTokens.ParseVisibility(settings.Visibility) ?? RepoVisibility.Private;
        RepoIssuesEnabled = settings.HasIssues ?? false;
        RepoWikiEnabled = settings.HasWiki ?? false;
        RepoProjectsEnabled = settings.HasProjects ?? false;
    }

    [RelayCommand]
    private async Task SaveRepoDetails()
    {
        var slug = Slug;
        var loaded = RepoSettings;
        if (slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        if (loaded is null || IsBusy) return;

        var description = RepoDescriptionDraft.Trim();
        var homepage = RepoHomepageDraft.Trim();
        var (addTopics, removeTopics) = DiffTopics(loaded.Topics, SplitTopics(RepoTopicsDraft));
        // Null means "leave unchanged"; sending an unchanged value would still be a
        // remote write, and an empty string is how a field is cleared.
        var descriptionArg = description == loaded.Description ? null : description;
        var homepageArg = homepage == loaded.Homepage ? null : homepage;
        if (descriptionArg is null && homepageArg is null && addTopics.Count == 0 && removeTopics.Count == 0)
        {
            GitHubStatusText = "Nothing to save — description, homepage and topics are unchanged.";
            return;
        }

        var gen = _generation;
        var ok = await RunGitHubOp(
            () => _gitHubService.EditRepoAsync(slug, descriptionArg, homepageArg, addTopics, removeTopics),
            "Save repository details");
        if (ok && IsCurrent(gen)) await LoadRepoSettings();
    }

    /// <summary>Comma-separated topics → trimmed, non-empty, de-duplicated list.</summary>
    internal static List<string> SplitTopics(string topics) =>
        [.. topics.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Topics to add and remove to turn <paramref name="current"/> into
    /// <paramref name="desired"/>. Compared case-insensitively: GitHub lowercases every
    /// topic it stores, so a re-cased entry is the same topic and must not read as a
    /// remove plus an add.
    /// </summary>
    internal static (List<string> Add, List<string> Remove) DiffTopics(
        IEnumerable<string> current, IEnumerable<string> desired)
    {
        var have = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var want = desired.ToList();
        var wanted = want.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ([.. want.Where(t => !have.Contains(t))],
                [.. have.Where(t => !wanted.Contains(t))]);
    }

    [RelayCommand]
    private async Task SaveRepoFeatures()
    {
        var slug = Slug;
        var loaded = RepoSettings;
        if (slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        if (loaded is null || IsBusy) return;

        var issues = FeatureChange(loaded.HasIssues, RepoIssuesEnabled);
        var wiki = FeatureChange(loaded.HasWiki, RepoWikiEnabled);
        var projects = FeatureChange(loaded.HasProjects, RepoProjectsEnabled);
        if (issues is null && wiki is null && projects is null)
        {
            GitHubStatusText = "Nothing to save — the feature toggles are unchanged.";
            return;
        }

        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.SetRepoFeaturesAsync(slug, issues, wiki, projects),
            "Save repository features");
        if (ok && IsCurrent(gen)) await LoadRepoSettings();
    }

    /// <summary>
    /// The value to send for one feature, or null to leave it alone. A flag the remote
    /// never reported is left alone whatever the checkbox says: the checkbox had nothing
    /// to show, and writing its default would turn a feature off nobody asked about.
    /// </summary>
    internal static bool? FeatureChange(bool? loaded, bool wanted) =>
        loaded is { } was && was != wanted ? wanted : null;

    [RelayCommand]
    private async Task ChangeDefaultBranch()
    {
        var slug = Slug;
        var loaded = RepoSettings;
        var branch = RepoDefaultBranchDraft.Trim();
        if (slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        if (loaded is null || IsBusy) return;
        if (branch.Length == 0)
        {
            GitHubStatusText = "Enter the branch to make default.";
            return;
        }
        if (branch == loaded.DefaultBranch)
        {
            GitHubStatusText = $"{branch} is already the default branch.";
            return;
        }

        var gen = _generation;
        if (!await ConfirmAsync("Change default branch?",
                $"Make {branch} the default branch of {slug}?\n\n" +
                $"New pull requests target it instead of {loaded.DefaultBranch}, and fresh clones check it out. " +
                "The branch must already exist on the remote.",
                "Change branch")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Default branch change");
            return;
        }
        var ok = await RunGitHubOp(() => _gitHubService.SetDefaultBranchAsync(slug, branch), "Set default branch");
        if (ok && IsCurrent(gen)) await LoadRepoSettings();
    }

    [RelayCommand]
    private async Task ChangeRepoVisibility()
    {
        var slug = Slug;
        var loaded = RepoSettings;
        if (slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        if (loaded is null || IsBusy) return;

        var visibility = SelectedRepoVisibility;
        var token = visibility.Token(); // enum → exact gh token; BuildVisibilityArgs can't see a bad value
        if (token == loaded.Visibility)
        {
            GitHubStatusText = $"{slug} is already {token}.";
            return;
        }

        var gen = _generation;
        // Outward-facing and immediate: going public exposes every commit in the
        // history, going private breaks every link anyone holds.
        var typed = await PromptForTextAsync("Change repository visibility?",
            VisibilityConfirmMessage(slug, loaded.Visibility, token), "Change visibility");
        if (!RepoNameConfirmed(typed, slug))
        {
            if (typed is not null) GitHubStatusText = $"Visibility unchanged — that isn't {slug}.";
            return;
        }
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Visibility change");
            return;
        }

        var result = await RunGitHubOpResult(() => _gitHubService.SetRepoVisibilityAsync(slug, token),
            "Change visibility");
        if (result is null || !IsCurrent(gen)) return;
        if (result.Success)
        {
            await LoadRepoSettings();
            return;
        }
        GitHubStatusText = VisibilityFailureMessage(result.FirstError);
    }

    internal static string VisibilityConfirmMessage(string slug, string from, string to) =>
        $"Change {slug} from {from} to {to}?\n\n" +
        (to == "public"
            ? "Every commit, branch and issue in this repository becomes readable by anyone."
            : "Existing links, forks and package references to this repository stop resolving for anyone without access.") +
        $"\n\nType {slug} to confirm.";

    /// <summary>
    /// A visibility change holds a server-side lock for several seconds; a follow-on
    /// change inside that window comes back as HTTP 422 or 409, which reads as an
    /// unexplained failure unless it is named.
    /// </summary>
    internal static string VisibilityFailureMessage(string error) =>
        error.Contains("422", StringComparison.Ordinal) || error.Contains("409", StringComparison.Ordinal)
            ? "Change visibility failed: a previous visibility change is still in progress — retry shortly."
            : $"Change visibility failed: {error}";

    /// <summary>
    /// Whether the typed text is this repository's slug. Trimmed and case-insensitive —
    /// GitHub resolves repository names case-insensitively, so a case difference is not
    /// a different repository — but nothing else is accepted: the bare name of a
    /// repository owned by someone else must not pass for this one.
    /// </summary>
    internal static bool RepoNameConfirmed(string? typed, string slug) =>
        typed is not null && slug.Length > 0 &&
        string.Equals(typed.Trim(), slug, StringComparison.OrdinalIgnoreCase);

    // ── Notifications (G-12) ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadNotifications()
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            NotificationsError = NoRemoteStatus;
            return;
        }
        var gen = _generation;
        NotificationsLoading = true;
        try
        {
            var notifications = await FetchNotificationsAsync(slug);
            if (!IsCurrent(gen)) return;
            if (notifications is null)
            {
                NotificationsError = "Couldn't load notifications. Check that the GitHub CLI is installed and signed in.";
                return;
            }
            NotificationsError = "";
            Notifications = new ObservableCollection<GitHubNotification>(notifications);
        }
        finally
        {
            if (IsCurrent(gen)) NotificationsLoading = false;
        }
    }

    /// <summary>
    /// Marking read is an explicit act. Opening a notification, refreshing the list, or
    /// leaving the tab never clears one — the thread stays unread until this runs.
    /// </summary>
    [RelayCommand]
    private async Task MarkNotificationRead(GitHubNotification? notification)
    {
        if (notification is null || Slug.Length == 0 || IsBusy) return;
        var gen = _generation;
        var ok = await RunGitHubOp(() => _gitHubService.MarkNotificationReadAsync(notification.ThreadId),
            "Mark notification read");
        if (ok && IsCurrent(gen)) await LoadNotifications();
    }

    [RelayCommand]
    private async Task MarkAllNotificationsRead()
    {
        var slug = Slug;
        if (slug.Length == 0 || IsBusy) return;
        var count = Notifications.Count;
        if (count == 0)
        {
            GitHubStatusText = "No unread notifications on this repository.";
            return;
        }
        var gen = _generation;
        if (!await ConfirmAsync("Mark all read?",
                $"Mark all {count} notification(s) on {slug} as read?\n\nThis cannot be undone from here.",
                "Mark all read")) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Mark all read");
            return;
        }
        var ok = await RunGitHubOp(() => _gitHubService.MarkRepoNotificationsReadAsync(slug), "Mark all read");
        if (ok && IsCurrent(gen)) await LoadNotifications();
    }

    [RelayCommand]
    private void OpenNotification(GitHubNotification? notification)
    {
        var slug = Slug;
        if (notification is null || slug.Length == 0) return;
        // A subject that maps to no web page (a release, a discussion, a check suite)
        // opens the repository rather than a guessed URL.
        OpenExternal(notification.WebUrl.Length > 0 ? notification.WebUrl : $"https://github.com/{slug}");
    }

    // ── Danger zone (G-09) ──────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the repository on GitHub. Three independent gates: the danger-zone
    /// opt-in re-read here rather than trusted from the bound flag, a typed slug, and
    /// the generation guard for a switch landing while the prompt is open. Nothing
    /// touches the working copy — the notice says exactly that.
    /// </summary>
    [RelayCommand]
    private async Task DeleteRepo()
    {
        var slug = Slug;
        if (slug.Length == 0)
        {
            GitHubStatusText = NoRemoteStatus;
            return;
        }
        // Re-read, not the bound property: the panel's visibility is a rendering
        // decision, and a command reachable from the keyboard must enforce the gate
        // itself.
        RefreshDangerZoneGate();
        if (!DangerZoneEnabled)
        {
            GitHubStatusText = DangerZoneOffNotice;
            return;
        }
        if (IsBusy) return;

        var gen = _generation;
        var typed = await PromptForTextAsync("Delete this repository?", RepoDeleteMessage(slug), "Delete repository");
        if (!RepoNameConfirmed(typed, slug))
        {
            if (typed is not null) GitHubStatusText = $"Repository not deleted — that isn't {slug}.";
            return;
        }
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Repository delete");
            return;
        }

        var localPath = RepoPath;
        var result = await RunGitHubOpResult(() => DeleteRepoRemoteAsync(slug), $"Delete {slug}");
        if (result is null || !IsCurrent(gen)) return;
        if (!result.Success)
        {
            DeleteScopeHintVisible = GitHubService.NeedsDeleteRepoScope(result.FirstError);
            RepoDeleteNotice = DeleteScopeHintVisible ? GitHubService.DeleteRepoScopeInstructions : "";
            return;
        }

        DeleteScopeHintVisible = false;
        RepoDeleteNotice = RepoDeletedNotice(slug, localPath);
        GitHubStatusText = RepoDeleteNotice;
        // The remote facts on the card described a repository that no longer exists;
        // null is "unknown", which is now the truth. The local clone and its origin
        // remote are left exactly as they are.
        if (Project is not null)
        {
            Project.OpenIssueCount = null;
            Project.OpenPrCount = null;
        }
        RepoSettings = null;
        RepoSettingsLoaded = false;
        Releases = [];
        ReleasesLoaded = false;
        WorkflowRuns = [];
        WorkflowRunsLoaded = false;
        Notifications = [];
    }

    internal static string RepoDeleteMessage(string slug) =>
        $"Delete {slug} from GitHub?\n\n" +
        "Its issues, pull requests, releases and Actions history go with it and cannot be restored from here. " +
        "The local files on this machine are not touched.\n\n" +
        $"Type {slug} to confirm.";

    /// <summary>What is actually true after a delete: the remote is gone, the clone is not.</summary>
    internal static string RepoDeletedNotice(string slug, string localPath) =>
        $"{slug} is deleted on GitHub. The local files remain at {localPath}, and this clone's origin remote " +
        "now points at a repository that no longer exists.";

    [RelayCommand]
    private void GrantDeleteScope()
    {
        if (_gitHubService.StartInteractiveDeleteScopeGrant() is null)
        {
            GitHubStatusText = "Couldn't start the GitHub CLI to grant the scope.";
            return;
        }
        GitHubStatusText = "Finish the scope grant in the console window, then retry the delete.";
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The exact string handed to the shell for a URL that came from a gh payload, or
    /// null for anything that must not be launched. Only http/https navigate: the launch
    /// path is ShellExecute, where file://, UNC and any registered protocol handler would
    /// start a local program instead. The parsed form is what travels, so a target padded
    /// with spaces or carrying an embedded control character reaches the shell encoded.
    /// </summary>
    internal static string? NavigableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri : null;

    /// <summary>Overridable so the row commands are reachable without launching a browser.</summary>
    internal virtual void OpenExternal(string url)
    {
        if (NavigableUrl(url) is not { } target) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("could not open an external link", ex);
        }
    }
}
