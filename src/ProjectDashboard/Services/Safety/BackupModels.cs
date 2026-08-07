namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Locates one backup: the bundle holding every object, and the sidecar refs
/// snapshot that records where each ref pointed. Serialized into the journal, so
/// it is a plain get/set DTO.
/// </summary>
public sealed class BackupHandle
{
    public string RepoPath { get; set; } = "";
    public string RepoKey { get; set; } = "";

    /// <summary>UTC capture stamp (yyyyMMdd-HHmmssfff); also the bundle/sidecar file stem.</summary>
    public string UtcStamp { get; set; } = "";

    public string BundlePath { get; set; } = "";
    public string RefsSnapshotPath { get; set; } = "";
}

/// <summary>One ref and the object id it resolved to at capture time.</summary>
public sealed class RefEntry
{
    public string Name { get; set; } = "";
    public string ObjectId { get; set; } = "";
}

/// <summary>
/// The ref state a backup restores to. HeadRef is the symbolic target
/// (e.g. refs/heads/main) or empty when HEAD was detached; HeadObjectId is where
/// HEAD resolved either way, so a detached HEAD round-trips too.
/// </summary>
public sealed class RefsSnapshot
{
    public string RepoPath { get; set; } = "";
    public string UtcStamp { get; set; } = "";
    public string HeadRef { get; set; } = "";
    public string HeadObjectId { get; set; } = "";
    public List<RefEntry> Refs { get; set; } = [];
}

/// <summary>
/// Outcome of a restore. The ref reconciliation is all-or-nothing, but the steps after it —
/// the HEAD reposition and the working-tree reset — can fail with the refs already back, so
/// <see cref="Success"/> false does not mean the repository is untouched.
/// <see cref="RefsRestored"/> is the flag that separates the two: true whenever the ref
/// transaction committed, so a caller never reports an unchanged repository over restored refs.
/// A restore's working-tree reset discards uncommitted changes; <see cref="WorktreeWasDirty"/>
/// and <see cref="DiscardedChangeCount"/> report what the reset threw away so a confirm UI can
/// warn before the caller triggers one.
/// </summary>
public sealed record RestoreResult(
    bool Success,
    string Message,
    bool WorktreeWasDirty = false,
    int DiscardedChangeCount = 0,
    bool RefsRestored = false);
