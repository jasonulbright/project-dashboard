namespace ProjectDashboard.Views.Windows;

/// <summary>
/// NavigationView that can register dynamically added menu items.
///
/// WPF-UI builds its PageIdOrTargetTag dictionary once (OnInitialized) and only
/// refreshes it when the ROOT MenuItems collection changes. Items added to a
/// nested item's MenuItems — our per-project sidebar entries — never register,
/// and GoBack() resolves journal entries with a throwing indexer, so navigating
/// project → project → back crashed with KeyNotFoundException. The library's
/// own walk is recursive and idempotent, just protected; expose it.
/// </summary>
public class AppNavigationView : NavigationView
{
    /// <summary>
    /// Call after mutating any nested MenuItems collection. Registration is
    /// additive-only and keyed by per-instance item Id, so it is only bounded
    /// when callers reuse item instances across rebuilds: fresh instances per
    /// rebuild grow the dictionaries monotonically, while removing stale
    /// entries instead would break journal resolution for GoBack targets.
    /// </summary>
    public void RegisterDynamicMenuItems()
    {
        AddItemsToDictionaries(MenuItems);
        AddItemsToDictionaries(FooterMenuItems);
    }
}
