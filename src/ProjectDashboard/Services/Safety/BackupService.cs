using System.IO;
using System.Text.Json;

namespace ProjectDashboard.Services.Safety;

/// <summary>Raised when a backup cannot be created or a restore is refused; loud by design.</summary>
public sealed class BackupException : Exception
{
    public BackupException(string message) : base(message) { }
}

/// <summary>
/// Captures and restores a repository's full object graph and ref layout before any
/// history-altering operation (R-01). A backup is a `git bundle --all` plus a sidecar
/// refs snapshot; a restore verifies the bundle before touching the repo and either
/// reconciles every ref back to the snapshot or leaves the repo untouched — never a
/// partial restore. All backups live under AppPaths (never inside a repo), keyed by
/// <see cref="RepoKey"/>, retained newest-N per repo.
/// </summary>
public sealed class BackupService
{
    private static readonly TimeSpan BundleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefTimeout = TimeSpan.FromSeconds(30);

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
    /// does not.
    /// </summary>
    public async Task<BackupHandle> CreateBackupAsync(string repoPath, CancellationToken ct = default)
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
        var snapshot = await CaptureRefsAsync(repoPath, stamp, ct);

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
    /// Restores the repo to the backup's ref state. The bundle is verified first; a
    /// failed verification aborts before any ref changes (never a partial restore). On
    /// success every ref is reconciled to the snapshot — refs the backup lacks are
    /// deleted, refs it has are set — HEAD is repositioned, and the working tree is reset.
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

        // Reconcile to EXACTLY the snapshot: delete every current ref the snapshot lacks,
        // then point each snapshot ref at its recorded object.
        var desired = snapshot.Refs.ToDictionary(r => r.Name, r => r.ObjectId, StringComparer.Ordinal);
        var current = await ReadCurrentRefsAsync(handle.RepoPath, ct);
        foreach (var name in current.Keys)
            if (!desired.ContainsKey(name))
            {
                var del = await _git.RunAsync(handle.RepoPath, ["update-ref", "-d", name], ct, RefTimeout);
                if (!del.Success)
                    return new RestoreResult(false, $"Failed to remove ref {name}: {del.FirstError}");
            }
        foreach (var (name, oid) in desired)
        {
            var set = await _git.RunAsync(handle.RepoPath, ["update-ref", name, oid], ct, RefTimeout);
            if (!set.Success)
                return new RestoreResult(false, $"Failed to set ref {name}: {set.FirstError}");
        }

        // Reposition HEAD, then sync the working tree. --no-deref for a detached HEAD
        // writes HEAD itself; a plain update-ref would follow the symref and move a branch.
        if (snapshot.HeadRef.Length > 0)
            await _git.RunAsync(handle.RepoPath, ["symbolic-ref", "HEAD", snapshot.HeadRef], ct, RefTimeout);
        else if (snapshot.HeadObjectId.Length > 0)
            await _git.RunAsync(handle.RepoPath, ["update-ref", "--no-deref", "HEAD", snapshot.HeadObjectId], ct, RefTimeout);

        if (snapshot.HeadObjectId.Length > 0 && !await IsBareAsync(handle.RepoPath, ct))
        {
            var reset = await _git.RunAsync(handle.RepoPath, ["reset", "--hard", snapshot.HeadObjectId], ct, BundleTimeout);
            if (!reset.Success)
                return new RestoreResult(false, $"Refs restored but working-tree reset failed: {reset.FirstError}");
        }

        return new RestoreResult(true, $"Restored {desired.Count} refs from {handle.UtcStamp}.");
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

    private async Task<RefsSnapshot> CaptureRefsAsync(string repoPath, string stamp, CancellationToken ct)
    {
        var snapshot = new RefsSnapshot { RepoPath = repoPath, UtcStamp = stamp };

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

    private async Task<Dictionary<string, string>> ReadCurrentRefsAsync(string repoPath, CancellationToken ct)
    {
        var refs = await _git.RunAsync(repoPath,
            ["for-each-ref", "--format=%(objectname) %(refname)"], ct, RefTimeout);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (refs.Success)
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
