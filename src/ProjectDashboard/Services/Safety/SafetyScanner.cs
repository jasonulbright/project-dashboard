using ProjectDashboard.Models;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// One repository's cheap-tier answer: every local branch, and how many backups are on disk.
/// <see cref="Error"/> is set when either read failed, and the corresponding value is then not a
/// measurement — a caller reports the repository as unread rather than as sound.
/// </summary>
public sealed record SafetyCheapScan(
    IReadOnlyList<BranchInfo> Branches,
    int BackupCount,
    string? Error);

/// <summary>
/// What verifying one repository's bundles found. <see cref="Checked"/> counts the bundles git was
/// actually run against, so a walk cut short by cancellation reports fewer than are on disk rather
/// than presenting a partial pass as a whole one.
/// </summary>
public sealed record SafetyBackupVerification(
    int OnDisk,
    int Checked,
    IReadOnlyList<string> FailedStamps,
    string? Error)
{
    public int Failed => FailedStamps.Count;
}

/// <summary>
/// Commits that live only in a reflog. <see cref="Count"/> is meaningless when <see cref="Error"/>
/// is set; a caller reports the repository as unmeasured rather than as holding none.
/// </summary>
public sealed record SafetyReflogOnlyScan(int Count, string? Error);

/// <summary>
/// The git the safety rollup runs. Every read here is leaseless and read-only: the rollup describes
/// repositories, it never claims one. Skipping a repository another operation holds is the caller's
/// decision, made against <see cref="RepoBusyRegistry"/> before a scan is handed a path.
///
/// The split by cost is the point of the type. The free tier never reaches this class at all, the
/// cheap tier is one ref read plus a directory listing per repository, and the expensive tier
/// verifies bundles or walks the object store and is only ever entered on an explicit ask.
/// </summary>
public sealed class SafetyScanner
{
    /// <summary>Per bundle, matching the budget the restore path already allows one verification.</summary>
    internal static readonly TimeSpan BundleVerifyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The reflog-only walk is bounded by the reflogs, which no setting bounds; the budget does.</summary>
    internal static readonly TimeSpan ReflogWalkTimeout = TimeSpan.FromMinutes(5);

    private readonly GitService _git;
    private readonly BackupService? _backups;
    private readonly OperationHistory _history;

    public SafetyScanner(GitService git, BackupService? backups = null, OperationHistory? history = null)
    {
        _git = git;
        _backups = backups;
        _history = history ?? new OperationHistory();
    }

    /// <summary>Cheap tier: one ref read and one directory listing, both read-only.</summary>
    public async Task<SafetyCheapScan> ScanAsync(string repoPath, CancellationToken ct = default)
    {
        var branches = await _git.GetBranchesResultAsync(repoPath, ct);

        var backupCount = 0;
        string? error = branches.HasError ? branches.ErrorText : null;
        if (_backups is not null)
        {
            try
            {
                backupCount = (await _backups.ListBackupsAsync(repoPath, ct)).Count;
            }
            catch (Exception ex)
            {
                Log.Warn($"could not list backups for the safety rollup of {repoPath}", ex);
                error = error is null ? ex.Message : error + " " + ex.Message;
            }
        }

        return new SafetyCheapScan(branches.Branches, backupCount, error);
    }

    /// <summary>
    /// Expensive tier: <c>git bundle verify</c> per bundle, newest first — the same command a
    /// restore runs before it touches anything, so a bundle that fails here is one a restore would
    /// refuse. Otherwise that is only ever learned at the moment a reader can least afford it.
    ///
    /// What it establishes is bounded and the caller states the bound: git reads the bundle header
    /// and checks the prerequisite commits exist here, and does not read the packed objects. A
    /// bundle whose pack is truncated or altered still passes, so no result from here may be
    /// reported as the objects being intact.
    ///
    /// The outcome is appended to the repository's operation ledger, which is what makes
    /// "last checked" a durable fact rather than one this session holds and forgets.
    /// </summary>
    public async Task<SafetyBackupVerification> VerifyBackupsAsync(string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await VerifyCoreAsync(repoPath, ct);
        Record(repoPath, "Check backup bundles", VerifyOutcome(result), DescribeVerification(result), started);
        return result;
    }

    private async Task<SafetyBackupVerification> VerifyCoreAsync(string repoPath, CancellationToken ct)
    {
        if (_backups is null)
            return new SafetyBackupVerification(0, 0, [], "No backup store is configured, so nothing was verified.");

        List<BackupHandle> handles;
        try
        {
            handles = await _backups.ListBackupsAsync(repoPath, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list backups to verify for {repoPath}", ex);
            return new SafetyBackupVerification(0, 0, [], ex.Message);
        }

        var failed = new List<string>();
        var checkedCount = 0;
        foreach (var handle in handles)
        {
            if (ct.IsCancellationRequested)
                return new SafetyBackupVerification(handles.Count, checkedCount, failed,
                    "The check was cancelled, so the remaining bundles were not checked.");

            var verify = await _git.RunAsync(
                repoPath, ["bundle", "verify", handle.BundlePath], ct, BundleVerifyTimeout);
            checkedCount++;
            if (!verify.Success) failed.Add(handle.UtcStamp);
        }

        return new SafetyBackupVerification(handles.Count, checkedCount, failed, null);
    }

    /// <summary>
    /// Expensive tier: commits reachable from a reflog and from no ref. Those are exactly the
    /// commits a backup bundle never captured — it bundles refs — so they are what a restore of the
    /// newest backup would not put back and what a deep clean would make unrecoverable.
    /// </summary>
    public async Task<SafetyReflogOnlyScan> CountReflogOnlyAsync(string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await CountReflogOnlyCoreAsync(repoPath, ct);
        Record(repoPath, "Check for reflog-only commits",
            result.Error is null ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            result.Error ?? DescribeReflogOnly(result.Count), started);
        return result;
    }

    private async Task<SafetyReflogOnlyScan> CountReflogOnlyCoreAsync(string repoPath, CancellationToken ct)
    {
        var walk = await _git.RunAsync(
            repoPath, ["rev-list", "--count", "--reflog", "--not", "--all"], ct, ReflogWalkTimeout);
        if (!walk.Success)
            return new SafetyReflogOnlyScan(0, walk.FirstError);

        var printed = walk.StdOut.Trim();
        return int.TryParse(printed, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? new SafetyReflogOnlyScan(count, null)
            : new SafetyReflogOnlyScan(0, $"git printed a count this build could not read: '{printed}'");
    }

    /// <summary>
    /// A token that changes whenever the object store does, used to decide whether a cached
    /// expensive answer still describes the repository. Null when the store could not be measured,
    /// which invalidates rather than preserves a cached answer: an unmeasured store is not an
    /// unchanged one.
    /// </summary>
    public async Task<string?> ObjectStoreGenerationAsync(string repoPath, CancellationToken ct = default)
    {
        var counts = await _git.CountObjectsAsync(repoPath, ct);
        return counts is null ? null : $"{counts.LooseObjects}/{counts.PackedObjects}";
    }

    private static OperationOutcome VerifyOutcome(SafetyBackupVerification result) =>
        result.Error is not null ? OperationOutcome.Unknown
        : result.Failed > 0 ? OperationOutcome.Failed
        : OperationOutcome.Succeeded;

    private static string DescribeVerification(SafetyBackupVerification result) =>
        result.Error is not null ? result.Error
        : result.OnDisk == 0 ? "No backup bundle is on disk for this repository."
        : result.Failed > 0
            ? $"{result.Failed} of {result.Checked} bundle(s) would be refused by a restore: "
              + $"{string.Join(", ", result.FailedStamps)}. {SafetyCopy.BackupCheckLimit}"
            : $"{result.Checked} bundle(s) passed. {SafetyCopy.BackupCheckLimit}";

    private static string DescribeReflogOnly(int count) =>
        count == 0
            ? "No commit is reachable from a reflog alone."
            : $"{count} commit(s) are reachable from a reflog alone, so no backup bundle holds them.";

    /// <summary>
    /// Best effort, like every other writer against the ledger: a record that could not be written
    /// must not turn a read-only check into a failure.
    /// </summary>
    private void Record(string repoPath, string label, OperationOutcome outcome, string detail, DateTimeOffset started)
    {
        try
        {
            _history.Append(OperationRecord.For(
                repoPath, OperationCategory.Maintenance, label, outcome, detail, started));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not record the safety check '{label}' for {repoPath}", ex);
        }
    }
}
