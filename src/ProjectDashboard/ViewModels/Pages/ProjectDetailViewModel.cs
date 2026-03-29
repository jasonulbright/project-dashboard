using System.Diagnostics;
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

    public ProjectDetailViewModel(ProjectDiscoveryService discoveryService, GitService gitService, GitHubService gitHubService)
    {
        _discoveryService = discoveryService;
        _gitService = gitService;
        _gitHubService = gitHubService;

        SaveManifestCommand = new AsyncRelayCommand(SaveManifestAsync);
        LoadDetailsCommand = new AsyncRelayCommand(LoadDetailsAsync);
        OpenCommitCommand = new RelayCommand<GitCommit>(OpenCommit);
        OpenIssueCommand = new RelayCommand<GitHubIssue>(OpenIssue);
    }

    private void OpenCommit(GitCommit? commit)
    {
        if (commit is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        var url = $"https://github.com/{Project.GitHubSlug}/commit/{commit.ShortHash}";
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

    public async Task SetProjectAsync(ProjectInfo project)
    {
        Project = project;

        // Always refresh from disk to get full data (cache may have sparse objects)
        var refreshed = await _discoveryService.RefreshProjectAsync(project);
        Project = refreshed;

        ReadmeText = refreshed.ReadmeContent ?? "";
        ChangelogText = refreshed.ChangelogContent ?? "";
        Commits = new ObservableCollection<GitCommit>(refreshed.RecentCommits ?? []);
        Issues = new ObservableCollection<GitHubIssue>(refreshed.Issues ?? []);

        SelectedProjectType = refreshed.Manifest.ProjectType;
        SelectedStatus = refreshed.Manifest.Status;
        SelectedCategory = refreshed.Manifest.Category;
        ValidationSchedule = refreshed.Manifest.ValidationSchedule;
        Notes = refreshed.Manifest.Notes;
    }

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
