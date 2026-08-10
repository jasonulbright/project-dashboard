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
/// </summary>
public sealed class BackupService
{
    private static readonly TimeSpan BundleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefTimeout = TimeSpan.FromSeconds(30);

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
    /// </summary>
    public async Task<BackupHandle> CreateBackupAsync(
        string repoPath, string operation = "", CancellationToken ct = default)
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

        // The snapshot's own object ids are fed in as explicit revisions, so a ref moved or
        // deleted between the capture above and this call still has its recorded object in the
        // bundle — the restore reads its refs from the sidecar and would otherwise name an object
        // the bundle never received. `--all` rides along because git refuses a bundle that names
        // no ref, and it keeps the capture a superset: every ref plus the top refs/stash entry,
        // but no reflogs and no deeper stash-stack entries, so those older stash states and
        // reflog-only commits are unreachable in the bundle and are lost on restore.
        var pinned = snapshot.Refs.Select(r => r.ObjectId)
            .Append(snapshot.HeadObjectId)
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
            throw new BackupException($"Backup bundle for '{repoPath}' failed verification: {verify.Detail}");
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

        return new BackupDetails(snapshot.Operation, snapshot.Refs.Count, snapshot.HeadRef, snapshot.HeadObjectId, bytes);
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
    public long? MeasureBackupBytes(BackupHandle handle)
    {
        long total = 0;
        foreach (var path in new[] { handle.BundlePath, handle.RefsSnapshotPath, handle.RefsSnapshotPath + ".bak" })
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
            return new RestoreResult(false, $"Bundle failed verification — refusing to restore: {verify.Detail}");

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

    private int RetentionCount()
    {
        var n = _settings.Load().BackupRetentionCount;
        return n < 1 ? 1 : n;
    }

    private void PruneOldBackups(string repoPath, int keep)
    {
        var dir = SafetyPaths.BackupDirFor(RepoKey.For(repoPath));
        if (!Directory.Exists(dir)) return;

        var bundles = Directory.GetFiles(dir, "*.bundle")
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)
            .ToList();

        foreach (var bundle in bundles.Skip(keep))
        {
            var stamp = Path.GetFileNameWithoutExtension(bundle);
            TryDelete(bundle);
            TryDelete(Path.Combine(dir, stamp + ".refs.json"));
            TryDelete(Path.Combine(dir, stamp + ".refs.json.bak"));
        }

        // A prune whose bundle went and whose snapshot did not leaves the same orphan a failed
        // delete does, and this loop enumerates bundles, so it would never revisit it.
        ReapOrphanedSnapshots(dir);
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
