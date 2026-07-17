using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
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

    public DashboardViewModel(ProjectDiscoveryService discoveryService, INavigationService navigationService, SettingsService settingsService, GitHubService gitHubService, GitService gitService, ProjectWatcherService watcher)
    {
        _discoveryService = discoveryService;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _gitHubService = gitHubService;
        _gitService = gitService;
        _watcher = watcher;

        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);
        ForceRefreshCommand = new AsyncRelayCommand(ForceRefreshAsync);

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
            try
            {
                if (repoDirs.Count == 0)
                {
                    if (!LoadProjectsCommand.IsRunning)
                        await LoadProjectsCommand.ExecuteAsync(null);
                    return;
                }

                var names = new HashSet<string>(repoDirs, StringComparer.OrdinalIgnoreCase);
                var affected = Projects.Where(p => !p.IsRemoteOnly && names.Contains(p.DirectoryName)).ToList();
                foreach (var project in affected)
                {
                    // Local-only refresh — the watcher fires on every save; no gh/network here.
                    var refreshed = await _discoveryService.RefreshProjectLocalAsync(project.FullPath);
                    var idx = Projects.IndexOf(project);
                    if (idx >= 0) Projects[idx] = refreshed;
                }
                if (affected.Count > 0)
                {
                    OnPropertyChanged(nameof(Projects));
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
        var settings = _settingsService.Load();
        var interval = Math.Max(30, settings.RefreshIntervalSeconds);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(interval)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (LoadProjectsCommand.IsRunning) return;
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

    // Transient operation feedback (clone / bulk sync progress and outcomes).
    [ObservableProperty] private string _opStatusText = "";

    partial void OnSelectedCategoryChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedSortChanged(string value) => ApplyFilters();

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

        // Extract the text from the TextBox inside the dialog
        var stack = dialog.Content as System.Windows.Controls.StackPanel;
        var textBox = stack?.Children[1] as Wpf.Ui.Controls.TextBox;
        var projectName = textBox?.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(projectName)) return;

        // Sanitize: lowercase, replace spaces with hyphens, alphanumeric + hyphens only
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

        // Create folder structure
        Directory.CreateDirectory(projectPath);

        // README
        File.WriteAllText(Path.Combine(projectPath, "README.md"),
            $"# {projectName}\n\n");

        // CHANGELOG
        File.WriteAllText(Path.Combine(projectPath, "CHANGELOG.md"),
            $"# Changelog\n\n## [0.1.0] - {DateTime.Now:yyyy-MM-dd}\n\n### Added\n- Initial project scaffold\n");

        // Project metadata -> stored out-of-source in %APPDATA%, not in the repo.
        var manifest = new ProjectManifest
        {
            ProjectType = "unknown",
            Status = "experimental",
            Category = "Uncategorized",
            ValidationSchedule = "none",
            Notes = ""
        };
        await _discoveryService.SaveManifestAsync(projectPath, manifest);

        // git init + stage + first commit — resolved git, real timeouts, errors surfaced.
        var gitError = await _gitService.InitWithFirstCommitAsync(projectPath, "Initial project scaffold");
        if (gitError is not null)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Project created, git setup incomplete",
                Content = $"The folder and files were created, but git reported:\n\n{gitError}",
                CloseButtonText = "OK"
            }.ShowDialogAsync();
        }

        // Refresh dashboard
        await ForceRefreshAsync();
    }

    /// <summary>
    /// Clone dialog: paste a URL or pick from the signed-in user's repositories
    /// (type-to-filter). Clones into the configured projects root, then refreshes.
    /// </summary>
    [RelayCommand]
    private async Task CloneRepo()
    {
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
        urlBox.TextChanged += (_, _) =>
        {
            var term = urlBox.Text.Trim();
            list.ItemsSource = term.Length == 0
                ? repos
                : repos.Where(r => r.NameWithOwner.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Clone repository",
            Content = new System.Windows.Controls.StackPanel { Children = { urlBox, list } },
            PrimaryButtonText = "Clone",
            CloseButtonText = "Cancel"
        };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        // Selection wins over typed filter text; a full URL or owner/repo both work.
        string url;
        if (list.SelectedItem is RemoteRepo picked)
            url = $"https://github.com/{picked.NameWithOwner}.git";
        else
        {
            var typed = urlBox.Text.Trim();
            if (typed.Length == 0) return;
            url = typed.Contains("://") || typed.Contains('@') ? typed
                : $"https://github.com/{typed.TrimEnd('/')}.git";
        }

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

        OpStatusText = $"Cloning {repoName}…";
        var error = await _gitService.CloneAsync(url, settings.ProjectsRootPath);
        OpStatusText = error is null ? $"Cloned {repoName}." : $"Clone failed: {error}";
        if (error is null)
            await ForceRefreshAsync();
    }

    /// <summary>
    /// Fetches every clean repo with a remote; fast-forwards the ones behind,
    /// pushes the ones ahead. Dirty, diverged, conflicted, and error repos are
    /// skipped and reported — bulk sync must never create surprise merges.
    /// </summary>
    [RelayCommand]
    private async Task SyncAll()
    {
        var candidates = Projects.Where(p =>
                !p.GitStatus.HasError &&
                !p.GitStatus.IsDirty &&
                !p.GitStatus.NeedsAttention &&
                !p.GitStatus.IsDetached &&
                !string.IsNullOrEmpty(p.GitStatus.RemoteUrl))
            .ToList();
        var skipped = Projects.Count - candidates.Count;
        if (candidates.Count == 0)
        {
            OpStatusText = "Sync all: no clean repos with a remote to sync.";
            return;
        }

        var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        var semaphore = new SemaphoreSlim(4);

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

        var settings = _settingsService.Load();
        var target = Path.Combine(settings.ProjectsRootPath, project.DirectoryName);
        if (Directory.Exists(target))
        {
            OpStatusText = $"Clone: {project.DirectoryName} already exists in the projects root.";
            return;
        }

        OpStatusText = $"Cloning {project.DirectoryName}…";
        var url = $"https://github.com/{project.RemoteSlug}.git";
        var error = await _gitService.CloneAsync(url, settings.ProjectsRootPath);
        OpStatusText = error is null ? $"Cloned {project.DirectoryName}." : $"Clone failed: {error}";
        if (error is null)
            await ForceRefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshSingle(ProjectInfo? project)
    {
        if (project is null) return;
        var refreshed = await _discoveryService.RefreshProjectAsync(project);

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
        _settingsService.Save(settings);

        await ForceRefreshAsync();
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
        await ForceRefreshAsync();
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
            // Flag, don't mutate the manifest — Status must never be overwritten by view state.
            full.IsHidden = true;
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

    private async Task LoadProjectsAsync()
    {
        SyncWatcherToSettings();
        try
        {
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

    private async Task ForceRefreshAsync()
    {
        try
        {
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

        // Sort
        filtered = SelectedSort switch
        {
            "Last Commit" => filtered.OrderByDescending(p => p.GitStatus.LastCommitDate),
            "Status" => filtered.OrderBy(p => p.Manifest.Status).ThenBy(p => p.DisplayName),
            "Dirty First" => filtered.OrderByDescending(p => p.GitStatus.IsDirty).ThenBy(p => p.DisplayName),
            "Category" => filtered.OrderBy(p => p.Manifest.Category).ThenBy(p => p.DisplayName),
            _ => filtered.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
        };

        SetDisplayedProjects(filtered);
    }
}
