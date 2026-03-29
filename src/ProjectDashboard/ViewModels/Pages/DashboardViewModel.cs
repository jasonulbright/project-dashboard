using System.Diagnostics;
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

    [ObservableProperty] private string _activeFilter = "all"; // "all", "dirty", "issues"

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
    private void FilterIssues()
    {
        ActiveFilter = "issues";
        SelectedCategory = "All";
        SearchText = "";
        ApplyFilters();
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
