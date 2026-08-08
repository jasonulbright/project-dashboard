namespace ProjectDashboard.Models;

/// <summary>
/// One reflog entry as the viewer shows it. <see cref="Selector"/> is the index form
/// (<c>main@{2}</c>), which shifts as new entries are recorded, so every operation is bound to
/// <see cref="Sha"/> instead. <see cref="When"/> is the moment the entry was written, not the
/// commit date — a reset records today against a commit from last year.
/// </summary>
public sealed record ReflogEntry(
    string Selector,
    string Action,
    string Subject,
    string Sha,
    DateTimeOffset? When)
{
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;

    /// <summary>The action and its subject as one line, with no separator when the entry recorded no subject.</summary>
    public string Description => Subject.Length > 0 ? $"{Action}: {Subject}" : Action;
}
