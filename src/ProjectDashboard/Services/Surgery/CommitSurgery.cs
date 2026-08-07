namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// Amends a fix into an older commit without an editor: the staged changes become a
/// `fixup!` commit on the tip, then an autosquash rebase folds it back into its target.
///
/// The autosquash pass deliberately keeps git's generated todo — that arrangement of the
/// `fixup!` commit against its target IS the instruction — so this is the one rebase the
/// driver runs with a no-op sequence editor rather than a prepared todo.
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

        var ancestor = await _git.RunAsync(repoPath, ["merge-base", "--is-ancestor", target, "HEAD"], ct, ShortTimeout);
        if (ancestor.ExitCode != 0)
            return RebaseRunResult.Failed($"commit {Short(target)} is not an ancestor of HEAD — nothing would replay onto it");

        var parents = await _git.RunAsync(repoPath, ["rev-list", "--max-count=1", "--parents", target], ct, ShortTimeout);
        if (!parents.Success)
            return RebaseRunResult.Failed($"could not read the parents of {Short(target)}: {parents.FirstError}");
        var parentIds = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        if (parentIds.Count > 1)
            return RebaseRunResult.Failed($"commit {Short(target)} is a merge — a fix cannot be folded into it");

        // A root commit has no parent to rebase onto, so the replay has to start at --root.
        var baseSha = parentIds.Count == 1 ? parentIds[0] : null;

        var fixup = await _git.RunAsync(repoPath,
            ["commit", "--no-verify", $"--fixup={target}"], ct, CommitTimeout);
        if (!fixup.Success)
            return RebaseRunResult.Failed($"could not record the fixup commit: {fixup.FirstError}");

        var fixupSha = await HeadShaAsync(repoPath, ct);

        var result = await _driver.AutosquashAsync(repoPath, baseSha, policy, ct);
        if (result.Success) return result;

        // `rebase --abort` restores the pre-rebase tip, which still carries the fixup commit.
        // A soft reset off it puts the same changes back in the index, so a refused injection
        // leaves refs, index, and worktree exactly as the caller had them.
        var note = " — the fixup commit is still on the tip, unfolded";
        if (result.Aborted && fixupSha.Length > 0 && await HeadShaAsync(repoPath, ct) == fixupSha)
        {
            var unwind = await _git.RunAsync(repoPath, ["reset", "--soft", fixupSha + "^"], ct, CommitTimeout);
            note = unwind.Success
                ? " — the fixup was unwound and the fix is staged again, as before"
                : $" — and the fixup commit could not be unwound ({unwind.FirstError}); restore from the backup";
        }

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
            HeadAfter = await HeadShaAsync(repoPath, ct),
            Todo = result.Todo
        };
    }

    private async Task<string> HeadShaAsync(string repoPath, CancellationToken ct)
    {
        var head = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", "HEAD"], ct, ShortTimeout);
        return head.Success ? head.StdOut.Trim() : "";
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
