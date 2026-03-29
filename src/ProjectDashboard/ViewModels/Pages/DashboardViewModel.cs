using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    public int TotalCount => Projects.Count;
    public int DirtyCount => Projects.Count(p => p.GitStatus.IsDirty);
    public int TodoCount => Projects.Count(p => p.Manifest.Notes.Contains("TODO:", StringComparison.OrdinalIgnoreCase));
    public int IssueCount => Projects.Sum(p => p.OpenIssueCount);
    public int HiddenCount
    {
        get
        {
            var s = _settingsService.Load();
            var root = s.ProjectsRootPath;
            return s.ExcludedDirectories.Count(d =>
                Directory.Exists(Path.Combine(root, d)) &&
                Directory.Exists(Path.Combine(root, d, ".git")));
        }
    }

    public IAsyncRelayCommand LoadProjectsCommand { get; }
    public IAsyncRelayCommand ForceRefreshCommand { get; }

    public DashboardViewModel(ProjectDiscoveryService discoveryService, INavigationService navigationService, SettingsService settingsService)
    {
        _discoveryService = discoveryService;
        _navigationService = navigationService;
        _settingsService = settingsService;

        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);
        ForceRefreshCommand = new AsyncRelayCommand(ForceRefreshAsync);

        // Fire and forget load on construction
        _ = LoadProjectsCommand.ExecuteAsync(null);

        // Auto-refresh timer
        StartRefreshTimer();
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
            if (!LoadProjectsCommand.IsRunning)
            {
                await LoadProjectsCommand.ExecuteAsync(null);
            }
        };
        _refreshTimer.Start();
    }

    [ObservableProperty] private string _activeFilter = "all"; // "all", "dirty", "todos", "issues", "hidden"
    [ObservableProperty] private ObservableCollection<ProjectInfo> _hiddenProjects = [];

    partial void OnSelectedCategoryChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedSortChanged(string value) => ApplyFilters();

    [RelayCommand]
    private void FilterAll()
    {
        ActiveFilter = "all";
        SelectedCategory = "All";
        SearchText = "";
        ApplyFilters();
    }

    [RelayCommand]
    private void FilterDirty()
    {
        ActiveFilter = "dirty";
        SelectedCategory = "All";
        SearchText = "";
        ApplyFilters();
    }

    [RelayCommand]
    private void FilterTodos()
    {
        ActiveFilter = "todos";
        SelectedCategory = "All";
        SearchText = "";
        ApplyFilters();
    }

    [RelayCommand]
    private void FilterHidden()
    {
        ShowHiddenProjects();
    }

    [RelayCommand]
    private void FilterIssues()
    {
        ActiveFilter = "issues";
        SelectedCategory = "All";
        SearchText = "";
        ApplyFilters();
    }

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

        // .gitignore
        File.WriteAllText(Path.Combine(projectPath, ".gitignore"),
            "project-manifest.json\n");

        // project-manifest.json
        var manifest = new ProjectManifest
        {
            ProjectType = "unknown",
            Status = "experimental",
            Category = "Uncategorized",
            ValidationSchedule = "none",
            Notes = ""
        };
        File.WriteAllText(Path.Combine(projectPath, "project-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        // git init + commit on background thread
        await Task.Run(() =>
        {
            var gitInit = Process.Start(new ProcessStartInfo("git", "init")
                { WorkingDirectory = projectPath, UseShellExecute = false, CreateNoWindow = true });
            gitInit?.WaitForExit();

            var gitAdd = Process.Start(new ProcessStartInfo("git", "add -A")
                { WorkingDirectory = projectPath, UseShellExecute = false, CreateNoWindow = true });
            gitAdd?.WaitForExit();

            var gitCommit = Process.Start(new ProcessStartInfo("git", "commit -m \"Initial project scaffold\"")
                { WorkingDirectory = projectPath, UseShellExecute = false, CreateNoWindow = true });
            gitCommit?.WaitForExit();
        });

        // Refresh dashboard
        await ForceRefreshAsync();
    }

    [RelayCommand]
    private void OpenProject(ProjectInfo? project)
    {
        if (project is null) return;
        SelectedProject = project;
        _navigationService.Navigate(typeof(ProjectDetailPage));
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
            ApplyFilters();
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(DirtyCount));
            OnPropertyChanged(nameof(IssueCount));
        }
    }

    [RelayCommand]
    private void OpenGitHub(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.GitHubSlug)) return;
        Process.Start(new ProcessStartInfo($"https://github.com/{project.GitHubSlug}") { UseShellExecute = true });
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

        // Refresh hidden list and main list
        ShowHiddenProjects();
        await ForceRefreshAsync();
    }

    public async void ShowHiddenProjects()
    {
        ActiveFilter = "hidden";

        var settings = _settingsService.Load();
        var rootPath = settings.ProjectsRootPath;
        var excluded = new HashSet<string>(settings.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);

        var hiddenDirs = Directory.GetDirectories(rootPath)
            .Where(d => excluded.Contains(Path.GetFileName(d)) && Directory.Exists(Path.Combine(d, ".git")))
            .ToList();

        var hiddenList = new List<ProjectInfo>();
        foreach (var dir in hiddenDirs)
        {
            var dirName = Path.GetFileName(dir);
            hiddenList.Add(new ProjectInfo
            {
                DirectoryName = dirName,
                FullPath = dir,
                DisplayName = dirName,
                Manifest = new ProjectManifest { Status = "hidden" }
            });
        }

        FilteredProjects = new ObservableCollection<ProjectInfo>(hiddenList.OrderBy(p => p.DisplayName));
    }

    [RelayCommand]
    private void OpenFolder(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrEmpty(project.FullPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", project.FullPath));
    }

    private async Task LoadProjectsAsync()
    {
        var results = await _discoveryService.DiscoverAllAsync();
        UpdateProjectList(results);
    }

    private async Task ForceRefreshAsync()
    {
        var results = await _discoveryService.ForceRefreshAllAsync();
        UpdateProjectList(results);
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
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(TodoCount));
        OnPropertyChanged(nameof(IssueCount));
        OnPropertyChanged(nameof(HiddenCount));
    }

    private void ApplyFilters()
    {
        var filtered = Projects.AsEnumerable();

        // Summary bar filter
        if (ActiveFilter == "dirty")
            filtered = filtered.Where(p => p.GitStatus.IsDirty);
        else if (ActiveFilter == "todos")
            filtered = filtered.Where(p => p.Manifest.Notes.Contains("TODO:", StringComparison.OrdinalIgnoreCase));
        else if (ActiveFilter == "issues")
            filtered = filtered.Where(p => p.OpenIssueCount >= 1);

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

        FilteredProjects = new ObservableCollection<ProjectInfo>(filtered);
    }
}
