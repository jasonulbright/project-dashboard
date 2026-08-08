using ProjectDashboard.Services.Rewrite;

namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// What to do when a rebase stops mid-flight (conflict, or a pick that would become
/// empty). There is no in-app merge editor, so automatic resolution is never attempted.
/// </summary>
public enum RebaseConflictPolicy
{
    /// <summary>Run `git rebase --abort` and report which commit stopped it. The repository is left exactly as it was.</summary>
    AbortAndReport,

    /// <summary>Leave the rebase stopped so the user can finish it in a terminal. The repo stays mid-rebase and the state banner reports it.</summary>
    LeaveStopped
}

/// <summary>How far back a reset moves the index and working tree.</summary>
public enum ResetMode
{
    Soft,
    Mixed,
    Hard
}

/// <summary>
/// What to do about commit signing on an operation that creates commits in a repository
/// configured to sign them. Every replayed commit is re-signed, and a signing key whose
/// passphrase is not cached makes git wait on a pinentry prompt this app cannot answer — the
/// operation then runs out its timeout and is killed.
///
/// <see cref="NotChosen"/> is refused rather than defaulted: signing off would drop signatures
/// the user asked for, signing on would risk the stall, and neither is this layer's call to
/// make silently.
/// </summary>
public enum SigningChoice
{
    NotChosen,

    /// <summary>Sign as configured, accepting that an uncached passphrase stalls the operation until its timeout.</summary>
    KeepSigning,

    /// <summary>Run with `commit.gpgsign=false`; the commits this operation creates or replays come out unsigned.</summary>
    ProceedUnsigned
}

/// <summary>
/// Which gate turned a gated operation away before git ran. <see cref="None"/> once the
/// operation reached git, whatever it then reported.
///
/// Only <see cref="UncommittedChanges"/> describes a tree a stash resolves. The
/// <see cref="UnstagedChanges"/> gate guards an operation whose input is the staged changes,
/// and `git stash push` takes those with it, so stashing there removes what the operation runs on.
/// </summary>
public enum SurgeryRefusal
{
    None,
    RepositoryBusy,
    RepositoryUnreadable,
    OperationInProgress,
    UncommittedChanges,
    UnstagedChanges,
    NothingStaged,
    BackupFailed,

    /// <summary>The repository signs commits and no <see cref="SigningChoice"/> was made; see that type for why this is not defaulted.</summary>
    CommitSigningChoiceRequired
}

/// <summary>What the clean-tree gate demands before a destructive operation runs.</summary>
public enum TreeRequirement
{
    /// <summary>No index or working-tree changes at all. Rebase, hard reset, revert, cherry-pick.</summary>
    Clean,

    /// <summary>Staged changes are the operation's input; unstaged changes would make git refuse the follow-up rebase.</summary>
    NoUnstagedChanges,

    /// <summary>The operation is defined on a dirty tree. Soft and mixed reset.</summary>
    Any
}

/// <summary>One commit as an interactive-rebase todo line source.</summary>
public sealed record RebaseCommit(string Sha, string Subject);

/// <summary>
/// The commits an edit may rearrange, oldest first, plus the commit they replay onto.
/// A null <see cref="BaseSha"/> means the range reaches the root commit and the rebase
/// runs with `--root`.
/// </summary>
public sealed class RebaseScope
{
    public required string RepoPath { get; init; }

    public string? BaseSha { get; init; }

    public required IReadOnlyList<RebaseCommit> Commits { get; init; }

    public bool IncludesRoot => BaseSha is null;

    public bool Contains(string sha) => Commits.Any(c => string.Equals(c.Sha, sha, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Outcome of one driven rebase. A failure is data: <see cref="ConflictCommit"/> names the
/// commit that stopped the replay, and exactly one of <see cref="Aborted"/> (refs, index and
/// tracked content are back to their pre-operation state) or <see cref="LeftStopped"/> (the
/// repository is mid-rebase for the terminal) is set whenever the rebase stopped rather than
/// failing to start.
/// </summary>
public sealed class RebaseRunResult
{
    public required bool Success { get; init; }

    public string? FailureReason { get; init; }

    public string? ConflictCommit { get; init; }

    public string? ConflictSubject { get; init; }

    public bool Aborted { get; init; }

    public bool LeftStopped { get; init; }

    /// <summary>True when the stop was a pick that would produce an empty commit, not a content conflict.</summary>
    public bool StoppedEmpty { get; init; }

    /// <summary>True when the rebase exceeded its timeout, was killed, and then aborted.</summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Untracked, non-ignored paths that appeared during a replay that was then aborted — a hook's
    /// doing, which `git rebase --abort` does not undo. The tree gate refuses them on the next
    /// operation, so they are reported rather than left to be discovered there.
    /// </summary>
    public IReadOnlyList<string> UntrackedAdded { get; init; } = [];

    /// <summary>
    /// True when no ref, index entry, or tracked file changed: a refusal made before git ran, or
    /// a stop that was aborted all the way back. It is the signal that no recovery marker should
    /// survive the call. <see cref="UntrackedAdded"/> is reported separately because git's abort
    /// does not remove those.
    /// </summary>
    public bool RepositoryUntouched { get; init; }

    public string HeadAfter { get; init; } = "";

    /// <summary>The todo actually handed to git — the audit trail for what the driver asked for.</summary>
    public IReadOnlyList<string> Todo { get; init; } = [];

    internal static RebaseRunResult Failed(string reason) =>
        new() { Success = false, FailureReason = reason, RepositoryUntouched = true };
}

/// <summary>
/// Outcome of a reset, revert, or cherry-pick. <see cref="Conflicted"/> means the repository
/// is deliberately left mid-operation with the conflicted paths listed: there is no in-app
/// conflict editor, so the user finishes or aborts it from the terminal.
/// </summary>
public sealed class HistoryEditResult
{
    public required bool Success { get; init; }

    public string? FailureReason { get; init; }

    public bool Conflicted { get; init; }

    public IReadOnlyList<string> ConflictPaths { get; init; } = [];

    /// <summary>
    /// True when the operation finished but git's sequencer state is still present — a
    /// `revert --no-commit` leaves REVERT_HEAD behind. Every gated operation refuses a repository
    /// in that state, so the outcome is a success the caller has to conclude, not a clean one.
    /// </summary>
    public bool LeftMidOperation { get; init; }

    /// <summary>What is still outstanding when <see cref="LeftMidOperation"/> is set.</summary>
    public string? Advisory { get; init; }

    /// <summary>
    /// True when the result is a refusal made before git could change anything, so no backup
    /// needs keeping and no recovery marker should survive the call.
    /// </summary>
    public bool RepositoryUntouched { get; init; }

    public string HeadAfter { get; init; } = "";

    internal static HistoryEditResult Failed(string reason) =>
        new() { Success = false, FailureReason = reason, RepositoryUntouched = true };
}

/// <summary>
/// The result of a gated <see cref="SurgeryCoordinator"/> operation. <see cref="Undo"/> is
/// present whenever a backup was taken — including on failure, so an operation that stopped
/// part-way is still restorable. Exactly one of <see cref="Rebase"/> and <see cref="Edit"/>
/// carries the underlying git-level detail.
/// </summary>
public sealed class SurgeryResult
{
    public required bool Success { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>
    /// Restore to the pre-operation backup. A soft or mixed reset runs against a dirty tree by
    /// design, and <see cref="UndoHandle.RestoreAsync"/> ends in reset --hard, so undoing one
    /// discards uncommitted work the backup never captured. A caller MUST confirm before
    /// restoring a dirty tree and MUST surface <see cref="Safety.RestoreResult.WorktreeWasDirty"/>
    /// and <see cref="Safety.RestoreResult.DiscardedChangeCount"/> from the outcome.
    /// </summary>
    public UndoHandle? Undo { get; init; }

    /// <summary>Set when the operation succeeded yet left work for the user to conclude — see <see cref="HistoryEditResult.LeftMidOperation"/>.</summary>
    public string? Advisory { get; init; }

    public RebaseRunResult? Rebase { get; init; }

    public HistoryEditResult? Edit { get; init; }

    /// <summary>Which gate refused before git ran, or <see cref="SurgeryRefusal.None"/> once it did.</summary>
    public SurgeryRefusal Refusal { get; init; }

    /// <summary>
    /// True only where no ref, index entry, or tracked file moved: a gate refusal, or an outcome
    /// whose underlying result proves the repository is back where it started. A failure that left
    /// the operation as an exception leaves this false — what had already reached the repository is
    /// unknown at that point, which is the case <see cref="Undo"/> exists for. A caller MUST NOT
    /// withhold the undo unless this is true.
    /// </summary>
    public bool RepositoryUntouched { get; init; }

    /// <summary>A gate refusal: nothing ran, so nothing moved.</summary>
    internal static SurgeryResult Refused(string reason, SurgeryRefusal refusal) =>
        new() { Success = false, FailureReason = reason, Refusal = refusal, RepositoryUntouched = true };

    /// <summary>A failure whose effect on the repository is unknown. <paramref name="undo"/> is the way back.</summary>
    internal static SurgeryResult Failed(string reason, UndoHandle? undo = null) =>
        new() { Success = false, FailureReason = reason, Undo = undo };
}
