using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// One interrupted operation as the rollup names it, assembled from the recovery journal and, when
/// the ledger holds one, the record written for it. <see cref="RecordedLabel"/> is empty when no
/// ledger record could be matched, which is reported rather than filled in from the journal alone.
/// </summary>
public sealed record InterruptedOperation(
    string RepoPath,
    string Phase,
    string Stamp,
    string? BackupStamp,
    string RecordedLabel);

/// <summary>
/// The free tier: every signal that can be answered from the project list the dashboard already
/// holds, the recovery journal read at startup, and the age of the discovery cache.
///
/// Pure and static by construction. That is what the zero-process guarantee rests on — nothing
/// here can reach git — and it is what makes each claim assertable without a repository on disk.
/// </summary>
public static class SafetySurvey
{
    /// <summary>
    /// Divergence means a branch that is both ahead of and behind its upstream, which is the state
    /// no fast-forward resolves. A branch only ahead or only behind is ordinary work in progress
    /// and the dashboard's own sync refusals already draw the line in the same place.
    /// </summary>
    public static bool IsDiverged(int ahead, int behind) => ahead > 0 && behind > 0;

    /// <summary>The repositories a portfolio check would read: local clones with a usable path.</summary>
    public static IReadOnlyList<ProjectInfo> Checkable(IEnumerable<ProjectInfo> projects) =>
        projects.Where(p => !p.IsRemoteOnly && !string.IsNullOrWhiteSpace(p.FullPath)).ToList();

    public static IReadOnlyList<SafetyFinding> Interrupted(IReadOnlyList<InterruptedOperation> pending) =>
        pending.Select(entry => new SafetyFinding(
            SafetySignal.InterruptedOperation,
            SafetySeverity.NeedsAttention,
            entry.RepoPath,
            NameOf(entry.RepoPath),
            entry.RecordedLabel.Length > 0
                ? entry.RecordedLabel
                : $"Interrupted history operation ({(entry.Phase.Length > 0 ? entry.Phase : "unrecorded phase")})",
            DescribeInterrupted(entry),
            SafetyAction.OpenRecoveryBackups,
            "Recover…")).ToList();

    /// <summary>
    /// What is known about one interrupted operation, and what is not. The backup is named when the
    /// journal recorded one; when it did not, what a restore would put back is unknown and the row
    /// says so rather than offering a restore that may reach nothing.
    /// </summary>
    private static string DescribeInterrupted(InterruptedOperation entry)
    {
        var when = entry.Stamp.Length > 0 ? $"Recorded {entry.Stamp}. " : "";
        var backup = entry.BackupStamp is { Length: > 0 } stamp
            ? $"The backup it named ({stamp}) is still on disk."
            : "The record names no backup, so what a restore would put back is unknown.";
        return when + "Nothing has been restored. " + backup;
    }

    /// <summary>
    /// A repository git could not be read in is its own finding. Folding it into the clean count
    /// would report an unmeasured repository as measured and found sound.
    /// </summary>
    public static IReadOnlyList<SafetyFinding> StatusUnreadable(IEnumerable<ProjectInfo> projects) =>
        Checkable(projects)
            .Where(p => p.GitStatus.HasError)
            .Select(p => new SafetyFinding(
                SafetySignal.StatusUnreadable,
                SafetySeverity.NeedsAttention,
                p.FullPath,
                p.DirectoryName,
                "Status unavailable",
                "git could not read this repository, so none of the checks below describe it.",
                SafetyAction.OpenChanges,
                "Open repository"))
            .ToList();

    /// <summary>Current-branch divergence, which the card pass already read. The all-branch answer is the cheap tier's.</summary>
    public static IReadOnlyList<SafetyFinding> DivergedCurrentBranch(IEnumerable<ProjectInfo> projects) =>
        Checkable(projects)
            .Where(p => !p.GitStatus.HasError && IsDiverged(p.GitStatus.AheadBy, p.GitStatus.BehindBy))
            .Select(p => new SafetyFinding(
                SafetySignal.DivergedBranch,
                SafetySeverity.WorthALook,
                p.FullPath,
                p.DirectoryName,
                $"{Branch(p)} has diverged from its upstream",
                $"{p.GitStatus.AheadBy} ahead, {p.GitStatus.BehindBy} behind. Neither side fast-forwards onto the other.",
                SafetyAction.OpenBranches,
                "Open Branches"))
            .ToList();

    /// <summary>The all-branch answer for one repository, which replaces that repository's free-tier row.</summary>
    public static IReadOnlyList<SafetyFinding> DivergedBranches(
        ProjectInfo project, IReadOnlyList<BranchInfo> branches) =>
        branches
            .Where(b => b.Upstream.Length > 0 && !b.UpstreamGone && IsDiverged(b.Ahead, b.Behind))
            .Select(b => new SafetyFinding(
                SafetySignal.DivergedBranch,
                SafetySeverity.WorthALook,
                project.FullPath,
                project.DirectoryName,
                $"{b.Name} has diverged from {b.Upstream}",
                $"{b.Ahead} ahead, {b.Behind} behind. Neither side fast-forwards onto the other.",
                SafetyAction.OpenBranches,
                "Open Branches"))
            .ToList();

    public static IReadOnlyList<SafetyFinding> NoRemote(IEnumerable<ProjectInfo> projects) =>
        Checkable(projects)
            .Where(p => !p.GitStatus.HasError && p.GitStatus.RemoteUrl.Length == 0)
            .Select(p => new SafetyFinding(
                SafetySignal.NoRemote,
                SafetySeverity.WorthALook,
                p.FullPath,
                p.DirectoryName,
                "No remote configured",
                "Every commit here exists on this machine only. A backup bundle is local too.",
                SafetyAction.OpenRemotes,
                "Open Remotes"))
            .ToList();

    /// <summary>
    /// Uncommitted work. Reported at the lowest volume and counted with the same predicate the
    /// dashboard's Dirty chip uses, so the two surfaces can never disagree about how many there are.
    /// </summary>
    public static IReadOnlyList<SafetyFinding> UncommittedWork(IEnumerable<ProjectInfo> projects) =>
        projects
            .Where(p => p.GitStatus.IsDirty)
            .Select(p => new SafetyFinding(
                SafetySignal.UncommittedWork,
                SafetySeverity.Informational,
                p.FullPath,
                p.DirectoryName,
                $"{p.GitStatus.TotalChanges} uncommitted change(s)",
                "Uncommitted work is in no commit, so no backup bundle and no remote holds it.",
                SafetyAction.OpenChanges,
                "Open Changes"))
            .ToList();

    /// <summary>
    /// Staleness of the whole scanned set, which is the only staleness there is: the cache carries
    /// one stamp for every card in it, not one per card. A null stamp is a set that has not been
    /// scanned in this session and whose age is therefore unknown, never a set of age zero.
    /// </summary>
    public static IReadOnlyList<SafetyFinding> StaleProjectData(
        DateTimeOffset? lastDiscoveryAt, int refreshIntervalSeconds, DateTimeOffset now)
    {
        if (lastDiscoveryAt is null)
            return [new SafetyFinding(
                SafetySignal.StaleProjectData,
                SafetySeverity.Informational,
                "",
                "",
                "Project data age unknown",
                "Nothing has recorded when this project list was read, so how old these cards are is unknown.",
                SafetyAction.Rescan,
                "Rescan")];

        var age = now - lastDiscoveryAt.Value;
        if (age.TotalSeconds <= refreshIntervalSeconds) return [];

        return [new SafetyFinding(
            SafetySignal.StaleProjectData,
            SafetySeverity.Informational,
            "",
            "",
            $"Project data is older than the refresh interval",
            $"Read {SafetyCopy.Stamp(lastDiscoveryAt.Value)}, which is longer ago than the "
            + $"{refreshIntervalSeconds}s refresh interval. Every card above describes the repository as it was then.",
            SafetyAction.Rescan,
            "Rescan")];
    }

    private static string Branch(ProjectInfo project) =>
        project.GitStatus.Branch.Length > 0 ? project.GitStatus.Branch : "The current branch";

    private static string NameOf(string repoPath)
    {
        var name = Path.GetFileName(repoPath.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : repoPath;
    }
}
