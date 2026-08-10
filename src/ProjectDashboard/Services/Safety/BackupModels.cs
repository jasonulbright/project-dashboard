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

    /// <summary>
    /// What the backup was taken for, in the words a reader browsing backups needs. Empty for a
    /// sidecar written before the field existed, which a listing reports as unrecorded rather
    /// than guessing.
    /// </summary>
    public string Operation { get; set; } = "";

    public List<RefEntry> Refs { get; set; } = [];
}

/// <summary>
/// What a backup's sidecar says about the state it captured, for a surface listing backups to
/// restore. Read from disk on demand: a backup that cannot produce this is one whose sidecar is
/// missing or unreadable, which is exactly the backup a restore would refuse.
/// </summary>
public sealed record BackupDetails(
    string Operation,
    int RefCount,
    string HeadRef,
    string HeadObjectId,
    long BundleBytes);

/// <summary>
/// What a delete left on disk. The two partial outcomes are not interchangeable:
/// <see cref="BundleRemains"/> leaves the backup whole and still restorable, because the snapshot
/// is only removed once the bundle is; <see cref="SnapshotRemains"/> means the bundle is gone and
/// the backup with it, and what is left restores nothing.
/// </summary>
public enum BackupDeleteState
{
    Deleted,
    BundleRemains,
    SnapshotRemains
}

/// <summary>
/// Whether a bundle reads back. <see cref="Unknown"/> is git failing to answer — a kill on
/// timeout, a launch that produced no verdict — and is never a pass: every caller treats
/// anything other than <see cref="Verified"/> as a bundle it may not restore from.
/// </summary>
public enum BundleVerifyState
{
    Verified,
    Failed,
    Unknown
}

/// <summary>
/// One <c>git bundle verify</c> answer. <paramref name="Detail"/> is git's verbatim output for a
/// pass and its verbatim failure text otherwise.
/// </summary>
public sealed record BundleVerifyResult(BundleVerifyState State, string Detail)
{
    public bool Verified => State == BundleVerifyState.Verified;
}

/// <summary>
/// Outcome of a restore. The ref reconciliation is all-or-nothing, but the steps after it —
/// the HEAD reposition and the working-tree reset — can fail with the refs already back, so
/// <see cref="Success"/> false does not mean the repository is untouched.
/// <see cref="RefsRestored"/> is the flag that separates the two: true whenever the ref
/// transaction committed, so a caller never reports an unchanged repository over restored refs.
/// A restore's working-tree reset discards uncommitted changes; <see cref="WorktreeWasDirty"/>
/// and <see cref="DiscardedChangeCount"/> report what the reset threw away so a confirm UI can
/// warn before the caller triggers one. Both are measured against the pre-restore HEAD, before any
/// ref moves, so they count the reader's own uncommitted edits and nothing else: content that
/// differs only because the restored history differs is restored work, not discarded work, and a
/// count taken after the ref transaction would fold it in and call a clean tree dirty.
/// </summary>
public sealed record RestoreResult(
    bool Success,
    string Message,
    bool WorktreeWasDirty = false,
    int DiscardedChangeCount = 0,
    bool RefsRestored = false);
