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
        Action<Action>? uiPost = null,
        Services.Safety.OperationHistory? history = null)
    {
        // Defaulted rather than left null: a page that recorded nothing would leave the ledger
        // describing only what the coordinators did, and the overlay would report that gap as
        // "no operations recorded" for a repository this page had just mutated.
        _history = history ?? new Services.Safety.OperationHistory();
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
        RefreshTaxonomyChoices();
        if (_settingsService is not null)
        {
            _settingsService.Changed += OnSettingsChangedForDiffLayout;
            _settingsService.Changed += OnSettingsChangedForTaxonomy;
        }
        if (discoveryService?.Manifests is { } manifestEvents)
            manifestEvents.ValuesRenamed += OnTaxonomyValuesRenamed;

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

    /// <summary>
    /// Names the project the notes belonged to. The line is read on the page of the project
    /// switched TO, where an unqualified "notes not saved" would describe the wrong repository.
    /// </summary>
    internal static string NotesLeftUnsaved(string projectName) =>
        $"Notes for {projectName} were not saved — the write failed while leaving that project.";

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

        if (!await SaveNotesAsync(Project, Notes))
        {
            NotesStatusText = NotesSaveFailed;
            return;
        }

        NotesStatusText = "Notes saved.";
        IsEditingNotes = false;
    }

    /// <summary>
    /// Writes the editor's text onto a project's own manifest, carrying every metadata field
    /// across as that project already had it: a notes write is never a metadata edit nobody saved.
    /// </summary>
    private Task<bool> SaveNotesAsync(ProjectInfo project, string notes)
    {
        var stored = project.Manifest;
        return PersistManifestAsync(project, new ProjectManifest
        {
            Description = stored.Description,
            ProjectType = stored.ProjectType,
            Status = stored.Status,
            Category = stored.Category,
            ValidationSchedule = stored.ValidationSchedule,
            Notes = notes
        });
    }

    /// <summary>
    /// Writes the notes the editor still holds before the project they belong to leaves the page,
    /// and returns the notice the switch owes when that write did not land, else null.
    ///
    /// The write is bound to the OUTGOING project and runs before the swap, so it cannot land on
    /// the repository taking the screen. The switch proceeds either way: a page that refused to
    /// navigate over a failed write would strand the reader on it, which is the worse outcome.
    /// The unsaved text goes to the log, where it can still be read back.
    /// </summary>
    private async Task<string?> SaveNotesLeavingProjectAsync(ProjectInfo incoming)
    {
        if (!IsEditingNotes || Project is null || IsSameRepo(incoming)) return null;

        var outgoing = Project;
        var pending = Notes;
        // Closed before the await: the editor is closing either way, and a second switch
        // arriving mid-write would otherwise start the same write again.
        IsEditingNotes = false;

        if (await SaveNotesAsync(outgoing, pending)) return null;

        Log.Error($"Notes for {outgoing.FullPath} were not saved before the project was left; " +
                  $"the unsaved text follows.{Environment.NewLine}{pending}");
        return NotesLeftUnsaved(outgoing.DisplayName);
    }

    /// <summary>
    /// Claimed by each call to <see cref="SetProjectAsync"/> before it awaits the outgoing
    /// project's notes write. A later call can run to completion during that await, so the
    /// captured value is what tells a resuming continuation that the project it names is no
    /// longer the one the reader asked for. Separate from <see cref="_generation"/>, which counts
    /// switches actually APPLIED and invalidates their in-flight reads — bumping that here would
    /// invalidate the loads of a switch that is still on screen.
    /// Read and written only between awaits, like the generation counter beside it.
    /// </summary>
    private int _switchSequence;

    public async Task SetProjectAsync(ProjectInfo project)
    {
        var switchToken = ++_switchSequence;

        // Ahead of the swap, while Project still names the repository the text was typed against.
        var unsavedNotes = await SaveNotesLeavingProjectAsync(project);

        // A later switch took the page while that write was in flight, and has already applied.
        // Applying this one would put the project clicked BEFORE it back on screen and bump the
        // generation out from under the loads the visible switch started.
        if (switchToken != _switchSequence)
        {
            // The notice still lands: it names the project the notes were typed in, so it is
            // true on whatever page is showing, and this is the only moment it can be told.
            if (unsavedNotes is not null) NotesStatusText = unsavedNotes;
            return;
        }

        // Local data renders instantly from what discovery already loaded. The issues
        // LIST is the one remote thing this page shows, and discovery no longer
        // prefetches it for every repo — refresh it lazily for just this project.
        ApplyProject(project);
        // After the switch: ApplyProjectContent clears the notes line, so a notice written
        // any earlier is wiped by the very switch that owes it.
        if (unsavedNotes is not null) NotesStatusText = unsavedNotes;
        _ = LoadIssuesLazilyAsync(project);
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

    private Task LoadIssuesLazilyAsync(ProjectInfo project)
    {
        if (string.IsNullOrEmpty(project.GitHubSlug))
        {
            // The list seeded from the model is whatever an earlier scan cached, and for a
            // repository with no remote nothing ever queried one. Left unmarked, an empty
            // list reads as "no open issues" about a repository that has none to report.
            if (ReferenceEquals(Project, project)) IssuesError = NoRemoteStatus;
            return Task.CompletedTask;
        }
        // The seeded rows carry no depth of their own; this read is what establishes whether the
        // repository has more issues than the window holds. A reload of the project already open
        // re-reads the window it was paged to — a project switch is what resets that window.
        IssuesPageLoad = LoadIssuePageAsync(_issuesWindowSize);
        return IssuesPageLoad;
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
        CloseOperationHistoryOnProjectSwitch();
        CloseWorkflowLogOnProjectSwitch();

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
        DiffIsTruncated = false;
        SelectedDiffLine = null;
        CommitMessage = "";
        AmendMode = false;
        // The signing answer is per repository and deliberately unpersisted, so leaving one
        // takes the answer with it rather than carrying "unsigned" into the next.
        ResetSigningState();
        // The line named the repository being left, down to which account owns it.
        ResetGhIdentity();
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
        ResetHealthState();
        Stashes = [];
        StashesLoaded = false;
        SelectedStash = null;
        NewStashMessage = "";
        StashIncludeUntracked = false;
        SelectedCommit = null;
        CommitFiles = [];
        CommitDiffLines = [];
        CommitDiffIsTruncated = false;
        ResetHistoryWindow();
        CloseFileHistoryOnProjectSwitch();
        CloseCommitGraphOnProjectSwitch();
        CloseFindOnProjectSwitch();
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
        SigningRefresh = SafeRefreshSigningAsync();
        // Off the held status answer: a repository load re-reads which account the remote's host
        // resolves to, not gh's state, which changes only when the reader runs gh themselves.
        GhIdentityRefresh = SafeRefreshGhIdentityAsync(false);
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
        _manifestBaseline = p.Manifest.Copy();
        RefreshTaxonomyChoices();
        // A reload of the project already open re-reads the stored notes. While the editor is
        // open it holds text nothing has written yet, and the stored value is what that text is
        // replacing. A switch closes the editor first, so the incoming project still re-reads.
        if (!IsEditingNotes) Notes = p.Manifest.Notes;
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

    /// <summary>The manifest as this page last loaded or saved it, for telling an edit from a hold.</summary>
    private ProjectManifest _manifestBaseline = new();

    /// <summary>
    /// Persists a manifest, adopting it onto the project only once the store reports the write
    /// durable. The project is passed rather than read from <see cref="Project"/>: a write started
    /// on the way out of a project outlives the swap, and reading the live one would adopt the
    /// outgoing project's manifest onto the repository that took the screen.
    ///
    /// Taxonomy fields the reader did not touch are re-read from the store on the way out: this
    /// page writes the whole manifest, and a rename cascade that landed while it was open left
    /// the store holding a newer value under a field this editor is still showing at its loaded
    /// one — written back untouched, that stale hold would silently revert the cascade.
    /// </summary>
    private async Task<bool> PersistManifestAsync(ProjectInfo project, ProjectManifest manifest)
    {
        if (_discoveryService.Manifests.TryGet(project.FullPath, out var stored) && stored is not null)
        {
            foreach (var field in Taxonomy.Fields)
            {
                var edited = Taxonomy.ValueOf(manifest, field);
                var baseline = Taxonomy.ValueOf(_manifestBaseline, field);
                var current = Taxonomy.ValueOf(stored, field);
                if (string.Equals(edited, baseline, StringComparison.Ordinal)
                    && !string.Equals(current, baseline, StringComparison.Ordinal))
                    Taxonomy.SetValue(manifest, field, current);
            }
        }

        // The project's identity travels with the path: a scan that re-keyed this record while the
        // editor was open leaves this page holding a path the record moved off, and a write by
        // path alone would create an empty record there instead of reaching the edited one.
        if (!await _discoveryService.SaveManifestAsync(project.FullPath, manifest, project.Fingerprint)) return false;

        project.Manifest = manifest;
        project.HasManifest = true;
        _manifestBaseline = manifest.Copy();
        return true;
    }

    private async Task SaveManifestAsync()
    {
        if (Project is null) return;

        ManifestStatusText = await PersistManifestAsync(Project, EditedManifest())
            ? "Metadata saved."
            : ManifestSaveFailed;
    }
}
