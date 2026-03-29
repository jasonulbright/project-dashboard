using ProjectDashboard.Services;
using Wpf.Ui.Appearance;

namespace ProjectDashboard.ViewModels.Pages;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly GitHubService _gitHubService;
    private readonly ProjectDiscoveryService _discoveryService;

    [ObservableProperty] private ApplicationTheme _currentTheme = ApplicationTheme.Dark;
    [ObservableProperty] private string _projectsRootPath = "";
    [ObservableProperty] private int _refreshIntervalSeconds = 300;
    [ObservableProperty] private string _excludedDirectories = "";
    [ObservableProperty] private string _gitHubStatus = "Checking...";
    [ObservableProperty] private string _syncStatus = "";

    public SettingsViewModel(SettingsService settingsService, GitHubService gitHubService, ProjectDiscoveryService discoveryService)
    {
        _settingsService = settingsService;
        _gitHubService = gitHubService;
        _discoveryService = discoveryService;

        LoadSettings();
        _ = CheckGitHubStatusAsync();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        ProjectsRootPath = settings.ProjectsRootPath;
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        ExcludedDirectories = string.Join(", ", settings.ExcludedDirectories);

        if (Enum.TryParse<ApplicationTheme>(settings.Theme, out var theme))
            CurrentTheme = theme;
    }

    private async Task CheckGitHubStatusAsync()
    {
        try
        {
            var available = await _gitHubService.IsAvailableAsync();
            GitHubStatus = available ? "Authenticated" : "Not authenticated";
        }
        catch
        {
            GitHubStatus = "Not authenticated";
        }
    }

    [RelayCommand]
    private void ChangeTheme(string themeParameter)
    {
        if (Enum.TryParse<ApplicationTheme>(themeParameter, out var theme))
        {
            CurrentTheme = theme;
            ApplicationThemeManager.Apply(theme);
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var settings = new Models.AppSettings
        {
            ProjectsRootPath = ProjectsRootPath,
            RefreshIntervalSeconds = RefreshIntervalSeconds,
            Theme = CurrentTheme.ToString(),
            ExcludedDirectories = ExcludedDirectories
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };

        _settingsService.Save(settings);
    }

    [RelayCommand]
    private async Task ForceSync()
    {
        SyncStatus = "Syncing...";
        await _discoveryService.ForceRefreshAllAsync();
        SyncStatus = $"Synced at {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Projects Root Folder",
            InitialDirectory = ProjectsRootPath
        };

        if (dialog.ShowDialog() == true)
        {
            ProjectsRootPath = dialog.FolderName;
        }
    }
}
