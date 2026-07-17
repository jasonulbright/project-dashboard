using ProjectDashboard.Services;
using Wpf.Ui.Appearance;

namespace ProjectDashboard.ViewModels.Pages;

public partial class SettingsViewModel : ObservableObject
{
    /// <summary>Assembly version — the single source; never hand-maintained in XAML.</summary>
    public static string AppVersion { get; } =
        $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"}";

    private readonly SettingsService _settingsService;
    private readonly GitHubService _gitHubService;
    private readonly DashboardViewModel _dashboardViewModel;

    [ObservableProperty] private ApplicationTheme _currentTheme = ApplicationTheme.Dark;
    [ObservableProperty] private string _projectsRootPath = "";
    [ObservableProperty] private int _refreshIntervalSeconds = 300;
    [ObservableProperty] private string _excludedDirectories = "";
    [ObservableProperty] private string _gitHubStatus = "Checking...";
    [ObservableProperty] private string _ghPath = "";
    [ObservableProperty] private string _syncStatus = "";

    public SettingsViewModel(SettingsService settingsService, GitHubService gitHubService, DashboardViewModel dashboardViewModel)
    {
        _settingsService = settingsService;
        _gitHubService = gitHubService;
        _dashboardViewModel = dashboardViewModel;

        LoadSettings();
        _ = CheckGitHubStatusAsync();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        ProjectsRootPath = settings.ProjectsRootPath;
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        ExcludedDirectories = string.Join(", ", settings.ExcludedDirectories);
        GhPath = settings.GhPath;

        if (Enum.TryParse<ApplicationTheme>(settings.Theme, out var theme))
            CurrentTheme = theme;
    }

    private async Task CheckGitHubStatusAsync()
    {
        try
        {
            GitHubStatus = await _gitHubService.GetAuthSummaryAsync();
        }
        catch
        {
            GitHubStatus = "Unavailable";
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
        // Load-then-mutate so window state (and any unseen fields) survive a Settings save.
        var settings = _settingsService.Load();
        settings.ProjectsRootPath = ProjectsRootPath;
        settings.RefreshIntervalSeconds = RefreshIntervalSeconds;
        settings.Theme = CurrentTheme.ToString();
        settings.ExcludedDirectories = ExcludedDirectories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        settings.GhPath = GhPath.Trim();

        _settingsService.Save(settings);
    }

    [RelayCommand]
    private async Task ForceSync()
    {
        // Refresh through the dashboard VM so the visible list updates too —
        // refreshing only the discovery cache left the UI stale until the timer.
        SyncStatus = "Syncing...";
        await _dashboardViewModel.ForceRefreshCommand.ExecuteAsync(null);
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

    [RelayCommand]
    private async Task BrowseGh()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate gh.exe (GitHub CLI)",
            Filter = "GitHub CLI (gh.exe)|gh.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            GhPath = dialog.FileName;
            SaveSettings();
            await CheckGitHubStatusAsync();
        }
    }

    [RelayCommand]
    private async Task RecheckGitHub()
    {
        GitHubStatus = "Checking...";
        SaveSettings();
        await CheckGitHubStatusAsync();
    }
}
