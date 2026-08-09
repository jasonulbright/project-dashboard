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

    public BackupService(GitService git, SettingsService settings)
    {
        _git = git;
        _settings = settings;
    }

    /// <summary>
    /// Bundles every ref and records the ref layout, then prunes older backups down to
    /// BackupRetentionCount. Throws <see cref="BackupException"/> on any failure — a
    /// caller must never proceed with a destructive op believing a backup exists when it
    /// does not. <paramref name="operation"/> is recorded in the sidecar so a reader browsing
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

        // `git bundle --all` captures every ref plus the top refs/stash entry, but no reflogs
        // and no deeper stash-stack entries; those older stash states and reflog-only commits
        // are unreachable in the bundle and are lost on restore.
        var bundle = await _git.RunAsync(repoPath, ["bundle", "create", bundlePath, "--all"], ct, BundleTimeout);
        if (!bundle.Success || !File.Exists(bundlePath))
            throw new BackupException($"git bundle create failed for '{repoPath}': {bundle.FirstError}");

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
    /// Restores the repo to the backup's ref state. The bundle is verified first; a
    /// failed verification aborts before any ref changes. Every ref is then reconciled to
    /// the snapshot in one transaction — refs the backup lacks are deleted, refs it has are
    /// set — after which HEAD is repositioned and the working tree is reset.
    ///
    /// A failure in either step after that transaction returns <see cref="RestoreResult.Success"/>
    /// false with <see cref="RestoreResult.RefsRestored"/> true: the pre-rewrite content is
    /// back in the repository even though the restore did not finish. A caller MUST NOT report
    /// an unsuccessful restore as an unchanged repository without reading that flag.
    ///
    /// The final reset is --hard, so it discards every uncommitted change in the worktree,
    /// including edits made after the backup was captured, which the bundle never held. A
    /// caller MUST confirm with the user before invoking this against a dirty tree, and MUST
    /// surface <see cref="RestoreResult.WorktreeWasDirty"/> and
    /// <see cref="RestoreResult.DiscardedChangeCount"/> from the result, which report what the
    /// reset threw away. Silently restoring is data loss the backup does not cover.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(BackupHandle handle, CancellationToken ct = default)
    {
        if (!File.Exists(handle.BundlePath))
            return new RestoreResult(false, $"Bundle missing: {handle.BundlePath}");

        var snapshot = ReadSnapshot(handle.RefsSnapshotPath);
        if (snapshot is null)
            return new RestoreResult(false, $"Refs snapshot missing or unreadable: {handle.RefsSnapshotPath}");

        // Verify against the target repo (prerequisite objects, if any, must be present)
        // BEFORE mutating anything. A corrupt or truncated bundle stops here.
        var verify = await _git.RunAsync(handle.RepoPath, ["bundle", "verify", handle.BundlePath], ct, BundleTimeout);
        if (!verify.Success)
            return new RestoreResult(false, $"Bundle failed verification — refusing to restore: {verify.FirstError}");

        // Unpack the bundle's objects into the store WITHOUT moving refs: a plain fetch
        // refuses to update a branch that is checked out, so refs are set explicitly below
        // from the snapshot once every object it names is present.
        var unbundle = await _git.RunAsync(handle.RepoPath,
            ["bundle", "unbundle", handle.BundlePath], ct, BundleTimeout);
        if (!unbundle.Success)
            return new RestoreResult(false, $"Unbundle failed: {unbundle.FirstError}");

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
        var current = await ReadCurrentRefsAsync(handle.RepoPath, ct);
        if (current is null)
            return new RestoreResult(false,
                $"The current ref layout of '{handle.RepoPath}' could not be read — refusing to restore.");

        var script = new StringBuilder();
        foreach (var (name, oid) in current)
            if (!desired.ContainsKey(name))
                script.Append("delete ").Append(name).Append(' ').Append(oid).Append('\n');
        foreach (var (name, oid) in desired)
            script.Append("update ").Append(name).Append(' ').Append(oid).Append(' ')
                .Append(current.TryGetValue(name, out var old) ? old : MustNotExist).Append('\n');

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

        var wasDirty = false;
        var discardedCount = 0;
        if (snapshot.HeadObjectId.Length > 0 && !await IsBareAsync(handle.RepoPath, ct))
        {
            // The reset is an explicit, backup-preceded recovery, but it silently discards any
            // uncommitted work; count that first so a confirm UI can warn before it happens.
            var dirty = await _git.RunAsync(handle.RepoPath, ["status", "--porcelain"], ct, RefTimeout);
            if (dirty.Success)
            {
                discardedCount = dirty.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                wasDirty = discardedCount > 0;
            }

            var reset = await _git.RunAsync(handle.RepoPath, ["reset", "--hard", snapshot.HeadObjectId], ct, BundleTimeout);
            if (!reset.Success)
                return new RestoreResult(false, $"Refs restored but working-tree reset failed: {reset.FirstError}",
                    wasDirty, discardedCount, RefsRestored: true);
        }

        return new RestoreResult(true, $"Restored {desired.Count} refs from {handle.UtcStamp}.",
            wasDirty, discardedCount, RefsRestored: true);
    }

    /// <summary>Removes a backup's bundle and sidecar (with its .bak). Best-effort; missing files are not an error.</summary>
    public Task DeleteBackupAsync(BackupHandle handle, CancellationToken ct = default)
    {
        TryDelete(handle.BundlePath);
        TryDelete(handle.RefsSnapshotPath);
        TryDelete(handle.RefsSnapshotPath + ".bak");
        return Task.CompletedTask;
    }

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
    /// Every ref and its object id, or null when git could not read the layout. An unreadable
    /// layout is never an empty one: reconciling against it would skip every delete the snapshot
    /// calls for and report a full restore over refs the backup never held.
    /// </summary>
    private async Task<Dictionary<string, string>?> ReadCurrentRefsAsync(string repoPath, CancellationToken ct)
    {
        var refs = await _git.RunAsync(repoPath,
            ["for-each-ref", "--format=%(objectname) %(refname)"], ct, RefTimeout);
        if (!refs.Success)
            return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, oid) in ParseRefLines(refs.StdOut))
            map[name] = oid;
        return map;
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
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Backup cleanup could not delete {path}", ex); }
    }
}
