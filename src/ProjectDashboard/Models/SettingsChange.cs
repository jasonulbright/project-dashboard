using ProjectDashboard.Services;

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

    /// <summary>
    /// The paths the file watcher should be pointed at; empty when auto-refresh is off. One
    /// watcher cannot cover disjoint roots, so this is a set rather than a path.
    /// </summary>
    public static IReadOnlyList<string> WatcherRoots(AppSettings settings) =>
        settings.EnableAutoRefresh
            ? [.. ProjectRootSettings.Scannable(settings).Select(r => r.Path)]
            : [];

    public static bool ThemeChanged(SettingsChange change) =>
        !string.Equals(change.Previous.Theme, change.Current.Theme, StringComparison.OrdinalIgnoreCase);

    public static bool RefreshIntervalChanged(SettingsChange change) =>
        EffectiveRefreshSeconds(change.Previous) != EffectiveRefreshSeconds(change.Current);

    /// <summary>Floor for the background-fetch interval; the feature is a trickle, never a poll.</summary>
    public const int MinimumFetchMinutes = 15;

    public static int EffectiveFetchMinutes(int configuredMinutes) =>
        Math.Max(MinimumFetchMinutes, configuredMinutes);

    public static int EffectiveFetchMinutes(AppSettings settings) =>
        EffectiveFetchMinutes(settings.ScheduledFetchIntervalMinutes);

    public static bool ScheduledFetchChanged(SettingsChange change) =>
        change.Previous.EnableScheduledFetch != change.Current.EnableScheduledFetch
        || EffectiveFetchMinutes(change.Previous) != EffectiveFetchMinutes(change.Current);

    public static bool WatcherTargetChanged(SettingsChange change) =>
        !NamesEqual(WatcherRoots(change.Previous), WatcherRoots(change.Current));

    /// <summary>
    /// True when the discovered set itself can differ, so the cached scan is stale and a
    /// full re-scan is the only thing that can show it. The discovery cache is keyed on age
    /// alone, so a plain reload would re-serve the previous root's projects until it expires.
    ///
    /// The roots are compared as an ordered sequence, whole: a reorder changes which root wins
    /// a same-named repository, and a depth, exclusion, or enabled edit changes the set outright.
    /// A comparison that missed any of them would leave a settings write that silently does not
    /// rescan.
    /// </summary>
    public static bool RediscoveryRequired(SettingsChange change) =>
        !RootsEqual(change.Previous.ProjectRoots, change.Current.ProjectRoots)
        || change.Previous.EnableGitHubDiscovery != change.Current.EnableGitHubDiscovery
        || !PathsEqual(change.Previous.GhPath.Trim(), change.Current.GhPath.Trim());

    private static bool RootsEqual(IReadOnlyList<ProjectRoot> left, IReadOnlyList<ProjectRoot> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!PathsEqual(left[i].Path, right[i].Path)) return false;
            if (left[i].Enabled != right[i].Enabled) return false;
            if (left[i].MaxDepth != right[i].MaxDepth) return false;
            if (!NamesEqual(left[i].ExcludedDirectories, right[i].ExcludedDirectories)) return false;
        }
        return true;
    }

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

    /// <summary>
    /// The metadata lists. The manifest editor's pickers and every card chip are derived from
    /// them and held until something says otherwise, so a rename made in Settings reaches an
    /// already-open page only through here.
    /// </summary>
    public static bool TaxonomyChanged(SettingsChange change) =>
        Taxonomy.Fields.Any(field => !EntriesEqual(
            Taxonomy.Entries(change.Previous.Taxonomy ?? new TaxonomyConfig(), field),
            Taxonomy.Entries(change.Current.Taxonomy ?? new TaxonomyConfig(), field)));

    private static bool EntriesEqual(IReadOnlyList<TaxonomyEntry> left, IReadOnlyList<TaxonomyEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(left[i].Color, right[i].Color, StringComparison.OrdinalIgnoreCase)) return false;
            if (left[i].ShowOnCard != right[i].ShowOnCard) return false;
        }
        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool NamesEqual(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count == right.Count && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
}
