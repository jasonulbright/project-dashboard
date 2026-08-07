namespace ProjectDashboard.Services.History;

/// <summary>One ref that differs between source and import target. A null object id means the ref is absent on that side.</summary>
public sealed record RefDifference(string RefName, string? SourceObjectId, string? TargetObjectId);

public sealed class IdentityVerificationResult
{
    public required bool RefSetsMatch { get; init; }
    public required bool FsckPassed { get; init; }
    public required int SourceRefCount { get; init; }
    public required int TargetRefCount { get; init; }
    public required IReadOnlyList<RefDifference> Differences { get; init; }
    public required IReadOnlyList<string> SourceRefLines { get; init; }
    public required IReadOnlyList<string> TargetRefLines { get; init; }
    public required string FsckOutput { get; init; }

    public bool Success => RefSetsMatch && FsckPassed;

    public string Describe()
    {
        if (Success) return $"identity proven over {SourceRefCount} refs; fsck --strict clean";
        var parts = new List<string>();
        if (!RefSetsMatch)
        {
            parts.Add($"{Differences.Count} ref difference(s):");
            foreach (var d in Differences)
                parts.Add($"  {d.RefName}: source={d.SourceObjectId ?? "(absent)"} target={d.TargetObjectId ?? "(absent)"}");
        }
        if (!FsckPassed)
            parts.Add($"fsck --strict failed: {FsckOutput.Trim()}");
        return string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// Proves round-trip identity: every ref name in the source resolves to the same object id
/// in the import target. Object ids cover all reachable content transitively, so equal ref
/// sets mean equal history, trees, blobs, and tag objects. The target additionally must
/// survive `git fsck --strict`.
/// </summary>
public static class IdentityVerifier
{
    private static readonly Dictionary<string, string> GitEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0"
    };

    public static async Task<IdentityVerificationResult> VerifyAsync(
        string gitExecutable, string sourceRepository, string targetRepository,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var sourceRefs = await ReadRefsAsync(gitExecutable, sourceRepository, timeout, ct);
        var targetRefs = await ReadRefsAsync(gitExecutable, targetRepository, timeout, ct);

        // for-each-ref excludes HEAD on both sides, so commits reachable only from a
        // detached HEAD are exported yet invisible to this comparison; the pipeline's
        // explicit HEAD alignment is what covers them.
        var differences = new List<RefDifference>();
        foreach (var (name, sourceId) in sourceRefs)
        {
            if (!targetRefs.TryGetValue(name, out var targetId))
                differences.Add(new RefDifference(name, sourceId, null));
            else if (!string.Equals(sourceId, targetId, StringComparison.Ordinal))
                differences.Add(new RefDifference(name, sourceId, targetId));
        }
        foreach (var (name, targetId) in targetRefs)
            if (!sourceRefs.ContainsKey(name))
                differences.Add(new RefDifference(name, null, targetId));

        var fsck = await ProcessRunner.RunAsync(
            gitExecutable, ["fsck", "--strict"], targetRepository, timeout, GitEnvironment, ct);
        var fsckOutput = (fsck.StdErr + "\n" + fsck.StdOut).Trim();

        return new IdentityVerificationResult
        {
            RefSetsMatch = differences.Count == 0,
            FsckPassed = fsck.Success,
            SourceRefCount = sourceRefs.Count,
            TargetRefCount = targetRefs.Count,
            Differences = differences,
            SourceRefLines = [.. sourceRefs.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} {r.Value}")],
            TargetRefLines = [.. targetRefs.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} {r.Value}")],
            FsckOutput = fsckOutput
        };
    }

    private static async Task<Dictionary<string, string>> ReadRefsAsync(
        string gitExecutable, string repository, TimeSpan timeout, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            gitExecutable, ["for-each-ref", "--format=%(refname) %(objectname)"],
            repository, timeout, GitEnvironment, ct);
        if (!result.Success)
            throw new HistoryPipelineException("verify", $"for-each-ref failed in '{repository}'", result.ExitCode, result.StdErr);

        // Ref names cannot contain SP or LF, so line/space splitting is unambiguous.
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var sp = line.LastIndexOf(' ');
            if (sp <= 0) continue;
            refs[line[..sp]] = line[(sp + 1)..];
        }
        return refs;
    }
}
