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
        Services.Safety.DeepCleanService? deepClean = null)
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
        ConfirmPrompt = ConfirmAsync;
        ConfirmSurgeryAsync = c => ConfirmAsync(c.Title, c.Message, c.ConfirmLabel);

        SaveManifestCommand = new AsyncRelayCommand(SaveManifestAsync);
        LoadDetailsCommand = new AsyncRelayCommand(LoadDetailsAsync);
        OpenCommitCommand = new RelayCommand<GitCommit>(OpenCommit);
        OpenIssueCommand = new RelayCommand<GitHubIssue>(OpenIssue);
    }

    private void OpenCommit(GitCommit? commit)
    {
        if (commit is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        var url = $"https://github.com/{Project.GitHubSlug}/commit/{commit.Ref}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenIssue(GitHubIssue? issue)
    {
        if (issue is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        var url = $"https://github.com/{Project.GitHubSlug}/issues/{issue.Number}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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

    [RelayCommand]
    private void ToggleEditNotes() => IsEditingNotes = !IsEditingNotes;

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
        if (string.IsNullOrEmpty(project.GitHubSlug)) return;
        try
        {
            var issues = await _gitHubService.GetIssuesAsync(project.GitHubSlug, "open");
            project.Issues = issues; // cache on the model for the next visit
            if (ReferenceEquals(Project, project)) // user may have moved on mid-fetch
                Issues = new ObservableCollection<GitHubIssue>(issues);
        }
        catch (Exception ex)
        {
            Log.Warn($"Issue list load failed for {project.GitHubSlug}", ex);
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
        DiffLines = [];
        DiffTitle = "";
        DiffIsBinary = false;
        CommitMessage = "";
        AmendMode = false;
        IsBusy = false;
        SyncStatusText = "";
        // The retry offer belongs to the previous project's failure; left set it
        // would replay that repo's op from the new project's page.
        StaleLockRetryVisible = false;
        _staleLockRetryOp = null;
        _staleLockRetryRepo = "";
        BranchLabel = "";
        AheadBehindLabel = "";
        Branches = [];
        NewBranchName = "";
        Stashes = [];
        StashesLoaded = false;
        SelectedCommit = null;
        CommitFiles = [];
        CommitDiffLines = [];
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
        Commits = new ObservableCollection<GitCommit>(p.RecentCommits ?? []);
        Issues = new ObservableCollection<GitHubIssue>(p.Issues ?? []);

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

    private async Task SaveManifestAsync()
    {
        if (Project is null) return;

        var manifest = new ProjectManifest
        {
            ProjectType = SelectedProjectType,
            Status = SelectedStatus,
            Category = SelectedCategory,
            ValidationSchedule = ValidationSchedule,
            Notes = Notes
        };

        await _discoveryService.SaveManifestAsync(Project.FullPath, manifest);
        Project.Manifest = manifest;
    }
}
