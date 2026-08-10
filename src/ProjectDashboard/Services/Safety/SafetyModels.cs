namespace ProjectDashboard.Services.Safety;

/// <summary>
/// One kind of thing the safety rollup reports on. A closed vocabulary so a surface groups and
/// orders findings on a value rather than on a substring of their text.
/// </summary>
public enum SafetySignal
{
    InterruptedOperation,
    StatusUnreadable,
    DivergedBranch,
    NoRemote,
    UnverifiedBackup,
    ReflogOnlyCommits,
    StaleProjectData,
    UncommittedWork,
}

/// <summary>
/// How loudly a finding is carried. Seven signals over a whole portfolio report constantly, so the
/// ranking is what keeps an interrupted operation from being read at the same volume as a
/// repository with uncommitted work.
/// </summary>
public enum SafetySeverity
{
    NeedsAttention,
    WorthALook,
    Informational,
}

/// <summary>
/// What a signal costs to answer. <see cref="Free"/> is computed from state the dashboard already
/// holds and spawns no git process; <see cref="Cheap"/> is one extra read per repository;
/// <see cref="Expensive"/> walks the object store or verifies a bundle and never runs unasked.
/// </summary>
public enum SafetyTier
{
    Free,
    Cheap,
    Expensive,
}

/// <summary>
/// Whether a tier's answer has been asked for. <see cref="NotRun"/> is not the same fact as an
/// answer of nothing found, and a surface that renders the two alike claims a check it never ran.
/// </summary>
public enum SafetyTierState
{
    NotRun,
    Running,
    Ran,
}

/// <summary>
/// The one thing a finding offers. Every value names a surface that carries its own gates — the
/// rollup performs no destructive or recovering operation itself.
/// </summary>
public enum SafetyAction
{
    None,
    OpenRecoveryBackups,
    OpenBackups,
    OpenChanges,
    OpenBranches,
    OpenReflog,
    OpenRemotes,
    Rescan,

    /// <summary>Runs this repository's bundle verification. The only expensive work a row starts.</summary>
    VerifyBackups,

    /// <summary>Runs this repository's reflog-only walk.</summary>
    CheckReflogOnly,
}

/// <summary>
/// One thing worth knowing about one repository. <see cref="RepoPath"/> is empty for a finding
/// that belongs to the portfolio rather than to a repository, which is the only case in which
/// <see cref="SafetyAction.Rescan"/> appears.
/// </summary>
public sealed record SafetyFinding(
    SafetySignal Signal,
    SafetySeverity Severity,
    string RepoPath,
    string RepoName,
    string Headline,
    string Detail,
    SafetyAction Action,
    string ActionLabel);

/// <summary>
/// The rollup's own wording. Pure, so every claim it makes is assertable without standing up a
/// dashboard — and so the honest phrasings stay in one place rather than being restated per group.
/// </summary>
public static class SafetyCopy
{
    /// <summary>
    /// Carried wherever the page reports that nothing is interrupted. The journal is written
    /// without a retained copy, so a torn or unreadable one yields no entries at all and an empty
    /// result cannot stand as proof.
    /// </summary>
    public const string InterruptedCaveat =
        "An empty result is not proof that nothing was interrupted: the recovery journal keeps no "
        + "second copy, so one that could not be read reports nothing pending. The backups on disk "
        + "are the record that survives that.";

    /// <summary>Shown for a tier nobody has asked to run. Never rendered as a count of zero.</summary>
    public const string NotChecked = "Not checked.";

    /// <summary>
    /// Why one repository was left out. A portfolio check is read-only and takes no lease, so it
    /// skips the repository another operation holds rather than reading refs mid-swap.
    /// </summary>
    public const string RepoBusyRefusal = "This repository is busy with another operation.";

    /// <summary>
    /// The severity line. The third count is what was measured and found nothing, deliberately not
    /// called clear: the tiers that did not run are named on the line below it.
    /// </summary>
    public static string Rollup(int needsAttention, int worthALook, int nothingFound) =>
        $"{needsAttention} need attention · {worthALook} worth a look · {nothingFound} with nothing found";

    /// <summary>
    /// Tail naming what a count left out, or empty when it left out nothing. A count that silently
    /// excludes the repositories it could not read reports a smaller portfolio than there is.
    /// </summary>
    public static string Skipped(int skipped) =>
        skipped == 0 ? "" : $" {skipped} skipped (busy).";

    /// <summary>
    /// Which tiers have run, in the words the header carries. Stated whatever the findings are:
    /// an absence of findings from a tier that never ran is not a clean bill of health.
    /// </summary>
    public static string TiersRun(SafetyTierState cheap, int verified, int reflogChecked, int repoCount)
    {
        var branches = cheap switch
        {
            SafetyTierState.Ran => "Branches and backups checked across the portfolio.",
            SafetyTierState.Running => "Checking branches and backups…",
            _ => "Free checks only — branches and backups not checked.",
        };

        var expensive = repoCount == 0
            ? ""
            : $" Backups verified on {verified} of {repoCount}; reflog-only commits checked on {reflogChecked} of {repoCount}.";

        return branches + expensive;
    }

    /// <summary>
    /// What one repository's backups amount to, in the same vocabulary the Backups browser uses for
    /// one bundle. A bundle found bad and a bundle the verifier never answered for are counted and
    /// worded apart, and never summed: a reader told a backup failed acts on a defect it may not
    /// have.
    /// </summary>
    public static string BackupState(int onDisk, int failed, int unknown, DateTimeOffset? checkedAt)
    {
        if (onDisk == 0) return "No backup on disk.";
        if (checkedAt is null) return $"{onDisk} backup(s) on disk, none verified.";

        var when = Stamp(checkedAt.Value);
        if (failed == 0 && unknown == 0) return $"{onDisk} backup(s) verified on {when}.";

        var parts = new List<string>();
        if (failed > 0) parts.Add($"{failed} failed verification");
        if (unknown > 0) parts.Add($"{unknown} could not be verified");
        return $"Of {onDisk} backup(s) on {when}: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// The limit of what verification establishes, carried beside every result that reports one. A
    /// reader told a backup is verified would otherwise take it as proof the objects are intact.
    /// </summary>
    public const string BackupCheckLimit =
        "Verifying runs the same check a restore makes first: it reads the bundle's header and "
        + "prerequisites, not the packed objects, so a bundle that verifies can still be damaged.";

    /// <summary>
    /// A cached expensive result always carries when it was taken. A result shown without its age
    /// is read as current, and this page never silently re-runs an expensive check to make it so.
    /// </summary>
    public static string Stamp(DateTimeOffset when) =>
        when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}
