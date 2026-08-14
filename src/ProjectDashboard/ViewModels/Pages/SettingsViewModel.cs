using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Update;
using Wpf.Ui.Appearance;

namespace ProjectDashboard.ViewModels.Pages;

public partial class SettingsViewModel : ObservableObject
{
    /// <summary>Assembly version — the single source; never hand-maintained in XAML.</summary>
    public static string AppVersion => AppVersionInfo.Display;

    private readonly SettingsService _settingsService;
    private readonly GitHubService _gitHubService;
    private readonly DashboardViewModel _dashboardViewModel;

    /// <summary>Null when the host supplied none; the page then offers no manual check.</summary>
    private readonly UpdateCheckService? _updateCheck;

    private readonly ManifestStore _manifests;

    /// <summary>Null when the host supplied none; the page then reports no scan has run.</summary>
    private readonly ProjectDiscoveryService? _discovery;

    [ObservableProperty] private ApplicationTheme _currentTheme = ApplicationTheme.Dark;
    [ObservableProperty] private int _refreshIntervalSeconds = 300;
    [ObservableProperty] private string _gitHubStatus = "Checking...";
    [ObservableProperty] private string _ghPath = "";
    [ObservableProperty] private bool _enableGitHubDiscovery = true;
    [ObservableProperty] private bool _enableAutoRefresh = true;
    [ObservableProperty] private bool _dangerZoneEnabled;
    [ObservableProperty] private bool _enableUpdateCheck = true;
    [ObservableProperty] private string _updateCheckStatus = "";
    [ObservableProperty] private bool _enableScheduledFetch;
    [ObservableProperty] private int _scheduledFetchIntervalMinutes = 60;
    [ObservableProperty] private string _scheduledFetchStatus = "";

    private readonly ScheduledFetchService? _scheduledFetch;
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private string _saveStatus = "";

    public SettingsViewModel(SettingsService settingsService, GitHubService gitHubService, DashboardViewModel dashboardViewModel, UpdateCheckService? updateCheck = null, ManifestStore? manifests = null, ProjectDiscoveryService? discovery = null, Services.Safety.BackupService? backups = null, ScheduledFetchService? scheduledFetch = null)
    {
        _scheduledFetch = scheduledFetch;
        _settingsService = settingsService;
        _gitHubService = gitHubService;
        _dashboardViewModel = dashboardViewModel;
        _updateCheck = updateCheck;
        _backups = backups;
        // Defaulted rather than left null: the section reads one file, and a page that showed no
        // records at all would read as a reader having none rather than as a host wiring gap.
        _manifests = manifests ?? new ManifestStore();
        _discovery = discovery;

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
        LoadRoots(settings);
        LoadMetadata();
        LoadBackupSettings(settings);
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        GhPath = settings.GhPath;
        EnableGitHubDiscovery = settings.EnableGitHubDiscovery;
        EnableAutoRefresh = settings.EnableAutoRefresh;
        DangerZoneEnabled = settings.DangerZoneEnabled;
        EnableUpdateCheck = settings.EnableUpdateCheck;
        UpdateCheckStatus = DescribeLastCheck(settings);
        EnableScheduledFetch = settings.EnableScheduledFetch;
        ScheduledFetchIntervalMinutes = settings.ScheduledFetchIntervalMinutes;
        ScheduledFetchStatus = _scheduledFetch?.StatusLine ?? "";

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

    private async Task CheckGitHubStatusAsync(bool refresh = false)
    {
        try
        {
            GitHubStatus = await FetchAuthSummaryAsync();
        }
        catch
        {
            GitHubStatus = "Unavailable";
        }
        await ReadGhAccountsAsync(refresh);
    }

    /// <summary>One row of the account table: what gh holds for a host, and nothing it holds back.</summary>
    public sealed record GhAccountRow(
        string Host, string Login, string ActiveLabel, string Scopes, string AccessibleName);

    [ObservableProperty] private ObservableCollection<GhAccountRow> _ghAccounts = [];
    [ObservableProperty] private bool _ghAccountsVisible;

    /// <summary>
    /// Why the table below the status line is absent, or "" when its contents are the answer. An
    /// account read this version of gh answered in a shape the app does not recognise leaves the
    /// exit-code summary standing alone, and only this line tells that apart from a machine that
    /// really does hold one account.
    /// </summary>
    [ObservableProperty] private string _ghAccountsNotice = "";

    internal const string GhAccountsUnreadable =
        "The GitHub CLI did not report its accounts in a form this app reads, so the line above is all that "
        + "was established — no account, host, or scope is claimed here.";

    /// <summary>
    /// The two status reads the gh section stands on. Overridable so the degrade path and every
    /// table shape are reachable without gh installed.
    /// </summary>
    internal virtual Task<string> FetchAuthSummaryAsync() => _gitHubService.GetAuthSummaryAsync();

    internal virtual Task<GhAuthState?> FetchAuthStateAsync(bool refresh)
        => _gitHubService.GetAuthStateAsync(refresh);

    /// <summary>
    /// The account table. The status read carries scopes and hosts as gh names them and no token:
    /// nothing here asks gh for a token value, so none can reach the screen or the log.
    /// </summary>
    private async Task ReadGhAccountsAsync(bool refresh)
    {
        GhAuthState? state;
        try { state = await FetchAuthStateAsync(refresh); }
        catch { state = null; }

        GhAccounts = [.. (state?.Accounts ?? []).Select(ToRow)];
        GhAccountsVisible = GhAccounts.Count > 0;
        // A gh that is absent already explains itself on the line above; repeating it as a parse
        // failure would name a second fault where there is one.
        GhAccountsNotice = state is null && GitHubStatus is "Signed in" or "Found, not signed in"
            ? GhAccountsUnreadable
            : "";
    }

    internal static GhAccountRow ToRow(GhAccount account)
    {
        var scopes = account.ScopeList;
        var state = account.IsUsable
            ? account.Active ? "active" : "not the account gh targets for this host"
            : $"sign-in state {account.State}";
        return new GhAccountRow(
            account.Host,
            account.Login,
            account.Active ? "Active" : "",
            scopes,
            $"{account.Login} on {account.Host}, {state}"
            + (scopes.Length > 0 ? $", scopes {scopes}" : ", no scopes reported"));
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
        EnsureOneDefault();
        settings.ProjectRoots = RootsFromRows();
        settings.DefaultRootPath = DefaultRootFromRows();
        // The singular compatibility fields are re-derived from the list on the way to disk;
        // leaving them at the values this page loaded would read as an edit to the first root.
        ProjectRootSettings.SyncLegacyFields(settings);
        // Clamp to a sane floor so a stray tiny/zero/negative value can't spin the timer.
        settings.RefreshIntervalSeconds = SettingsDelta.EffectiveRefreshSeconds(RefreshIntervalSeconds);
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        settings.Theme = CurrentTheme.ToString();
        settings.GhPath = GhPath.Trim();
        settings.EnableGitHubDiscovery = EnableGitHubDiscovery;
        settings.EnableAutoRefresh = EnableAutoRefresh;
        settings.DangerZoneEnabled = DangerZoneEnabled;
        settings.EnableUpdateCheck = EnableUpdateCheck;
        settings.EnableScheduledFetch = EnableScheduledFetch;
        settings.ScheduledFetchIntervalMinutes = SettingsDelta.EffectiveFetchMinutes(ScheduledFetchIntervalMinutes);
        ScheduledFetchIntervalMinutes = settings.ScheduledFetchIntervalMinutes;
        SaveBackupSettings(settings);

        // The startup probe covers only a location unwritable at launch. A volume that
        // turns read-only mid-session fails here, and an unreported failure loses the
        // edit at the next Load with the page still showing it as applied.
        SaveStatus = _settingsService.Save(settings)
            ? SavedMessage(DateTime.Now, _dashboardViewModel is { } dashboard ? dashboard.RescanStatus : "")
            : $"Save failed — could not write {AppPaths.SettingsFile}. See the log for details.";

        UpdateCheckStatus = DescribeLastCheck(settings);
    }

    /// <summary>
    /// The update line: the last check's own outcome, and when it ran. A failing check is
    /// silent on the dashboard, so this line is where a check that has been failing since an
    /// earlier session becomes visible.
    /// </summary>
    internal static string DescribeLastCheck(AppSettings settings)
    {
        if (!settings.EnableUpdateCheck) return UpdateCheckService.DisabledStatus;
        if (settings.LastUpdateCheckUtc is not { } stamp) return "Not checked yet.";

        var outcome = settings.LastUpdateCheckStatus.Length > 0 ? settings.LastUpdateCheckStatus : "Checked.";
        return $"{outcome} Last checked {stamp.ToLocalTime():yyyy-MM-dd HH:mm}.";
    }

    /// <summary>
    /// The check the user asked for: it ignores the launch cooldown and reports its own
    /// reason, including a failure the launch path would have kept to the log. The toggle is
    /// persisted first — the checker reads the file, so an on-screen tick that had not been
    /// saved would otherwise be answered as though the feature were off.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (_updateCheck is null)
        {
            UpdateCheckStatus = "Update checks are unavailable in this session.";
            return;
        }

        SaveSettings();
        UpdateCheckStatus = "Checking...";
        UpdateCheckStatus = (await _updateCheck.CheckAsync(manual: true)).Status;
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
            // A different gh is a different account store, so the held answer describes the one
            // that was just replaced.
            await CheckGitHubStatusAsync(refresh: true);
        }
    }

    [RelayCommand]
    private async Task RecheckGitHub()
    {
        GitHubStatus = "Checking...";
        SaveSettings();
        await CheckGitHubStatusAsync(refresh: true);
    }
}
