namespace ProjectDashboard.Models;

/// <summary>A settings write that reached disk: the state before it and the state after.</summary>
public sealed record SettingsChange(AppSettings Previous, AppSettings Current);

/// <summary>
/// Which live-apply paths a settings write has to drive. Pure, so every trigger is
/// assertable without a window: a trigger that misses leaves the app running the
/// previous value while the page shows the new one.
/// </summary>
public static class SettingsDelta
{
    /// <summary>Floor for the periodic-reconcile interval; a smaller value would spin the timer.</summary>
    public const int MinimumRefreshSeconds = 30;

    public static int EffectiveRefreshSeconds(int configuredSeconds) =>
        Math.Max(MinimumRefreshSeconds, configuredSeconds);

    public static int EffectiveRefreshSeconds(AppSettings settings) =>
        EffectiveRefreshSeconds(settings.RefreshIntervalSeconds);

    /// <summary>The path the file watcher should be pointed at; empty when auto-refresh is off.</summary>
    public static string WatcherRoot(AppSettings settings) =>
        settings.EnableAutoRefresh ? settings.ProjectsRootPath : "";

    public static bool ThemeChanged(SettingsChange change) =>
        !string.Equals(change.Previous.Theme, change.Current.Theme, StringComparison.OrdinalIgnoreCase);

    public static bool RefreshIntervalChanged(SettingsChange change) =>
        EffectiveRefreshSeconds(change.Previous) != EffectiveRefreshSeconds(change.Current);

    public static bool WatcherTargetChanged(SettingsChange change) =>
        !PathsEqual(WatcherRoot(change.Previous), WatcherRoot(change.Current));

    /// <summary>
    /// True when the discovered set itself can differ, so the cached scan is stale and a
    /// full re-scan is the only thing that can show it. The discovery cache is keyed on age
    /// alone, so a plain reload would re-serve the previous root's projects until it expires.
    /// </summary>
    public static bool RediscoveryRequired(SettingsChange change) =>
        !PathsEqual(change.Previous.ProjectsRootPath, change.Current.ProjectsRootPath)
        || !NamesEqual(change.Previous.ExcludedDirectories, change.Current.ExcludedDirectories)
        || change.Previous.EnableGitHubDiscovery != change.Current.EnableGitHubDiscovery
        || !PathsEqual(change.Previous.GhPath.Trim(), change.Current.GhPath.Trim());

    /// <summary>
    /// The diff pane's layout. The pane caches the rendering it is showing, so a write from
    /// another surface has to reach it — nothing else re-reads the value.
    /// </summary>
    public static bool DiffLayoutChanged(SettingsChange change) =>
        change.Previous.DiffSideBySide != change.Current.DiffSideBySide;

    /// <summary>Card ordering and density: a re-read of the grid's preferences, no re-scan.</summary>
    public static bool ViewPreferencesChanged(SettingsChange change) =>
        !string.Equals(change.Previous.CardDensity, change.Current.CardDensity, StringComparison.OrdinalIgnoreCase)
        || !NamesEqual(change.Previous.PinnedProjectPaths, change.Current.PinnedProjectPaths);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool NamesEqual(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count == right.Count && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
}
