using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// The gated entry point for history editing and commit surgery, on the same rails as the
/// rewrite engine: acquire the busy lease, refuse a tree the operation cannot run against,
/// take a verified backup, journal the in-flight operation, operate, then clear the journal.
/// The lease is released in a finally; nothing is ever pushed. Every operation here can move a
/// branch off commits, including a soft or mixed reset to an earlier revision, so every one of
/// them takes a backup and hands back an undo.
///
/// The journal is the next-launch recovery marker, so it survives only outcomes that may have
/// left the repository altered: a rebase stopped for the terminal, a conflicted revert or
/// cherry-pick, and any failure the coordinator cannot classify. An outcome that proves nothing
/// moved — a driver refusal, an aborted rebase — clears it, because a marker for an operation
/// that never ran trains the user to dismiss the prompt that matters. The backup stays on disk
/// either way, so undo still works after a refusal.
///
/// An operation that creates commits in a repository with commit.gpgsign on is refused until the
/// caller supplies a <see cref="SigningChoice"/>. Signing is never turned off on this layer's own
/// initiative: `-c commit.gpgsign=false` is passed only for
/// <see cref="SigningChoice.ProceedUnsigned"/>.
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

    /// <summary>
    /// Clears the recovery marker after a restore has put <paramref name="repoPath"/> back at its
    /// backup. The operation the marker describes no longer exists in the repository, so leaving
    /// it would report an interrupted rewrite at the next launch for history that is already whole.
    /// </summary>
    public Task ConcludeUndoAsync(string repoPath, CancellationToken ct = default) =>
        _journal.CompleteAsync(repoPath, ct);

    public Task<SurgeryResult> ReorderAsync(
        string repoPath, int depth, IReadOnlyList<string> shasInNewOrder,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, unsigned, token) => _driver.ReorderAsync(scope, shasInNewOrder, policy, unsigned, token), signing, ct);

    public Task<SurgeryResult> DropAsync(
        string repoPath, int depth, IReadOnlyList<string> shasToDrop,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, unsigned, token) => _driver.DropAsync(scope, shasToDrop, policy, unsigned, token), signing, ct);

    public Task<SurgeryResult> SquashAsync(
        string repoPath, int depth, IReadOnlyList<string> shasToFold, string? newMessage = null,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, unsigned, token) => _driver.SquashAsync(scope, shasToFold, newMessage, policy, unsigned, token), signing, ct);

    public Task<SurgeryResult> RewordAsync(
        string repoPath, int depth, string sha, string newMessage,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, unsigned, token) => _driver.RewordAsync(scope, sha, newMessage, policy, unsigned, token), signing, ct);

    /// <summary>
    /// Applies one combined plan — reorder, drops, squashes and rewords together — on the same
    /// rails as every other rebase here. The plan is compiled against the range this call loads,
    /// so a plan built against history that has since moved is refused before git runs.
    /// </summary>
    public Task<SurgeryResult> RunPlanAsync(
        string repoPath, int depth, RebaseTodo todo,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunRebaseAsync(repoPath, depth, (scope, unsigned, token) => _driver.RunPlanAsync(scope, todo, policy, unsigned, token), signing, ct);

    /// <summary>
    /// Folds the staged changes into an older commit. The tree gate here admits staged changes —
    /// they are the operation's input — while still refusing unstaged ones, which git would
    /// reject at the rebase.
    /// </summary>
    public Task<SurgeryResult> InjectStagedIntoAsync(
        string repoPath, string targetCommit,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunGatedAsync(repoPath, TreeRequirement.NoUnstagedChanges, backup: true, "inject", signing, async (unsigned, token) =>
        {
            var run = await _surgery.InjectStagedIntoAsync(repoPath, targetCommit, policy, unsigned, token);
            return (run.Success, run.FailureReason, run, null);
        }, ct);

    /// <summary>
    /// Moves the branch. Every mode takes the full rails: the target is an arbitrary revision, so
    /// a soft or mixed reset drops commits from the branch exactly as a hard one does — only the
    /// index and worktree are treated differently. A hard reset additionally discards uncommitted
    /// work, which is why only it demands a clean tree.
    /// </summary>
    public Task<SurgeryResult> ResetAsync(
        string repoPath, string target, ResetMode mode, CancellationToken ct = default) =>
        // A reset moves a ref; it creates no commit, so the signing gate does not apply.
        RunGatedAsync(repoPath,
            mode == ResetMode.Hard ? TreeRequirement.Clean : TreeRequirement.Any,
            backup: true, "reset", null, async (_, token) =>
        {
            var edit = await _edits.ResetAsync(repoPath, target, mode, token);
            return (edit.Success, edit.FailureReason, null, edit);
        }, ct);

    /// <summary>
    /// Reverts a commit. A conflicted revert is a failure result, but the repository is left
    /// mid-revert deliberately — the journal and backup stay behind so undo still works.
    /// </summary>
    public Task<SurgeryResult> RevertAsync(
        string repoPath, string commit, bool autoCommit = true,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunGatedAsync(repoPath, TreeRequirement.Clean, backup: true, "revert", signing, async (unsigned, token) =>
        {
            var edit = await _edits.RevertAsync(repoPath, commit, autoCommit, unsigned, token);
            return (edit.Success, edit.FailureReason, null, edit);
        }, ct);

    public Task<SurgeryResult> CherryPickAsync(
        string repoPath, IReadOnlyList<string> commits,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default) =>
        RunGatedAsync(repoPath, TreeRequirement.Clean, backup: true, "cherry-pick", signing, async (unsigned, token) =>
        {
            var edit = await _edits.CherryPickAsync(repoPath, commits, unsigned, token);
            return (edit.Success, edit.FailureReason, null, edit);
        }, ct);

    private Task<SurgeryResult> RunRebaseAsync(
        string repoPath, int depth, Func<RebaseScope, bool, CancellationToken, Task<RebaseRunResult>> operate,
        SigningChoice signing, CancellationToken ct) =>
        RunGatedAsync(repoPath, TreeRequirement.Clean, backup: true, "rebase", signing, async (unsigned, token) =>
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

            var run = await operate(scope, unsigned, token);
            return (run.Success, run.FailureReason, run, null);
        }, ct);

    /// <summary>
    /// Whether <paramref name="repoPath"/> is configured to sign the commits it creates. An
    /// unreadable or unset value reads as off, which is git's own default; a repository that does
    /// sign always has the setting to say so.
    /// </summary>
    private async Task<bool> SignsCommitsAsync(string repoPath, CancellationToken ct)
    {
        var configured = await _git.RunAsync(repoPath, ["config", "--type=bool", "--get", "commit.gpgsign"], ct);
        return configured.Success && configured.StdOut.Trim() == "true";
    }

    private async Task<SurgeryResult> RunGatedAsync(
        string repoPath,
        TreeRequirement requirement,
        bool backup,
        string phase,
        SigningChoice? signing,
        Func<bool, CancellationToken, Task<(bool Success, string? Reason, RebaseRunResult? Rebase, HistoryEditResult? Edit)>> operate,
        CancellationToken ct)
    {
        // 1. Busy gate: a second destructive operation on the same repo is refused, not queued.
        if (!_busy.TryAcquire(repoPath, out var lease))
            return SurgeryResult.Refused(
                $"repository is busy with another operation: {repoPath}", SurgeryRefusal.RepositoryBusy);

        UndoHandle? undo = null;
        var journalled = false;
        try
        {
            // 2. Tree gate: what the operation needs the working tree to look like. Reported
            // with the offending files so the caller can act rather than guess.
            var state = await _git.GetWorkingStateAsync(repoPath, ct);
            if (state is null)
                return SurgeryResult.Refused(
                    $"repository '{repoPath}' could not be read by git", SurgeryRefusal.RepositoryUnreadable);
            if (state.Activity != Models.RepoActivity.None)
                return SurgeryResult.Refused(
                    $"the repository is already in the middle of a {Describe(state.Activity)} — finish or abort it first",
                    SurgeryRefusal.OperationInProgress);
            var (refusalKind, refusal) = CheckTree(state, requirement);
            if (refusal is not null)
                return SurgeryResult.Refused(refusal, refusalKind);

            // 2b. Signing gate, for the operations that create commits. Every commit this
            // operation writes is signed with the configured key, and a key whose passphrase is
            // not cached makes git wait on a pinentry prompt no window shows — the operation then
            // burns its whole timeout and is killed mid-replay. The caller decides, and the
            // decision is required rather than defaulted: signing off silently would strip
            // signatures the user asked for.
            var unsigned = false;
            if (signing is { } choice && await SignsCommitsAsync(repoPath, ct))
            {
                if (choice == SigningChoice.NotChosen)
                    return SurgeryResult.Refused(
                        "this repository signs commits (commit.gpgsign is on), and this operation writes commits — " +
                        "every one of them is re-signed. If the key's passphrase is not cached, git waits on a prompt " +
                        "this app cannot answer and the operation is killed at its timeout. Choose whether to sign as " +
                        "configured or to proceed without signing.",
                        SurgeryRefusal.CommitSigningChoiceRequired);
                unsigned = choice == SigningChoice.ProceedUnsigned;
            }

            // 3. Verified backup: no destructive operation proceeds without one.
            if (backup)
            {
                BackupHandle handle;
                try
                {
                    handle = await _backup.CreateBackupAsync(repoPath, $"Commit surgery ({phase})", ct);
                }
                catch (BackupException ex)
                {
                    return SurgeryResult.Refused(
                        $"backup failed — no history was touched: {ex.Message}", SurgeryRefusal.BackupFailed);
                }
                undo = new UndoHandle(_backup, _busy, handle);

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
                (success, reason, rebase, edit) = await operate(unsigned, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The operation threw part-way: what already reached the repository is unknown, so
                // the journal stays pending and the result claims nothing about the repository.
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
                Edit = edit,
                RepositoryUntouched = !success && ProvesNothingMoved(rebase, edit)
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

    /// <summary>Which requirement the tree fails and how to say so, or <see cref="SurgeryRefusal.None"/> with a null reason.</summary>
    private static (SurgeryRefusal Kind, string? Reason) CheckTree(Models.WorkingState state, TreeRequirement requirement)
    {
        switch (requirement)
        {
            case TreeRequirement.Clean when state.IsDirty:
                return (SurgeryRefusal.UncommittedChanges,
                    $"working tree has {state.Files.Count} uncommitted change(s) — " +
                    $"refusing (stash or commit first): {List(state.Files)}");
            case TreeRequirement.NoUnstagedChanges:
            {
                var unstaged = state.Unstaged.ToList();
                if (unstaged.Count > 0)
                    return (SurgeryRefusal.UnstagedChanges,
                        $"working tree has {unstaged.Count} unstaged change(s) — " +
                        $"stage or stash them first: {List(unstaged)}");
                if (!state.Staged.Any())
                    return (SurgeryRefusal.NothingStaged, "nothing is staged — stage the fix first");
                return (SurgeryRefusal.None, null);
            }
            default:
                return (SurgeryRefusal.None, null);
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
