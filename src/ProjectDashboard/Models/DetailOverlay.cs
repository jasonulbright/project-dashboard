namespace ProjectDashboard.Models;

/// <summary>
/// A full-page pane on the detail page that a deep link can open. Distinct from
/// <see cref="DetailTab"/>: an overlay draws over the work area rather than selecting inside it,
/// so the two travel separately and a link may carry either, both, or neither.
/// </summary>
public enum DetailOverlay
{
    Backups,

    /// <summary>The Backups browser opened on the bundle an interrupted operation recorded.</summary>
    RecoveryBackups,

    Reflog,
}
