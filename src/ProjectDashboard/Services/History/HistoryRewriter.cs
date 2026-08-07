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
/// for each op's needle across target tips and rewritten commits) and report. Changed
/// payloads are held in memory until import, so peak memory is the sum of changed payload
/// bytes plus one payload in flight; unchanged payloads stream from the spool untouched.
/// </summary>
public sealed class HistoryRewriter
{
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

    public HistoryRewriter(string? gitExecutable = null) =>
        _gitExe = gitExecutable ?? HistoryPipeline.ResolveGitExecutable();

    public async Task<RewriteReport> RunAsync(HistoryRewriteRequest request, CancellationToken ct = default)
    {
        request.Rewrite.Validate();

        var transformer = new BlobTransformer(request.Rewrite.ContentOps);
        var binarySkips = new List<BinarySkip>();
        var blobsChanged = 0;
        long bytesDelta = 0;

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
            TransformAsync = (parsed, token) =>
            {
                TransformBlobs(parsed, transformer, binarySkips, ref blobsChanged, ref bytesDelta, token);
                return Task.CompletedTask;
            }
        }, ct);

        var commitMap = BuildCommitMap(result);
        var changedTrees = await FindChangedTreesAsync(request, commitMap, ct);

        var fsck = await ProcessRunner.RunAsync(
            _gitExe, ["fsck", "--strict"], request.TargetBareRepository,
            request.VerificationTimeout, GitEnvironment, ct);
        if (!fsck.Success)
            throw new HistoryPipelineException(
                "verify", "fsck --strict failed on the rewrite target", fsck.ExitCode, fsck.StdErr + "\n" + fsck.StdOut);

        var scrubChecks = await RunScrubChecksAsync(request, commitMap, changedTrees, ct);

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
            ScrubChecks = scrubChecks
        };
        if (request.ReportPath is { } reportPath)
            await report.WriteAsync(reportPath, ct);
        return report;
    }

    /// <summary>
    /// One pass over the parsed records: each blob payload is read from the spool,
    /// transformed, and materialized as inline bytes only when the bytes changed.
    /// File commands that reference content the stream does not carry (inline payloads,
    /// blob oids) are refused — content the transform cannot see cannot be scrubbed, and
    /// the mandated export flags never produce those shapes.
    /// </summary>
    private static void TransformBlobs(
        ParsedExport parsed, BlobTransformer transformer, List<BinarySkip> binarySkips,
        ref int blobsChanged, ref long bytesDelta, CancellationToken ct)
    {
        using var spool = new FileStream(
            parsed.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);

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
                    var payload = ReadSlice(spool, slice);
                    var outcome = transformer.Transform(payload);
                    switch (outcome.Class)
                    {
                        case TransformClass.BinarySkipped:
                            binarySkips.Add(new BinarySkip(blob.Mark, slice.Length));
                            break;
                        case TransformClass.Changed:
                            blob.Data.InlineBytes = outcome.Bytes;
                            bytesDelta += outcome.Bytes!.LongLength - slice.Length;
                            blobsChanged++;
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

    /// <summary>Joins each commit's original oid (from --show-original-ids) with its imported oid (from --export-marks).</summary>
    private static Dictionary<string, string> BuildCommitMap(HistoryPipelineResult result)
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
            if (!marks.TryGetValue(commit.Mark, out var importedOid))
                throw new HistoryPipelineException("verify", $"commit mark :{commit.Mark} is missing from the import marks file");
            map[originalOid] = importedOid;
        }
        return map;
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

    private async Task<List<ScrubCheckResult>> RunScrubChecksAsync(
        HistoryRewriteRequest request, Dictionary<string, string> commitMap,
        List<string> changedTrees, CancellationToken ct)
    {
        var commits = await CollectScrubCommitsAsync(request, commitMap, changedTrees, ct);
        var candidateCount = commits.Count;
        string? sampleNote = null;
        if (candidateCount > ScrubSampleCap)
        {
            commits = SampleEvenly(commits, ScrubSampleCap);
            sampleNote = $"sampled {commits.Count} of {candidateCount} candidate commits";
        }

        var checks = new List<ScrubCheckResult>();
        foreach (var op in request.Rewrite.ContentOps)
        {
            switch (op)
            {
                case LiteralReplace literal:
                    if (!TryDescribeNeedle(literal.Find, out var needle))
                    {
                        checks.Add(Skipped("literal", Convert.ToHexString(literal.Find),
                            "needle is not expressible as a single-line UTF-8 grep argument"));
                        break;
                    }
                    checks.Add(await GrepAsync(request, "literal", needle,
                        ["grep", "-I", "--fixed-strings", "-e", needle], commits, sampleNote, ct));
                    break;

                case RegexReplace regex:
                    if (!IsEreExpressible(regex, out var flags, out var why))
                    {
                        checks.Add(Skipped("regex", regex.Pattern, why));
                        break;
                    }
                    List<string> grepArgs = ["grep", "-I", "-E", .. flags, "-e", regex.Pattern];
                    checks.Add(await GrepAsync(request, "regex", regex.Pattern, grepArgs, commits, sampleNote, ct));
                    break;
            }
        }
        return checks;

        static ScrubCheckResult Skipped(string kind, string needle, string why) => new()
        {
            Kind = kind,
            Needle = needle,
            Performed = false,
            CommitsChecked = 0,
            Hits = [],
            Note = why
        };
    }

    /// <summary>Every ref tip in the target (tags peeled to commits) plus the new oid of every content-changed commit.</summary>
    private async Task<List<string>> CollectScrubCommitsAsync(
        HistoryRewriteRequest request, Dictionary<string, string> commitMap,
        List<string> changedTrees, CancellationToken ct)
    {
        var refs = await ProcessRunner.RunAsync(
            _gitExe, ["for-each-ref", "--format=%(objecttype) %(objectname) %(*objecttype) %(*objectname)"],
            request.TargetBareRepository, request.VerificationTimeout, GitEnvironment, ct);
        if (!refs.Success)
            throw new HistoryPipelineException("verify", "for-each-ref failed on the rewrite target", refs.ExitCode, refs.StdErr);

        var commits = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(refs.StdOut))
        {
            var fields = line.Split(' ');
            var tip = fields[0] switch
            {
                "commit" => fields[1],
                "tag" when fields.Length >= 4 && fields[2] == "commit" => fields[3],
                _ => null
            };
            if (tip is not null && seen.Add(tip))
                commits.Add(tip);
        }
        foreach (var oldOid in changedTrees)
            if (seen.Add(commitMap[oldOid]))
                commits.Add(commitMap[oldOid]);
        return commits;
    }

    private async Task<ScrubCheckResult> GrepAsync(
        HistoryRewriteRequest request, string kind, string needle, IReadOnlyList<string> grepArgs,
        IReadOnlyList<string> commits, string? sampleNote, CancellationToken ct)
    {
        var hits = new List<string>();
        var overflow = 0;
        foreach (var chunk in Chunk(commits))
        {
            var args = new List<string>(grepArgs.Count + chunk.Count);
            args.AddRange(grepArgs);
            args.AddRange(chunk);
            var result = await ProcessRunner.RunAsync(
                _gitExe, args, request.TargetBareRepository, request.VerificationTimeout, GitEnvironment, ct);
            // git grep: 0 = matches found, 1 = no matches, anything else is a failure.
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
                throw new HistoryPipelineException("verify", $"git grep failed during the scrub check", result.ExitCode, result.StdErr);
            }
        }

        var notes = new List<string>();
        if (sampleNote is not null) notes.Add(sampleNote);
        if (overflow > 0) notes.Add($"{overflow} further hit(s) not listed");
        return new ScrubCheckResult
        {
            Kind = kind,
            Needle = needle,
            Performed = true,
            CommitsChecked = commits.Count,
            Hits = hits,
            Note = notes.Count > 0 ? string.Join("; ", notes) : null
        };
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
