using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

public partial class ProjectDetailViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;

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

    public static List<string> ProjectTypes { get; } = ["powershell", "csharp", "wpf", "web", "tool", "library", "game", "unknown"];
    public static List<string> Statuses { get; } = ["active", "maintenance", "archived", "experimental"];
    public static List<string> CategoriesList { get; } = ["Uncategorized", "Automation", "Desktop App", "DevOps", "Game", "Infrastructure", "Library", "Tool", "Web"];
    public static List<string> Schedules { get; } = ["none", "daily", "weekly", "monthly"];

    public IAsyncRelayCommand SaveManifestCommand { get; }
    public IAsyncRelayCommand LoadDetailsCommand { get; }

    public ProjectDetailViewModel(ProjectDiscoveryService discoveryService, GitService gitService, GitHubService gitHubService)
    {
        _discoveryService = discoveryService;
        _gitService = gitService;
        _gitHubService = gitHubService;

        SaveManifestCommand = new AsyncRelayCommand(SaveManifestAsync);
        LoadDetailsCommand = new AsyncRelayCommand(LoadDetailsAsync);
    }

    public void SetProject(ProjectInfo project)
    {
        Project = project;

        // Populate from project data
        ReadmeText = project.ReadmeContent;
        ChangelogText = project.ChangelogContent;
        Commits = new ObservableCollection<GitCommit>(project.RecentCommits);
        Issues = new ObservableCollection<GitHubIssue>(project.Issues);

        // Manifest editor fields
        SelectedProjectType = project.Manifest.ProjectType;
        SelectedStatus = project.Manifest.Status;
        SelectedCategory = project.Manifest.Category;
        ValidationSchedule = project.Manifest.ValidationSchedule;
        Notes = project.Manifest.Notes;
    }

    private async Task LoadDetailsAsync()
    {
        if (Project is null) return;

        var refreshed = await _discoveryService.RefreshProjectAsync(Project);
        SetProject(refreshed);
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
