using System.IO;
using System.Text.Json;

namespace ProjectDashboard.Services.History;

/// <summary>
/// One payload the transform skipped as binary (not valid UTF-8). No path exists at this
/// layer: a blob is content addressed by mark, and the same mark may sit at many paths.
/// A null mark means an unmarked payload. Size is the payload's byte length when first
/// classified.
/// </summary>
public sealed record BinarySkip(long? Mark, long Size);

/// <summary>Outcome of one post-import scrub grep. Hits list `sha:path:content` lines; a non-empty list means the needle survives in the target.</summary>
public sealed class ScrubCheckResult
{
    /// <summary>"literal" or "regex".</summary>
    public required string Kind { get; init; }

    /// <summary>The needle text or regex pattern the check grepped for.</summary>
    public required string Needle { get; init; }

    /// <summary>False when the needle cannot be expressed as a git grep invocation; <see cref="Note"/> says why.</summary>
    public required bool Performed { get; init; }

    public required int CommitsChecked { get; init; }

    public required IReadOnlyList<string> Hits { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Everything a rewrite run proved about itself: what changed, what was skipped, how old
/// commits map to new ones, and what the verification greps found. Serializable with
/// System.Text.Json in both directions.
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
