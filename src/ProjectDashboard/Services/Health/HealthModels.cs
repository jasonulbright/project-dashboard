namespace ProjectDashboard.Services.Health;

/// <summary>
/// What one check found. <see cref="NotRun"/> is not <see cref="Unknown"/>: "we have not asked"
/// and "we asked and could not tell" are different facts, and a surface that renders them alike
/// claims a check it never ran. <see cref="NotApplicable"/> is the third: the condition the check
/// describes does not exist here.
/// </summary>
public enum HealthState
{
    Ok,
    Warn,
    Bad,
    Unknown,
    NotApplicable,
    NotRun,
}

/// <summary>
/// What a check costs. <see cref="Quick"/> runs on tab activation and is local and bounded;
/// <see cref="Deep"/> reads the object store or reaches a network and never runs unasked.
/// </summary>
public enum HealthTier
{
    Quick,
    Deep,
}

/// <summary>
/// Stable identity per check row, so the page keys results and tests name checks on a value
/// rather than on the wording of a title.
/// </summary>
public static class HealthCheckId
{
    public const string GitVersion = "git-version";
    public const string Locks = "locks";
    public const string ObjectStore = "object-store";
    public const string Signing = "signing";
    public const string Hooks = "hooks";
    public const string Lfs = "lfs";
    public const string Remotes = "remotes";
    public const string Backups = "backups";

    public const string Connectivity = "connectivity";
    public const string Strict = "strict";
    public const string Reachability = "reachability";
    public const string BackupVerify = "backup-verify";
    public const string LargeObjects = "large-objects";
}

/// <summary>
/// One row of the health page. <see cref="Detail"/> is the expandable long form and is empty when
/// the summary says everything there is; it carries verbatim git output under the pinned
/// <c>LC_ALL=C</c> locale wherever a read failed.
/// </summary>
public sealed record HealthCheck(
    string Id,
    string Title,
    HealthState State,
    string Summary,
    string Detail,
    HealthTier Tier);

/// <summary>
/// One object the large-object report names. <see cref="Path"/> is empty for an object no ref
/// reaches, which is a different fact from an object with no name — and the purge hand-off is
/// offered only for the ones that have one.
/// </summary>
public sealed record LargeObject(string Sha, long Bytes, string Path);

/// <summary>
/// What the large-object walk found. <see cref="Partial"/> is set when a capture budget or a
/// timeout stopped a pass short, so the ranking is over part of the object store and says so.
/// </summary>
public sealed record LargeObjectScan(
    IReadOnlyList<LargeObject> Objects,
    bool Partial,
    string? Error);

/// <summary>
/// The health page's own wording. Pure, so every claim it makes is assertable without standing up
/// a page, and so the honest phrasings live in one place rather than being restated per row.
///
/// Wording shared with the safety rollup is taken from <see cref="Safety.SafetyCopy"/> rather than
/// restated: the two surfaces report the same backups, and a bundle worded two ways is two claims.
/// </summary>
public static class HealthCopy
{
    /// <summary>
    /// What a clean <c>fsck --connectivity-only</c> establishes, and what it does not. The pass
    /// skips object content hashing entirely, so reporting it as a healthy repository would claim
    /// the one thing it never read.
    /// </summary>
    public const string ConnectivityClean =
        "Connectivity clean; object contents not verified.";

    /// <summary>Carried by the connectivity row before anyone presses it. Never rendered as a pass.</summary>
    public const string ConnectivityNotRun =
        "Not checked — run a connectivity check.";

    /// <summary>
    /// Why the object-store size row says nothing about integrity. A cheap reading beside an
    /// unrun expensive one reads as evidence for it unless the page says otherwise.
    /// </summary>
    public const string SizeIsNotIntegrity =
        "A size reading measures the object store; it reads no object and establishes nothing about integrity.";

    /// <summary>The time warning on the strict button, in the button's own words.</summary>
    public const string StrictCost =
        "Reads every object; can take many minutes on a large repository.";

    public const string StrictNotRun =
        "Not checked — the full read is a separate, explicit run.";

    /// <summary>
    /// What a strict pass establishes. Stated even on a pass: it is the strongest claim this page
    /// can make, and the row that carries it has to bound it too.
    /// </summary>
    public const string StrictClean =
        "Every object read and hashed; no corruption found. This is git's own check, not a guarantee "
        + "about objects a later write adds.";

    /// <summary>
    /// Why signing is reported as configuration. Nothing here runs <c>verify-commit</c>, so a
    /// repository configured to sign is not evidence that any commit in it is signed.
    /// </summary>
    public const string SigningIsConfigurationOnly =
        "Read from this repository's configuration. No commit or tag signature was verified, so this "
        + "says what git is set to do, not what it did.";

    /// <summary>
    /// Why a lock file found here is reported rather than removed. Deleting an index.lock under a
    /// live index write costs that operation its final rename; deleting a packed-refs.lock under a
    /// live `pack-refs` costs the ref transaction, and this page takes no lease at all.
    /// </summary>
    public const string LocksAreReportedNotRemoved =
        "Lock files are reported here, never removed. The Changes tab offers to clear a stale "
        + "index.lock after an operation fails on one; every other lock is removed by hand, and only "
        + "once no git process is running against this repository.";

    /// <summary>
    /// Why an unreachable remote is not called broken. The probe cannot tell a machine with no
    /// network from a remote that has gone, and a row that named the second would be guessing.
    /// </summary>
    public const string ReachabilityIsNotDiagnosis =
        "A remote that did not answer may be gone, may be unreachable from this network, or may want "
        + "credentials this application never supplies. The probe reports git's own words and does not "
        + "choose between them.";

    /// <summary>Header line before any check has run in this session.</summary>
    public const string NeverChecked = "Never checked in this session.";

    public const string NotChecked = "Not checked.";

    /// <summary>What the large-object walk ranks, and what it does not mean.</summary>
    public const string LargeObjectsScope =
        "The largest blobs in the object store, ranked by stored object size. An object is listed "
        + "whether or not a ref still reaches it; the ones a ref reaches carry the path git names them by.";

    /// <summary>Shown when a pass was cut short, beside the partial ranking it produced.</summary>
    public const string LargeObjectsPartial =
        "The walk was cut short, so this ranks part of the object store rather than all of it.";

    /// <summary>
    /// Why the quick tier is what it is. Carried in the header so a reader is never left inferring
    /// that an unrun deep check had nothing to report.
    /// </summary>
    public const string QuickTierScope =
        "The checks above are local reads that run on opening this tab. The checks below read the "
        + "object store or reach a network, and run only when asked.";

    /// <summary>A size in the units git's own reporting uses, without inventing precision it lacks.</summary>
    public static string Kib(long kib) =>
        kib >= 1024L * 1024L ? $"{kib / (1024.0 * 1024.0):0.0} GiB"
        : kib >= 1024L ? $"{kib / 1024.0:0.0} MiB"
        : $"{kib} KiB";

    /// <summary>A byte count in the same units, for the object sizes git reports in bytes.</summary>
    public static string Bytes(long bytes) =>
        bytes >= 1024L * 1024L * 1024L ? $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GiB"
        : bytes >= 1024L * 1024L ? $"{bytes / (1024.0 * 1024.0):0.0} MiB"
        : bytes >= 1024L ? $"{bytes / 1024.0:0.0} KiB"
        : $"{bytes} bytes";

    /// <summary>
    /// The header's own line. The stamp is the moment the quick tier last ran, which is the only
    /// thing on this page that is ever refreshed without being asked for.
    /// </summary>
    public static string LastChecked(DateTimeOffset? at) =>
        at is null ? NeverChecked : $"Quick checks last run {Safety.SafetyCopy.Stamp(at.Value)}.";
}
