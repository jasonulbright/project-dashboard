using System.Windows.Input;
using ProjectDashboard.Models;

namespace ProjectDashboard.Views.Pages;

/// <summary>The lazy fetch a tab still owes on activation.</summary>
public enum DetailTabLoad
{
    None,
    Branches,
    Stashes,
    PullRequests,
}

/// <summary>
/// Pure tab-routing logic for the detail page, kept free of control/git state so
/// the hotkey mapping and lazy-load dispatch are unit-testable without an STA host.
/// </summary>
public static class ProjectDetailTabs
{
    /// <summary>
    /// Maps a Ctrl+digit key to a zero-based tab index: D1..D9 → 0..8, D0 → 9.
    /// Returns null for any non-digit key or an index beyond the live tab count,
    /// so an unbound digit is an inert no-op rather than an out-of-range jump.
    /// </summary>
    public static int? TabIndexForDigit(Key key, int tabCount)
    {
        var index = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            Key.D0 => 9,
            _ => -1,
        };
        return index >= 0 && index < tabCount ? index : null;
    }

    /// <summary>
    /// The lazy load a tab needs on activation. Only Branches/Stashes/PullRequests
    /// fetch; each guard mirrors its command's own "already loaded" check so a
    /// revisit stays inert.
    /// </summary>
    public static DetailTabLoad LoadForTab(DetailTab tab, bool branchesLoaded, bool stashesLoaded, bool pullRequestsLoaded)
        => tab switch
        {
            DetailTab.Branches when !branchesLoaded => DetailTabLoad.Branches,
            DetailTab.Stashes when !stashesLoaded => DetailTabLoad.Stashes,
            DetailTab.PullRequests when !pullRequestsLoaded => DetailTabLoad.PullRequests,
            _ => DetailTabLoad.None,
        };
}
