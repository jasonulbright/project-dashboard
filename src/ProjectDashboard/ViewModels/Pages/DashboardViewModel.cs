using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.ViewModels.Pages;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly INavigationService _navigationService;

    [ObservableProperty] private ObservableCollection<ProjectInfo> _projects = [];
    [ObservableProperty] private ObservableCollection<ProjectInfo> _filteredProjects = [];
    [ObservableProperty] private ObservableCollection<string> _categories = ["All"];
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private string _searchText = "";

    /// <summary>Used to pass the selected project to ProjectDetailPage.</summary>
    public static ProjectInfo? SelectedProject { get; set; }

    public int TotalCount => Projects.Count;
    public int DirtyCount => Projects.Count(p => p.GitStatus.IsDirty);
    public int IssueCount => Projects.Sum(p => p.OpenIssueCount);

    public IAsyncRelayCommand LoadProjectsCommand { get; }

    public DashboardViewModel(ProjectDiscoveryService discoveryService, INavigationService navigationService)
    {
        _discoveryService = discoveryService;
        _navigationService = navigationService;

        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);

        // Fire and forget load on construction
        _ = LoadProjectsCommand.ExecuteAsync(null);
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    [RelayCommand]
    private void OpenProject(ProjectInfo? project)
    {
        if (project is null) return;
        SelectedProject = project;
        _navigationService.Navigate(typeof(ProjectDetailPage));
    }

    private async Task LoadProjectsAsync()
    {
        var results = await _discoveryService.DiscoverAllAsync();

        Projects = new ObservableCollection<ProjectInfo>(results);

        // Build categories
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
        OnPropertyChanged(nameof(IssueCount));
    }

    private void ApplyFilters()
    {
        var filtered = Projects.AsEnumerable();

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

        FilteredProjects = new ObservableCollection<ProjectInfo>(filtered);
    }
}
