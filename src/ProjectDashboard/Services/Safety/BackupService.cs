using System.IO;
using System.Text;
using System.Text.Json;

namespace ProjectDashboard.Services.Safety;

/// <summary>Raised when a backup cannot be created or a restore is refused; loud by design.</summary>
public sealed class BackupException : Exception
{
    public BackupException(string message) : base(message) { }
}

/// <summary>
/// Captures and restores a repository's full object graph and ref layout before any
/// history-altering operation. A backup is a `git bundle --all` plus a sidecar
/// refs snapshot; a restore verifies the bundle before touching the repo, and its ref
/// reconciliation either applies in full or changes nothing — never a partial ref state.
/// The HEAD reposition and working-tree reset run after that transaction commits, so a
/// failure there leaves the refs restored and reports
/// <see cref="RestoreResult.RefsRestored"/> true. All backups live under AppPaths (never
/// inside a repo), keyed by <see cref="RepoKey"/>, retained newest-N per repo.
///
/// A capture is one of two tiers, recorded in the sidecar. A standard capture holds the refs and
/// the top refs/stash entry. A deep capture also pins the objects no ref reaches — commits a
/// reflog alone holds, and every stash entry below the newest — so a later `git gc` cannot make
/// them unrecoverable. A restore reconciles refs from the sidecar either way: the extra objects
/// come back as objects, and neither tier replays a reflog or rebuilds a stash stack.
/// </summary>
public sealed class BackupService
{
    private static readonly TimeSpan BundleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The reflog walk is bounded by the reflogs, which no setting bounds; the budget is.</summary>
    private static readonly TimeSpan ReflogWalkTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The expected-old value that requires a ref to be absent. `git update-ref --stdin` reads a
    /// quoted empty string as "must not exist"; a zero object id means the same but only at the
    /// repository's own hash length, so it fails outright in a SHA-256 repository.
    /// </summary>
    private const string MustNotExist = "\"\"";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly GitService _git;
    private readonly SettingsService _settings;
    private readonly OperationHistory _history;

    public BackupService(GitService git, SettingsService settings, OperationHistory? history = null)
    {
        _git = git;
        _settings = settings;
        _history = history ?? new OperationHistory();
    }

    /// <summary>
    /// Bundles every ref, verifies the bundle reads back, and records the ref layout, then
    /// prunes older backups down to BackupRetentionCount. Throws <see cref="BackupException"/>
    /// on any failure — a caller must never proceed with a destructive op believing a backup
    /// exists when it does not. <paramref name="operation"/> is recorded in the sidecar so a reader browsing
    /// backups months later can tell which one preceded which change.
    ///
    /// <paramref name="deep"/> null reads AppSettings.DeepBackupCapture, so a caller that has no
    /// opinion follows the one the user set rather than a default this call chose. The tier is
    /// recorded in the sidecar either way: which objects a backup holds is not recoverable from
    /// the bundle later, and a restore that could not name it would have to guess.
    /// </summary>
    public async Task<BackupHandle> CreateBackupAsync(
        string repoPath, string operation = "", bool? deep = null, CancellationToken ct = default)
    {
        if (!GitService.IsGitRepo(repoPath))
            throw new BackupException($"'{repoPath}' is not a git repository — refusing to back up.");

        var repoKey = RepoKey.For(repoPath);
        var dir = SafetyPaths.BackupDirFor(repoKey);
        Directory.CreateDirectory(dir);

        var stamp = UniqueStamp(dir);
        var bundlePath = Path.Combine(dir, stamp + ".bundle");
        var refsPath = Path.Combine(dir, stamp + ".refs.json");

        // Snapshot refs BEFORE the bundle: the two are captured against the same repo
        // state, and a bundle with no matching snapshot is useless for a targeted restore.
        var snapshot = await CaptureRefsAsync(repoPath, stamp, operation, ct);

        var deepCapture = deep ?? _settings.Load().DeepBackupCapture;
        var deepOids = deepCapture ? await CaptureDeepOidsAsync(repoPath, ct) : [];
        snapshot.DeepCapture = deepCapture;
        snapshot.DeepObjectCount = deepOids.Count;

        // The snapshot's own object ids are fed in as explicit revisions, so a ref moved or
        // deleted between the capture above and this call still has its recorded object in the
        // bundle — the restore reads its refs from the sidecar and would otherwise name an object
        // the bundle never received. `--all` rides along because git refuses a bundle that names
        // no ref, and it keeps the capture a superset: every ref plus the top refs/stash entry.
        // A standard capture stops there, so a commit only a reflog holds and every stash entry
        // below the newest are unreachable in the bundle and are not in it. A deep capture pins
        // those object ids through the same list: the objects land in the bundle with their whole
        // ancestry, under no ref of their own, and no ref is created or moved to put them there.
        var pinned = snapshot.Refs.Select(r => r.ObjectId)
            .Append(snapshot.HeadObjectId)
            .Concat(deepOids)
            .Where(oid => oid.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var bundle = await _git.RunWithInputAsync(repoPath,
            ["bundle", "create", bundlePath, "--all", "--stdin"],
            string.Concat(pinned.Select(oid => oid + "\n")), ct, BundleTimeout);
        if (!bundle.Success || !File.Exists(bundlePath))
            throw new BackupException($"git bundle create failed for '{repoPath}': {bundle.FirstError}");

        // A zero exit and a file on disk say the write was attempted, not that the result can be
        // read back. The restore verifies before it touches anything, so a bundle that fails
        // verification is a backup that does not exist — and a caller told it exists proceeds
        // with a destructive operation on that belief.
        var verify = await VerifyBundleAsync(repoPath, bundlePath, ct);
        if (!verify.Verified)
        {
            TryDelete(bundlePath);
            throw new BackupException($"Backup bundle for '{repoPath}' {DescribeUnverified(verify)}");
        }

        try
        {
            DurableJsonFile.Write(refsPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex)
        {
            // A bundle whose sidecar never landed cannot be restored to a known ref state;
            // remove it so ListBackups never reports a half-written backup as usable.
            TryDelete(bundlePath);
            throw new BackupException($"Failed to write refs snapshot for '{repoPath}': {ex.Message}");
        }

        var handle = new BackupHandle
        {
            RepoPath = repoPath,
            RepoKey = repoKey,
            UtcStamp = stamp,
            BundlePath = bundlePath,
            RefsSnapshotPath = refsPath
        };

        PruneOldBackups(repoPath, RetentionCount());
        return handle;
    }

    /// <summary>Backups for a repo, newest first. Only pairs with both bundle and readable sidecar are returned.</summary>
    public Task<List<BackupHandle>> ListBackupsAsync(string repoPath, CancellationToken ct = default)
    {
        var repoKey = RepoKey.For(repoPath);
        var dir = SafetyPaths.BackupDirFor(repoKey);
        var handles = new List<BackupHandle>();
        if (!Directory.Exists(dir)) return Task.FromResult(handles);

        ReapOrphanedSnapshots(dir);

        foreach (var bundle in Directory.GetFiles(dir, "*.bundle"))
        {
            var stamp = Path.GetFileNameWithoutExtension(bundle);
            var refsPath = Path.Combine(dir, stamp + ".refs.json");
            if (!File.Exists(refsPath)) continue;
            handles.Add(new BackupHandle
            {
                RepoPath = repoPath,
                RepoKey = repoKey,
                UtcStamp = stamp,
                BundlePath = bundle,
                RefsSnapshotPath = refsPath
            });
        }

        // Stamp is a fixed-width sortable UTC string, so ordinal-descending is newest-first.
        handles.Sort((a, b) => string.CompareOrdinal(b.UtcStamp, a.UtcStamp));
        return Task.FromResult(handles);
    }

    /// <summary>
    /// What one backup's sidecar records. Null when the sidecar is missing or unreadable —
    /// which is also the state in which <see cref="RestoreAsync"/> refuses, so a listing that
    /// cannot describe a backup is telling the truth about it being unrestorable.
    /// </summary>
    public BackupDetails? ReadDetails(BackupHandle handle)
    {
        var snapshot = ReadSnapshot(handle.RefsSnapshotPath);
        if (snapshot is null) return null;

        long bytes = 0;
        try
        {
            var info = new FileInfo(handle.BundlePath);
            if (info.Exists) bytes = info.Length;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not size backup bundle {handle.BundlePath}", ex);
        }

        return new BackupDetails(snapshot.Operation, snapshot.Refs.Count, snapshot.HeadRef,
            snapshot.HeadObjectId, bytes, snapshot.DeepCapture, snapshot.DeepObjectCount);
    }

    /// <summary>
    /// Whether a backup's bundle still reads back, without restoring anything. The same check
    /// <see cref="RestoreAsync"/> makes as its precondition, so an answer here is the answer that
    /// restore would act on. It runs against the repository the backup was taken from, because a
    /// bundle whose prerequisite objects that repository no longer holds does not verify there
    /// however intact the file itself is.
    /// </summary>
    public async Task<BundleVerifyResult> VerifyBackupAsync(BackupHandle handle, CancellationToken ct = default)
    {
        if (!File.Exists(handle.BundlePath))
            return new BundleVerifyResult(BundleVerifyState.Failed, $"Bundle missing: {handle.BundlePath}");
        return await VerifyBundleAsync(handle.RepoPath, handle.BundlePath, ct);
    }

    /// <summary>
    /// Why a bundle is not verified, for a caller that refuses on either state. A check that never
    /// answered did not find the bundle bad, and the two are worded apart everywhere the reader
    /// meets them — a refusal calling an unanswered check a failed one names a defect the bundle
    /// may not have.
    /// </summary>
    private static string DescribeUnverified(BundleVerifyResult verify) =>
        verify.State == BundleVerifyState.Unknown
            ? $"could not be verified: {verify.Detail}"
            : $"failed verification: {verify.Detail}";

    /// <summary>
    /// A timeout is <see cref="BundleVerifyState.Unknown"/> rather than a failure: the kill says
    /// the question went unanswered, and a bundle called corrupt on that basis would send a reader
    /// to delete a backup that is intact.
    /// </summary>
    private async Task<BundleVerifyResult> VerifyBundleAsync(string repoPath, string bundlePath, CancellationToken ct)
    {
        var verify = await _git.RunAsync(repoPath, ["bundle", "verify", bundlePath], ct, BundleTimeout);
        if (verify.Success) return new BundleVerifyResult(BundleVerifyState.Verified, verify.StdOut.Trim());
        return new BundleVerifyResult(
            verify.TimedOut ? BundleVerifyState.Unknown : BundleVerifyState.Failed, verify.FirstError);
    }

    /// <summary>
    /// Bytes a backup occupies — bundle, refs sidecar, and the sidecar's .bak. Null when a file
    /// that exists could not be sized, so a caller reports the size as unknown rather than as a
    /// total silently missing whatever the read failed on.
    /// </summary>
    public long? MeasureBackupBytes(BackupHandle handle) =>
        SumFileBytes([handle.BundlePath, handle.RefsSnapshotPath, handle.RefsSnapshotPath + ".bak"]);

    /// <summary>The same three files, named from the folder and stem a walk has rather than a handle.</summary>
    private static long? MeasureStampBytes(string dir, string stamp) =>
        SumFileBytes([
            Path.Combine(dir, stamp + ".bundle"),
            Path.Combine(dir, stamp + SnapshotSuffix),
            Path.Combine(dir, stamp + SnapshotSuffix + ".bak")]);

    private static long? SumFileBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) total += info.Length;
            }
            catch (Exception ex)
            {
                Log.Warn($"could not size backup file {path}", ex);
                return null;
            }
        }
        return total;
    }

    /// <summary>
    /// What every repository's backups occupy right now. A directory read, never a git call, so a
    /// surface can show it on load and refresh it after anything that changes what is on disk.
    /// </summary>
    public BackupStorageTally MeasureStorage() => Tally(null, remove: false);

    /// <summary>
    /// What a prune to the current retention count would remove, for a confirmation to state
    /// before anything is deleted. Nothing is written.
    /// </summary>
    public BackupStorageTally PreviewPrune() => Tally(RetentionCount(), remove: false);

    /// <summary>
    /// Prunes every repository's backups to the current retention count, and reports what actually
    /// went — read from the files afterwards, not from the deletes having been attempted, because a
    /// bundle another process holds open stays on disk and must not be counted as reclaimed.
    ///
    /// Retention is otherwise applied only by the next capture for a repository, so a lowered count
    /// leaves an untouched repository over its limit until then; this is the action that closes
    /// that gap on demand.
    /// </summary>
    public BackupStorageTally PruneEveryRepository() => Tally(RetentionCount(), remove: true);

    internal const string UnsizedNotice = "some backup files could not be sized";

    internal const string UnremovedNotice =
        "some backups could not be removed — another process may hold them open";

    /// <summary>
    /// One traversal behind the storage figure, the prune preview, and the prune itself, so the
    /// number a confirmation shows and the set a prune removes cannot describe different backups.
    /// <paramref name="keep"/> null counts every backup; a value counts only those past it, in the
    /// same newest-first order <see cref="PruneDirectory"/> applies per repository.
    /// </summary>
    private BackupStorageTally Tally(int? keep, bool remove)
    {
        var repos = 0;
        var backups = 0;
        long bytes = 0;
        string? error = null;
        try
        {
            foreach (var (dir, bundles) in ReadBackupFolders())
            {
                var considered = keep is { } n ? bundles.Skip(n).ToList() : bundles;
                var counted = 0;
                foreach (var bundle in considered)
                {
                    var stamp = Path.GetFileNameWithoutExtension(bundle);
                    var size = MeasureStampBytes(dir, stamp);
                    if (size is null) error ??= UnsizedNotice;
                    if (remove)
                    {
                        RemoveStamp(dir, stamp);
                        if (File.Exists(bundle))
                        {
                            error ??= UnremovedNotice;
                            continue;
                        }
                    }
                    counted++;
                    bytes += size ?? 0;
                }
                if (remove) ReapOrphanedSnapshots(dir);
                if (counted == 0) continue;
                repos++;
                backups += counted;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not walk {SafetyPaths.BackupsRoot}", ex);
            error = ex.Message;
        }
        return new BackupStorageTally(repos, backups, bytes, error);
    }

    /// <summary>Each repository's backup folder paired with its bundles, newest first.</summary>
    private static List<(string Dir, List<string> Bundles)> ReadBackupFolders()
    {
        var root = SafetyPaths.BackupsRoot;
        if (!Directory.Exists(root)) return [];
        return [.. Directory.GetDirectories(root).Select(dir => (dir, BundlesNewestFirst(dir)))];
    }

    /// <summary>
    /// The one ordering every prune and every tally reads. Stamp is a fixed-width sortable UTC
    /// string, so ordinal-descending is newest-first — a second ordering written anywhere else
    /// could name a different set as the oldest than the one a confirmation counted.
    /// </summary>
    private static List<string> BundlesNewestFirst(string dir) =>
        [.. Directory.GetFiles(dir, "*.bundle")
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)];

    /// <summary>
    /// Restores the repo to the backup's ref state. The bundle is verified first; a
    /// failed verification aborts before any ref changes. Every ref is then reconciled to
    /// the snapshot in one transaction — refs the backup lacks are deleted, refs it has are
    /// set — after which HEAD is repositioned and the working tree is reset.
    ///
    /// Two kinds of ref are recorded in the sidecar and in the bundle but never reconciled:
    /// remote-tracking refs, which describe the remote rather than this repository's history and
    /// come back on the next fetch, and symbolic refs, whose update writes through to a target the
    /// same script already names. Neither is deleted, rewound, or recreated by a restore, and the
    /// result message counts what was left alone. Leaving a symbolic ref alone can leave it
    /// dangling: one created after the backup, aliasing a branch the reconciliation deletes,
    /// survives the restore pointing at a ref that no longer exists — a state git permits and
    /// resolves to nothing.
    ///
    /// A failure in either step after that transaction returns <see cref="RestoreResult.Success"/>
    /// false with <see cref="RestoreResult.RefsRestored"/> true: the pre-rewrite content is
    /// back in the repository even though the restore did not finish. A caller MUST NOT report
    /// an unsuccessful restore as an unchanged repository without reading that flag.
    ///
    /// The final reset is --hard, so it discards every uncommitted change in the worktree,
    /// including edits made after the backup was captured, which the bundle never held.
    /// <paramref name="allowDirty"/> is that discard, spelled by the caller: false refuses the
    /// restore outright while the tree is dirty, true proceeds and reports what the reset threw
    /// away in <see cref="RestoreResult.WorktreeWasDirty"/> and
    /// <see cref="RestoreResult.DiscardedChangeCount"/>, which a caller MUST surface. Only a
    /// caller whose user confirmed that discard passes true.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(BackupHandle handle, bool allowDirty, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await RestoreCoreAsync(handle, allowDirty, ct);
        _history.Append(OperationRecord.For(
            handle.RepoPath, OperationCategory.BackupRestore, $"Restore backup {handle.UtcStamp}",
            result.Success ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            result.Message, started,
            backupStamp: handle.UtcStamp,
            recovery: new RecoveryNote
            {
                Kind = RecoveryKind.RestoreFromBackup,
                AppliedUtc = DateTimeOffset.UtcNow
            }));
        return result;
    }

    private async Task<RestoreResult> RestoreCoreAsync(BackupHandle handle, bool allowDirty, CancellationToken ct)
    {
        if (!File.Exists(handle.BundlePath))
            return new RestoreResult(false, $"Bundle missing: {handle.BundlePath}");

        var snapshot = ReadSnapshot(handle.RefsSnapshotPath);
        if (snapshot is null)
            return new RestoreResult(false, $"Refs snapshot missing or unreadable: {handle.RefsSnapshotPath}");

        // Verify against the target repo (prerequisite objects, if any, must be present)
        // BEFORE mutating anything. A corrupt or truncated bundle stops here.
        var verify = await VerifyBundleAsync(handle.RepoPath, handle.BundlePath, ct);
        if (!verify.Verified)
            return new RestoreResult(false, $"Bundle {DescribeUnverified(verify)} — refusing to restore.");

        // Unpack the bundle's objects into the store WITHOUT moving refs: a plain fetch
        // refuses to update a branch that is checked out, so refs are set explicitly below
        // from the snapshot once every object it names is present.
        var unbundle = await _git.RunAsync(handle.RepoPath,
            ["bundle", "unbundle", handle.BundlePath], ct, BundleTimeout);
        if (!unbundle.Success)
            return new RestoreResult(false, $"Unbundle failed: {unbundle.FirstError}");

        // Read the working tree here rather than trusting a caller's earlier reading: that one is
        // as old as the bundle verify and the unbundle together, and the closing `reset --hard`
        // discards whatever was written since — which the bundle, holding committed history only,
        // cannot put back. This is the last point at which a refusal still changes nothing. An
        // unreadable tree is a refusal too, never a proceed on an assumed-clean one.
        //
        // This read is also the reported count, and it is taken before any ref moves for a reason:
        // the reconciliation repoints the branch under an unchanged index, so a read taken after it
        // shows every old-versus-restored difference as a staged change and would name a clean tree
        // dirty on nearly every restore.
        var bare = await IsBareAsync(handle.RepoPath, ct);
        var resetsWorktree = snapshot.HeadObjectId.Length > 0 && !bare;
        var wasDirty = false;
        var discardedCount = 0;
        if (resetsWorktree)
        {
            var dirty = await _git.RunAsync(handle.RepoPath, ["status", "--porcelain"], ct, RefTimeout);
            if (!dirty.Success)
                return new RestoreResult(false,
                    $"The working tree of '{handle.RepoPath}' could not be read — refusing to restore: {dirty.FirstError}");
            discardedCount = dirty.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            wasDirty = discardedCount > 0;
            if (wasDirty && !allowDirty)
                return new RestoreResult(false,
                    $"The working tree has {discardedCount} uncommitted change(s) that this restore's hard reset " +
                    "would discard, and the backup holds committed history only — refusing to restore.");
        }

        // Reconcile to EXACTLY the snapshot as ONE transaction: delete every current ref the
        // snapshot lacks, then point each snapshot ref at its recorded object. `git update-ref
        // --stdin` applies the whole script under a single lock and commits atomically, so a
        // concurrent ref lock, an IO stall, or a target object that is absent aborts the entire
        // reconciliation with NOTHING changed — never the partial, mislabeled-atomic restore
        // this rail exists to prevent. `delete <ref>` removes even the checked-out branch, where
        // `branch -d` would refuse.
        //
        // Every line carries the value the ref held when the current layout was read, so a ref
        // another process moved in the window since that read aborts the whole transaction
        // instead of being silently overwritten by the snapshot's value.
        var desired = snapshot.Refs.ToDictionary(r => r.Name, r => r.ObjectId, StringComparer.Ordinal);
        var layout = await ReadCurrentRefsAsync(handle.RepoPath, ct);
        if (layout is null)
            return new RestoreResult(false,
                $"The current ref layout of '{handle.RepoPath}' could not be read — refusing to restore.");

        var script = new StringBuilder();
        var restoredCount = 0;
        foreach (var (name, oid) in layout.Reconcilable)
            if (!desired.ContainsKey(name))
                script.Append("delete ").Append(name).Append(' ').Append(oid).Append('\n');
        foreach (var (name, oid) in desired)
        {
            if (IsRemoteTracking(name) || layout.Symbolic.Contains(name))
                continue;
            restoredCount++;
            script.Append("update ").Append(name).Append(' ').Append(oid).Append(' ')
                .Append(layout.Reconcilable.TryGetValue(name, out var old) ? old : MustNotExist).Append('\n');
        }

        if (script.Length > 0)
        {
            var reconcile = await RunUpdateRefStdinAsync(handle.RepoPath, script.ToString(), ct);
            if (!reconcile.Success)
                return new RestoreResult(false,
                    $"Ref reconciliation transaction failed — nothing changed: {reconcile.FirstError}");
        }

        // Past this line the refs hold the backup's objects again, so every remaining failure
        // is a partly-restored repository, never an untouched one.

        // Reposition HEAD, then sync the working tree. --no-deref for a detached HEAD
        // writes HEAD itself; a plain update-ref would follow the symref and move a branch.
        var head = snapshot.HeadRef.Length > 0
            ? await _git.RunAsync(handle.RepoPath, ["symbolic-ref", "HEAD", snapshot.HeadRef], ct, RefTimeout)
            : snapshot.HeadObjectId.Length > 0
                ? await _git.RunAsync(handle.RepoPath, ["update-ref", "--no-deref", "HEAD", snapshot.HeadObjectId], ct, RefTimeout)
                : null;
        if (head is { Success: false })
            return new RestoreResult(false,
                $"Refs restored but HEAD could not be repositioned: {head.FirstError}",
                RefsRestored: true);

        if (resetsWorktree)
        {
            var reset = await _git.RunAsync(handle.RepoPath, ["reset", "--hard", snapshot.HeadObjectId], ct, BundleTimeout);
            if (!reset.Success)
                return new RestoreResult(false, $"Refs restored but working-tree reset failed: {reset.FirstError}",
                    wasDirty, discardedCount, RefsRestored: true);
        }

        var untouched = desired.Count - restoredCount;
        var untouchedNote = untouched > 0
            ? $" {untouched} remote-tracking or symbolic ref(s) were left as they are."
            : "";
        return new RestoreResult(true, $"Restored {restoredCount} refs from {handle.UtcStamp}.{untouchedNote}",
            wasDirty, discardedCount, RefsRestored: true);
    }

    /// <summary>
    /// Removes a backup's bundle and refs snapshot (with its .bak) and reports what survived.
    /// Best-effort and never throws; missing files are not an error.
    ///
    /// The snapshot is removed only once the bundle is, so a bundle another process holds open
    /// leaves the pair intact and still restorable. The reverse — the bundle gone and the snapshot
    /// held — destroys the backup, and the two are reported apart because a caller wording them
    /// alike would tell a reader their backup survived while it did not.
    /// </summary>
    public Task<BackupDeleteState> DeleteBackupAsync(BackupHandle handle, CancellationToken ct = default)
    {
        TryDelete(handle.BundlePath);
        if (File.Exists(handle.BundlePath)) return Task.FromResult(BackupDeleteState.BundleRemains);
        TryDelete(handle.RefsSnapshotPath);
        TryDelete(handle.RefsSnapshotPath + ".bak");
        return Task.FromResult(BackupFilesRemain(handle)
            ? BackupDeleteState.SnapshotRemains
            : BackupDeleteState.Deleted);
    }

    /// <summary>
    /// Whether any file of a backup is still on disk. Read after a delete rather than the
    /// listing: a listing answers which pairs are usable, not which bytes were removed.
    /// </summary>
    public bool BackupFilesRemain(BackupHandle handle) =>
        File.Exists(handle.BundlePath)
        || File.Exists(handle.RefsSnapshotPath)
        || File.Exists(handle.RefsSnapshotPath + ".bak");

    /// <summary>
    /// A sortable UTC stamp guaranteed distinct within a repo's backup dir: two backups
    /// in the same millisecond would otherwise share a file stem and the second would
    /// overwrite the first. A "-NN" suffix disambiguates while preserving ordinal order.
    /// </summary>
    private static string UniqueStamp(string dir)
    {
        var baseStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var stamp = baseStamp;
        for (var n = 1; File.Exists(Path.Combine(dir, stamp + ".bundle")); n++)
            stamp = $"{baseStamp}-{n:D2}";
        return stamp;
    }

    private async Task<RefsSnapshot> CaptureRefsAsync(string repoPath, string stamp, string operation, CancellationToken ct)
    {
        var snapshot = new RefsSnapshot { RepoPath = repoPath, UtcStamp = stamp, Operation = operation };

        var refs = await _git.RunAsync(repoPath,
            ["for-each-ref", "--format=%(objectname) %(refname)"], ct, RefTimeout);
        if (!refs.Success)
            throw new BackupException($"git for-each-ref failed for '{repoPath}': {refs.FirstError}");
        foreach (var (name, oid) in ParseRefLines(refs.StdOut))
            snapshot.Refs.Add(new RefEntry { Name = name, ObjectId = oid });

        // A symbolic HEAD records the branch; a detached HEAD has none, so HeadRef stays
        // empty and only the object id is kept.
        var symref = await _git.RunAsync(repoPath, ["symbolic-ref", "-q", "HEAD"], ct, RefTimeout);
        if (symref.Success) snapshot.HeadRef = symref.StdOut.Trim();

        var headOid = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", "HEAD"], ct, RefTimeout);
        if (headOid.Success) snapshot.HeadObjectId = headOid.StdOut.Trim();

        return snapshot;
    }

    /// <summary>
    /// Object ids reachable from a reflog and from no ref. One walk covers both halves of what a
    /// standard capture misses: `git stash push` keeps its stack as refs/stash's own reflog, so
    /// stash@{1} and below are reflog entries rather than refs, and are enumerated here with the
    /// commits an amend or a reset left behind.
    ///
    /// A walk that fails throws rather than falling back to a standard capture: a backup recorded
    /// as deep has to hold what that word claims, and one silently narrowed to the refs would be
    /// read months later as covering history it never received.
    /// </summary>
    private async Task<List<string>> CaptureDeepOidsAsync(string repoPath, CancellationToken ct)
    {
        var walk = await _git.RunAsync(
            repoPath, ["rev-list", "--reflog", "--not", "--all"], ct, ReflogWalkTimeout);
        if (!walk.Success)
            throw new BackupException(
                $"The reflogs of '{repoPath}' could not be read, so no deep backup was written: {walk.FirstError}");

        return [.. walk.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(oid => oid.Length > 0)];
    }

    /// <summary>
    /// The refs a reconciliation may move, and the symbolic ones it must not.
    /// <see cref="Reconcilable"/> holds name → object id for the rest.
    /// </summary>
    private sealed record RefLayout(Dictionary<string, string> Reconcilable, HashSet<string> Symbolic);

    /// <summary>
    /// A remote-tracking ref describes the remote, which no restore changes, and `git fetch`
    /// repopulates it — the same rule the swap applies to the same repository.
    /// </summary>
    private static bool IsRemoteTracking(string name) =>
        name.StartsWith("refs/remotes/", StringComparison.Ordinal);

    /// <summary>
    /// The current ref layout, or null when git could not read it. An unreadable layout is never
    /// an empty one: reconciling against it would skip every delete the snapshot calls for and
    /// report a full restore over refs the backup never held.
    ///
    /// Remote-tracking and symbolic refs are separated out because a reconciliation must not name
    /// them. An update to a symbolic ref writes through to its target, so a script naming both is
    /// rejected whole — "multiple updates for '&lt;target&gt;' (including one via symref)" — and a
    /// default clone carries exactly that pair in refs/remotes/origin/HEAD.
    /// </summary>
    private async Task<RefLayout?> ReadCurrentRefsAsync(string repoPath, CancellationToken ct)
    {
        var refs = await _git.RunAsync(repoPath,
            ["for-each-ref", "--format=%(objectname) %(refname) %(symref)"], ct, RefTimeout);
        if (!refs.Success)
            return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbolic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in refs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // A ref name can hold no space, so the first field is the object id, the second the
            // name, and whatever follows is the symbolic target when there is one.
            var line = raw.TrimEnd('\r');
            var oidEnd = line.IndexOf(' ');
            if (oidEnd <= 0) continue;
            var rest = line[(oidEnd + 1)..];
            var nameEnd = rest.IndexOf(' ');
            var name = nameEnd < 0 ? rest : rest[..nameEnd];
            if (name.Length == 0) continue;
            if (nameEnd >= 0 && rest[(nameEnd + 1)..].Trim().Length > 0)
            {
                symbolic.Add(name);
                continue;
            }
            if (IsRemoteTracking(name)) continue;
            map[name] = line[..oidEnd];
        }
        return new RefLayout(map, symbolic);
    }

    private static IEnumerable<(string Name, string ObjectId)> ParseRefLines(string stdout)
    {
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            yield return (line[(sp + 1)..], line[..sp]);
        }
    }

    private async Task<bool> IsBareAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, ["rev-parse", "--is-bare-repository"], ct, RefTimeout);
        return result.Success && result.StdOut.Trim() == "true";
    }

    /// <summary>
    /// Feeds a reconciliation script to `git update-ref --stdin`, which applies every delete
    /// and update as one atomic transaction.
    /// </summary>
    private Task<ProcessResult> RunUpdateRefStdinAsync(string repoPath, string script, CancellationToken ct)
        => _git.RunWithInputAsync(repoPath, ["update-ref", "--stdin"], script, ct, RefTimeout);

    private static RefsSnapshot? ReadSnapshot(string path)
    {
        try { return DurableJsonFile.Read<RefsSnapshot>(path, JsonOptions); }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read refs snapshot {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// The retention a configured count actually produces. A count below one keeps one: a value
    /// that pruned every backup would leave a destructive operation with nothing to undo it.
    /// </summary>
    public static int EffectiveRetention(int configured) => configured < 1 ? 1 : configured;

    private int RetentionCount() => EffectiveRetention(_settings.Load().BackupRetentionCount);

    private void PruneOldBackups(string repoPath, int keep) =>
        PruneDirectory(SafetyPaths.BackupDirFor(RepoKey.For(repoPath)), keep);

    private static void PruneDirectory(string dir, int keep)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var bundle in BundlesNewestFirst(dir).Skip(keep))
            RemoveStamp(dir, Path.GetFileNameWithoutExtension(bundle));

        // A prune whose bundle went and whose snapshot did not leaves the same orphan a failed
        // delete does, and this loop enumerates bundles, so it would never revisit it.
        ReapOrphanedSnapshots(dir);
    }

    private static void RemoveStamp(string dir, string stamp)
    {
        TryDelete(Path.Combine(dir, stamp + ".bundle"));
        TryDelete(Path.Combine(dir, stamp + SnapshotSuffix));
        TryDelete(Path.Combine(dir, stamp + SnapshotSuffix + ".bak"));
    }

    private const string SnapshotSuffix = ".refs.json";

    /// <summary>
    /// Removes refs snapshots with no bundle beside them. A snapshot alone restores nothing, and
    /// no surface can reach one: both <see cref="ListBackupsAsync"/> and
    /// <see cref="PruneOldBackups"/> enumerate bundles, so an orphan left by a half-finished
    /// delete would hold disk that nothing would ever name again.
    ///
    /// A capture writes its bundle before its snapshot, so this pairing never describes a backup
    /// being created and the reap cannot race one. Best effort: a file that cannot be removed is
    /// logged and left for the next read.
    /// </summary>
    private static void ReapOrphanedSnapshots(string dir)
    {
        try
        {
            foreach (var path in Directory.GetFiles(dir, "*" + SnapshotSuffix + "*"))
            {
                // A wildcard is matched against short file names too, so the suffix decides rather
                // than the pattern — and a .tmp mid-write belongs to neither case, so it is left.
                var name = Path.GetFileName(path);
                var stem =
                    name.EndsWith(SnapshotSuffix, StringComparison.OrdinalIgnoreCase)
                        ? name[..^SnapshotSuffix.Length]
                        : name.EndsWith(SnapshotSuffix + ".bak", StringComparison.OrdinalIgnoreCase)
                            ? name[..^(SnapshotSuffix.Length + 4)]
                            : null;
                if (stem is null || File.Exists(Path.Combine(dir, stem + ".bundle"))) continue;
                Log.Warn($"Discarding a refs snapshot with no bundle beside it: {path}");
                TryDelete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not scan {dir} for refs snapshots with no bundle", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Backup cleanup could not delete {path}", ex); }
    }
}
