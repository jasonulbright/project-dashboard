using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// The gated entry point for history editing and commit surgery, on the same rails as the
/// rewrite engine: acquire the busy lease, refuse a tree the operation cannot run against,
/// take a verified backup, journal the in-flight operation, operate, then clear the journal.
/// The lease is released in a finally; nothing is ever pushed.
///
/// A failure leaves the journal AND the backup on disk on purpose, so undo and next-launch
/// recovery both still work. That includes a revert or cherry-pick that stops on a conflict:
/// the coordinator does not abort it — leaving it mid-operation is the documented behaviour,
/// and the pending journal is what makes it recoverable.
///
/// Soft and mixed resets are the exception to the backup rule: they move a ref and the index
/// without discarding committed work or worktree content, so they take the lease only. A hard
/// reset destroys uncommitted work and goes through the full rails like any rewrite.
/// </summary>
public sealed class SurgeryCoordinator
{
    private readonly BackupService _backup;
    private readonly RepoBusyRegistry _busy;
    private readonly GitService _git;
    private readonly RebaseDriver _driver;
    private readonly CommitSurgery _surgery;
    private readonly HistoryEdits _edits;
    private readonly RewriteJournal _journal;

    public SurgeryCoordinator(
        BackupService backup,
        RepoBusyRegistry busy,
        GitService git,
        RebaseDriver? driver = null,
        CommitSurgery? surgery = null,
        HistoryEdits? edits = null,
        RewriteJournal? journal = null)
    {
        _backup = backup;
        _busy = busy;
        _git = git;
        _driver = driver ?? new RebaseDriver(git);
        _surgery = surgery ?? new CommitSurgery(git, _driver);
        _edits = edits ?? new HistoryEdits(git);
        _journal = journal ?? new RewriteJournal();
    }

    /// <summary>The commits an edit may rearrange. Read outside the gate so a UI can plan before committing to anything.</summary>
    public Task<RebaseScope> LoadScopeAsync(string repoPath, int depth, CancellationToken ct = default) =>
        _driver.LoadScopeAsync(repoPath, depth, ct);

    public Task<SurgeryResult> ReorderAsync(
        string repoPath, int depth, IReadOnlyList<string> shasInNewOrder,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, token) => _driver.ReorderAsync(scope, shasInNewOrder, policy, token), policy, ct);

    public Task<SurgeryResult> DropAsync(
        string repoPath, int depth, IReadOnlyList<string> shasToDrop,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, token) => _driver.DropAsync(scope, shasToDrop, policy, token), policy, ct);

    public Task<SurgeryResult> SquashAsync(
        string repoPath, int depth, IReadOnlyList<string> shasToFold, string? newMessage = null,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, token) => _driver.SquashAsync(scope, shasToFold, newMessage, policy, token), policy, ct);

    public Task<SurgeryResult> RewordAsync(
        string repoPath, int depth, string sha, string newMessage,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, token) => _driver.RewordAsync(scope, sha, newMessage, policy, token), policy, ct);

    /// <summary>
    /// Folds the staged changes into an older commit. The tree gate here admits staged changes —
    /// they are the operation's input — while still refusing unstaged ones, which git would
    /// reject at the rebase.
    /// </summary>
    public Task<SurgeryResult> InjectStagedIntoAsync(
        string repoPath, string targetCommit,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default) =>
        RunGatedAsync(repoPath, TreeRequirement.NoUnstagedChanges, backup: true, "inject", async token =>
        {
            var run = await _surgery.InjectStagedIntoAsync(repoPath, targetCommit, policy, token);
            return (run.Success, run.FailureReason, run, null);
        }, ct);

    /// <summary>Moves the branch. A hard reset takes the full rails; soft and mixed take the lease only.</summary>
    public Task<SurgeryResult> ResetAsync(
        string repoPath, string target, ResetMode mode, CancellationToken ct = default) =>
        RunGatedAsync(repoPath,
            mode == ResetMode.Hard ? TreeRequirement.Clean : TreeRequirement.Any,
            backup: mode == ResetMode.Hard, "reset", async token =>
        {
            var edit = await _edits.ResetAsync(repoPath, target, mode, token);
            return (edit.Success, edit.FailureReason, null, edit);
        }, ct);

    /// <summary>
    /// Reverts a commit. A conflicted revert is a failure result, but the repository is left
    /// mid-revert deliberately — the journal and backup stay behind so undo still works.
    /// </summary>
    public Task<SurgeryResult> RevertAsync(
        string repoPath, string commit, bool autoCommit = true, CancellationToken ct = default) =>
        RunGatedAsync(repoPath, TreeRequirement.Clean, backup: true, "revert", async token =>
        {
            var edit = await _edits.RevertAsync(repoPath, commit, autoCommit, token);
            return (edit.Success, edit.FailureReason, null, edit);
        }, ct);

    public Task<SurgeryResult> CherryPickAsync(
        string repoPath, IReadOnlyList<string> commits, CancellationToken ct = default) =>
        RunGatedAsync(repoPath, TreeRequirement.Clean, backup: true, "cherry-pick", async token =>
        {
            var edit = await _edits.CherryPickAsync(repoPath, commits, token);
            return (edit.Success, edit.FailureReason, null, edit);
        }, ct);

    private Task<SurgeryResult> RunRebaseAsync(
        string repoPath, int depth, Func<RebaseScope, CancellationToken, Task<RebaseRunResult>> operate,
        RebaseConflictPolicy policy, CancellationToken ct) =>
        RunGatedAsync(repoPath, TreeRequirement.Clean, backup: true, "rebase", async token =>
        {
            RebaseScope scope;
            try
            {
                scope = await _driver.LoadScopeAsync(repoPath, depth, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (false, $"the range is not editable: {ex.Message}", null, null);
            }

            var run = await operate(scope, token);
            return (run.Success, run.FailureReason, run, null);
        }, ct);

    private async Task<SurgeryResult> RunGatedAsync(
        string repoPath,
        TreeRequirement requirement,
        bool backup,
        string phase,
        Func<CancellationToken, Task<(bool Success, string? Reason, RebaseRunResult? Rebase, HistoryEditResult? Edit)>> operate,
        CancellationToken ct)
    {
        // 1. Busy gate: a second destructive operation on the same repo is refused, not queued.
        if (!_busy.TryAcquire(repoPath, out var lease))
            return SurgeryResult.Failed($"repository is busy with another operation: {repoPath}");

        UndoHandle? undo = null;
        var journalled = false;
        try
        {
            // 2. Tree gate: what the operation needs the working tree to look like. Reported
            // with the offending files so the caller can act rather than guess.
            var state = await _git.GetWorkingStateAsync(repoPath, ct);
            if (state is null)
                return SurgeryResult.Failed($"repository '{repoPath}' could not be read by git");
            if (state.Activity != Models.RepoActivity.None)
                return SurgeryResult.Failed(
                    $"the repository is already in the middle of a {Describe(state.Activity)} — finish or abort it first");
            var refusal = CheckTree(state, requirement);
            if (refusal is not null)
                return SurgeryResult.Failed(refusal);

            // 3. Verified backup: no destructive operation proceeds without one.
            if (backup)
            {
                BackupHandle handle;
                try
                {
                    handle = await _backup.CreateBackupAsync(repoPath, ct);
                }
                catch (BackupException ex)
                {
                    return SurgeryResult.Failed($"backup failed — no history was touched: {ex.Message}");
                }
                undo = new UndoHandle(_backup, handle);

                // 4. Journal: the in-flight operation is on disk before anything moves, so a
                // crash part-way through is detectable at the next launch.
                await _journal.BeginAsync(new RewriteJournalEntry
                {
                    RepoPath = repoPath,
                    BackupHandle = handle,
                    Phase = phase,
                    UtcStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                }, ct);
                journalled = true;
            }

            // 5. Operate.
            bool success;
            string? reason;
            RebaseRunResult? rebase;
            HistoryEditResult? edit;
            try
            {
                (success, reason, rebase, edit) = await operate(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return SurgeryResult.Failed($"the {phase} failed: {ex.Message}", undo);
            }

            // 6. The journal stays pending only when the repository may have been left altered:
            // a success that left sequencer state behind, a stopped rebase, a conflicted revert
            // or cherry-pick. A success that concluded, and any refusal that proves nothing
            // moved, clear it — the backup stays either way.
            if (journalled && (success ? edit?.LeftMidOperation != true : ProvesNothingMoved(rebase, edit)))
                await _journal.CompleteAsync(repoPath, ct);

            return new SurgeryResult
            {
                Success = success,
                FailureReason = reason,
                Advisory = edit?.Advisory,
                Undo = undo,
                Rebase = rebase,
                Edit = edit
            };
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Whether a failed outcome establishes that no ref, index entry, or tracked file moved — a
    /// rebase that aborted all the way back, or a refusal that never reached git. Both results
    /// null means the operation was refused before any git-level work existed to report.
    /// </summary>
    private static bool ProvesNothingMoved(RebaseRunResult? rebase, HistoryEditResult? edit)
    {
        if (rebase is not null) return rebase.RepositoryUntouched;
        if (edit is not null) return edit.RepositoryUntouched;
        return true;
    }

    private static string? CheckTree(Models.WorkingState state, TreeRequirement requirement)
    {
        switch (requirement)
        {
            case TreeRequirement.Clean when state.IsDirty:
                return $"working tree has {state.Files.Count} uncommitted change(s) — " +
                       $"refusing (stash or commit first): {List(state.Files)}";
            case TreeRequirement.NoUnstagedChanges:
            {
                var unstaged = state.Unstaged.ToList();
                if (unstaged.Count > 0)
                    return $"working tree has {unstaged.Count} unstaged change(s) — " +
                           $"stage or stash them first: {List(unstaged)}";
                if (!state.Staged.Any())
                    return "nothing is staged — stage the fix first";
                return null;
            }
            default:
                return null;
        }
    }

    private static string List(IReadOnlyList<Models.WorkingFile> files)
    {
        var names = files.Take(10).Select(f => f.Path).ToList();
        var listed = string.Join(", ", names);
        if (files.Count > names.Count) listed += $", … (+{files.Count - names.Count} more)";
        return listed;
    }

    private static string Describe(Models.RepoActivity activity) => activity switch
    {
        Models.RepoActivity.Merging => "merge",
        Models.RepoActivity.Rebasing => "rebase",
        Models.RepoActivity.CherryPicking => "cherry-pick",
        Models.RepoActivity.Reverting => "revert",
        Models.RepoActivity.Bisecting => "bisect",
        _ => "operation"
    };
}
