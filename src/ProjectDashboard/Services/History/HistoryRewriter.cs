using System.IO;
using System.Text;

namespace ProjectDashboard.Services.History;

public sealed class HistoryRewriteRequest
{
    public required string SourceRepository { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Receives the rewritten history. The source repository's refs are never touched; swapping is a later, rails-gated stage.</summary>
    public required string TargetBareRepository { get; init; }

    public required TimeSpan ExportTimeout { get; init; }

    public required TimeSpan ImportTimeout { get; init; }

    public required RewriteOptions Rewrite { get; init; }

    public TimeSpan VerificationTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>When set, the report is also written here as JSON. The containing directory must exist.</summary>
    public string? ReportPath { get; init; }

    public Action<HistoryProgress>? Progress { get; init; }

    public string? GitExecutable { get; init; }
}

/// <summary>
/// Content rewrite over full history: export, parse, transform every blob (all-files
/// scope), import into a fresh bare target, then verify (fsck --strict, plus scrub greps
/// for each op's needle across the full rewritten commit set, byte-scanning skipped blobs
/// and scanning paths) and report. Changed payloads are held in memory until import, so
/// peak memory is the sum of changed payload bytes plus one payload in flight; a
/// configurable ceiling refuses the run before that sum can exhaust memory. Unchanged
/// payloads stream from the spool untouched.
/// </summary>
public sealed class HistoryRewriter
{
    /// <summary>Default ceiling on the summed size of changed payloads held in memory before import.</summary>
    public const long DefaultChangedPayloadCeiling = 1L * 1024 * 1024 * 1024;

    /// <summary>Shas per git invocation, kept far under the 32 KiB Windows command-line ceiling.</summary>
    private const int ShaChunk = 200;

    /// <summary>Above this many candidate commits the scrub grep samples evenly instead of checking all.</summary>
    private const int ScrubSampleCap = 2048;

    private const int ScrubHitCap = 200;

    private static readonly Dictionary<string, string> GitEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0"
    };

    private readonly string _gitExe;
    private readonly long _regexPayloadLimit;
    private readonly long _changedPayloadCeiling;

    public HistoryRewriter(
        string? gitExecutable = null,
        long regexPayloadLimit = BlobTransformer.DefaultRegexPayloadLimit,
        long changedPayloadCeiling = DefaultChangedPayloadCeiling)
    {
        _gitExe = gitExecutable ?? HistoryPipeline.ResolveGitExecutable();
        _regexPayloadLimit = regexPayloadLimit;
        _changedPayloadCeiling = changedPayloadCeiling;
    }

    public async Task<RewriteReport> RunAsync(HistoryRewriteRequest request, CancellationToken ct = default)
    {
        var rewrite = request.Rewrite;
        rewrite.Validate();

        var transformer = new BlobTransformer(rewrite.ContentOps, _regexPayloadLimit);
        var literalOps = rewrite.ContentOps.OfType<LiteralReplace>().ToList();
        var messageTransformer = rewrite.MessageOps.Count > 0
            ? new BlobTransformer(rewrite.MessageOps, _regexPayloadLimit)
            : null;

        // Resolve the commit scope against the source before any export work; a bad ref
        // fails here for nothing rather than after a full export.
        var inScopeCommitOids = await ResolveCommitScopeAsync(rewrite.CommitScope, request, ct);

        TransformTally? legacyTally = null;
        ScopedRewriteOutcome? scoped = null;

        Func<ParsedExport, CancellationToken, Task> transform;
        if (rewrite.IsLegacyAllFiles)
        {
            legacyTally = new TransformTally();
            transform = (parsed, token) =>
            {
                TransformBlobs(parsed, transformer, literalOps, _changedPayloadCeiling, legacyTally, token);
                return Task.CompletedTask;
            };
        }
        else
        {
            transform = (parsed, token) =>
            {
                scoped = new ScopedRewritePass(
                    parsed, transformer, literalOps, messageTransformer, rewrite.IdentityMappings,
                    rewrite.FileScope, inScopeCommitOids, rewrite.Purge, _changedPayloadCeiling).Run(token);
                return Task.CompletedTask;
            };
        }

        var pipeline = new HistoryPipeline(request.GitExecutable);
        var result = await pipeline.RunAsync(new HistoryPipelineOptions
        {
            SourceRepository = request.SourceRepository,
            WorkingDirectory = request.WorkingDirectory,
            TargetBareRepository = request.TargetBareRepository,
            ExportTimeout = request.ExportTimeout,
            ImportTimeout = request.ImportTimeout,
            Progress = request.Progress,
            GitExecutable = request.GitExecutable,
            TransformAsync = transform
        }, ct);

        var blobsChanged = legacyTally?.BlobsChanged ?? scoped!.BlobsChanged;
        var bytesDelta = legacyTally?.BytesDelta ?? scoped!.BytesDelta;
        var skips = legacyTally?.Skips ?? scoped!.Skips;
        var byteSurvivors = legacyTally?.ByteSurvivors ?? scoped!.ByteSurvivors;
        var prunedMarks = scoped?.PrunedMarkToSurvivingMark ?? [];

        var commitMap = BuildCommitMap(result, prunedMarks);
        var markToPath = BuildMarkToPath(result.Index);
        var binarySkips = skips
            .Select(s => new BinarySkip(s.Mark, s.Size, s.Mark is { } m ? markToPath.GetValueOrDefault(m) : null, s.Reason))
            .ToList();
        var paths = CollectPaths(result.Index);
        var changedTrees = await FindChangedTreesAsync(request, commitMap, ct);

        var fsck = await ProcessRunner.RunAsync(
            _gitExe, ["fsck", "--strict"], request.TargetBareRepository,
            request.VerificationTimeout, GitEnvironment, ct);
        if (!fsck.Success)
            throw new HistoryPipelineException(
                "verify", "fsck --strict failed on the rewrite target", fsck.ExitCode, fsck.StdErr + "\n" + fsck.StdOut);

        var scope = new ScrubScope(rewrite, inScopeCommitOids, commitMap, changedTrees);
        var scrubChecks = await RunScrubChecksAsync(request, commitMap, binarySkips, byteSurvivors, paths, scope, ct);

        var report = new RewriteReport
        {
            SourceRepository = request.SourceRepository,
            TargetBareRepository = request.TargetBareRepository,
            CommitCount = commitMap.Count,
            BlobsChanged = blobsChanged,
            BytesDelta = bytesDelta,
            BinarySkips = binarySkips,
            CommitMap = commitMap,
            CommitsWithChangedTrees = changedTrees,
            FsckOutput = (fsck.StdErr + "\n" + fsck.StdOut).Trim(),
            ScrubChecks = scrubChecks,
            ScopeDescription = $"files: {rewrite.FileScope.Describe()}; commits: {rewrite.CommitScope.Describe()}",
            InScopeCommitCount = inScopeCommitOids?.Count ?? commitMap.Count,
            OutOfScopeCommitsWithChangedTrees = scope.OutOfScopeChangedTrees,
            MessagesChanged = scoped?.MessagesChanged ?? 0,
            IdentitiesRewritten = scoped?.IdentitiesRewritten ?? 0,
            FileCommandsRemoved = scoped?.FileCommandsRemoved ?? 0,
            CommitsPruned = prunedMarks.Count,
            BlobsSplit = scoped?.Splits.Count ?? 0
        };
        if (request.ReportPath is { } reportPath)
            await report.WriteAsync(reportPath, ct);
        return report;
    }

    /// <summary>Resolves a commit scope to full source commit oids (lowercase). Null means all history.</summary>
    private async Task<HashSet<string>?> ResolveCommitScopeAsync(
        CommitScope scope, HistoryRewriteRequest request, CancellationToken ct)
    {
        switch (scope)
        {
            case AllHistoryScope:
                return null;

            case ExplicitCommitsScope explicitCommits:
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var commitish in explicitCommits.Commits)
                {
                    var rev = await ProcessRunner.RunAsync(
                        _gitExe, ["rev-parse", "--verify", "--quiet", commitish + "^{commit}"],
                        request.SourceRepository, request.VerificationTimeout, GitEnvironment, ct);
                    if (!rev.Success || rev.StdOut.Trim().Length == 0)
                        throw new HistoryPipelineException("scope", $"commit '{commitish}' does not resolve in the source repository");
                    set.Add(rev.StdOut.Trim().ToLowerInvariant());
                }
                return set;
            }

            case CommitRangeScope range:
            {
                var spec = range.FromRef is { } from ? $"{from}..{range.ToRef}" : range.ToRef;
                var revList = await ProcessRunner.RunAsync(
                    _gitExe, ["rev-list", spec], request.SourceRepository, request.VerificationTimeout, GitEnvironment, ct);
                if (!revList.Success)
                    throw new HistoryPipelineException("scope", $"commit range '{spec}' does not resolve in the source repository", revList.ExitCode, revList.StdErr);
                var set = new HashSet<string>(
                    SplitLines(revList.StdOut).Select(l => l.ToLowerInvariant()), StringComparer.Ordinal);
                if (set.Count == 0)
                    throw new HistoryPipelineException("scope", $"commit range '{spec}' selected no commits");
                return set;
            }

            default:
                throw new NotSupportedException($"commit scope {scope.GetType().Name} is not supported");
        }
    }

    /// <summary>Running counts and coverage material collected during the single transform pass.</summary>
    private sealed class TransformTally
    {
        public int BlobsChanged;
        public long BytesDelta;
        public readonly List<(long? Mark, long Size, string Reason)> Skips = [];

        /// <summary>Literal needles found verbatim inside a skipped (unscrubbable) blob's bytes — a definite survivor git grep -I cannot see.</summary>
        public readonly List<(LiteralReplace Op, long? Mark, long Size)> ByteSurvivors = [];
    }

    /// <summary>
    /// One pass over the parsed records: each blob payload is read from the spool,
    /// transformed, and materialized as inline bytes only when the bytes changed.
    /// File commands that reference content the stream does not carry (inline payloads,
    /// blob oids) are refused — content the transform cannot see cannot be scrubbed, and
    /// the mandated export flags never produce those shapes.
    /// </summary>
    private static void TransformBlobs(
        ParsedExport parsed, BlobTransformer transformer, IReadOnlyList<LiteralReplace> literalOps,
        long changedPayloadCeiling, TransformTally tally, CancellationToken ct)
    {
        using var spool = new FileStream(
            parsed.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);

        long changedBytes = 0;
        foreach (var record in parsed.Records)
        {
            ct.ThrowIfCancellationRequested();
            switch (record)
            {
                case BlobRecord blob:
                    var slice = blob.Data.SourceSlice;
                    if (slice.Length > int.MaxValue)
                        throw new NotSupportedException(
                            $"a {slice.Length}-byte blob cannot be materialized for transformation (2 GiB payload ceiling)");
                    // Gate the regex payload limit against the slice length before reading
                    // it, so an over-limit blob is a reported skip (feeding scrub
                    // incompleteness) rather than a materialized allocation or a run-
                    // aborting throw. The rest of the repository still rewrites.
                    if (transformer.HasRegexOp && slice.Length > transformer.RegexPayloadLimit)
                    {
                        tally.Skips.Add((blob.Mark, slice.Length,
                            $"exceeds the {transformer.RegexPayloadLimit}-byte regex transform limit"));
                        break;
                    }
                    var payload = ReadSlice(spool, slice);
                    var outcome = transformer.Transform(payload);
                    switch (outcome.Class)
                    {
                        case TransformClass.BinarySkipped:
                            tally.Skips.Add((blob.Mark, slice.Length, "not valid UTF-8"));
                            // The transform left these bytes untouched, so any literal
                            // needle present survives where git grep -I cannot see it.
                            foreach (var op in literalOps)
                                if (payload.AsSpan().IndexOf(op.Find) >= 0)
                                    tally.ByteSurvivors.Add((op, blob.Mark, slice.Length));
                            break;
                        case TransformClass.Changed:
                            blob.Data.InlineBytes = outcome.Bytes;
                            tally.BytesDelta += outcome.Bytes!.LongLength - slice.Length;
                            tally.BlobsChanged++;
                            // Changed payloads stay resident until import; refuse before
                            // their running sum can exhaust memory.
                            changedBytes += outcome.Bytes.LongLength;
                            if (changedBytes > changedPayloadCeiling)
                                throw new HistoryPipelineException(
                                    "transform",
                                    $"changed payloads reached {changedBytes} bytes, past the {changedPayloadCeiling}-byte ceiling — rewrite refused before exhausting memory");
                            break;
                    }
                    break;

                case CommitRecord commit:
                    foreach (var fileModify in commit.FileCommands.OfType<FileModify>())
                    {
                        if (fileModify.IsInline)
                            throw new NotSupportedException(
                                "a stream carrying inline file payloads cannot be rewritten by this stage");
                        if (fileModify.MarkRef is null && fileModify.Mode != "160000")
                            throw new NotSupportedException(
                                $"filemodify references content by oid ('{Encoding.UTF8.GetString(fileModify.RawLine)}') — content outside the stream cannot be scrubbed");
                    }
                    break;
            }
        }
    }

    private static byte[] ReadSlice(FileStream spool, SpoolSlice slice)
    {
        var payload = new byte[slice.Length];
        spool.Seek(slice.Offset, SeekOrigin.Begin);
        spool.ReadExactly(payload);
        return payload;
    }

    /// <summary>
    /// Joins each commit's original oid (from --show-original-ids) with its imported oid
    /// (from --export-marks). A pruned commit has no imported oid of its own; its original oid
    /// maps to the imported oid of the surviving commit its children were rewired onto, so the
    /// map still resolves every source commit.
    /// </summary>
    private static Dictionary<string, string> BuildCommitMap(
        HistoryPipelineResult result, IReadOnlyDictionary<long, long> prunedMarkToSurvivingMark)
    {
        var marks = new Dictionary<long, string>();
        foreach (var raw in File.ReadLines(result.MarksPath))
        {
            // Marks file lines are `:N <oid>`.
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            if (line[0] != ':' || sp <= 1 || !long.TryParse(line.AsSpan(1, sp - 1), out var mark))
                throw new HistoryPipelineException("verify", $"malformed marks line '{line}' in {result.MarksPath}");
            marks[mark] = line[(sp + 1)..];
        }

        var map = new Dictionary<string, string>();
        foreach (var commit in result.Index.CommitsInOrder)
        {
            if (commit.OriginalOid is not { } originalOid)
                throw new HistoryPipelineException("verify", $"commit mark :{commit.Mark} has no original-oid — export ran without --show-original-ids");

            var effectiveMark = commit.Mark;
            // Follow a pruned commit to its surviving replacement (the chain is already flat).
            if (prunedMarkToSurvivingMark.TryGetValue(effectiveMark, out var surviving))
                effectiveMark = surviving;

            if (!marks.TryGetValue(effectiveMark, out var importedOid))
                throw new HistoryPipelineException("verify", $"commit mark :{commit.Mark} is missing from the import marks file");
            map[originalOid] = importedOid;
        }
        return map;
    }

    /// <summary>Blob mark to one path whose file command references it, so a skipped blob can name where it lives.</summary>
    private static Dictionary<long, string> BuildMarkToPath(FastExportIndex index)
    {
        var map = new Dictionary<long, string>();
        foreach (var commit in index.Commits.Values)
            foreach (var modify in commit.FileModifies)
                if (modify.MarkRef is { } mark && !map.ContainsKey(mark))
                    map[mark] = modify.Path.ToString();
        return map;
    }

    /// <summary>
    /// Every distinct path present in the target's file commands. Paths are never rewritten
    /// by this stage, so a needle in a filename survives with no other signal — the scrub
    /// scans these directly.
    /// </summary>
    private static List<(byte[] Bytes, string Text)> CollectPaths(FastExportIndex index)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<(byte[], string)>();
        void Add(GitPath path)
        {
            if (seen.Add(path.ToString()))
                paths.Add((path.PathBytes, path.ToString()));
        }
        foreach (var commit in index.Commits.Values)
            foreach (var command in commit.Record.FileCommands)
                switch (command)
                {
                    case FileModify modify: Add(modify.Path); break;
                    case FileRename rename: Add(rename.Source); Add(rename.Destination); break;
                    case FileCopy copy: Add(copy.Source); Add(copy.Destination); break;
                    case FileDelete delete: Add(delete.Path); break;
                }
        return paths;
    }

    /// <summary>
    /// A commit's root tree changes exactly when its snapshot references a transformed
    /// blob, so comparing old and new tree oids identifies content-touched commits
    /// without simulating snapshots. Descendants of a changed commit keep their own
    /// trees unless their snapshots also changed.
    /// </summary>
    private async Task<List<string>> FindChangedTreesAsync(
        HistoryRewriteRequest request, Dictionary<string, string> commitMap, CancellationToken ct)
    {
        var oldTrees = await ResolveTreesAsync(request.SourceRepository, commitMap.Keys.ToList(), request, ct);
        var newTrees = await ResolveTreesAsync(request.TargetBareRepository, commitMap.Values.Distinct().ToList(), request, ct);

        var changed = new List<string>();
        foreach (var (oldOid, newOid) in commitMap)
            if (!string.Equals(oldTrees[oldOid], newTrees[newOid], StringComparison.Ordinal))
                changed.Add(oldOid);
        return changed;
    }

    private async Task<Dictionary<string, string>> ResolveTreesAsync(
        string repository, IReadOnlyList<string> commitOids, HistoryRewriteRequest request, CancellationToken ct)
    {
        var trees = new Dictionary<string, string>(commitOids.Count, StringComparer.Ordinal);
        foreach (var chunk in Chunk(commitOids))
        {
            var args = new List<string> { "rev-parse" };
            args.AddRange(chunk.Select(oid => oid + "^{tree}"));
            var result = await ProcessRunner.RunAsync(
                _gitExe, args, repository, request.VerificationTimeout, GitEnvironment, ct);
            if (!result.Success)
                throw new HistoryPipelineException("verify", $"rev-parse ^{{tree}} batch failed in '{repository}'", result.ExitCode, result.StdErr);

            var lines = SplitLines(result.StdOut);
            if (lines.Count != chunk.Count)
                throw new HistoryPipelineException("verify", $"rev-parse returned {lines.Count} lines for {chunk.Count} commits in '{repository}'");
            for (var i = 0; i < chunk.Count; i++)
                trees[chunk[i]] = lines[i];
        }
        return trees;
    }

    /// <summary>
    /// The in-scope commit set, path filter, and git-grep pathspecs a scoped scrub greps
    /// within. A scoped scrub is honest about being partial: it greps only the in-scope
    /// commits and paths, and every check it produces is flagged
    /// <see cref="ScrubCheckResult.WithinScopeOnly"/> so an empty hit list can never be read
    /// as a global clean bill.
    /// </summary>
    private sealed class ScrubScope
    {
        public bool ContentScoped { get; }
        public bool PathScoped { get; }
        public bool CommitScoped { get; }

        /// <summary>
        /// Out-of-scope commits whose tree still differs. Under a commit scope this is
        /// expected, not a defect: a git snapshot inherits, so a rewrite inside an in-scope
        /// commit propagates to every descendant that does not re-touch the path.
        /// </summary>
        public int OutOfScopeChangedTrees { get; }

        public List<string> InScopeCommits { get; }
        public IReadOnlyList<string> PathSpecs { get; }
        public Func<string, bool> PathInScope { get; }

        public ScrubScope(
            RewriteOptions rewrite, HashSet<string>? inScopeCommitOids,
            Dictionary<string, string> commitMap, IReadOnlyList<string> changedTreeOids)
        {
            PathScoped = !rewrite.FileScope.IsAllFiles;
            CommitScoped = inScopeCommitOids is not null;
            ContentScoped = PathScoped || CommitScoped;
            OutOfScopeChangedTrees = inScopeCommitOids is null
                ? 0
                : changedTreeOids.Count(oid => !inScopeCommitOids.Contains(oid.ToLowerInvariant()));
            InScopeCommits = inScopeCommitOids is null
                ? commitMap.Values.Distinct().ToList()
                : inScopeCommitOids.Where(commitMap.ContainsKey).Select(o => commitMap[o]).Distinct().ToList();
            PathInScope = rewrite.FileScope.Matches;
            PathSpecs = rewrite.FileScope switch
            {
                ExplicitPathsScope paths => paths.Paths.Select(PathGlob.Normalize).ToList(),
                GlobScope globs => globs.Patterns.Select(p => $":(glob){p}").ToList(),
                _ => []
            };
        }
    }

    /// <summary>
    /// Verifies each op's needle against the rewritten history. Coverage is honest by
    /// construction: git grep runs over every in-scope commit (a survivor rides a commit
    /// whose tree did not change, so a tips-plus-changed candidate set would miss exactly the
    /// survival case), the byte-level fallback catches literal needles inside skipped blobs
    /// git grep -I cannot read, and paths are scanned for needles no content grep sees. A
    /// check reports <see cref="ScrubCheckResult.Complete"/> only when nothing was sampled,
    /// skipped, grep-rejected, or left outside a scope. Message and identity ops are verified
    /// in-process against the rewritten messages/headers. This method never throws — a
    /// completed rewrite must always be reportable, so a failed check degrades to a note.
    /// </summary>
    private async Task<List<ScrubCheckResult>> RunScrubChecksAsync(
        HistoryRewriteRequest request, Dictionary<string, string> commitMap,
        IReadOnlyList<BinarySkip> binarySkips, IReadOnlyList<(LiteralReplace Op, long? Mark, long Size)> byteSurvivors,
        IReadOnlyList<(byte[] Bytes, string Text)> paths, ScrubScope scope, CancellationToken ct)
    {
        var allCommits = scope.InScopeCommits;
        var commits = allCommits;
        var sampled = false;
        if (allCommits.Count > ScrubSampleCap)
        {
            commits = SampleEvenly(allCommits, ScrubSampleCap);
            sampled = true;
        }
        var hasSkips = binarySkips.Count > 0;

        // Only in-scope paths belong in the content scrub — an out-of-scope path carrying the
        // needle is a deliberate survivor, surfaced by the scope note, not a scrub failure.
        var scopedPaths = paths.Where(p => scope.PathInScope(p.Text)).ToList();

        var checks = new List<ScrubCheckResult>();
        foreach (var op in request.Rewrite.ContentOps)
        {
            try
            {
                switch (op)
                {
                    case LiteralReplace literal:
                        var literalExtras = scopedPaths
                            .Where(p => p.Bytes.AsSpan().IndexOf(literal.Find) >= 0)
                            .Select(p => $"path: {p.Text}")
                            .Concat(byteSurvivors
                                .Where(b => ReferenceEquals(b.Op, literal))
                                .Select(b => $"binary-blob mark :{b.Mark?.ToString() ?? "?"}: {b.Size} byte(s) carry the needle"))
                            .ToList();
                        if (!TryDescribeNeedle(literal.Find, out var needle))
                        {
                            checks.Add(Make("literal", Convert.ToHexString(literal.Find),
                                new GrepOutcome(false, [], "needle is not expressible as a single-line UTF-8 grep argument"),
                                literalExtras));
                            break;
                        }
                        checks.Add(Make("literal", needle,
                            await GrepAsync(request, ["grep", "-I", "--fixed-strings", "-e", needle], commits, scope.PathSpecs, ct),
                            literalExtras));
                        break;

                    case RegexReplace regex:
                        var regexExtras = MatchPaths(scopedPaths, regex);
                        if (!IsEreExpressible(regex, out var flags, out var why))
                        {
                            checks.Add(Make("regex", regex.Pattern, new GrepOutcome(false, [], why), regexExtras));
                            break;
                        }
                        List<string> grepArgs = ["grep", "-I", "-E", .. flags, "-e", regex.Pattern];
                        checks.Add(Make("regex", regex.Pattern, await GrepAsync(request, grepArgs, commits, scope.PathSpecs, ct), regexExtras));
                        break;
                }
            }
            catch (Exception ex)
            {
                var (kind, needle) = op is RegexReplace r
                    ? ("regex", r.Pattern)
                    : ("literal", Convert.ToHexString(((LiteralReplace)op).Find));
                checks.Add(NoteOnly(kind, needle, $"scrub check could not run: {ex.Message}"));
            }
        }

        checks.AddRange(await MessageScrubChecksAsync(request, ct));
        checks.AddRange(await IdentityScrubChecksAsync(request, ct));
        return checks;

        ScrubCheckResult Make(string kind, string needle, GrepOutcome grep, IReadOnlyList<string> extraHits)
        {
            var hits = new List<string>(grep.Hits);
            hits.AddRange(extraHits);

            var notes = new List<string>();
            if (grep.Note is { } gn) notes.Add(gn);
            if (grep.Performed && sampled)
                notes.Add($"sampled {commits.Count} of {allCommits.Count} commit(s); unsampled commits were not grepped");
            if (hasSkips)
                notes.Add($"{binarySkips.Count} blob(s) the transform skipped are invisible to git grep");
            if (scope.ContentScoped)
            {
                var scopeNote = new List<string> { $"scrubbed within scope only ({allCommits.Count} in-scope commit(s))" };
                if (scope.PathScoped)
                    scopeNote.Add("occurrences at out-of-scope paths are intentionally retained, not cleaned");
                if (scope.CommitScoped)
                    scopeNote.Add($"{scope.OutOfScopeChangedTrees} out-of-scope commit(s) have a changed tree: a rewrite inside an in-scope commit is inherited by every descendant that does not re-touch the path");
                notes.Add(string.Join("; ", scopeNote));
            }

            return new ScrubCheckResult
            {
                Kind = kind,
                Needle = needle,
                Performed = grep.Performed,
                Complete = grep.Performed && !sampled && !hasSkips && !scope.ContentScoped,
                WithinScopeOnly = scope.ContentScoped,
                CommitsChecked = grep.Performed ? commits.Count : 0,
                Hits = hits,
                Note = notes.Count > 0 ? string.Join("; ", notes) : null
            };
        }
    }

    private static ScrubCheckResult NoteOnly(string kind, string needle, string note) => new()
    {
        Kind = kind,
        Needle = needle,
        Performed = false,
        Complete = false,
        WithinScopeOnly = false,
        CommitsChecked = 0,
        Hits = [],
        Note = note
    };

    /// <summary>
    /// Verifies message ops in-process against every rewritten commit and tag message. The
    /// message corpus is small, so the real op (literal byte search or the actual .NET regex)
    /// is applied directly — more faithful than the git-grep ERE gate the tree scrub needs.
    /// </summary>
    private async Task<List<ScrubCheckResult>> MessageScrubChecksAsync(HistoryRewriteRequest request, CancellationToken ct)
    {
        var checks = new List<ScrubCheckResult>();
        if (request.Rewrite.MessageOps.Count == 0) return checks;

        string corpus;
        try
        {
            corpus = await FetchMessageCorpusAsync(request, ct);
        }
        catch (Exception ex)
        {
            foreach (var op in request.Rewrite.MessageOps)
                checks.Add(NoteOnly(OpKind(op, "message"), OpNeedle(op), $"message scrub could not read the target: {ex.Message}"));
            return checks;
        }

        foreach (var op in request.Rewrite.MessageOps)
        {
            var hits = SurvivorsIn(corpus, op, "message");
            checks.Add(new ScrubCheckResult
            {
                Kind = OpKind(op, "message"),
                Needle = OpNeedle(op),
                Performed = true,
                Complete = true,
                WithinScopeOnly = false,
                CommitsChecked = 0,
                Hits = hits,
                Note = "messages verified in-process across all commits and tags"
            });
        }
        return checks;
    }

    private async Task<List<ScrubCheckResult>> IdentityScrubChecksAsync(HistoryRewriteRequest request, CancellationToken ct)
    {
        var checks = new List<ScrubCheckResult>();
        if (request.Rewrite.IdentityMappings.Count == 0) return checks;

        List<(string Name, string Email)> identities;
        try
        {
            identities = await FetchIdentitiesAsync(request, ct);
        }
        catch (Exception ex)
        {
            foreach (var mapping in request.Rewrite.IdentityMappings)
                checks.Add(NoteOnly("identity", DescribeMapping(mapping), $"identity scrub could not read the target: {ex.Message}"));
            return checks;
        }

        foreach (var mapping in request.Rewrite.IdentityMappings)
        {
            var hits = identities
                .Where(id => (mapping.OldName is null || mapping.OldName == id.Name)
                          && (mapping.OldEmail is null || mapping.OldEmail == id.Email)
                          && ((mapping.NewName is { } nn && nn != id.Name) || (mapping.NewEmail is { } ne && ne != id.Email)))
                .Select(id => $"identity survives: {id.Name} <{id.Email}>")
                .Distinct()
                .ToList();
            checks.Add(new ScrubCheckResult
            {
                Kind = "identity",
                Needle = DescribeMapping(mapping),
                Performed = true,
                Complete = true,
                WithinScopeOnly = false,
                CommitsChecked = 0,
                Hits = hits,
                Note = "author/committer/tagger identities verified in-process across all history"
            });
        }
        return checks;
    }

    private static string OpKind(ContentOp op, string prefix) => op is RegexReplace ? $"{prefix}-regex" : $"{prefix}-literal";

    private static string OpNeedle(ContentOp op) => op switch
    {
        RegexReplace r => r.Pattern,
        LiteralReplace l => System.Text.Unicode.Utf8.IsValid(l.Find) ? Encoding.UTF8.GetString(l.Find) : Convert.ToHexString(l.Find),
        _ => "?"
    };

    private static string DescribeMapping(IdentityMapping m) =>
        $"{m.OldName ?? "*"} <{m.OldEmail ?? "*"}> => {m.NewName ?? "(same)"} <{m.NewEmail ?? "(same)"}>";

    private static List<string> SurvivorsIn(string corpus, ContentOp op, string label)
    {
        switch (op)
        {
            case LiteralReplace literal when System.Text.Unicode.Utf8.IsValid(literal.Find):
                var needle = Encoding.UTF8.GetString(literal.Find);
                return corpus.Contains(needle, StringComparison.Ordinal)
                    ? [$"{label} carries the needle: {needle}"] : [];
            case LiteralReplace:
                return [];
            case RegexReplace regex:
                var compiled = new System.Text.RegularExpressions.Regex(
                    regex.Pattern, regex.Options | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                return compiled.IsMatch(corpus) ? [$"{label} matches pattern: {regex.Pattern}"] : [];
            default:
                return [];
        }
    }

    private async Task<string> FetchMessageCorpusAsync(HistoryRewriteRequest request, CancellationToken ct)
    {
        var commitMessages = await ProcessRunner.RunAsync(
            _gitExe, ["log", "--all", "-z", "--format=%B"], request.TargetBareRepository,
            request.VerificationTimeout, GitEnvironment, ct);
        if (!commitMessages.Success)
            throw new HistoryPipelineException("verify", "git log for message scrub failed", commitMessages.ExitCode, commitMessages.StdErr);
        var tagMessages = await ProcessRunner.RunAsync(
            _gitExe, ["for-each-ref", "refs/tags", "--format=%(contents)"], request.TargetBareRepository,
            request.VerificationTimeout, GitEnvironment, ct);
        return commitMessages.StdOut + "\n" + (tagMessages.Success ? tagMessages.StdOut : "");
    }

    private async Task<List<(string Name, string Email)>> FetchIdentitiesAsync(HistoryRewriteRequest request, CancellationToken ct)
    {
        var log = await ProcessRunner.RunAsync(
            _gitExe, ["log", "--all", "--format=%an%x1f%ae%x1f%cn%x1f%ce"], request.TargetBareRepository,
            request.VerificationTimeout, GitEnvironment, ct);
        if (!log.Success)
            throw new HistoryPipelineException("verify", "git log for identity scrub failed", log.ExitCode, log.StdErr);

        var identities = new List<(string, string)>();
        foreach (var line in SplitLines(log.StdOut))
        {
            var f = line.Split('\x1f');
            if (f.Length == 4)
            {
                identities.Add((f[0], f[1]));
                identities.Add((f[2], f[3]));
            }
        }

        var taggers = await ProcessRunner.RunAsync(
            _gitExe, ["for-each-ref", "refs/tags", "--format=%(taggername)%1f%(taggeremail)"],
            request.TargetBareRepository, request.VerificationTimeout, GitEnvironment, ct);
        if (taggers.Success)
            foreach (var line in SplitLines(taggers.StdOut))
            {
                var f = line.Split('\x1f');
                if (f.Length == 2 && f[0].Length > 0)
                    identities.Add((f[0], f[1].Trim('<', '>')));
            }
        return identities;
    }

    /// <summary>Decoded paths matching a regex op — a filename the pattern would hit, which content grep never sees.</summary>
    private static List<string> MatchPaths(IReadOnlyList<(byte[] Bytes, string Text)> paths, RegexReplace regex)
    {
        var compiled = new System.Text.RegularExpressions.Regex(
            regex.Pattern, regex.Options | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return paths.Where(p => compiled.IsMatch(p.Text)).Select(p => $"path: {p.Text}").ToList();
    }

    /// <summary>Result of one scrub grep. <see cref="Performed"/> is false when git grep could not run the pattern (rejected or timed out).</summary>
    private readonly record struct GrepOutcome(bool Performed, List<string> Hits, string? Note);

    private async Task<GrepOutcome> GrepAsync(
        HistoryRewriteRequest request, IReadOnlyList<string> grepArgs,
        IReadOnlyList<string> commits, IReadOnlyList<string> pathSpecs, CancellationToken ct)
    {
        var hits = new List<string>();
        var overflow = 0;
        foreach (var chunk in Chunk(commits))
        {
            var args = new List<string>(grepArgs.Count + chunk.Count + pathSpecs.Count + 1);
            args.AddRange(grepArgs);
            args.AddRange(chunk);
            // A pathspec after `--` narrows the grep to in-scope paths only.
            if (pathSpecs.Count > 0)
            {
                args.Add("--");
                args.AddRange(pathSpecs);
            }
            var result = await ProcessRunner.RunAsync(
                _gitExe, args, request.TargetBareRepository, request.VerificationTimeout, GitEnvironment, ct);
            // git grep: 0 = matches found, 1 = no matches, anything else means the grep
            // itself could not run (a pattern .NET accepts but ERE rejects, a timeout) —
            // that is not a proof of clean, so the check is marked not-performed, never
            // aborting a completed rewrite.
            if (result.ExitCode == 0)
            {
                foreach (var line in SplitLines(result.StdOut))
                {
                    if (hits.Count < ScrubHitCap) hits.Add(line);
                    else overflow++;
                }
            }
            else if (result.ExitCode != 1 || result.TimedOut)
            {
                var reason = result.TimedOut ? "git grep timed out" : $"git grep exited {result.ExitCode}: {result.StdErr.Trim()}";
                return new GrepOutcome(false, hits, reason);
            }
        }

        return new GrepOutcome(true, hits, overflow > 0 ? $"{overflow} further hit(s) not listed" : null);
    }

    private static bool TryDescribeNeedle(byte[] find, out string needle)
    {
        needle = "";
        if (!System.Text.Unicode.Utf8.IsValid(find)) return false;
        var text = Encoding.UTF8.GetString(find);
        // NUL cannot survive argv, and a newline splits the grep line model.
        if (text.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0) return false;
        needle = text;
        return true;
    }

    /// <summary>
    /// Conservative .NET-to-POSIX-ERE compatibility gate for the scrub grep: only
    /// constructs both engines read identically pass. Alphanumeric escapes (\d, \b, …),
    /// inline groups (?…), and control characters in the pattern all disqualify; the
    /// check then skips with a note instead of grepping a lie.
    /// </summary>
    private static bool IsEreExpressible(RegexReplace regex, out List<string> flags, out string why)
    {
        flags = [];
        why = "";
        var unsupportedOptions = regex.Options
            & ~(System.Text.RegularExpressions.RegexOptions.IgnoreCase
              | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (unsupportedOptions != System.Text.RegularExpressions.RegexOptions.None)
        {
            why = $"regex options {unsupportedOptions} have no git grep equivalent; scrub grep skipped";
            return false;
        }
        if (regex.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            flags.Add("-i");

        var pattern = regex.Pattern;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c is '\0' or '\n' or '\r')
            {
                why = "pattern contains control characters grep arguments cannot carry; scrub grep skipped";
                return false;
            }
            if (c == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?')
            {
                why = "pattern uses (?…) constructs POSIX ERE lacks; scrub grep skipped";
                return false;
            }
            // POSIX character classes [:…:], collating [.….], and equivalence [=…=]
            // are literal bracket contents to .NET but structural to ERE, so the two
            // engines read the same bracket expression differently — grepping the ERE
            // reading would verify the wrong thing.
            if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] is ':' or '.' or '=')
            {
                why = "pattern uses a POSIX class/collating/equivalence bracket ERE and .NET read differently; scrub grep skipped";
                return false;
            }
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length) { why = "pattern ends in a bare backslash"; return false; }
                var escaped = pattern[++i];
                if (char.IsLetterOrDigit(escaped) || escaped is '<' or '>')
                {
                    why = $"escape \\{escaped} differs between .NET regex and POSIX ERE; scrub grep skipped";
                    return false;
                }
            }
        }
        return true;
    }

    private static List<string> SplitLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

    private static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> items)
    {
        for (var i = 0; i < items.Count; i += ShaChunk)
            yield return items.Skip(i).Take(ShaChunk).ToList();
    }

    private static List<string> SampleEvenly(List<string> commits, int cap)
    {
        var sampled = new List<string>(cap);
        for (var i = 0; i < cap; i++)
            sampled.Add(commits[(int)((long)i * commits.Count / cap)]);
        return sampled;
    }
}
