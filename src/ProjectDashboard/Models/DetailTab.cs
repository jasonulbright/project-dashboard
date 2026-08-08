namespace ProjectDashboard.Models;

/// <summary>
/// Stable identity for each work-area tab, independent of its header text so a
/// header rename never re-routes lazy loading. Values 0..6 are the shipped tabs
/// and must keep their positions; later tabs append without renumbering.
/// </summary>
public enum DetailTab
{
    Overview,
    Changes,
    History,
    Branches,
    Issues,
    PullRequests,
    Stashes,
    Actions,
    Releases,
    Repo,
    Internals,
}
