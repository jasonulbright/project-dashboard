using System.IO;
using System.Text.Json;

namespace ProjectDashboard.Services.History;

/// <summary>
/// One payload the transform could not scrub: a blob that is not valid UTF-8 or larger
/// than the regex payload limit, or a commit/tag message that is not valid UTF-8.
/// <see cref="Path"/> is one path whose file command referenced the blob's mark (a mark may
/// sit at several paths); null for a message, and when the blob is unmarked or no file
/// command referenced it. <see cref="Mark"/> is null for a message. Size is the payload's
/// byte length. Each entry is an op that did not run, so a non-empty list means the scrub
/// cannot prove those bytes clean — see <see cref="Reason"/>.
/// </summary>
public sealed record BinarySkip(long? Mark, long Size, string? Path, string Reason);

/// <summary>
/// Outcome of one post-import scrub check. <see cref="Hits"/> lists `sha:path:content`
/// grep lines plus synthetic `path:`/`binary-blob` markers from the byte- and path-level
/// fallback scans; a non-empty list means the needle survives in the target. An empty
/// list proves the needle is gone only when <see cref="Complete"/> is true.
/// </summary>
public sealed class ScrubCheckResult
{
    /// <summary>"literal" or "regex".</summary>
    public required string Kind { get; init; }

    /// <summary>The needle text or regex pattern the check grepped for.</summary>
    public required string Needle { get; init; }

    /// <summary>False when the needle cannot be expressed as a git grep invocation, or git grep rejected it; <see cref="Note"/> says why.</summary>
    public required bool Performed { get; init; }

    /// <summary>
    /// True only when this needle's verification covered everything: the check ran over every
    /// commit (no sampling), nothing it was responsible for was skipped — a blob that is
    /// binary or over-limit for the tree scrub, a message that is not valid UTF-8 for the
    /// message scrub — and paths were scanned. When false, an empty <see cref="Hits"/> list
    /// does not prove the needle is gone — <see cref="Note"/> names what was not covered.
    /// </summary>
    public required bool Complete { get; init; }

    public required int CommitsChecked { get; init; }

    public required IReadOnlyList<string> Hits { get; init; }

    /// <summary>
    /// True when the check covered only a scope (a path/commit subset). An empty
    /// <see cref="Hits"/> list then proves only "scrubbed within scope", never "scrubbed
    /// everywhere"; <see cref="Complete"/> is always false while this is true. It does not
    /// mean out-of-scope content is unchanged: under a commit scope a rewrite propagates into
    /// every descendant that does not re-touch the path. <see cref="Note"/> carries the count.
    /// </summary>
    public bool WithinScopeOnly { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Everything a rewrite run proved about itself: what changed, what was skipped, how old
/// commits map to new ones, and what the verification greps found. Serializable with
/// System.Text.Json in both directions.
/// Reading the scrub result: a non-empty <see cref="ScrubCheckResult.Hits"/> list means
/// the needle survives in the target. An empty hit list proves the needle is gone only
/// when that check's <see cref="ScrubCheckResult.Complete"/> is true; while any check is
/// incomplete — a skipped blob, a sampled commit set, or a grep that could not run — an
/// empty hit list is silence, not a clean bill.
/// </summary>
public sealed class RewriteReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public required string SourceRepository { get; init; }

    public required string TargetBareRepository { get; init; }

    public required int CommitCount { get; init; }

    /// <summary>Payloads whose transformed bytes differ from the original. Identical output counts as untouched.</summary>
    public required int BlobsChanged { get; init; }

    /// <summary>Sum over changed payloads of (new length - old length); negative when content shrank.</summary>
    public required long BytesDelta { get; init; }

    public required IReadOnlyList<BinarySkip> BinarySkips { get; init; }

    /// <summary>Original commit oid to imported commit oid, in history stream order. Unchanged history maps oids to themselves.</summary>
    public required IReadOnlyDictionary<string, string> CommitMap { get; init; }

    /// <summary>Original oids of commits whose root tree differs in the target — the commits whose content a transform actually touched.</summary>
    public required IReadOnlyList<string> CommitsWithChangedTrees { get; init; }

    public required string FsckOutput { get; init; }

    public required IReadOnlyList<ScrubCheckResult> ScrubChecks { get; init; }

    /// <summary>
    /// Human-readable scope the run applied, e.g. "files: globs [src/**]; commits: range A..B".
    /// Distinguishes a scoped scrub — see <see cref="ScrubCheckResult.WithinScopeOnly"/> — from
    /// an all-files/all-history one so the wizard never presents "scrubbed within scope" as
    /// "scrubbed everywhere".
    /// </summary>
    public string ScopeDescription { get; init; } = "files: all files; commits: all history";

    /// <summary>Count of commits the scope selected for content transforms/purge (all history when unscoped).</summary>
    public int InScopeCommitCount { get; init; }

    /// <summary>
    /// Commits in <see cref="CommitsWithChangedTrees"/> the commit scope did not select. A
    /// git snapshot inherits, so a rewrite inside an in-scope commit reaches every descendant
    /// that does not re-touch the path — out-of-scope content does change, and this counts how
    /// much. Zero when the run had no commit scope.
    /// </summary>
    public int OutOfScopeCommitsWithChangedTrees { get; init; }

    /// <summary>Commit and tag messages whose bytes a message op changed.</summary>
    public int MessagesChanged { get; init; }

    /// <summary>Author/committer/tagger header lines an identity mapping rewrote.</summary>
    public int IdentitiesRewritten { get; init; }

    /// <summary>File commands (M/D/R/C) a purge dropped.</summary>
    public int FileCommandsRemoved { get; init; }

    /// <summary>Commits pruned after a purge left them empty.</summary>
    public int CommitsPruned { get; init; }

    /// <summary>Shared blobs split so an in-scope rewrite did not corrupt out-of-scope history.</summary>
    public int BlobsSplit { get; init; }

    /// <summary>
    /// Writes the report as indented JSON to exactly <paramref name="reportPath"/>. The
    /// containing directory must already exist — backup-layout creation belongs to the
    /// swap stage, not here.
    /// </summary>
    public async Task WriteAsync(string reportPath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(reportPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, ct);
    }
}
