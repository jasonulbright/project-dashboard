using ProjectDashboard.Models;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// What a deep clean did, or why it never started. <see cref="Before"/> and <see cref="After"/>
/// are null when git could not measure the object store, so a caller reports the reclaim as
/// unmeasured rather than as zero.
/// </summary>
public sealed record DeepCleanResult(
    bool Success,
    string? RefusalReason,
    RepoObjectCounts? Before,
    RepoObjectCounts? After)
{
    public static DeepCleanResult Refused(string reason) => new(false, reason, null, null);

    /// <summary>Kibibytes the object store shrank by. Negative when repacking cost more than pruning saved.</summary>
    public long ReclaimedKiB => Before is null || After is null ? 0 : Before.TotalKiB - After.TotalKiB;

    public bool Measured => Before is not null && After is not null;
}

/// <summary>
/// Makes a replaced history unreachable and reclaims what it occupied.
///
/// A completed rewrite leaves every pre-rewrite commit in the repository: the swap moved the
/// refs, and each move is recorded in that ref's reflog, which keeps the old tip — and everything
/// under it — reachable. Until the reflogs are expired and the store is pruned, a purge has
/// removed content from the history the tools show and from nothing else.
///
/// This is a reclaim, not a repack for size: `gc --prune=now` is what drops the newly
/// unreferenced objects, and `--aggressive` only recomputes deltas at a cost unrelated to that.
/// It is therefore not used.
///
/// The old history stays recoverable from the backup bundle taken before the rewrite, which holds
/// every ref as it stood then. What this makes unrecoverable is everything that lived ONLY in a
/// reflog — an amended commit, a commit a reset moved away from, a rewrite's pre-swap tips made
/// after the last backup — because `git bundle --all` never captured any of it.
/// </summary>
public sealed class DeepCleanService
{
    private readonly GitService _git;
    private readonly RepoBusyRegistry _busy;
    private readonly RewriteJournal _journal;

    public DeepCleanService(GitService git, RepoBusyRegistry busy, RewriteJournal journal)
    {
        _git = git;
        _busy = busy;
        _journal = journal;
    }

    /// <summary>
    /// The reason a repository is not eligible, in the order the gates run, or null when it is.
    /// Read on its own so a surface can state the refusal before the reader types anything.
    /// </summary>
    public async Task<string?> DescribeBlockerAsync(string repoPath, CancellationToken ct = default)
    {
        if (repoPath.Length == 0 || !GitService.IsGitRepo(repoPath))
            return $"'{repoPath}' is not a git repository.";

        // The journal is read from disk rather than from the startup snapshot: an operation this
        // session interrupted is pending now and was not pending when the app launched.
        if (await _journal.ReadPendingAsync(repoPath, ct) is not null)
            return InterruptedOperationRefusal;

        var state = await _git.GetWorkingStateAsync(repoPath, ct);
        if (state is null)
            return $"'{repoPath}' could not be read by git.";
        if (state.Activity != RepoActivity.None)
            return $"A {state.Activity.ToString().ToLowerInvariant()} is in progress. Finish or abort it in a " +
                   "terminal first — pruning under it would remove objects it still needs.";

        var stashes = await _git.GetStashesAsync(repoPath, ct);
        if (stashes.Count > 0)
            return StashRefusal(stashes.Count);

        return null;
    }

    /// <summary>Shown wherever an interrupted operation blocks the clean, so both surfaces state the same reason.</summary>
    public const string InterruptedOperationRefusal =
        "An interrupted history operation is recorded for this repository. Expiring the reflogs would remove the " +
        "only in-repository trace of where that operation left it. Restore or discard that record under Backups first.";

    /// <summary>
    /// Why stashes block the clean. Expiring reflogs is what removes the replaced history, and the
    /// stash stack IS a reflog, so there is no version of this that keeps both.
    /// </summary>
    public static string StashRefusal(int count) =>
        $"This repository has {count} stash entr{(count == 1 ? "y" : "ies")}. The stash stack is a reflog, so " +
        "expiring the reflogs erases every one of them, and a backup bundle holds only the top entry — the rest " +
        "exist nowhere else. Apply or drop them first.";

    /// <summary>
    /// Expires every reflog and prunes, under the repository lease. Measures the object store on
    /// both sides so the reclaim reported is one that was observed, never one that was computed
    /// from what the operation was expected to remove.
    /// </summary>
    public async Task<DeepCleanResult> RunAsync(string repoPath, CancellationToken ct = default)
    {
        if (await DescribeBlockerAsync(repoPath, ct) is { } blocker)
            return DeepCleanResult.Refused(blocker);

        if (!_busy.TryAcquire(repoPath, out var lease))
            return DeepCleanResult.Refused($"Repository is busy with another operation: {repoPath}");

        using (lease)
        {
            // Re-read under the lease: the gates above ran unleased, so an operation that started
            // between them and here would have written a journal entry nothing has checked.
            if (await _journal.ReadPendingAsync(repoPath, ct) is not null)
                return DeepCleanResult.Refused(InterruptedOperationRefusal);

            var before = await _git.CountObjectsAsync(repoPath, ct);

            var expire = await _git.ExpireReflogsAsync(repoPath, ct);
            if (!expire.Success)
                return DeepCleanResult.Refused($"Expiring the reflogs failed, so nothing was pruned: {expire.FirstError}");

            var gc = await _git.GarbageCollectAsync(repoPath, ct);
            if (!gc.Success)
                // The reflogs are gone and the objects behind them are unreachable; only the
                // reclaim did not happen. Claiming nothing changed would be false.
                return new DeepCleanResult(false,
                    "The reflogs were expired, so the replaced history is already unreachable, but the prune failed " +
                    $"and the objects are still on disk: {gc.FirstError}",
                    before, await _git.CountObjectsAsync(repoPath, ct));

            return new DeepCleanResult(true, null, before, await _git.CountObjectsAsync(repoPath, ct));
        }
    }
}
