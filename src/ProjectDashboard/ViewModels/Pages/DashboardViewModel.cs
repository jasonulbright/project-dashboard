using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.ViewModels.Pages;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly INavigationService _navigationService;
    private readonly SettingsService _settingsService;
    private readonly GitHubService _gitHubService;
    private readonly GitService _gitService;
    private readonly ProjectWatcherService _watcher;
    private readonly RepoBusyRegistry _busyRegistry;
    private readonly Action<Action> _uiPost;

    /// <summary>Null when the host supplied none; the dashboard then reports no interrupted operations rather than inventing one.</summary>
    private readonly RewriteRecoveryService? _recovery;
    private DispatcherTimer? _refreshTimer;

    [ObservableProperty] private ObservableCollection<ProjectInfo> _projects = [];
    [ObservableProperty] private ObservableCollection<ProjectInfo> _filteredProjects = [];
    [ObservableProperty] private ObservableCollection<string> _categories = ["All"];
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ObservableCollection<string> _sortOptions = ["Name", "Last Commit", "Status", "Dirty First", "Category"];
    [ObservableProperty] private string _selectedSort = "Name";

    /// <summary>Used to pass the selected project to ProjectDetailPage.</summary>
    public static ProjectInfo? SelectedProject { get; set; }

    /// <summary>
    /// Raised to open a project's detail view. MainWindow selects that project's
    /// sidebar item so navigation lands on the RIGHT project — navigating by page
    /// TYPE resolves to the first item of that type and the selection handler then
    /// overwrites SelectedProject with the wrong one.
    /// </summary>
    public event Action<ProjectInfo>? NavigateToProjectRequested;

    /// <summary>
    /// Raised to open a project's detail view with one work-area tab already selected.
    /// </summary>
    public event Action<ProjectInfo, DetailTab>? NavigateToProjectTabRequested;

    public int TotalCount => Projects.Count;
    public int CloudCount => Projects.Count(p => p.IsRemoteOnly);
    public bool HasCloud => CloudCount > 0;
    public int DirtyCount => Projects.Count(p => p.GitStatus.IsDirty);
    public int TodoCount => Projects.Count(p => p.TaskCount > 0 || p.BugCount > 0 || p.WaitCount > 0);
    public int TotalBugCount => Projects.Sum(p => p.BugCount);
    public int TotalWaitCount => Projects.Sum(p => p.WaitCount);
    public int TotalTaskCount => Projects.Sum(p => p.TaskCount);
    public int IssueCount => Projects.Sum(p => p.OpenIssueCount ?? 0);
    public int HiddenCount
    {
        get
        {
            var s = _settingsService.Load();
            var root = s.ProjectsRootPath;
            return s.ExcludedDirectories.Count(d =>
                Directory.Exists(Path.Combine(root, d)) &&
                GitService.IsGitRepo(Path.Combine(root, d)));
        }
    }

    public int MismatchCount => Projects.Count(p => !p.IsRemoteOnly && p.HasRemoteMismatch);
    public int IncompleteCount => Projects.Count(p => !p.IsRemoteOnly && p.HasIncompleteMetadata);
    public bool HasMismatches => MismatchCount > 0;
    public bool HasIncomplete => IncompleteCount > 0;

    public IAsyncRelayCommand LoadProjectsCommand { get; }
    public IAsyncRelayCommand ForceRefreshCommand { get; }

    /// <summary>
    /// <paramref name="uiPost"/> runs a callback on the UI thread; null takes the running
    /// application's dispatcher. A host without an <see cref="Application"/> has no
    /// dispatcher to marshal through, and a default that silently drops the callback there
    /// would drop the re-scan that a released repository lease is supposed to start.
    /// </summary>
    public DashboardViewModel(ProjectDiscoveryService discoveryService, INavigationService navigationService, SettingsService settingsService, GitHubService gitHubService, GitService gitService, ProjectWatcherService watcher, RepoBusyRegistry busyRegistry, Action<Action>? uiPost = null, RewriteRecoveryService? recovery = null)
    {
        _discoveryService = discoveryService;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _gitHubService = gitHubService;
        _gitService = gitService;
        _watcher = watcher;
        _busyRegistry = busyRegistry;
        _uiPost = uiPost ?? PostToApplicationDispatcher;
        _searchService = new RepoSearchService(gitService, busyRegistry);
        _recovery = recovery;
        // Detection completed before any window existed, so the list is read rather than
        // subscribed to; the change event only carries a later decision to drop a record.
        if (recovery is not null) recovery.PendingChanged += UpdateRecoveryBanner;
        UpdateRecoveryBanner();

        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);
        ForceRefreshCommand = new AsyncRelayCommand(ForceRefreshAsync);

        // The empty-state choice depends on whether a load is in flight, and IsRunning
        // raises only on the command itself.
        LoadProjectsCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
            NotifyContentState();
            if (!LoadProjectsCommand.IsRunning) _ = DrainRescanAsync();
        };

        // Every condition the re-scan gate refuses on needs a completion signal, or a scan
        // queued behind it waits for the next settings write that never comes.
        ForceRefreshCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning) && !ForceRefreshCommand.IsRunning)
                _ = DrainRescanAsync();
        };

        _settingsService.Changed += OnSettingsChanged;
        _busyRegistry.Changed += OnRepoBusyChanged;

        ReloadViewPreferences();

        // Fire and forget load on construction
        _ = LoadProjectsCommand.ExecuteAsync(null);

        // Auto-refresh timer (periodic full reconcile) + file watcher (immediate, per-repo)
        StartRefreshTimer();
        StartWatcher();
    }

    private void StartWatcher()
    {
        // Subscribe only; SyncWatcherToSettings (from the initial load) points it at the root.
        _watcher.Changed += OnRepoDirsChanged;
    }

    /// <summary>Watcher fired: refresh just the affected repos (empty = full refresh). Marshals to UI.</summary>
    private void OnRepoDirsChanged(IReadOnlyCollection<string> repoDirs)
    {
        _ = Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            // A bulk op (sync all / clone) is already writing git state; don't read a repo
            // mid-write (index.lock contention) only to have the op's own refresh clobber it.
            // A full scan in flight owns the project list; a second one replaces it underneath.
            if (_bulkOpRunning || LoadProjectsCommand.IsRunning || ForceRefreshCommand.IsRunning) return;
            try
            {
                if (repoDirs.Count == 0)
                {
                    await LoadProjectsCommand.ExecuteAsync(null);
                    return;
                }

                var names = new HashSet<string>(repoDirs, StringComparer.OrdinalIgnoreCase);
                // Never read a repo a destructive op is actively rewriting: its refs are mid-swap.
                var affected = Projects.Where(p => !p.IsRemoteOnly && names.Contains(p.DirectoryName)
                    && !_busyRegistry.IsBusy(p.FullPath)).ToList();
                var changed = false;
                foreach (var project in affected)
                {
                    // Local-only refresh — the watcher fires on every save; no gh/network here.
                    var refreshed = await _discoveryService.RefreshProjectLocalAsync(project.FullPath);
                    if (refreshed is null) continue;

                    // Carry forward GitHub-derived data a local refresh can't know, so the card
                    // doesn't flip to "local"/no-issues and drop out of a filtered view.
                    if (project.GitStatus.Visibility is "public" or "private" or "internal" or "unknown")
                        refreshed.GitStatus.Visibility = project.GitStatus.Visibility;

                    // Mutate the EXISTING instance in place (raises change) rather than replacing
                    // it — keeps sidebar/palette references valid and avoids a full sidebar rebuild
                    // (which would drop the current selection/focus on every save).
                    project.GitStatus = refreshed.GitStatus;
                    project.RecentCommits = refreshed.RecentCommits;
                    changed = true;
                }
                if (changed)
                {
                    ApplyFilters();
                    NotifySummary();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("watcher-driven refresh failed", ex);
            }
        });
    }

    private void StartRefreshTimer()
    {
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(SettingsDelta.EffectiveRefreshSeconds(_settingsService.Load()))
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            // Don't let the periodic reconcile collide with a bulk op or any scan in flight.
            if (LoadProjectsCommand.IsRunning || _bulkOpRunning || ForceRefreshCommand.IsRunning) return;
            try
            {
                await LoadProjectsCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                // A transient background-refresh failure must not pop an error dialog
                // every interval; the next tick retries anyway.
                Log.Warn("Scheduled refresh failed", ex);
            }
        };
        _refreshTimer.Start();
    }

    [ObservableProperty] private string _activeFilter = "all"; // "all", "dirty", "todos", "issues", "hidden"

    /// <summary>Backing list for the Hidden view; ApplyFilters sources from it while ActiveFilter == "hidden".</summary>
    private List<ProjectInfo> _hiddenSnapshot = [];

    // GitHub-not-ready banner state
    [ObservableProperty] private bool _ghBannerVisible;
    [ObservableProperty] private bool _ghSignInVisible;
    [ObservableProperty] private string _ghBannerText = "";
    private bool _ghBannerDismissed;

    // Discovery failure banner — a faulted scan must not just show an empty dashboard.
    [ObservableProperty] private string _discoveryErrorText = "";
    [ObservableProperty] private bool _discoveryErrorVisible;

    // Interrupted-history banner. The per-repository offer to restore lives on that
    // repository's page; a reader who never opens it would otherwise never learn the
    // operation was interrupted at all, so the dashboard names the repositories.
    [ObservableProperty] private bool _recoveryBannerVisible;
    [ObservableProperty] private string _recoveryBannerText = "";

    // Transient operation feedback (clone / bulk sync progress and outcomes).
    [ObservableProperty] private string _opStatusText = "";

    private bool _bulkOpActive;

    /// <summary>
    /// Serializes the dashboard-level bulk ops (clone, sync all) so their refreshes can't
    /// race. Clearing it releases a re-scan queued behind the operation.
    /// </summary>
    private bool _bulkOpRunning
    {
        get => _bulkOpActive;
        set
        {
            _bulkOpActive = value;
            if (!value) _ = DrainRescanAsync();
        }
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedSortChanged(string value) => ApplyFilters();

    // ── View preferences: pinned cards + density (X-10) ───────────────────────

    private readonly RepoSearchService _searchService;
    private HashSet<string> _pinnedKeys = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isCompactDensity;

    public Thickness CardPadding => IsCompactDensity ? new Thickness(9) : new Thickness(14);
    public double CardMinHeight => IsCompactDensity ? 132 : 200;

    /// <summary>Names the density the toggle switches TO, so the control reads as its own action.</summary>
    public string DensityToggleLabel => IsCompactDensity ? "Comfortable" : "Compact";

    partial void OnDiscoveryErrorVisibleChanged(bool value) => NotifyContentState();

    partial void OnIsCompactDensityChanged(bool value)
    {
        OnPropertyChanged(nameof(CardPadding));
        OnPropertyChanged(nameof(CardMinHeight));
        OnPropertyChanged(nameof(DensityToggleLabel));
    }

    /// <summary>Reads pinned paths and density from settings without touching the project list.</summary>
    private void ReloadViewPreferences()
    {
        var settings = _settingsService.Load();
        _pinnedKeys = DashboardOrdering.KeySet(settings.PinnedProjectPaths);
        IsCompactDensity = string.Equals(settings.CardDensity, "compact", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ToggleDensity()
    {
        IsCompactDensity = !IsCompactDensity;
        // Load-mutate-save, never a fresh AppSettings: the window geometry and every
        // other key live in the same file and a wholesale write would reset them.
        var settings = _settingsService.Load();
        settings.CardDensity = IsCompactDensity ? "compact" : "comfortable";
        _settingsService.Save(settings);
    }

    [RelayCommand]
    private void TogglePin(ProjectInfo? project)
    {
        // A remote-only card has no path to key a pin on; it isn't in the grid's local set.
        if (project is null || project.IsRemoteOnly || string.IsNullOrEmpty(project.FullPath)) return;

        var key = DashboardOrdering.RepoKey(project.FullPath);
        var settings = _settingsService.Load();

        if (_pinnedKeys.Remove(key))
            settings.PinnedProjectPaths = DashboardOrdering.WithoutPin(settings.PinnedProjectPaths, project.FullPath);
        else
        {
            _pinnedKeys.Add(key);
            settings.PinnedProjectPaths = DashboardOrdering.WithPin(settings.PinnedProjectPaths, project.FullPath);
        }

        _settingsService.Save(settings);

        ApplyPinnedFlags();
        ApplyFilters();
    }

    private void ApplyPinnedFlags()
    {
        foreach (var project in Projects) project.IsPinned = DashboardOrdering.IsPinned(project, _pinnedKeys);
        foreach (var project in _hiddenSnapshot) project.IsPinned = DashboardOrdering.IsPinned(project, _pinnedKeys);
    }

    // ── Empty states (X-14) ───────────────────────────────────────────────────

    /// <summary>
    /// Probed once per load: two bindings read the root and every dashboard-search
    /// keystroke re-raises them, so reading settings per property would mean a
    /// synchronous file read and deserialize per character on the UI thread.
    /// </summary>
    private bool _rootExists = true;
    private string _configuredRoot = "";

    private void ProbeConfiguredRoot()
    {
        _configuredRoot = _settingsService.Load().ProjectsRootPath;
        _rootExists = Directory.Exists(_configuredRoot);
    }

    public DashboardContent Content => DashboardEmptyState.Select(
        LoadProjectsCommand.IsRunning, DiscoveryErrorVisible, _rootExists, Projects.Count, FilteredProjects.Count);

    public bool ShowLoading => Content == DashboardContent.Loading;
    public bool ShowScanFailed => Content == DashboardContent.ScanFailed;
    public bool ShowRootMissing => Content == DashboardContent.RootMissing;
    public bool ShowEmptyRoot => Content == DashboardContent.EmptyRoot;
    public bool ShowNoMatches => Content == DashboardContent.NoMatches;
    public bool ShowCards => Content == DashboardContent.Cards;

    /// <summary>Inline progress for a reload that keeps the rendered grid in place.</summary>
    public bool ShowRefreshing => LoadProjectsCommand.IsRunning && Content == DashboardContent.Cards;

    public string ConfiguredRootPath => _configuredRoot;

    private void NotifyContentState()
    {
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowScanFailed));
        OnPropertyChanged(nameof(ShowRootMissing));
        OnPropertyChanged(nameof(ShowEmptyRoot));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(ShowRefreshing));
        OnPropertyChanged(nameof(ConfiguredRootPath));
    }

    // ── Card quick actions (X-11) ─────────────────────────────────────────────

    [RelayCommand] private Task FetchProject(ProjectInfo? project) => RunCardActionAsync(project, CardAction.Fetch);
    [RelayCommand] private Task PullProject(ProjectInfo? project) => RunCardActionAsync(project, CardAction.Pull);
    [RelayCommand] private Task PushProject(ProjectInfo? project) => RunCardActionAsync(project, CardAction.Push);

    /// <summary>
    /// Runs one card-level git op under the same refusals bulk sync applies. Every path
    /// out of here writes OpStatusText: a refused action that said nothing would read as
    /// a dead button.
    /// </summary>
    private async Task RunCardActionAsync(ProjectInfo? project, CardAction action)
    {
        if (project is null) return;
        var verb = DashboardCardActions.Verb(action);
        var name = project.DirectoryName;

        var busy = !string.IsNullOrEmpty(project.FullPath) && _busyRegistry.IsBusy(project.FullPath);
        var refusal = DashboardCardActions.RefuseReason(project, action, _bulkOpRunning, busy);
        if (refusal is not null)
        {
            OpStatusText = $"{verb} {name}: {refusal}";
            return;
        }

        // The lease both claims the repo and makes the watcher, the periodic reconcile,
        // and Sync All skip it while git is writing refs here.
        if (!_busyRegistry.TryAcquire(project.FullPath, out var lease))
        {
            OpStatusText = $"{verb} {name}: {DashboardCardActions.BusyReason}";
            return;
        }

        try
        {
            OpStatusText = $"{verb} {name}…";
            var fetch = await _gitService.FetchAsync(project.FullPath);
            if (!fetch.Success)
            {
                OpStatusText = $"{name}: fetch failed — {fetch.FirstError}";
                return;
            }
            if (action == CardAction.Fetch)
            {
                OpStatusText = $"{name}: fetched.";
                return;
            }

            var state = await _gitService.GetWorkingStateAsync(project.FullPath);
            var postFetch = DashboardCardActions.RefuseReason(
                project, action, bulkOpRunning: false, repoBusy: false, hasUpstream: state?.HasUpstream);
            if (postFetch is not null)
            {
                OpStatusText = $"{verb} {name}: {postFetch}";
                return;
            }
            if (state is null)
            {
                OpStatusText = $"{verb} {name}: {DashboardCardActions.StatusUnavailableReason}";
                return;
            }

            // Divergence is only knowable after the fetch; the pre-flight guard saw stale counts.
            if (state.Ahead > 0 && state.Behind > 0)
            {
                OpStatusText = $"{verb} {name}: diverged (↑{state.Ahead} ↓{state.Behind}) — resolve in a terminal.";
                return;
            }

            if (action == CardAction.Pull)
            {
                if (state.Behind == 0) { OpStatusText = $"{name}: already up to date."; return; }
                var pull = await _gitService.PullAsync(project.FullPath);
                OpStatusText = pull.Success
                    ? $"{name}: pulled {state.Behind}."
                    : $"{name}: pull failed — {pull.FirstError}";
            }
            else
            {
                if (state.Ahead == 0) { OpStatusText = $"{name}: nothing to push."; return; }
                var push = await _gitService.PushAsync(project.FullPath);
                OpStatusText = push.Success
                    ? $"{name}: pushed {state.Ahead}."
                    : $"{name}: push failed — {push.FirstError}";
            }
        }
        catch (Exception ex)
        {
            OpStatusText = $"{verb} {name}: {ex.Message}";
            Log.Warn($"card {verb} failed for {project.FullPath}", ex);
        }
        finally
        {
            // Released before the refresh so the reconcile it triggers can read the repo.
            lease.Dispose();
            await RefreshSingle(project);
        }
    }

    // ── Deep links into the detail page's tabs (X-11) ─────────────────────────

    [RelayCommand] private void OpenChangesTab(ProjectInfo? project) => OpenAtTab(project, DetailTab.Changes);
    [RelayCommand] private void OpenBranchesTab(ProjectInfo? project) => OpenAtTab(project, DetailTab.Branches);
    [RelayCommand] private void OpenIssuesTab(ProjectInfo? project) => OpenAtTab(project, DetailTab.Issues);
    [RelayCommand] private void OpenPullRequestsTab(ProjectInfo? project) => OpenAtTab(project, DetailTab.PullRequests);

    private void OpenAtTab(ProjectInfo? project, DetailTab tab)
    {
        if (project is null || project.IsRemoteOnly || string.IsNullOrEmpty(project.FullPath)) return;
        SelectedProject = project;
        if (NavigateToProjectTabRequested is not null)
            NavigateToProjectTabRequested.Invoke(project, tab);
        else
            OpenProject(project);
    }

    [RelayCommand]
    private void CopyPath(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.FullPath)) return;
        try
        {
            Clipboard.SetText(project.FullPath);
            OpStatusText = $"Copied {project.FullPath}";
        }
        catch (Exception ex)
        {
            // Another process holding the clipboard makes SetText throw; that is a
            // failed copy, not a crash.
            OpStatusText = $"Copy path failed — {ex.Message}";
            Log.Warn("clipboard copy failed", ex);
        }
    }

    // ── Global search fan-out (X-12) ──────────────────────────────────────────

    /// <summary>
    /// Searches every discovered local repo. Remote-only cards carry no working tree,
    /// so they never reach the fan-out.
    /// </summary>
    public Task<RepoSearchResult> SearchAllReposAsync(string term, CancellationToken ct)
    {
        var targets = Projects
            .Where(p => !p.IsRemoteOnly && !string.IsNullOrEmpty(p.FullPath))
            .Select(p => new RepoSearchTarget(p.DisplayName, p.FullPath))
            .ToList();
        return _searchService.SearchAsync(term, targets, ct);
    }

    /// <summary>The loaded project whose working tree is at this path, if any.</summary>
    public ProjectInfo? FindByPath(string repoPath) =>
        Projects.FirstOrDefault(p => !p.IsRemoteOnly
            && !string.IsNullOrEmpty(p.FullPath)
            && string.Equals(DashboardOrdering.RepoKey(p.FullPath), DashboardOrdering.RepoKey(repoPath),
                StringComparison.OrdinalIgnoreCase));

    /// <summary>Summary-bar filter. Key: "all" | "dirty" | "todos" | "issues" | "mismatch" | "incomplete".</summary>
    [RelayCommand]
    private void SetFilter(string? filter)
    {
        ActiveFilter = string.IsNullOrEmpty(filter) ? "all" : filter;
        SelectedCategory = "All";
        SearchText = "";
        ApplyFilters();
    }

    [RelayCommand]
    private Task FilterHidden() => ShowHiddenProjectsAsync();

    [RelayCommand]
    private async Task NewProject()
    {
        if (_bulkOpRunning) { OpStatusText = "Another operation is in progress — try again in a moment."; return; }
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "New Project",
            Content = new System.Windows.Controls.StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.TextBlock
                    {
                        Text = "Project name (folder name, lowercase, no spaces):",
                        Margin = new System.Windows.Thickness(0, 0, 0, 8)
                    },
                    new Wpf.Ui.Controls.TextBox
                    {
                        Name = "ProjectNameBox",
                        PlaceholderText = "my-new-project",
                        MinWidth = 300
                    }
                }
            },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel"
        };

        var result = await dialog.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        var stack = dialog.Content as System.Windows.Controls.StackPanel;
        var textBox = stack?.Children[1] as Wpf.Ui.Controls.TextBox;
        var projectName = textBox?.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(projectName)) return;

        projectName = System.Text.RegularExpressions.Regex.Replace(
            projectName.ToLowerInvariant().Replace(' ', '-'), @"[^a-z0-9\-]", "");

        if (string.IsNullOrWhiteSpace(projectName)) return;

        var settings = _settingsService.Load();
        var projectPath = Path.Combine(settings.ProjectsRootPath, projectName);

        if (Directory.Exists(projectPath))
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Error",
                Content = $"Folder already exists: {projectPath}",
                CloseButtonText = "OK"
            }.ShowDialogAsync();
            return;
        }

        var gitError = await ScaffoldProjectAsync(projectPath, projectName);
        if (gitError is not null)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Project created, git setup incomplete",
                Content = $"The folder and files were created, but git reported:\n\n{gitError}",
                CloseButtonText = "OK"
            }.ShowDialogAsync();
        }
    }

    /// <summary>
    /// Seeds the new project's folder and brings it onto the grid, holding the bulk-op flag
    /// across both. Returns git's error text when the repository was left without its first
    /// commit, so the report reaches the user outside the flag rather than stalling every
    /// queued re-scan behind a modal.
    /// </summary>
    internal async Task<string?> ScaffoldProjectAsync(string projectPath, string projectName)
    {
        _bulkOpRunning = true;
        try
        {
            Directory.CreateDirectory(projectPath);

            File.WriteAllText(Path.Combine(projectPath, "README.md"),
                $"# {projectName}\n\n");

            File.WriteAllText(Path.Combine(projectPath, "CHANGELOG.md"),
                $"# Changelog\n\n## [0.1.0] - {DateTime.Now:yyyy-MM-dd}\n\n### Added\n- Initial project scaffold\n");

            // Project metadata -> stored out-of-source under AppPaths.RoamingDir, not in the repo.
            var manifest = new ProjectManifest
            {
                ProjectType = "unknown",
                Status = "experimental",
                Category = "Uncategorized",
                ValidationSchedule = "none",
                Notes = ""
            };
            await _discoveryService.SaveManifestAsync(projectPath, manifest);

            var gitError = await _gitService.InitWithFirstCommitAsync(projectPath, "Initial project scaffold");
            await ForceRefreshAsync();
            return gitError;
        }
        finally { _bulkOpRunning = false; }
    }

    /// <summary>
    /// Clone dialog: paste a URL or pick from the signed-in user's repositories
    /// (type-to-filter). Clones into the configured projects root, then refreshes.
    /// </summary>
    [RelayCommand]
    private async Task CloneRepo()
    {
        if (_bulkOpRunning) { OpStatusText = "Another operation is in progress — try again in a moment."; return; }
        List<RemoteRepo> repos = [];
        try { repos = await _gitHubService.GetUserReposAsync(); }
        catch (Exception ex) { Log.Warn("repo list for clone unavailable", ex); }

        var urlBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Repository URL, owner/repo — or type to filter your repos below",
            MinWidth = 460
        };
        var list = new System.Windows.Controls.ListBox
        {
            MaxHeight = 280,
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
            ItemsSource = repos,
            DisplayMemberPath = nameof(RemoteRepo.NameWithOwner)
        };
        System.Windows.Automation.AutomationProperties.SetName(list, "Your repositories");
        System.Windows.Automation.AutomationProperties.SetName(urlBox, "Repository URL or filter");
        var syncing = false;
        urlBox.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var term = urlBox.Text.Trim();
            list.ItemsSource = term.Length == 0
                ? repos
                : repos.Where(r => r.NameWithOwner.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        // Picking a repo writes its slug into the box, so editing the filter afterward
        // can't silently discard the choice — the text itself is a valid clone target.
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is RemoteRepo r)
            {
                syncing = true;
                urlBox.Text = r.NameWithOwner;
                syncing = false;
            }
        };

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Clone repository",
            Content = new System.Windows.Controls.StackPanel { Children = { urlBox, list } },
            PrimaryButtonText = "Clone",
            CloseButtonText = "Cancel"
        };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        // Selection writes its slug into the box, so the box text is authoritative; a full
        // URL, owner/repo, or a picked repo all resolve here.
        var typed = urlBox.Text.Trim();
        if (typed.Length == 0)
        {
            if (list.SelectedItem is not RemoteRepo picked) return;
            typed = picked.NameWithOwner;
        }
        var url = typed.Contains("://") || typed.Contains('@') ? typed
            : $"https://github.com/{typed.TrimEnd('/')}.git";

        var repoName = GitRemote.RepoNameFromUrl(url);
        if (repoName.Length == 0)
        {
            OpStatusText = "Clone: that doesn't look like a valid repository URL.";
            return;
        }

        var settings = _settingsService.Load();
        var target = Path.Combine(settings.ProjectsRootPath, repoName);
        if (Directory.Exists(target))
        {
            OpStatusText = $"Clone: {repoName} already exists in the projects root.";
            return;
        }

        _bulkOpRunning = true;
        try
        {
            OpStatusText = $"Cloning {repoName}…";
            var error = await _gitService.CloneAsync(url, settings.ProjectsRootPath);
            OpStatusText = error is null ? $"Cloned {repoName}." : $"Clone failed: {error}";
            if (error is null)
                await ForceRefreshAsync();
        }
        finally { _bulkOpRunning = false; }
    }

    /// <summary>
    /// Fetches every clean repo with a remote; fast-forwards the ones behind,
    /// pushes the ones ahead. Dirty, diverged, conflicted, and error repos are
    /// skipped and reported — bulk sync must never create surprise merges.
    /// </summary>
    [RelayCommand]
    private async Task SyncAll()
    {
        if (_bulkOpRunning) { OpStatusText = "Another operation is in progress — try again in a moment."; return; }
        var candidates = Projects.Where(p =>
                !p.GitStatus.HasError &&
                !p.GitStatus.IsDirty &&
                !p.GitStatus.NeedsAttention &&
                !p.GitStatus.IsDetached &&
                // Remote-only cards have RemoteUrl but FullPath == ""; an empty
                // path makes git run in the process cwd instead of the repo.
                !p.IsRemoteOnly &&
                !string.IsNullOrEmpty(p.FullPath) &&
                !string.IsNullOrEmpty(p.GitStatus.RemoteUrl) &&
                // A repo under a destructive op is off-limits to bulk sync until it releases.
                !_busyRegistry.IsBusy(p.FullPath))
            .ToList();
        var skipped = Projects.Count - candidates.Count;
        if (candidates.Count == 0)
        {
            OpStatusText = "Sync all: no clean repos with a remote to sync.";
            return;
        }

        _bulkOpRunning = true;
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        var semaphore = new SemaphoreSlim(4);

        try
        {
        await Task.WhenAll(candidates.Select(async p =>
        {
            await semaphore.WaitAsync();
            try
            {
                var name = p.DirectoryName;
                var fetch = await _gitService.FetchAsync(p.FullPath);
                if (!fetch.Success)
                {
                    outcomes.Add($"{name}: fetch failed — {fetch.FirstError}");
                    return;
                }

                var state = await _gitService.GetWorkingStateAsync(p.FullPath);
                if (state is null || !state.HasUpstream) return; // fetched; nothing to reconcile

                switch (ahead: state.Ahead, behind: state.Behind)
                {
                    case (0, 0):
                        break;
                    case (0, > 0):
                        var pull = await _gitService.PullAsync(p.FullPath);
                        outcomes.Add(pull.Success ? $"{name}: pulled {state.Behind}" : $"{name}: pull failed — {pull.FirstError}");
                        break;
                    case ( > 0, 0):
                        var push = await _gitService.PushAsync(p.FullPath);
                        outcomes.Add(push.Success ? $"{name}: pushed {state.Ahead}" : $"{name}: push failed — {push.FirstError}");
                        break;
                    default:
                        outcomes.Add($"{name}: diverged (↑{state.Ahead} ↓{state.Behind}) — resolve in a terminal");
                        break;
                }
            }
            catch (Exception ex)
            {
                outcomes.Add($"{p.DirectoryName}: {ex.Message}");
                Log.Warn($"sync-all failed for {p.FullPath}", ex);
            }
            finally
            {
                var n = Interlocked.Increment(ref done);
                OpStatusText = $"Sync all: {n}/{candidates.Count}…";
                semaphore.Release();
            }
        }));

        var changed = outcomes.OrderBy(s => s).ToList();
        OpStatusText = changed.Count == 0
            ? $"Sync all: {candidates.Count} repos fetched, everything already in sync." + (skipped > 0 ? $" ({skipped} skipped)" : "")
            : $"Sync all: done. {changed.Count} repos changed" + (skipped > 0 ? $" ({skipped} skipped)." : ".");

        if (changed.Count > 0)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Sync all — results",
                Content = string.Join("\n", changed),
                CloseButtonText = "OK"
            }.ShowDialogAsync();
        }

        await ForceRefreshAsync();
        }
        finally { _bulkOpRunning = false; }
    }

    [RelayCommand]
    private void OpenProject(ProjectInfo? project)
    {
        if (project is null) return;
        // Remote-only cards have no local repo to open — clicking clones instead.
        if (project.IsRemoteOnly)
        {
            _ = CloneRemoteOnly(project);
            return;
        }
        SelectedProject = project;
        if (NavigateToProjectRequested is not null)
            NavigateToProjectRequested.Invoke(project);
        else
            _navigationService.Navigate(typeof(ProjectDetailPage));
    }

    /// <summary>Clones a Cloud card's repo into the projects root, then refreshes.</summary>
    [RelayCommand]
    private async Task CloneRemoteOnly(ProjectInfo? project)
    {
        if (project is null || !project.IsRemoteOnly || project.RemoteSlug.Length == 0) return;
        // A card LeftClick calls this raw method (not the AsyncRelayCommand), so it bypasses
        // the command's IsRunning gate — a fast second click would clone twice into one path.
        if (_bulkOpRunning) { OpStatusText = "Another operation is in progress — try again in a moment."; return; }

        var settings = _settingsService.Load();
        var target = Path.Combine(settings.ProjectsRootPath, project.DirectoryName);
        if (Directory.Exists(target))
        {
            OpStatusText = $"Clone: {project.DirectoryName} already exists in the projects root.";
            return;
        }

        _bulkOpRunning = true;
        try
        {
            OpStatusText = $"Cloning {project.DirectoryName}…";
            var url = $"https://github.com/{project.RemoteSlug}.git";
            var error = await _gitService.CloneAsync(url, settings.ProjectsRootPath);
            OpStatusText = error is null ? $"Cloned {project.DirectoryName}." : $"Clone failed: {error}";
            if (error is null)
                await ForceRefreshAsync();
        }
        finally { _bulkOpRunning = false; }
    }

    [RelayCommand]
    private async Task RefreshSingle(ProjectInfo? project)
    {
        // A remote-only card has no repo to run git in; refreshing an empty path
        // would spawn git in the process cwd and throw from the manifest lookup.
        if (project is null || project.IsRemoteOnly || string.IsNullOrEmpty(project.FullPath)) return;
        var refreshed = await _discoveryService.RefreshProjectAsync(project);
        if (refreshed is null) return;

        // A refresh REPLACES the instance; the pin flag is view state the new one lacks.
        refreshed.IsPinned = DashboardOrdering.IsPinned(refreshed, _pinnedKeys);

        var idx = Projects.IndexOf(project);
        if (idx >= 0)
        {
            Projects[idx] = refreshed;
            // Indexer writes don't raise PropertyChanged(Projects); poke the sidebar.
            OnPropertyChanged(nameof(Projects));
        }
        else
        {
            // Hidden-view cards live in the hidden snapshot, not Projects.
            var hIdx = _hiddenSnapshot.IndexOf(project);
            if (hIdx < 0) return;
            refreshed.IsHidden = true;
            _hiddenSnapshot[hIdx] = refreshed;
        }

        ApplyFilters();
        NotifySummary();
    }

    [RelayCommand]
    private void OpenGitHub(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.GitHubSlug)) return;
        Process.Start(new ProcessStartInfo($"https://github.com/{project.GitHubSlug}") { UseShellExecute = true });
    }

    /// <summary>Opens the repo's open-issues list on GitHub (the same set the card count reflects).</summary>
    [RelayCommand]
    private void OpenIssues(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.GitHubSlug)) return;
        Process.Start(new ProcessStartInfo(
            $"https://github.com/{project.GitHubSlug}/issues?q=is:issue+is:open") { UseShellExecute = true });
    }

    /// <summary>Opens the repo's open pull-requests list on GitHub.</summary>
    [RelayCommand]
    private void OpenPullRequests(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.GitHubSlug)) return;
        Process.Start(new ProcessStartInfo(
            $"https://github.com/{project.GitHubSlug}/pulls") { UseShellExecute = true });
    }

    /// <summary>Opens a pre-filled, labeled GitHub "new issue" page for the project.</summary>
    [RelayCommand]
    private void ReportBug(ProjectInfo? project)
        => OpenNewIssue(project, "bug", BugReportBody());

    [RelayCommand]
    private void RequestFeature(ProjectInfo? project)
        => OpenNewIssue(project, "enhancement", "");

    private static void OpenNewIssue(ProjectInfo? project, string label, string body)
    {
        if (project is null || string.IsNullOrEmpty(project.GitHubSlug)) return;
        var url = $"https://github.com/{project.GitHubSlug}/issues/new"
                + $"?labels={Uri.EscapeDataString(label)}"
                + $"&body={Uri.EscapeDataString(body)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string BugReportBody()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var net = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        return "**Describe the bug**\n\n\n"
             + "**Steps to reproduce**\n\n\n"
             + "**Environment**\n"
             + $"- App: Project Dashboard {asm}\n"
             + $"- OS: {os}\n"
             + $"- .NET: {net}\n";
    }

    [RelayCommand]
    private async Task HideProject(ProjectInfo? project)
    {
        if (project is null) return;

        var settings = _settingsService.Load();
        var excluded = new List<string>(settings.ExcludedDirectories) { project.DirectoryName };
        settings.ExcludedDirectories = excluded.Distinct().ToArray();
        // The exclusion change is what schedules the re-scan; a second direct scan here
        // would run concurrently with it over the same grid.
        _settingsService.Save(settings);

        await DrainRescanAsync();
        if (RescanQueued)
            OpStatusText = $"{project.DisplayName} is now hidden — its card clears when the queued rescan runs.";
    }

    [RelayCommand]
    private async Task UnhideProject(ProjectInfo? project)
    {
        if (project is null) return;

        var settings = _settingsService.Load();
        var excluded = new List<string>(settings.ExcludedDirectories);
        excluded.Remove(project.DirectoryName);
        settings.ExcludedDirectories = excluded.ToArray();
        _settingsService.Save(settings);

        // Refresh main list first, then re-render the hidden view without the unhidden repo.
        await DrainRescanAsync();
        // The hidden view drops it either way; without the re-scan the grid has not picked
        // it up yet, so the card is in neither list until the queued scan runs.
        if (RescanQueued)
            OpStatusText = $"{project.DisplayName} is no longer hidden — it returns to the grid when the queued rescan runs.";
        await ShowHiddenProjectsAsync();
    }

    public async Task ShowHiddenProjectsAsync()
    {
        ActiveFilter = "hidden";

        var settings = _settingsService.Load();
        var rootPath = settings.ProjectsRootPath;
        if (!Directory.Exists(rootPath))
        {
            FilteredProjects = [];
            return;
        }
        var excluded = new HashSet<string>(settings.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);

        var hiddenDirs = Directory.GetDirectories(rootPath)
            .Where(d => excluded.Contains(Path.GetFileName(d)) && GitService.IsGitRepo(d))
            .ToList();

        var hiddenList = new List<ProjectInfo>();
        foreach (var dir in hiddenDirs)
        {
            var dirName = Path.GetFileName(dir);
            var stub = new ProjectInfo { DirectoryName = dirName, FullPath = dir, DisplayName = dirName };
            var full = await _discoveryService.RefreshProjectAsync(stub);
            if (full is null) continue;
            // Flag, don't mutate the manifest — Status must never be overwritten by view state.
            full.IsHidden = true;
            full.IsPinned = DashboardOrdering.IsPinned(full, _pinnedKeys);
            hiddenList.Add(full);
        }

        _hiddenSnapshot = hiddenList.OrderBy(p => p.DisplayName).ToList();
        ApplyFilters();
    }

    /// <summary>
    /// Synchronizes FilteredProjects to the target sequence with minimal remove/
    /// insert/move operations instead of replacing the collection. Surviving items
    /// keep their item containers, so keyboard focus on a card outlives every
    /// search keystroke, chip click, and sort change (a wholesale replacement
    /// regenerated all containers and silently dropped focus).
    /// </summary>
    private void SetDisplayedProjects(IEnumerable<ProjectInfo> target)
    {
        var desired = target.ToList();
        var desiredSet = new HashSet<ProjectInfo>(desired);

        for (int i = FilteredProjects.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(FilteredProjects[i]))
                FilteredProjects.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var current = FilteredProjects.IndexOf(item);
            if (current < 0)
                FilteredProjects.Insert(Math.Min(i, FilteredProjects.Count), item);
            else if (current != i)
                FilteredProjects.Move(current, i);
        }
    }

    [RelayCommand]
    private void OpenFolder(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.FullPath)) return;
        // Shell-execute the folder itself — passing it as an unquoted explorer.exe
        // argument split paths containing spaces into multiple tokens.
        Process.Start(new ProcessStartInfo(project.FullPath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenTerminal(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.FullPath)) return;
        Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{project.FullPath}\"")
            { UseShellExecute = true });
    }

    private string _watchedRoot = "";

    /// <summary>Re-point the watcher if the root path or the toggle changed since last time.</summary>
    private void SyncWatcherToSettings()
    {
        var settings = _settingsService.Load();
        var root = settings.EnableAutoRefresh ? settings.ProjectsRootPath : "";
        if (string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase)) return;

        _watchedRoot = root;
        if (root.Length == 0) _watcher.Stop();
        else _watcher.Start(root);
    }

    // ── Live-apply settings (X-09) ────────────────────────────────────────────

    /// <summary>What a queued or running settings-driven re-scan is doing; empty when idle.</summary>
    [ObservableProperty] private string _rescanStatus = "";

    private bool _rescanQueued;
    private Task _rescanDrain = Task.CompletedTask;

    /// <summary>The period the reconcile timer is actually running at.</summary>
    internal TimeSpan RefreshInterval => _refreshTimer?.Interval ?? TimeSpan.Zero;

    /// <summary>The re-scan currently in flight, or a completed task when none is.</summary>
    internal Task PendingRescan => _rescanDrain;

    /// <summary>True while a re-scan is waiting for the repositories to be free.</summary>
    internal bool RescanQueued => _rescanQueued;

    /// <summary>
    /// Applies a settings write to the running app. Every branch is a re-derive from the
    /// new state, never a mutation of what the writer already changed, so a write from any
    /// source — this page, the Settings page, an external editor — lands the same way.
    /// </summary>
    private void OnSettingsChanged(SettingsChange change)
    {
        if (SettingsDelta.RefreshIntervalChanged(change) && _refreshTimer is not null)
            _refreshTimer.Interval = TimeSpan.FromSeconds(SettingsDelta.EffectiveRefreshSeconds(change.Current));

        if (SettingsDelta.WatcherTargetChanged(change))
            SyncWatcherToSettings();

        if (SettingsDelta.ViewPreferencesChanged(change))
        {
            ReloadViewPreferences();
            ApplyPinnedFlags();
            ApplyFilters();
        }

        if (SettingsDelta.RediscoveryRequired(change))
            RequestRescan();
    }

    private void OnRepoBusyChanged(string repoPath)
    {
        if (!_rescanQueued) return;
        // The registry raises from whichever thread released the lease; the drain touches
        // bound state and the discovery pipeline, both of which belong to the UI thread.
        _uiPost(() => _ = DrainRescanAsync());
    }

    private static void PostToApplicationDispatcher(Action callback) =>
        _ = Application.Current?.Dispatcher.InvokeAsync(callback);

    private void RequestRescan()
    {
        _rescanQueued = true;
        _ = DrainRescanAsync();
    }

    /// <summary>
    /// The one in-flight re-scan, shared by every caller. Two overlapping full scans would
    /// each rebuild the card grid from a list the other is still writing.
    /// </summary>
    private Task DrainRescanAsync() =>
        _rescanDrain.IsCompleted ? _rescanDrain = RunQueuedRescanAsync() : _rescanDrain;

    /// <summary>
    /// Runs the queued re-scan once nothing else owns the repositories. A repo under a
    /// rewrite or surgery is mid-swap: reading it there is what the busy registry exists to
    /// prevent, so the scan waits for the last lease rather than being dropped, and says so.
    /// </summary>
    private async Task RunQueuedRescanAsync()
    {
        try
        {
            while (_rescanQueued)
            {
                if (!RescanAllowed())
                {
                    RescanStatus = DashboardRescan.QueuedStatus;
                    return;
                }

                _rescanQueued = false;
                RescanStatus = DashboardRescan.RunningStatus;
                // Through the command: its IsRunning is what the gate below and every
                // other scan trigger read, and a direct call leaves them reading idle for
                // the whole drain.
                await ForceRefreshCommand.ExecuteAsync(null);
            }
            RescanStatus = "";
        }
        catch (Exception ex)
        {
            RescanStatus = "";
            Log.Warn("settings-driven rescan failed", ex);
        }
    }

    private bool RescanAllowed() => DashboardRescan.Allowed(
        _bulkOpRunning, _busyRegistry.AnyBusy, LoadProjectsCommand.IsRunning, ForceRefreshCommand.IsRunning);

    private async Task LoadProjectsAsync()
    {
        SyncWatcherToSettings();
        try
        {
            ProbeConfiguredRoot();
            var results = await _discoveryService.DiscoverAllAsync();
            UpdateProjectList(results);
            DiscoveryErrorVisible = false;
        }
        catch (Exception ex)
        {
            // The ctor kicks this off fire-and-forget: without this catch a faulted
            // scan (unplugged drive, denied root) showed an empty dashboard forever
            // with no explanation and the exception parked unobserved on the command.
            ReportDiscoveryFailure(ex);
        }
        await UpdateGhBannerAsync();
    }

    private Task _forceRefresh = Task.CompletedTask;

    /// <summary>
    /// The one in-flight force refresh, shared by every caller. The command's CanExecute
    /// stops a second toolbar press, but the palette, F5, the Settings page, and the
    /// settings-driven drain all execute without consulting it; two overlapping runs each
    /// replace the project list from a git fan-out the other is still running.
    ///
    /// Every direct caller holds _bulkOpRunning across the call, or runs through
    /// ForceRefreshCommand. Neither leaves the re-scan gate reading idle: a settings write
    /// arriving during an unguarded direct call passes the gate, coalesces onto the scan
    /// already in flight, and its change never reaches the grid.
    /// </summary>
    private Task ForceRefreshAsync() =>
        _forceRefresh.IsCompleted ? _forceRefresh = RunForceRefreshAsync() : _forceRefresh;

    private async Task RunForceRefreshAsync()
    {
        try
        {
            ProbeConfiguredRoot();
            var results = await _discoveryService.ForceRefreshAllAsync();
            UpdateProjectList(results);
            DiscoveryErrorVisible = false;
        }
        catch (Exception ex)
        {
            ReportDiscoveryFailure(ex);
        }
        await UpdateGhBannerAsync();
    }

    private void ReportDiscoveryFailure(Exception ex)
    {
        Log.Error("Project discovery failed", ex);
        var root = _settingsService.Load().ProjectsRootPath;
        DiscoveryErrorText = $"Couldn't scan {root} — {ex.Message}";
        DiscoveryErrorVisible = true;
        NotifyContentState();
    }

    private async Task UpdateGhBannerAsync()
    {
        if (_ghBannerDismissed) { GhBannerVisible = false; return; }

        string summary;
        try { summary = await _gitHubService.GetAuthSummaryAsync(); }
        catch { summary = "Unavailable"; }

        if (summary == "Signed in") { GhBannerVisible = false; return; }

        GhSignInVisible = summary == "Found, not signed in";
        GhBannerText = GhSignInVisible
            ? "GitHub features are off — you're not signed in to the GitHub CLI. Repos show as local until you sign in."
            : "GitHub features are off — the GitHub CLI (gh) wasn't found. Repos show as local; install gh, then set its path in Settings.";
        GhBannerVisible = true;
    }

    [RelayCommand]
    private void DismissGhBanner()
    {
        _ghBannerDismissed = true;
        GhBannerVisible = false;
    }

    /// <summary>
    /// Names the repositories whose last history operation was interrupted. It offers no action
    /// of its own: restoring is gated on the repository's own page, where the backup, the
    /// clean-tree check, and the typed confirmation live.
    /// </summary>
    private void UpdateRecoveryBanner()
    {
        RecoveryBannerText = DescribeInterrupted(_recovery?.Pending ?? []) ?? "";
        RecoveryBannerVisible = RecoveryBannerText.Length > 0;
    }

    /// <summary>
    /// The banner's wording, or null when there is nothing to report. Kept pure so the claim it
    /// makes is testable without standing up a dashboard's timer, watcher, and first scan.
    /// </summary>
    internal static string? DescribeInterrupted(IReadOnlyList<RewriteJournalEntry> pending)
    {
        if (pending.Count == 0) return null;

        var named = pending
            .Select(e => System.IO.Path.GetFileName(e.RepoPath.TrimEnd('\\', '/')))
            .Where(n => n.Length > 0)
            .ToList();

        // An entry whose path yields no name is still one of the count, so it is listed as a
        // remainder — otherwise the count claims more repositories than the text names.
        var unnamed = pending.Count - named.Count;
        if (unnamed > 0)
            named.Add(unnamed == 1 ? "an unnamed repository" : $"{unnamed} unnamed repositories");
        var listed = string.Join(", ", named);
        return pending.Count == 1
            ? $"A history operation on {listed} was interrupted. Open that project to restore its backup or dismiss the record — nothing has been restored."
            : $"History operations on {pending.Count} repositories were interrupted ({listed}). Open each project to restore its backup or dismiss the record — nothing has been restored.";
    }

    [RelayCommand]
    private void OpenSettings() => _navigationService.Navigate(typeof(SettingsPage));

    [RelayCommand]
    private async Task GhSignIn()
    {
        var proc = _gitHubService.StartInteractiveAuthLogin();
        if (proc is null) return;

        try { await proc.WaitForExitAsync(); } catch { }

        // Re-evaluate; if signed in now, pull GitHub data.
        await UpdateGhBannerAsync();
        if (!GhBannerVisible)
            await LoadProjectsCommand.ExecuteAsync(null);
    }

    private void UpdateProjectList(List<ProjectInfo> results)
    {
        Projects = new ObservableCollection<ProjectInfo>(results);

        // Settings may have changed since the last load (Settings page, another window),
        // and cached ProjectInfo carries whatever IsPinned was serialized with.
        ReloadViewPreferences();
        ApplyPinnedFlags();

        var cats = results
            .Select(p => p.Manifest.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Categories = new ObservableCollection<string>(["All", .. cats]);

        ApplyFilters();
        NotifySummary();
    }

    /// <summary>Raise change notification for every summary-count property in one place.</summary>
    private void NotifySummary()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CloudCount));
        OnPropertyChanged(nameof(HasCloud));
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(TodoCount));
        OnPropertyChanged(nameof(TotalTaskCount));
        OnPropertyChanged(nameof(TotalBugCount));
        OnPropertyChanged(nameof(TotalWaitCount));
        OnPropertyChanged(nameof(IssueCount));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(MismatchCount));
        OnPropertyChanged(nameof(IncompleteCount));
        OnPropertyChanged(nameof(HasMismatches));
        OnPropertyChanged(nameof(HasIncomplete));
    }

    private void ApplyFilters()
    {
        // The Hidden view has its own source list — without this, ANY ApplyFilters
        // call (search keystroke, sort change, timer refresh) silently replaced the
        // hidden list with the normal project set while "Hidden" stayed selected.
        var filtered = ActiveFilter == "hidden"
            ? _hiddenSnapshot.AsEnumerable()
            : Projects.AsEnumerable();

        // Summary bar filter
        if (ActiveFilter == "dirty")
            filtered = filtered.Where(p => p.GitStatus.IsDirty);
        else if (ActiveFilter == "todos")
            filtered = filtered.Where(p => p.TaskCount > 0 || p.BugCount > 0 || p.WaitCount > 0);
        else if (ActiveFilter == "issues")
            filtered = filtered.Where(p => p.OpenIssueCount >= 1);
        else if (ActiveFilter == "mismatch")
            filtered = filtered.Where(p => p.HasRemoteMismatch);
        else if (ActiveFilter == "incomplete")
            filtered = filtered.Where(p => p.HasIncompleteMetadata);
        else if (ActiveFilter == "public")
            filtered = filtered.Where(p => p.GitStatus.Visibility == "public");
        else if (ActiveFilter == "private")
            filtered = filtered.Where(p => p.GitStatus.Visibility == "private");
        else if (ActiveFilter == "nonlocal")
            filtered = filtered.Where(p => p.GitStatus.Visibility != "local");
        else if (ActiveFilter == "cloud")
            filtered = filtered.Where(p => p.IsRemoteOnly);

        if (!string.IsNullOrEmpty(SelectedCategory) && SelectedCategory != "All")
        {
            filtered = filtered.Where(p =>
                string.Equals(p.Manifest.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText;
            filtered = filtered.Where(p =>
                p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.DirectoryName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        SetDisplayedProjects(DashboardOrdering.Apply(filtered, SelectedSort, _pinnedKeys));
        NotifyContentState();
    }
}

/// <summary>Which body the dashboard shows instead of, or as, the card grid.</summary>
public enum DashboardContent
{
    Loading,
    ScanFailed,
    RootMissing,
    EmptyRoot,
    NoMatches,
    Cards,
}

/// <summary>
/// Chooses the dashboard body from load state alone. An empty grid has four distinct
/// causes — a scan still running, a scan that faulted, a root that isn't there, and a
/// root with no repositories — and rendering one blank panel for all of them tells the
/// user nothing about which.
/// </summary>
public static class DashboardEmptyState
{
    /// <summary>
    /// A list already on screen outranks every transient state: the periodic reload, a
    /// watcher refresh, a faulted scan and a vanished root are all recoverable, and
    /// swapping the grid out for a panel discards the user's scroll position and focus
    /// along with a set of cards that still work. Those states report themselves beside
    /// the grid instead. Below that, a load in flight wins over a stale failure so a
    /// retry doesn't keep showing the error it is retrying, and filter-emptiness is last
    /// because it only means anything once a scan has produced projects.
    /// </summary>
    public static DashboardContent Select(
        bool loading, bool scanFailed, bool rootExists, int discoveredCount, int filteredCount)
    {
        if (filteredCount > 0) return DashboardContent.Cards;
        if (loading) return DashboardContent.Loading;
        if (scanFailed) return DashboardContent.ScanFailed;
        if (!rootExists) return DashboardContent.RootMissing;
        if (discoveredCount == 0) return DashboardContent.EmptyRoot;
        return DashboardContent.NoMatches;
    }
}

/// <summary>Card-level git verbs that share bulk sync's refusals.</summary>
public enum CardAction
{
    Fetch,
    Pull,
    Push,
}

/// <summary>
/// Pre-flight refusals for the per-card git verbs. Pure so every refusal is
/// assertable without a repo: the guard, not the button, is what keeps a surprise
/// merge or a push from a diverged branch off the dashboard.
/// </summary>
public static class DashboardCardActions
{
    public const string BulkReason = "another operation is in progress.";
    public const string NotClonedReason = "not cloned locally.";
    public const string BusyReason = "busy with another operation.";
    public const string StatusUnavailableReason = "status unavailable.";
    public const string DetachedReason = "detached HEAD — check out a branch first.";
    public const string NoRemoteReason = "no remote.";
    public const string NoUpstreamReason = "no upstream branch.";
    public const string DirtyReason = "uncommitted changes.";

    public static string Verb(CardAction action) => action switch
    {
        CardAction.Pull => "Pull",
        CardAction.Push => "Push",
        _ => "Fetch",
    };

    /// <summary>
    /// The reason this action is refused, or null when it may run. <paramref name="hasUpstream"/>
    /// is null before the caller has read the working state; false refuses Pull and Push.
    /// Fetch is exempt from the working-tree refusals — it writes remote refs only and
    /// cannot disturb uncommitted work — but shares every repo-level one.
    /// </summary>
    public static string? RefuseReason(
        ProjectInfo? project,
        CardAction action,
        bool bulkOpRunning,
        bool repoBusy,
        bool? hasUpstream = null)
    {
        if (project is null) return NotClonedReason;
        if (bulkOpRunning) return BulkReason;
        if (project.IsRemoteOnly || string.IsNullOrEmpty(project.FullPath)) return NotClonedReason;
        if (repoBusy) return BusyReason;
        if (project.GitStatus.HasError) return StatusUnavailableReason;
        if (project.GitStatus.NeedsAttention) return $"{project.GitStatus.AttentionLabel} — resolve in a terminal.";
        if (project.GitStatus.IsDetached) return DetachedReason;
        if (string.IsNullOrEmpty(project.GitStatus.RemoteUrl)) return NoRemoteReason;

        if (action == CardAction.Fetch) return null;

        if (hasUpstream == false) return NoUpstreamReason;
        if (project.GitStatus.IsDirty) return DirtyReason;
        if (project.GitStatus.AheadBy > 0 && project.GitStatus.BehindBy > 0)
            return $"diverged (↑{project.GitStatus.AheadBy} ↓{project.GitStatus.BehindBy}) — resolve in a terminal.";

        return null;
    }
}

/// <summary>
/// When a settings-driven re-scan may run. Pure so the refusal is assertable without a
/// repository: a scan that reads a repo mid-rewrite sees a half-swapped ref set, and one
/// that overlaps another full scan rebuilds the grid from a list still being written.
/// </summary>
public static class DashboardRescan
{
    public const string RunningStatus = "Rescanning projects…";
    public const string QueuedStatus = "Rescan queued — a repository operation is still running.";

    public static bool Allowed(bool bulkOpRunning, bool anyRepoBusy, bool loadRunning, bool forceRefreshRunning) =>
        !bulkOpRunning && !anyRepoBusy && !loadRunning && !forceRefreshRunning;
}

/// <summary>Card-grid ordering: the active sort, with pinned projects lifted to the front.</summary>
public static class DashboardOrdering
{
    /// <summary>
    /// Pinning is a partition of the sorted sequence, not an extra sort key: each side
    /// keeps the active sort's order exactly, including for sorts whose keys tie.
    /// </summary>
    public static IEnumerable<ProjectInfo> Apply(IEnumerable<ProjectInfo> projects, string sort, ISet<string> pinnedKeys)
    {
        var sorted = Sort(projects, sort).ToList();
        return sorted.Where(p => IsPinned(p, pinnedKeys)).Concat(sorted.Where(p => !IsPinned(p, pinnedKeys)));
    }

    private static IEnumerable<ProjectInfo> Sort(IEnumerable<ProjectInfo> projects, string sort) => sort switch
    {
        "Last Commit" => projects.OrderByDescending(p => p.GitStatus.LastCommitDate),
        "Status" => projects.OrderBy(p => p.Manifest.Status).ThenBy(p => p.DisplayName),
        "Dirty First" => projects.OrderByDescending(p => p.GitStatus.IsDirty).ThenBy(p => p.DisplayName),
        "Category" => projects.OrderBy(p => p.Manifest.Category).ThenBy(p => p.DisplayName),
        _ => projects.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase),
    };

    public static bool IsPinned(ProjectInfo project, ISet<string> pinnedKeys) =>
        !string.IsNullOrEmpty(project.FullPath) && pinnedKeys.Contains(RepoKey(project.FullPath));

    /// <summary>
    /// Pin bookkeeping over the stored path list, keyed case-insensitively like the
    /// in-memory set. Ordinal equality here leaves a differently-cased spelling of the
    /// same repository behind on unpin — the glyph clears, the entry survives the
    /// restart, and every later pin appends another entry that can never be removed.
    /// </summary>
    public static string[] WithPin(IEnumerable<string> paths, string path) =>
        [.. Without(paths, path), path];

    public static string[] WithoutPin(IEnumerable<string> paths, string path) => [.. Without(paths, path)];

    private static IEnumerable<string> Without(IEnumerable<string> paths, string path)
    {
        var key = RepoKey(path);
        return paths.Where(p => !string.Equals(RepoKey(p), key, StringComparison.OrdinalIgnoreCase));
    }

    public static HashSet<string> KeySet(IEnumerable<string> paths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var key = RepoKey(path);
            if (key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Comparison key for a repo path: a trailing separator or a relative spelling must
    /// not make one pinned repo look like two. An unparseable path keys as itself rather
    /// than throwing — a damaged settings entry is inert, not fatal.
    /// </summary>
    public static string RepoKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex)
        {
            Log.Warn($"unusable repo path in settings: {path}", ex);
            return path.Trim();
        }
    }
}
