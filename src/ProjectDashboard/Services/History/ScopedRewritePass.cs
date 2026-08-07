using System.IO;
using System.Text;

namespace ProjectDashboard.Services.History;

/// <summary>Everything one scoped transform pass observed, feeding the report and the commit map.</summary>
public sealed class ScopedRewriteOutcome
{
    public int BlobsChanged;
    public long BytesDelta;

    /// <summary>In-scope blob versions the transform could not scrub (binary or over-limit).</summary>
    public readonly List<(long? Mark, long Size, string Reason)> Skips = [];

    /// <summary>Literal needles found verbatim inside a skipped in-scope blob — a survivor git grep -I cannot see.</summary>
    public readonly List<(LiteralReplace Op, long? Mark, long Size)> ByteSurvivors = [];

    /// <summary>Shared blobs split: the original mark stayed for out-of-scope refs, a fresh mark carries the rewrite for in-scope refs.</summary>
    public readonly List<(long OldMark, long NewMark, string Path)> Splits = [];

    public int MessagesChanged;
    public int IdentitiesRewritten;
    public int FileCommandsRemoved;

    /// <summary>Pruned commit mark to the surviving mark its children were rewired onto.</summary>
    public readonly Dictionary<long, long> PrunedMarkToSurvivingMark = [];
}

/// <summary>
/// A scope-aware rewrite over parsed records. Content ops touch only blob versions reachable
/// through an in-scope (path ∩ commit) file command; a blob shared by in- and out-of-scope
/// references is split — the original mark is left for out-of-scope refs, and a freshly minted
/// mark (above <see cref="FastExportIndex.MaxMark"/>) carries the rewrite, with only the
/// in-scope M-lines repointed. Message and identity rewrites run across all history. Purge
/// drops matching file commands and prunes commits left empty where it is safe to rewire.
/// </summary>
public sealed class ScopedRewritePass
{
    private readonly ParsedExport _parsed;
    private readonly BlobTransformer _contentTransformer;
    private readonly IReadOnlyList<LiteralReplace> _contentLiteralOps;
    private readonly BlobTransformer? _messageTransformer;
    private readonly IReadOnlyList<IdentityMapping> _identityMappings;
    private readonly FileScope _fileScope;
    private readonly HashSet<string>? _inScopeCommitOids;
    private readonly PurgeSpec? _purge;
    private readonly long _changedPayloadCeiling;

    private long _nextMark;

    public ScopedRewritePass(
        ParsedExport parsed,
        BlobTransformer contentTransformer,
        IReadOnlyList<LiteralReplace> contentLiteralOps,
        BlobTransformer? messageTransformer,
        IReadOnlyList<IdentityMapping> identityMappings,
        FileScope fileScope,
        HashSet<string>? inScopeCommitOids,
        PurgeSpec? purge,
        long changedPayloadCeiling)
    {
        _parsed = parsed;
        _contentTransformer = contentTransformer;
        _contentLiteralOps = contentLiteralOps;
        _messageTransformer = messageTransformer;
        _identityMappings = identityMappings;
        _fileScope = fileScope;
        _inScopeCommitOids = inScopeCommitOids;
        _purge = purge;
        _changedPayloadCeiling = changedPayloadCeiling;
        _nextMark = parsed.Index.MaxMark;
    }

    private bool CommitInScope(CommitRecord commit) =>
        _inScopeCommitOids is null
        || (commit.OriginalOid is { } oid && _inScopeCommitOids.Contains(oid.ToLowerInvariant()));

    public ScopedRewriteOutcome Run(CancellationToken ct)
    {
        var outcome = new ScopedRewriteOutcome();

        RefuseUnscrubbableRefs();
        var blobRefs = ClassifyBlobReferences();
        TransformBlobs(blobRefs, outcome, ct);
        if (_messageTransformer is not null)
            RewriteMessages(outcome, ct);
        if (_identityMappings.Count > 0)
            RewriteIdentities(outcome, ct);
        if (_purge is not null)
            ApplyPurge(outcome, ct);

        return outcome;
    }

    /// <summary>The all-files pass refuses inline/oid file payloads; scoped runs inherit the same guard so unseeable content is never silently kept.</summary>
    private void RefuseUnscrubbableRefs()
    {
        foreach (var record in _parsed.Records)
            if (record is CommitRecord commit)
                foreach (var fileModify in commit.FileCommands.OfType<FileModify>())
                {
                    if (fileModify.IsInline)
                        throw new NotSupportedException(
                            "a stream carrying inline file payloads cannot be rewritten by this stage");
                    if (fileModify.MarkRef is null && fileModify.Mode != "160000")
                        throw new NotSupportedException(
                            $"filemodify references content by oid ('{Encoding.UTF8.GetString(fileModify.RawLine)}') — content outside the stream cannot be scrubbed");
                }
    }

    private sealed class BlobRefInfo
    {
        public readonly List<FileModify> InScopeRefs = [];
        public int OutOfScopeRefs;
        public string? AnyPath;
    }

    /// <summary>Per blob mark, the in-scope M-lines and a count of out-of-scope ones — the split decision hinges on both being non-zero.</summary>
    private Dictionary<long, BlobRefInfo> ClassifyBlobReferences()
    {
        var refs = new Dictionary<long, BlobRefInfo>();
        foreach (var record in _parsed.Records)
        {
            if (record is not CommitRecord commit) continue;
            var commitInScope = CommitInScope(commit);
            foreach (var fm in commit.FileCommands.OfType<FileModify>())
            {
                if (fm.MarkRef is not { } mark) continue;
                if (!refs.TryGetValue(mark, out var info))
                    refs[mark] = info = new BlobRefInfo();
                info.AnyPath ??= fm.Path.ToString();
                if (commitInScope && _fileScope.Matches(fm.Path.ToString()))
                    info.InScopeRefs.Add(fm);
                else
                    info.OutOfScopeRefs++;
            }
        }
        return refs;
    }

    private void TransformBlobs(Dictionary<long, BlobRefInfo> blobRefs, ScopedRewriteOutcome outcome, CancellationToken ct)
    {
        using var spool = new FileStream(
            _parsed.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);

        // Splits are collected then spliced so list indices stay stable during the walk.
        var insertAfter = new Dictionary<BlobRecord, List<BlobRecord>>();
        long changedBytes = 0;

        foreach (var record in _parsed.Records)
        {
            ct.ThrowIfCancellationRequested();
            if (record is not BlobRecord blob || blob.Mark is not { } mark) continue;
            if (!blobRefs.TryGetValue(mark, out var info) || info.InScopeRefs.Count == 0) continue;

            var slice = blob.Data.SourceSlice;
            if (slice.Length > int.MaxValue)
                throw new NotSupportedException(
                    $"a {slice.Length}-byte blob cannot be materialized for transformation (2 GiB payload ceiling)");
            if (_contentTransformer.HasRegexOp && slice.Length > _contentTransformer.RegexPayloadLimit)
            {
                outcome.Skips.Add((mark, slice.Length,
                    $"exceeds the {_contentTransformer.RegexPayloadLimit}-byte regex transform limit"));
                continue;
            }

            var payload = ReadSlice(spool, slice);
            var result = _contentTransformer.Transform(payload);
            switch (result.Class)
            {
                case TransformClass.BinarySkipped:
                    outcome.Skips.Add((mark, slice.Length, "not valid UTF-8"));
                    foreach (var op in _contentLiteralOps)
                        if (payload.AsSpan().IndexOf(op.Find) >= 0)
                            outcome.ByteSurvivors.Add((op, mark, slice.Length));
                    break;

                case TransformClass.Changed:
                    var newBytes = result.Bytes!;
                    changedBytes += newBytes.LongLength;
                    if (changedBytes > _changedPayloadCeiling)
                        throw new HistoryPipelineException(
                            "transform",
                            $"changed payloads reached {changedBytes} bytes, past the {_changedPayloadCeiling}-byte ceiling — rewrite refused before exhausting memory");
                    outcome.BlobsChanged++;
                    outcome.BytesDelta += newBytes.LongLength - slice.Length;

                    if (info.OutOfScopeRefs == 0)
                    {
                        // Every reference is in scope: rewrite the shared blob in place.
                        blob.Data.InlineBytes = newBytes;
                    }
                    else
                    {
                        // Shared across scope boundary: keep the original for out-of-scope
                        // refs, mint a copy for the in-scope ones. A naive in-place rewrite
                        // here would corrupt out-of-scope history.
                        var newMark = ++_nextMark;
                        var copy = new BlobRecord
                        {
                            Mark = newMark,
                            Data = new DataBlock { InlineBytes = newBytes, TrailingLf = true }
                        };
                        if (!insertAfter.TryGetValue(blob, out var list))
                            insertAfter[blob] = list = [];
                        list.Add(copy);
                        foreach (var fm in info.InScopeRefs)
                            fm.Repoint(newMark);
                        outcome.Splits.Add((mark, newMark, info.AnyPath ?? FirstInScopePath(info)));
                    }
                    break;
            }
        }

        if (insertAfter.Count > 0)
            SpliceNewBlobs(insertAfter);
    }

    private static string FirstInScopePath(BlobRefInfo info) =>
        info.InScopeRefs.Count > 0 ? info.InScopeRefs[0].Path.ToString() : "(unknown)";

    private void SpliceNewBlobs(Dictionary<BlobRecord, List<BlobRecord>> insertAfter)
    {
        var rebuilt = new List<FastExportRecord>(_parsed.Records.Count + insertAfter.Sum(kv => kv.Value.Count));
        foreach (var record in _parsed.Records)
        {
            rebuilt.Add(record);
            if (record is BlobRecord blob && insertAfter.TryGetValue(blob, out var copies))
                rebuilt.AddRange(copies);
        }
        _parsed.Records.Clear();
        _parsed.Records.AddRange(rebuilt);
    }

    private void RewriteMessages(ScopedRewriteOutcome outcome, CancellationToken ct)
    {
        foreach (var record in _parsed.Records)
        {
            ct.ThrowIfCancellationRequested();
            DataBlock? message = record switch
            {
                CommitRecord c => c.Message,
                TagRecord t => t.Message,
                _ => null
            };
            if (message?.InlineBytes is not { } bytes) continue;
            var result = _messageTransformer!.Transform(bytes);
            if (result.Class == TransformClass.Changed)
            {
                message.InlineBytes = result.Bytes;
                outcome.MessagesChanged++;
            }
        }
    }

    private void RewriteIdentities(ScopedRewriteOutcome outcome, CancellationToken ct)
    {
        foreach (var record in _parsed.Records)
        {
            ct.ThrowIfCancellationRequested();
            List<byte[]>? headers = record switch
            {
                CommitRecord c => c.HeaderLines,
                TagRecord t => t.HeaderLines,
                _ => null
            };
            if (headers is null) continue;
            for (var i = 0; i < headers.Count; i++)
                if (IdentityHeader.TryRewrite(headers[i], _identityMappings, out var rewritten))
                {
                    headers[i] = rewritten;
                    outcome.IdentitiesRewritten++;
                }
        }
    }

    private bool PurgeMatches(FileModify fm, long? blobSize)
    {
        if (_purge!.Paths is { } paths && paths.Matches(fm.Path.ToString())) return true;
        if (_purge.MinBlobSize is { } min && blobSize is { } size && size >= min) return true;
        return false;
    }

    private void ApplyPurge(ScopedRewriteOutcome outcome, CancellationToken ct)
    {
        var blobs = _parsed.Index.Blobs;
        foreach (var record in _parsed.Records)
        {
            ct.ThrowIfCancellationRequested();
            if (record is not CommitRecord commit || !CommitInScope(commit)) continue;
            var removed = commit.FileCommands.RemoveAll(cmd =>
            {
                switch (cmd)
                {
                    case FileModify fm:
                        long? size = fm.MarkRef is { } m && blobs.TryGetValue(m, out var e) ? e.Payload.Length : null;
                        return PurgeMatches(fm, size);
                    case FileDelete del:
                        return _purge!.Paths is { } p1 && p1.Matches(del.Path.ToString());
                    case FileRename ren:
                        return _purge!.Paths is { } p2 && p2.Matches(ren.Destination.ToString());
                    case FileCopy cp:
                        return _purge!.Paths is { } p3 && p3.Matches(cp.Destination.ToString());
                    default:
                        return false;
                }
            });
            outcome.FileCommandsRemoved += removed;
        }

        PruneEmptyCommits(outcome, ct);
    }

    /// <summary>
    /// Prunes commits left with no file commands, but only where rewiring is unambiguous: a
    /// non-merge commit with a single mark parent, referenced only as a commit parent (never
    /// by a tag or reset), and not a branch tip (it has at least one child). Children are
    /// rewired to the pruned commit's parent, transitively; merge parents that collapse to a
    /// duplicate are de-duplicated. Tips, roots, merges, and tag/reset targets are left as
    /// valid empty commits rather than risk a broken ref graph.
    /// </summary>
    private void PruneEmptyCommits(ScopedRewriteOutcome outcome, CancellationToken ct)
    {
        var commitsByMark = new Dictionary<long, CommitRecord>();
        var childCount = new Dictionary<long, int>();
        var externallyReferenced = new HashSet<long>();

        foreach (var record in _parsed.Records)
            switch (record)
            {
                case CommitRecord c when c.Mark is { } m:
                    commitsByMark[m] = c;
                    break;
                case TagRecord { FromRef: { } from }:
                    if (ParseMarkRef(from) is { } tm) externallyReferenced.Add(tm);
                    break;
                case ResetRecord { FromRef: { } rfrom }:
                    if (ParseMarkRef(rfrom) is { } rm) externallyReferenced.Add(rm);
                    break;
            }

        foreach (var c in commitsByMark.Values)
            foreach (var parent in c.Parents)
                if (ParseMarkRef(parent.DataRef) is { } pm)
                    childCount[pm] = childCount.GetValueOrDefault(pm) + 1;

        // Candidate marks in stream order so transitive replacement resolves parents first.
        var replacement = new Dictionary<long, byte[]>();
        var pruned = new HashSet<long>();
        foreach (var record in _parsed.Records)
        {
            if (record is not CommitRecord commit || commit.Mark is not { } mark) continue;
            if (commit.FileCommands.Count != 0) continue;
            if (commit.Parents.Count != 1 || commit.Parents[0].IsMerge) continue;
            if (ParseMarkRef(commit.Parents[0].DataRef) is null) continue;
            if (externallyReferenced.Contains(mark)) continue;
            if (childCount.GetValueOrDefault(mark) == 0) continue; // a branch tip — keep

            var parentRef = commit.Parents[0].DataRef;
            // Follow the parent through any already-pruned ancestor.
            if (ParseMarkRef(parentRef) is { } pmark && replacement.TryGetValue(pmark, out var resolved))
                parentRef = resolved;
            replacement[mark] = parentRef;
            pruned.Add(mark);
        }

        if (pruned.Count == 0) return;

        // Rewire every surviving commit's parents through the replacement map, de-duplicating.
        foreach (var record in _parsed.Records)
        {
            if (record is not CommitRecord commit || commit.Mark is not { } mark || pruned.Contains(mark)) continue;
            RewireParents(commit, replacement);
        }

        // Record pruned-oid → surviving-mark for the commit map, resolving each chain to a mark.
        foreach (var (prunedMark, resolvedRef) in replacement)
            if (ParseMarkRef(resolvedRef) is { } survivingMark)
                outcome.PrunedMarkToSurvivingMark[prunedMark] = survivingMark;

        RemovePrunedRecords(pruned);
    }

    private static void RewireParents(CommitRecord commit, Dictionary<long, byte[]> replacement)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<ParentLink>(commit.Parents.Count);
        foreach (var parent in commit.Parents)
        {
            var dataRef = parent.DataRef;
            if (ParseMarkRef(dataRef) is { } m && replacement.TryGetValue(m, out var mapped))
                dataRef = mapped;
            if (seen.Add(Encoding.UTF8.GetString(dataRef)))
                kept.Add(new ParentLink { IsMerge = parent.IsMerge, DataRef = dataRef });
        }
        // A merge whose parents collapsed to one becomes a normal commit; its sole parent
        // must read as `from`, not `merge`.
        if (kept.Count == 1) kept[0] = new ParentLink { IsMerge = false, DataRef = kept[0].DataRef };
        commit.Parents.Clear();
        commit.Parents.AddRange(kept);
    }

    private void RemovePrunedRecords(HashSet<long> pruned)
    {
        var rebuilt = new List<FastExportRecord>(_parsed.Records.Count);
        for (var i = 0; i < _parsed.Records.Count; i++)
        {
            if (_parsed.Records[i] is CommitRecord c && c.Mark is { } m && pruned.Contains(m))
            {
                // Drop the commit's trailing blank separator with it, or the stream gains a
                // stray blank where fast-import expects a command.
                if (i + 1 < _parsed.Records.Count && _parsed.Records[i + 1] is BlankRecord) i++;
                continue;
            }
            rebuilt.Add(_parsed.Records[i]);
        }
        _parsed.Records.Clear();
        _parsed.Records.AddRange(rebuilt);
    }

    private static long? ParseMarkRef(byte[] dataRef) =>
        dataRef.Length > 1 && dataRef[0] == (byte)':' && Utf8Ascii.TryParseLong(dataRef.AsSpan(1), out var m) ? m : null;

    private static byte[] ReadSlice(FileStream spool, SpoolSlice slice)
    {
        var payload = new byte[slice.Length];
        spool.Seek(slice.Offset, SeekOrigin.Begin);
        spool.ReadExactly(payload);
        return payload;
    }
}
