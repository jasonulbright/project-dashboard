using System.Diagnostics;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

public partial class ProjectDetailViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;
    /// <summary>Null outside the app host; the danger zone then reads as switched off.</summary>
    private readonly SettingsService? _settingsService;

    [ObservableProperty] private ProjectInfo? _project;
    [ObservableProperty] private string _readmeText = "";
    [ObservableProperty] private string _changelogText = "";
    [ObservableProperty] private ObservableCollection<GitCommit> _commits = [];
    [ObservableProperty] private ObservableCollection<GitHubIssue> _issues = [];

    // Manifest editor properties
    [ObservableProperty] private string _manifestDescription = "";
    [ObservableProperty] private string _selectedProjectType = "unknown";
    [ObservableProperty] private string _selectedStatus = "active";
    [ObservableProperty] private string _selectedCategory = "Uncategorized";
    [ObservableProperty] private string _validationSchedule = "none";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _isEditingNotes;
    [ObservableProperty] private ObservableCollection<NoteLine> _noteLines = [];

    public static List<string> ProjectTypes { get; } = ["mecm-tool", "powershell-script", "web-app", "game", "framework", "library", "dashboard", "unknown"];
    public static List<string> Statuses { get; } = ["active", "maintenance", "archived", "experimental"];
    public static List<string> CategoriesList { get; } = ["MECM", "Web", "Games", "Infrastructure", "Utilities", "Uncategorized"];
    public static List<string> Schedules { get; } = ["none", "daily", "weekly", "monthly"];

    public IAsyncRelayCommand SaveManifestCommand { get; }
    public IAsyncRelayCommand LoadDetailsCommand { get; }
    public IRelayCommand<GitCommit> OpenCommitCommand { get; }
    public IRelayCommand<GitHubIssue> OpenIssueCommand { get; }

    public ProjectDetailViewModel(
        ProjectDiscoveryService discoveryService,
        GitService gitService,
        GitHubService gitHubService,
        IRewriteSessionFactory? rewriteSessions = null,
        Services.Safety.RepoBusyRegistry? busyRegistry = null,
        SettingsService? settingsService = null,
        Services.Safety.BackupService? backups = null,
        Services.Safety.RewriteRecoveryService? recovery = null,
        Services.Rewrite.ForcePushService? forcePush = null,
        Services.Safety.DeepCleanService? deepClean = null,
        SubmoduleService? submodules = null,
        ProjectWatcherService? watcher = null,
        Action<Action>? uiPost = null)
    {
        _discoveryService = discoveryService;
        _gitService = gitService;
        _gitHubService = gitHubService;
        _settingsService = settingsService;
        _rewriteSessions = rewriteSessions;
        _busyRegistry = busyRegistry ?? new Services.Safety.RepoBusyRegistry();
        // Null when the host supplied none: the Backups surface then refuses instead of
        // pretending a repository has no backups.
        _backups = backups;
        _recovery = recovery;
        _forcePush = forcePush;
        _deepClean = deepClean;
        _submoduleService = submodules;
        _watcher = watcher;
        // A host without an Application has no dispatcher to marshal through, and a default
        // that silently dropped the callback would drop the refresh a watcher signal owes.
        _uiPost = uiPost ?? PostToApplicationDispatcher;
        SubscribeToRepoChanges();
        ConfirmPrompt = ConfirmAsync;
        ConfirmSurgeryAsync = c => ConfirmAsync(c.Title, c.Message, c.ConfirmLabel);

        // The page outlives every settings write, so the layout is re-derived from the one
        // notification path rather than read once here and held until relaunch.
        RefreshDiffLayout();
        if (_settingsService is not null) _settingsService.Changed += OnSettingsChangedForDiffLayout;

        SaveManifestCommand = new AsyncRelayCommand(SaveManifestAsync);
        LoadDetailsCommand = new AsyncRelayCommand(LoadDetailsAsync);
        OpenCommitCommand = new RelayCommand<GitCommit>(OpenCommit);
        OpenIssueCommand = new RelayCommand<GitHubIssue>(OpenIssue);
    }

    private void OpenCommit(GitCommit? commit)
    {
        if (commit is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        OpenExternal($"https://github.com/{Project.GitHubSlug}/commit/{commit.Ref}");
    }

    private void OpenIssue(GitHubIssue? issue)
    {
        if (issue is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        OpenExternal($"https://github.com/{Project.GitHubSlug}/issues/{issue.Number}");
    }

    partial void OnNotesChanged(string value) => ParseNoteLines();

    private void ParseNoteLines()
    {
        var lines = (Notes ?? "").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(NoteLine.Parse)
            .ToList();
        NoteLines = new ObservableCollection<NoteLine>(lines);
    }

    /// <summary>Shown under the notes card; nothing else on the page reports a notes write.</summary>
    [ObservableProperty] private string _notesStatusText = "";

    internal const string NotesSaveFailed =
        "Notes not saved — the write failed, so this text is only in the editor. Try again.";

    public string NotesEditLabel => IsEditingNotes ? "Save" : "Edit";

    public string NotesEditName =>
        IsEditingNotes ? "Save the project notes" : "Edit the project notes";

    partial void OnIsEditingNotesChanged(bool value)
    {
        OnPropertyChanged(nameof(NotesEditLabel));
        OnPropertyChanged(nameof(NotesEditName));
    }

    /// <summary>
    /// Opens the notes editor, and on the way out writes what was typed. The editor is the only
    /// place notes exist until this runs, so a close that skipped the write would drop them.
    /// A write that did not land keeps the editor open over the text it holds.
    /// </summary>
    [RelayCommand]
    private async Task ToggleEditNotes()
    {
        if (!IsEditingNotes)
        {
            NotesStatusText = "";
            IsEditingNotes = true;
            return;
        }

        if (Project is null)
        {
            IsEditingNotes = false;
            return;
        }

        // Built from the manifest the project holds rather than from the metadata editor beside
        // it: closing the notes editor writes notes, never a metadata edit nobody saved.
        var stored = Project.Manifest;
        var manifest = new ProjectManifest
        {
            Description = stored.Description,
            ProjectType = stored.ProjectType,
            Status = stored.Status,
            Category = stored.Category,
            ValidationSchedule = stored.ValidationSchedule,
            Notes = Notes
        };

        if (!await PersistManifestAsync(manifest))
        {
            NotesStatusText = NotesSaveFailed;
            return;
        }

        NotesStatusText = "Notes saved.";
        IsEditingNotes = false;
    }

    public Task SetProjectAsync(ProjectInfo project)
    {
        // Local data renders instantly from what discovery already loaded. The issues
        // LIST is the one remote thing this page shows, and discovery no longer
        // prefetches it for every repo — refresh it lazily for just this project.
        ApplyProject(project);
        _ = LoadIssuesLazilyAsync(project);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The working-state refresh <see cref="ApplyProject"/> starts and does not await.
    /// Held so a caller can await that refresh itself; polling for the properties it
    /// writes makes the wait a wall-clock guess, and the guess is what goes flaky.
    /// </summary>
    internal Task WorkingStateRefresh { get; private set; } = Task.CompletedTask;

    private async Task SafeRefreshWorkingStateAsync()
    {
        try { await RefreshWorkingStateAsync(); }
        catch (Exception ex) { Log.Warn("working-state refresh failed", ex); }
    }

    private async Task LoadIssuesLazilyAsync(ProjectInfo project)
    {
        if (string.IsNullOrEmpty(project.GitHubSlug))
        {
            // The list seeded from the model is whatever an earlier scan cached, and for a
            // repository with no remote nothing ever queried one. Left unmarked, an empty
            // list reads as "no open issues" about a repository that has none to report.
            if (ReferenceEquals(Project, project)) IssuesError = NoRemoteStatus;
            return;
        }
        try
        {
            var issues = await FetchIssuesAsync(project.GitHubSlug);
            if (!ReferenceEquals(Project, project)) return; // user may have moved on mid-fetch
            if (issues is null)
            {
                IssuesError = IssuesFetchFailed;
                return;
            }
            project.Issues = issues; // cache on the model for the next visit
            IssuesError = "";
            Issues = new ObservableCollection<GitHubIssue>(issues);
        }
        catch (Exception ex)
        {
            Log.Warn($"Issue list load failed for {project.GitHubSlug}", ex);
            if (ReferenceEquals(Project, project)) IssuesError = IssuesFetchFailed;
        }
    }

    private void ApplyProject(ProjectInfo p)
    {
        // Every page load re-applies the project already open, so this is a reload far more
        // often than a switch. Running the teardown for a reload clears the busy gate and
        // resets the wizard out from under a rewrite that is still running or still holding
        // the only one-click undo for the history it replaced.
        if (IsSameRepo(p))
        {
            Project = p;
            // Edited on the Settings page, so a reload is the moment to re-read it.
            RefreshDangerZoneGate();
            ApplyProjectContent(p);
            return;
        }

        // Reads the OUTGOING repository's overlays too: one left open would keep describing the
        // previous repository behind the incoming project's page.
        CloseBackupsOnProjectSwitch();
        CloseForcePushOnProjectSwitch();
        CloseReflogOnProjectSwitch();
        CloseTagsOnProjectSwitch();

        // Reads the OUTGOING repository, so it runs before the swap below.
        ParkRewriteSessionForThisRepo();

        // Invalidate any in-flight async op from the previous project before swapping.
        BumpGeneration();

        Project = p;
        IsEditingNotes = false; // singleton VM: edit mode must not leak onto the next project

        // Reset ALL per-repo work-area state so nothing leaks between projects.
        WorkingState = null;
        StagedFiles = [];
        UnstagedFiles = [];
        ConflictedFiles = [];
        SelectedStagedFile = null;
        SelectedUnstagedFile = null;
        SelectedStagedFiles = [];
        SelectedUnstagedFiles = [];
        DiffLines = [];
        DiffTitle = "";
        DiffIsBinary = false;
        DiffIsCombined = false;
        SelectedDiffLine = null;
        CommitMessage = "";
        AmendMode = false;
        // The held signal named the repository being left; the incoming one is read below.
        _watcherRefreshPending = false;
        IsBusy = false;
        SyncStatusText = "";
        // The retry offer belongs to the previous project's failure; left set it
        // would replay that repo's op from the new project's page.
        StaleLockRetryVisible = false;
        _staleLockRetryOp = null;
        _staleLockRetryRepo = "";
        // The inverse belongs to the previous project's operation; left standing it would
        // offer to unstage in a repository that never staged anything.
        ClearUndoOffer();
        BranchLabel = "";
        AheadBehindLabel = "";
        Branches = [];
        NewBranchName = "";
        BranchesTabLoaded = false;
        Remotes = [];
        RemotesEmpty = false;
        SelectedRemote = null;
        RemoteBranches = [];
        RemoteBranchesEmpty = false;
        SelectedRemoteBranch = null;
        NewRemoteName = "";
        NewRemoteUrl = "";
        RemotesStatusText = "";
        RemotesErrorText = "";
        UpstreamChoices = [];
        SelectedUpstreamChoice = null;
        CompareBaseChoices = [];
        SelectedCompareBase = null;
        BranchCompareText = "";
        BranchExtrasStatusText = "";
        BranchExtrasErrorText = "";
        ResetInternalsState();
        Stashes = [];
        StashesLoaded = false;
        SelectedStash = null;
        NewStashMessage = "";
        StashIncludeUntracked = false;
        SelectedCommit = null;
        CommitFiles = [];
        CommitDiffLines = [];
        ResetHistoryWindow();
        CloseFileHistoryOnProjectSwitch();
        CloseCommitGraphOnProjectSwitch();
        PullRequests = [];
        PullRequestsLoaded = false;
        StateBannerVisible = false;
        StateBannerText = "";
        ResetGitHubState();
        ResetGitHubTabState();
        // A held dry run belongs to the repository it ran against; carrying it across a switch
        // would arm Execute on the new project with the previous one's report on screen. The
        // park above already took anything with an undo behind it, so this disposes only a
        // dry run, which changed nothing and is re-runnable.
        ResetRewriteState();
        RestoreParkedRewrite();
        RefreshRecoveryBanner();

        ApplyProjectContent(p);
    }

    /// <summary>Everything the page renders straight from the project model, re-read on every load.</summary>
    private void ApplyProjectContent(ProjectInfo p)
    {
        WorkingStateRefresh = SafeRefreshWorkingStateAsync();
        ReadmeText = p.ReadmeContent ?? "";
        ChangelogText = p.ChangelogContent ?? "";
        // Every reload builds fresh commit objects, so a selection held by reference is lost.
        // A reload of the SAME repository re-selects by sha; a commit the reload no longer
        // lists has none to match, and the selection clearing is the honest outcome.
        var selectedSha = SelectedCommit?.Ref;
        Commits = new ObservableCollection<GitCommit>(p.RecentCommits ?? []);
        if (selectedSha is not null)
            SelectedCommit = Commits.FirstOrDefault(
                c => string.Equals(c.Ref, selectedSha, StringComparison.OrdinalIgnoreCase));
        // The seeded list is whatever the last scan cached, so its depth is unknown here.
        // The first page load reads the answer from git with one commit of overlap.
        HistoryHasMore = Commits.Count > 0;
        Issues = new ObservableCollection<GitHubIssue>(p.Issues ?? []);

        ManifestStatusText = "";
        NotesStatusText = "";
        ManifestDescription = p.Manifest.Description;
        SelectedProjectType = p.Manifest.ProjectType;
        SelectedStatus = p.Manifest.Status;
        SelectedCategory = p.Manifest.Category;
        ValidationSchedule = p.Manifest.ValidationSchedule;
        Notes = p.Manifest.Notes;
    }

    /// <summary>
    /// Whether the incoming project is the one already open. Compared on the normalized
    /// repository key rather than the model instance: discovery hands out a fresh
    /// <see cref="ProjectInfo"/> per scan, so instance identity would call every reload a switch.
    /// </summary>
    private bool IsSameRepo(ProjectInfo p) =>
        RepoPath.Length > 0
        && p.FullPath.Length > 0
        && string.Equals(Services.Safety.RepoKey.For(RepoPath), Services.Safety.RepoKey.For(p.FullPath), StringComparison.Ordinal);

    private async Task LoadDetailsAsync()
    {
        if (Project is null) return;
        await SetProjectAsync(Project);
    }

    /// <summary>Shown beside the manifest editor; a failed write must not read as a saved one.</summary>
    [ObservableProperty] private string _manifestStatusText = "";

    internal const string ManifestSaveFailed =
        "Metadata not saved — the write failed, so this edit is only in the editor. Try again.";

    private ProjectManifest EditedManifest() => new()
    {
        Description = ManifestDescription,
        ProjectType = SelectedProjectType,
        Status = SelectedStatus,
        Category = SelectedCategory,
        ValidationSchedule = ValidationSchedule,
        Notes = Notes
    };

    /// <summary>
    /// Persists the editor's manifest, adopting it onto the project only once the store reports
    /// the write durable. False leaves the editor holding the edit and says so.
    /// </summary>
    private async Task<bool> PersistManifestAsync(ProjectManifest manifest)
    {
        if (Project is null) return false;
        if (!await _discoveryService.SaveManifestAsync(Project.FullPath, manifest)) return false;

        Project.Manifest = manifest;
        Project.HasManifest = true;
        return true;
    }

    private async Task SaveManifestAsync()
    {
        if (Project is null) return;

        ManifestStatusText = await PersistManifestAsync(EditedManifest())
            ? "Metadata saved."
            : ManifestSaveFailed;
    }
}
