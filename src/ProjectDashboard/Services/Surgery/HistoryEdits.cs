namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// Reset, revert, and cherry-pick against a real repository.
///
/// Conflict discipline: there is no in-app merge editor, so nothing here resolves a conflict
/// and nothing here aborts one either. A conflicted revert or cherry-pick leaves the repository
/// exactly where git left it — REVERT_HEAD or CHERRY_PICK_HEAD present, conflicted paths in the
/// index — which the existing working-state detection reports as
/// <see cref="Models.RepoActivity.Reverting"/> or <see cref="Models.RepoActivity.CherryPicking"/>,
/// so the state banner and Open in Terminal already cover finishing or abandoning it. The
/// conflicted paths come back in the result so a caller can name them without re-reading status.
///
/// Gating (busy lease, clean tree, backup, journal) belongs to <see cref="SurgeryCoordinator"/>;
/// these methods are the git-level operations only.
/// </summary>
public sealed class HistoryEdits
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    private readonly GitService _git;

    public HistoryEdits(GitService git) => _git = git;

    /// <summary>
    /// Moves the current branch to <paramref name="target"/>. Soft keeps index and worktree,
    /// mixed resets the index, hard discards uncommitted work as well. Every mode drops whatever
    /// commits lay between the target and the old tip, so all three run behind a backup.
    /// </summary>
    public async Task<HistoryEditResult> ResetAsync(
        string repoPath, string target, ResetMode mode, CancellationToken ct = default)
    {
        var resolved = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", target + "^{commit}"], ct, ShortTimeout);
        if (!resolved.Success)
            return HistoryEditResult.Failed($"'{target}' does not resolve to a commit in this repository");

        var flag = mode switch
        {
            ResetMode.Soft => "--soft",
            ResetMode.Mixed => "--mixed",
            ResetMode.Hard => "--hard",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown reset mode")
        };

        var reset = await _git.RunAsync(repoPath, ["reset", flag, resolved.StdOut.Trim()], ct, OperationTimeout);
        if (reset.Success)
            return new HistoryEditResult { Success = true, HeadAfter = await HeadShaAsync(repoPath, ct) };

        // A non-zero `git reset` is not a refusal: --hard writes tracked content as it walks and
        // reports failure on the first path it cannot replace, having already replaced the others.
        return new HistoryEditResult
        {
            Success = false,
            FailureReason = $"git reset {flag} failed: {reset.FirstError}",
            HeadAfter = await HeadShaAsync(repoPath, ct)
        };
    }

    /// <summary>
    /// Reverts one commit. With <paramref name="autoCommit"/> false the revert is staged and
    /// left uncommitted, which keeps git's REVERT_HEAD in place: the result then carries
    /// <see cref="HistoryEditResult.LeftMidOperation"/>, because every later gated operation
    /// refuses a repository that reads as mid-revert until it is committed or aborted.
    /// A conflict is reported, not resolved, and the repository stays mid-revert for the terminal.
    /// </summary>
    public async Task<HistoryEditResult> RevertAsync(
        string repoPath, string commit, bool autoCommit = true,
        bool disableSigning = false, CancellationToken ct = default)
    {
        var resolved = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", commit + "^{commit}"], ct, ShortTimeout);
        if (!resolved.Success)
            return HistoryEditResult.Failed($"'{commit}' does not resolve to a commit in this repository");

        var args = SigningPin(disableSigning);
        args.AddRange(["revert", "--no-edit"]);
        if (!autoCommit) args.Add("--no-commit");
        args.Add(resolved.StdOut.Trim());

        var result = await RunReplayAsync(repoPath, args, "revert", ct);
        if (!result.Success || !await IsMidRevertAsync(repoPath, ct)) return result;

        return new HistoryEditResult
        {
            Success = true,
            LeftMidOperation = true,
            Advisory = "the revert is staged but not committed — the repository is left mid-revert " +
                       "(REVERT_HEAD is present) and further history operations are refused until it is " +
                       "committed or `git revert --abort` is run",
            HeadAfter = result.HeadAfter
        };
    }

    /// <summary>Whether git's revert sequencer state survives — the state that makes the repository read as mid-revert.</summary>
    private async Task<bool> IsMidRevertAsync(string repoPath, CancellationToken ct)
    {
        var path = await _git.RunAsync(repoPath, ["rev-parse", "--git-path", "REVERT_HEAD"], ct, ShortTimeout);
        if (!path.Success) return false;
        var file = path.StdOut.Trim();
        if (file.Length == 0) return false;
        if (!System.IO.Path.IsPathRooted(file)) file = System.IO.Path.Combine(repoPath, file);
        return System.IO.File.Exists(file);
    }

    /// <summary>Applies commits onto the current branch in the order given, with the same conflict discipline as revert.</summary>
    public async Task<HistoryEditResult> CherryPickAsync(
        string repoPath, IReadOnlyList<string> commits,
        bool disableSigning = false, CancellationToken ct = default)
    {
        if (commits.Count == 0)
            return HistoryEditResult.Failed("no commits selected to cherry-pick");

        var args = SigningPin(disableSigning);
        args.Add("cherry-pick");
        foreach (var commit in commits)
        {
            var resolved = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", commit + "^{commit}"], ct, ShortTimeout);
            if (!resolved.Success)
                return HistoryEditResult.Failed($"'{commit}' does not resolve to a commit in this repository");
            args.Add(resolved.StdOut.Trim());
        }

        return await RunReplayAsync(repoPath, args, "cherry-pick", ct);
    }

    /// <summary>
    /// Runs a revert or cherry-pick and classifies the outcome. A non-zero exit with conflicted
    /// index entries is a conflict; a non-zero exit without them is a plain failure (the caller's
    /// arguments, or a refusal such as an unborn HEAD).
    /// </summary>
    private async Task<HistoryEditResult> RunReplayAsync(
        string repoPath, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var run = await _git.RunAsync(repoPath, args, ct, OperationTimeout);
        var head = await HeadShaAsync(repoPath, ct);
        if (run.Success)
            return new HistoryEditResult { Success = true, HeadAfter = head };

        var conflicts = await ConflictedPathsAsync(repoPath, ct);
        if (conflicts.Count == 0)
            return new HistoryEditResult
            {
                Success = false,
                FailureReason = run.TimedOut
                    ? $"git {what} timed out and was killed"
                    : $"git {what} failed: {run.FirstError}",
                HeadAfter = head
            };

        return new HistoryEditResult
        {
            Success = false,
            Conflicted = true,
            ConflictPaths = conflicts,
            FailureReason =
                $"the {what} conflicts in {conflicts.Count} file(s) — the repository is left mid-{what}; " +
                "resolve and continue, or abort, from a terminal",
            HeadAfter = head
        };
    }

    /// <summary>The leading `-c` pair that turns commit signing off for one call, or an empty vector that leaves the repository's own setting alone.</summary>
    private static List<string> SigningPin(bool disableSigning) =>
        disableSigning ? ["-c", "commit.gpgsign=false"] : [];

    private async Task<IReadOnlyList<string>> ConflictedPathsAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, ["diff", "--name-only", "--diff-filter=U"], ct, ShortTimeout);
        if (!result.Success) return [];
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    private async Task<string> HeadShaAsync(string repoPath, CancellationToken ct)
    {
        var head = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", "HEAD"], ct, ShortTimeout);
        return head.Success ? head.StdOut.Trim() : "";
    }
}
