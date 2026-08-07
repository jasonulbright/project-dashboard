namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// Amends a fix into an older commit without an editor: the staged changes become a
/// `fixup!` commit on the tip, then a prepared todo folds that one commit back into its target.
///
/// The todo is explicit — every commit from the target to the tip is picked in order and the
/// new fixup line is placed directly after the target. `--autosquash` is not used: it would
/// also rearrange and fold `fixup!`/`squash!` commits the user made themselves, and a `squash!`
/// in the range would rewrite the target's message. Only the commit this call created is folded;
/// any other `fixup!` in the range replays as an ordinary commit.
///
/// The target's message and author are preserved: a `fixup!` commit contributes only its
/// tree. Every commit after the target is replayed, so their hashes change while their
/// content does not.
/// </summary>
public sealed class CommitSurgery
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(2);

    private readonly GitService _git;
    private readonly RebaseDriver _driver;

    public CommitSurgery(GitService git, RebaseDriver driver)
    {
        _git = git;
        _driver = driver;
    }

    /// <summary>
    /// Folds whatever is currently staged into <paramref name="targetCommit"/>. Requires
    /// staged changes and no unstaged ones — git refuses to rebase over a dirty worktree,
    /// and the fixup commit would capture only part of the intended change.
    /// </summary>
    public async Task<RebaseRunResult> InjectStagedIntoAsync(
        string repoPath, string targetCommit,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
    {
        var resolved = await _git.RunAsync(repoPath,
            ["rev-parse", "--verify", "-q", targetCommit + "^{commit}"], ct, ShortTimeout);
        if (!resolved.Success)
            return RebaseRunResult.Failed($"'{targetCommit}' does not resolve to a commit in this repository");
        var target = resolved.StdOut.Trim();

        // `diff --cached --quiet` exits 1 when something is staged; that non-zero exit is the
        // signal, so the empty-index case cannot be mistaken for a git failure.
        var staged = await _git.RunAsync(repoPath, ["diff", "--cached", "--quiet"], ct, ShortTimeout);
        if (staged.TimedOut)
            return RebaseRunResult.Failed("could not read the index: git timed out");
        if (staged.ExitCode == 0)
            return RebaseRunResult.Failed("nothing is staged — stage the fix before injecting it into an older commit");

        // Enforced here, not only at the coordinator's tree gate: a direct call over a tree with
        // unstaged edits records a fixup holding half the intended change, and git then refuses
        // to rebase over the rest.
        var unstaged = await _git.RunAsync(repoPath, ["diff", "--quiet"], ct, ShortTimeout);
        if (unstaged.TimedOut)
            return RebaseRunResult.Failed("could not read the working tree: git timed out");
        if (unstaged.ExitCode != 0)
            return RebaseRunResult.Failed(
                "the working tree has unstaged changes — stage or stash them before injecting a fix into an older commit");

        var ancestor = await _git.RunAsync(repoPath, ["merge-base", "--is-ancestor", target, "HEAD"], ct, ShortTimeout);
        if (ancestor.ExitCode != 0)
            return RebaseRunResult.Failed($"commit {Short(target)} is not an ancestor of HEAD — nothing would replay onto it");

        var parents = await _git.RunAsync(repoPath, ["rev-list", "--max-count=1", "--parents", target], ct, ShortTimeout);
        if (!parents.Success)
            return RebaseRunResult.Failed($"could not read the parents of {Short(target)}: {parents.FirstError}");
        var parentIds = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        if (parentIds.Count > 1)
            return RebaseRunResult.Failed($"commit {Short(target)} is a merge — a fix cannot be folded into it");

        var ahead = await _git.RunAsync(repoPath, ["rev-list", "--count", target + "..HEAD"], ct, ShortTimeout);
        if (!ahead.Success || !int.TryParse(ahead.StdOut.Trim(), out var commitsAfterTarget))
            return RebaseRunResult.Failed(
                $"could not measure the range {Short(target)}..HEAD: {ahead.FirstError}");

        var fixup = await _git.RunAsync(repoPath,
            ["commit", "--no-verify", $"--fixup={target}"], ct, CommitTimeout);
        if (!fixup.Success)
            return RebaseRunResult.Failed($"could not record the fixup commit: {fixup.FirstError}");

        var fixupSha = await HeadShaAsync(repoPath, ct);
        if (fixupSha.Length == 0)
            return RebaseRunResult.Failed("the fixup commit was recorded but HEAD could not be read");

        // The scope is the target, everything already replaying after it, and the fixup itself:
        // the same span the fold rewrites. A root target yields a null base and a `--root` replay.
        RebaseScope scope;
        try
        {
            scope = await _driver.LoadScopeAsync(repoPath, commitsAfterTarget + 2, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (unwoundScope, scopeNote) = await TryUnwindFixupAsync(repoPath, fixupSha, ct);
            return new RebaseRunResult
            {
                Success = false,
                FailureReason = $"the range is not editable: {ex.Message}" + scopeNote,
                RepositoryUntouched = unwoundScope,
                HeadAfter = await HeadShaAsync(repoPath, ct)
            };
        }

        var result = await _driver.FoldFixupAsync(scope, target, fixupSha, policy, ct);
        if (result.Success) return result;

        // `rebase --abort` restores the pre-rebase tip, which still carries the fixup commit.
        // A soft reset off it puts the same changes back in the index, so a refused injection
        // leaves refs, index, and tracked content exactly as the caller had them.
        var unwound = false;
        var note = " — the fixup commit is still on the tip, unfolded";
        if (result.Aborted)
            (unwound, note) = await TryUnwindFixupAsync(repoPath, fixupSha, ct);

        return new RebaseRunResult
        {
            Success = false,
            FailureReason = result.FailureReason + note,
            ConflictCommit = result.ConflictCommit,
            ConflictSubject = result.ConflictSubject,
            StoppedEmpty = result.StoppedEmpty,
            LeftStopped = result.LeftStopped,
            TimedOut = result.TimedOut,
            Aborted = result.Aborted,
            UntrackedAdded = result.UntrackedAdded,
            RepositoryUntouched = result.Aborted && unwound,
            HeadAfter = await HeadShaAsync(repoPath, ct),
            Todo = result.Todo
        };
    }

    /// <summary>
    /// Drops the recorded fixup commit and leaves its content staged again. Only meaningful while
    /// the fixup is still the tip: anything else means the history moved and the backup is the
    /// way back.
    /// </summary>
    private async Task<(bool Unwound, string Note)> TryUnwindFixupAsync(
        string repoPath, string fixupSha, CancellationToken ct)
    {
        if (await HeadShaAsync(repoPath, ct) != fixupSha)
            return (false, " — the fixup commit is still on the tip, unfolded");

        var unwind = await _git.RunAsync(repoPath, ["reset", "--soft", fixupSha + "^"], ct, CommitTimeout);
        return unwind.Success
            ? (true, " — the fixup was unwound and the fix is staged again, as before")
            : (false, $" — and the fixup commit could not be unwound ({unwind.FirstError}); restore from the backup");
    }

    private async Task<string> HeadShaAsync(string repoPath, CancellationToken ct)
    {
        var head = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", "HEAD"], ct, ShortTimeout);
        return head.Success ? head.StdOut.Trim() : "";
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
