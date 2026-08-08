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
    WorkflowRuns,
    Releases,
    RepoTab,
}

/// <summary>
/// What each lazy surface has already fetched for the project on screen. Named
/// members rather than positional flags: the surfaces differ only by type-identical
/// booleans, and a transposed pair would route a load to the wrong tab silently.
/// </summary>
public readonly record struct DetailTabLoadState(
    bool Branches,
    bool Stashes,
    bool PullRequests,
    bool WorkflowRuns,
    bool Releases,
    bool RepoTab);

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
    /// Position of the first tab tagged <paramref name="tab"/>, or null when the sequence
    /// carries none. A deep link names the surface it wants, never its position: the
    /// positions shift as tabs are added, and a stale one selects a different surface
    /// without failing.
    /// </summary>
    public static int? IndexOfTab(IEnumerable<DetailTab?> tags, DetailTab tab)
    {
        var index = 0;
        foreach (var tag in tags)
        {
            if (tag == tab) return index;
            index++;
        }
        return null;
    }

    /// <summary>
    /// The lazy load a tab needs on activation. Only the remote/expensive surfaces
    /// fetch; each guard mirrors its command's own "already loaded" check so a
    /// revisit stays inert.
    /// </summary>
    public static DetailTabLoad LoadForTab(DetailTab tab, DetailTabLoadState loaded)
        => tab switch
        {
            DetailTab.Branches when !loaded.Branches => DetailTabLoad.Branches,
            DetailTab.Stashes when !loaded.Stashes => DetailTabLoad.Stashes,
            DetailTab.PullRequests when !loaded.PullRequests => DetailTabLoad.PullRequests,
            DetailTab.Actions when !loaded.WorkflowRuns => DetailTabLoad.WorkflowRuns,
            DetailTab.Releases when !loaded.Releases => DetailTabLoad.Releases,
            DetailTab.Repo when !loaded.RepoTab => DetailTabLoad.RepoTab,
            _ => DetailTabLoad.None,
        };
}
