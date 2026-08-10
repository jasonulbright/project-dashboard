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
/// What verifying one repository's bundles found. <see cref="Checked"/> counts the bundles the
/// verifier was actually run against, so a pass cut short by cancellation reports fewer than are on
/// disk rather than presenting a partial result as a whole one.
///
/// <see cref="UnknownStamps"/> is kept apart from <see cref="FailedStamps"/> and never folded into
/// it: a verify that was killed on its timeout did not find the bundle bad, and a reader told a
/// backup failed acts on a defect it may not have.
/// </summary>
public sealed record SafetyBackupVerification(
    int OnDisk,
    int Checked,
    IReadOnlyList<string> FailedStamps,
    IReadOnlyList<string> UnknownStamps,
    string? Error)
{
    public int Failed => FailedStamps.Count;

    public int Unknown => UnknownStamps.Count;
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
    /// Expensive tier: every bundle verified through <see cref="BackupService.VerifyBackupAsync"/>,
    /// newest first — the same verifier the Backups browser and the restore's own precondition use,
    /// so the answer here is the answer a restore would act on and the two surfaces cannot word one
    /// bundle two ways.
    ///
    /// What verification establishes is bounded and the caller states the bound: git reads the
    /// bundle header and checks the prerequisite commits exist here, and does not read the packed
    /// objects. A bundle whose pack is truncated or altered still verifies.
    ///
    /// The outcome is appended to the repository's operation ledger, which is what makes
    /// "last verified" a durable fact rather than one this session holds and forgets.
    /// </summary>
    public async Task<SafetyBackupVerification> VerifyBackupsAsync(string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await VerifyCoreAsync(repoPath, ct);
        Record(repoPath, "Verify backup bundles", VerifyOutcome(result), DescribeVerification(result), started);
        return result;
    }

    private async Task<SafetyBackupVerification> VerifyCoreAsync(string repoPath, CancellationToken ct)
    {
        if (_backups is null)
            return new SafetyBackupVerification(0, 0, [], [],
                "No backup store is configured, so nothing was verified.");

        List<BackupHandle> handles;
        try
        {
            handles = await _backups.ListBackupsAsync(repoPath, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list backups to verify for {repoPath}", ex);
            return new SafetyBackupVerification(0, 0, [], [], ex.Message);
        }

        var failed = new List<string>();
        var unknown = new List<string>();
        var checkedCount = 0;
        foreach (var handle in handles)
        {
            if (ct.IsCancellationRequested)
                return new SafetyBackupVerification(handles.Count, checkedCount, failed, unknown,
                    "The check was cancelled, so the remaining bundles were not verified.");

            BundleVerifyResult verify;
            try
            {
                verify = await _backups.VerifyBackupAsync(handle, ct);
            }
            catch (Exception ex)
            {
                // A verifier that threw answered nothing about the bundle, which is not the same
                // fact as the bundle being bad.
                Log.Warn($"could not verify backup {handle.UtcStamp} for {repoPath}", ex);
                verify = new BundleVerifyResult(BundleVerifyState.Unknown, ex.Message);
            }

            checkedCount++;
            switch (verify.State)
            {
                case BundleVerifyState.Failed: failed.Add(handle.UtcStamp); break;
                case BundleVerifyState.Unknown: unknown.Add(handle.UtcStamp); break;
            }
        }

        return new SafetyBackupVerification(handles.Count, checkedCount, failed, unknown, null);
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
            repoPath, ["rev-list", "--count", "--reflog", "--not", "--all"], ct, BackupService.ReflogWalkTimeout);
        if (!walk.Success)
            return new SafetyReflogOnlyScan(0, walk.FirstError);

        var printed = walk.StdOut.Trim();
        return int.TryParse(printed, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? new SafetyReflogOnlyScan(count, null)
            : new SafetyReflogOnlyScan(0, $"git printed a count this build could not read: '{printed}'");
    }

    /// <summary>
    /// A bundle found bad is a failure; a bundle the verifier never answered for is unknown. The
    /// ledger keeps them apart for the same reason the surface does.
    /// </summary>
    private static OperationOutcome VerifyOutcome(SafetyBackupVerification result) =>
        result.Failed > 0 ? OperationOutcome.Failed
        : result.Error is not null || result.Unknown > 0 ? OperationOutcome.Unknown
        : OperationOutcome.Succeeded;

    private static string DescribeVerification(SafetyBackupVerification result)
    {
        if (result.Error is not null) return result.Error;
        if (result.OnDisk == 0) return "No backup bundle is on disk for this repository.";

        var parts = new List<string>();
        if (result.Failed > 0)
            parts.Add($"{result.Failed} failed verification ({string.Join(", ", result.FailedStamps)})");
        if (result.Unknown > 0)
            parts.Add($"{result.Unknown} could not be verified ({string.Join(", ", result.UnknownStamps)})");

        return (parts.Count == 0
            ? $"{result.Checked} of {result.OnDisk} bundle(s) verified."
            : $"Of {result.Checked} bundle(s) checked: {string.Join("; ", parts)}.")
            + " " + SafetyCopy.BackupCheckLimit;
    }

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
