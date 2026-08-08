using ProjectDashboard.Models;
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
    [ObservableProperty] private bool _enableGitHubDiscovery = true;
    [ObservableProperty] private bool _enableAutoRefresh = true;
    [ObservableProperty] private bool _dangerZoneEnabled;
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private string _saveStatus = "";

    public SettingsViewModel(SettingsService settingsService, GitHubService gitHubService, DashboardViewModel dashboardViewModel)
    {
        _settingsService = settingsService;
        _gitHubService = gitHubService;
        _dashboardViewModel = dashboardViewModel;

        LoadSettings();
        _ = CheckGitHubStatusAsync();
    }

    /// <summary>
    /// Re-snapshots persisted settings into the bound fields. Save writes every
    /// bound field back to disk, so a snapshot older than an external write
    /// (hide/unhide updating ExcludedDirectories) makes Save revert that write.
    /// The page calls this on every navigation; any future bound setting is
    /// covered by the same re-snapshot.
    /// </summary>
    public void LoadSettings()
    {
        var settings = _settingsService.Load();
        ProjectsRootPath = settings.ProjectsRootPath;
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        ExcludedDirectories = string.Join(", ", settings.ExcludedDirectories);
        GhPath = settings.GhPath;
        EnableGitHubDiscovery = settings.EnableGitHubDiscovery;
        EnableAutoRefresh = settings.EnableAutoRefresh;
        DangerZoneEnabled = settings.DangerZoneEnabled;

        if (Enum.TryParse<ApplicationTheme>(settings.Theme, out var theme))
        {
            CurrentTheme = theme;
            // The radio (CurrentTheme) and the applied theme must agree after a
            // re-snapshot: resetting only the radio after a live-but-unsaved
            // ChangeTheme lets Save persist a theme that is not on screen. Equal
            // themes skip the apply so plain revisits cause no re-theme flicker.
            if (ApplicationThemeManager.GetAppTheme() != theme)
                ApplicationThemeManager.Apply(theme);
        }
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
        // Clamp to a sane floor so a stray tiny/zero/negative value can't spin the timer.
        settings.RefreshIntervalSeconds = SettingsDelta.EffectiveRefreshSeconds(RefreshIntervalSeconds);
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        settings.Theme = CurrentTheme.ToString();
        settings.ExcludedDirectories = ExcludedDirectories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        settings.GhPath = GhPath.Trim();
        settings.EnableGitHubDiscovery = EnableGitHubDiscovery;
        settings.EnableAutoRefresh = EnableAutoRefresh;
        settings.DangerZoneEnabled = DangerZoneEnabled;

        // The startup probe covers only a location unwritable at launch. A volume that
        // turns read-only mid-session fails here, and an unreported failure loses the
        // edit at the next Load with the page still showing it as applied.
        SaveStatus = _settingsService.Save(settings)
            ? SavedMessage(DateTime.Now, _dashboardViewModel is { } dashboard ? dashboard.RescanStatus : "")
            : $"Save failed — could not write {AppPaths.SettingsFile}. See the log for details.";
    }

    /// <summary>Reports the deferral in the save notice, phrased as a fact about the save.</summary>
    internal const string QueuedRescanNotice = "rescan queued behind a repository operation.";

    /// <summary>
    /// The success notice, carrying what the save set in motion beyond the file write. Only
    /// the deferral is carried: a bare "Saved" against a re-scan that cannot start yet reads
    /// as a save that changed nothing on screen, while a re-scan already under way finishes
    /// seconds later and the notice does not, so quoting it leaves the page claiming a scan
    /// is running for the rest of the session.
    /// </summary>
    internal static string SavedMessage(DateTime at, string rescanStatus) =>
        rescanStatus == DashboardRescan.QueuedStatus
            ? $"Saved at {at:HH:mm:ss} — {QueuedRescanNotice}"
            : $"Saved at {at:HH:mm:ss}";

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
