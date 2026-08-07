using System.Text.RegularExpressions;

namespace ProjectDashboard.Services.History;

/// <summary>One content transformation applied to blob payloads, in list order.</summary>
public abstract class ContentOp
{
}

/// <summary>
/// Raw byte-level find/replace. Matches are found left-to-right and never overlap: after a
/// match the scan resumes past the consumed bytes, and replacement output is never
/// re-scanned, so a replacement that contains the needle cannot loop.
/// </summary>
public sealed class LiteralReplace : ContentOp
{
    public required byte[] Find { get; init; }

    /// <summary>May be empty (deletion) or longer than <see cref="Find"/>.</summary>
    public required byte[] Replace { get; init; }
}

/// <summary>
/// Regex replace over strictly UTF-8 decoded content. Payloads that are not valid UTF-8
/// are classified binary and skipped, never partially decoded.
/// </summary>
public sealed class RegexReplace : ContentOp
{
    public required string Pattern { get; init; }

    public required string Replacement { get; init; }

    public RegexOptions Options { get; init; } = RegexOptions.None;
}

/// <summary>Legacy file-scope selector. The richer <see cref="RewriteOptions.FileScope"/> supersedes it; only <see cref="AllFiles"/> is accepted.</summary>
public enum RewriteScope
{
    AllFiles
}

/// <summary>
/// Selects which repo-relative paths a content transform (and purge) touch. The blob
/// version behind a file command is in file-scope when its path matches. Default
/// <see cref="AllFilesScope"/> touches every path — the pre-scoping behavior.
/// </summary>
public abstract class FileScope
{
    /// <summary>True only for the all-files default, letting the engine take its legacy byte-identical path.</summary>
    public abstract bool IsAllFiles { get; }

    public abstract bool Matches(string repoRelativePath);

    public abstract string Describe();
}

public sealed class AllFilesScope : FileScope
{
    public override bool IsAllFiles => true;
    public override bool Matches(string repoRelativePath) => true;
    public override string Describe() => "all files";
}

/// <summary>In-scope when the path matches any of the globs. See <see cref="PathGlob"/> for the supported subset.</summary>
public sealed class GlobScope : FileScope
{
    public required IReadOnlyList<string> Patterns { get; init; }

    private readonly Lazy<PathGlob[]> _compiled;

    public GlobScope() => _compiled = new Lazy<PathGlob[]>(() => (Patterns ?? []).Select(p => new PathGlob(p)).ToArray());

    public override bool IsAllFiles => false;

    public override bool Matches(string repoRelativePath)
    {
        foreach (var glob in _compiled.Value)
            if (glob.IsMatch(repoRelativePath)) return true;
        return false;
    }

    public override string Describe() => $"globs [{string.Join(", ", Patterns)}]";
}

/// <summary>
/// In-scope when the path equals one of the listed paths, or lies under one of them
/// treated as a directory prefix (so listing a folder scopes its whole subtree).
/// </summary>
public sealed class ExplicitPathsScope : FileScope
{
    public required IReadOnlyList<string> Paths { get; init; }

    private readonly Lazy<HashSet<string>> _exact;
    private readonly Lazy<string[]> _prefixes;

    public ExplicitPathsScope()
    {
        _exact = new Lazy<HashSet<string>>(() =>
            new HashSet<string>((Paths ?? []).Select(PathGlob.Normalize), StringComparer.Ordinal));
        _prefixes = new Lazy<string[]>(() =>
            (Paths ?? []).Select(p => PathGlob.Normalize(p).TrimEnd('/') + "/").ToArray());
    }

    public override bool IsAllFiles => false;

    public override bool Matches(string repoRelativePath)
    {
        var p = PathGlob.Normalize(repoRelativePath);
        if (_exact.Value.Contains(p)) return true;
        foreach (var prefix in _prefixes.Value)
            if (p.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    public override string Describe() => $"paths [{string.Join(", ", Paths)}]";
}

/// <summary>
/// Selects which commits a content transform, purge, message op, and identity remap touch.
/// Resolution to concrete oids happens against the source repository before export. Default
/// <see cref="AllHistoryScope"/> touches every commit.
/// </summary>
public abstract class CommitScope
{
    public abstract bool IsAllHistory { get; }

    public abstract string Describe();
}

public sealed class AllHistoryScope : CommitScope
{
    public override bool IsAllHistory => true;
    public override string Describe() => "all history";
}

/// <summary>The listed refs/oids, each resolved to a single commit oid.</summary>
public sealed class ExplicitCommitsScope : CommitScope
{
    public required IReadOnlyList<string> Commits { get; init; }

    public override bool IsAllHistory => false;
    public override string Describe() => $"commits [{string.Join(", ", Commits)}]";
}

/// <summary>
/// The commits reachable from <see cref="ToRef"/> but not from <see cref="FromRef"/> —
/// git's <c>FromRef..ToRef</c> range. A null <see cref="FromRef"/> means every ancestor of
/// <see cref="ToRef"/> (inclusive).
/// </summary>
public sealed class CommitRangeScope : CommitScope
{
    public string? FromRef { get; init; }

    public required string ToRef { get; init; }

    public override bool IsAllHistory => false;
    public override string Describe() => $"range {(FromRef ?? "(root)")}..{ToRef}";
}

/// <summary>
/// Removes matching paths from history: file commands whose resulting path is in
/// <see cref="Paths"/> scope, or whose blob is at least <see cref="MinBlobSize"/> bytes, are
/// dropped. Commits left with no file commands are pruned where safe. A null
/// <see cref="Paths"/> with a set <see cref="MinBlobSize"/> targets purely by size.
/// </summary>
public sealed class PurgeSpec
{
    public FileScope? Paths { get; init; }

    /// <summary>
    /// Threshold in bytes, measured against the payload the import will receive — after any
    /// content op rewrote it and after a shared blob was split — not against the exported
    /// payload. A blob a content op shrank below the threshold is kept.
    /// </summary>
    public long? MinBlobSize { get; init; }
}

/// <summary>
/// Mailmap-style identity remap. A header (author/committer/tagger) whose name and email
/// match <see cref="OldName"/>/<see cref="OldEmail"/> is rewritten to the new pair. A null
/// match field matches any value; a null replacement field leaves that field unchanged.
/// At least one match field and one replacement field must be set.
/// </summary>
public sealed class IdentityMapping
{
    public string? OldName { get; init; }
    public string? OldEmail { get; init; }
    public string? NewName { get; init; }
    public string? NewEmail { get; init; }
}

public sealed class RewriteOptions
{
    public required IReadOnlyList<ContentOp> ContentOps { get; init; }

    /// <summary>
    /// Content transforms applied to commit and tag messages (literal/regex, same op types
    /// as <see cref="ContentOps"/>). Empty leaves messages untouched. Restricted by
    /// <see cref="CommitScope"/> — a tag is in scope when its target commit is.
    /// <see cref="FileScope"/> does not apply: a message has no path.
    /// </summary>
    public IReadOnlyList<ContentOp> MessageOps { get; init; } = [];

    /// <summary>
    /// Author/committer/tagger identity remaps. Restricted by <see cref="CommitScope"/> on
    /// the same terms as <see cref="MessageOps"/>.
    /// </summary>
    public IReadOnlyList<IdentityMapping> IdentityMappings { get; init; } = [];

    /// <summary>Path/size purge. Null means no purge.</summary>
    public PurgeSpec? Purge { get; init; }

    /// <summary>Restricts content transforms and purge to matching paths. Default: every path.</summary>
    public FileScope FileScope { get; init; } = new AllFilesScope();

    /// <summary>Restricts content transforms, purge, message ops, and identity remaps to matching commits. Default: all history.</summary>
    public CommitScope CommitScope { get; init; } = new AllHistoryScope();

    /// <summary>Legacy commit-message flag, still refused; use <see cref="MessageOps"/> instead.</summary>
    public bool ReplaceInCommitMessages { get; init; }

    public RewriteScope Scope { get; init; } = RewriteScope.AllFiles;

    /// <summary>True when nothing scopes, remaps, purges, or edits messages — the legacy all-files content rewrite.</summary>
    public bool IsLegacyAllFiles =>
        FileScope.IsAllFiles && CommitScope.IsAllHistory
        && MessageOps.Count == 0 && IdentityMappings.Count == 0 && Purge is null;

    /// <summary>
    /// Refuses every request this stage cannot execute faithfully. Runs before any
    /// export work, so a refused request costs nothing and touches nothing.
    /// </summary>
    public void Validate()
    {
        if (Scope != RewriteScope.AllFiles)
            throw new NotSupportedException(
                $"rewrite scope {(Enum.IsDefined(Scope) ? Scope.ToString() : ((int)Scope).ToString())} is not supported — this stage rewrites all files only");
        if (ReplaceInCommitMessages)
            throw new NotSupportedException("the ReplaceInCommitMessages flag for commit-message rewriting is not supported — use MessageOps instead");

        var contentOps = ContentOps ?? throw new ArgumentException("ContentOps must be non-null (empty is allowed when another operation is supplied)");
        var hasWork = contentOps.Count > 0 || MessageOps.Count > 0 || IdentityMappings.Count > 0 || Purge is not null;
        if (!hasWork)
            throw new ArgumentException("a rewrite with no content operations is a mistake, not a no-op — supply at least one op");

        ValidateContentOps(contentOps, "ContentOps");
        ValidateContentOps(MessageOps, "MessageOps");

        switch (CommitScope)
        {
            case ExplicitCommitsScope { Commits.Count: 0 }:
                throw new ArgumentException("ExplicitCommitsScope must name at least one commit");
            case CommitRangeScope range when string.IsNullOrWhiteSpace(range.ToRef):
                throw new ArgumentException("CommitRangeScope.ToRef must be a non-empty ref");
        }

        switch (FileScope)
        {
            case GlobScope { Patterns.Count: 0 }:
                throw new ArgumentException("GlobScope must carry at least one pattern");
            case GlobScope globs when globs.Patterns.Any(string.IsNullOrEmpty):
                throw new ArgumentException("GlobScope patterns must each be non-empty");
            case ExplicitPathsScope { Paths.Count: 0 }:
                throw new ArgumentException("ExplicitPathsScope must name at least one path");
        }

        if (Purge is { } purge)
        {
            if (purge.Paths is null && purge.MinBlobSize is null)
                throw new ArgumentException("PurgeSpec must set Paths, MinBlobSize, or both");
            if (purge.MinBlobSize is <= 0)
                throw new ArgumentException("PurgeSpec.MinBlobSize must be positive");
            switch (purge.Paths)
            {
                case GlobScope { Patterns.Count: 0 }:
                    throw new ArgumentException("PurgeSpec glob paths must carry at least one pattern");
                case ExplicitPathsScope { Paths.Count: 0 }:
                    throw new ArgumentException("PurgeSpec explicit paths must name at least one path");
            }
        }

        foreach (var mapping in IdentityMappings)
        {
            if (mapping.OldName is null && mapping.OldEmail is null)
                throw new ArgumentException("IdentityMapping must match on name, email, or both");
            if (mapping.NewName is null && mapping.NewEmail is null)
                throw new ArgumentException("IdentityMapping must replace name, email, or both");
        }
    }

    private static void ValidateContentOps(IReadOnlyList<ContentOp> ops, string label)
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case LiteralReplace literal:
                    if (literal.Find is null || literal.Find.Length == 0)
                        throw new ArgumentException($"{label}: LiteralReplace.Find must be at least one byte");
                    if (literal.Replace is null)
                        throw new ArgumentException($"{label}: LiteralReplace.Replace must be non-null (empty means deletion)");
                    break;
                case RegexReplace regex:
                    if (string.IsNullOrEmpty(regex.Pattern))
                        throw new ArgumentException($"{label}: RegexReplace.Pattern must be non-empty");
                    if (regex.Replacement is null)
                        throw new ArgumentException($"{label}: RegexReplace.Replacement must be non-null");
                    // A malformed pattern must fail here, not mid-rewrite after export work.
                    _ = new Regex(regex.Pattern, regex.Options);
                    break;
                default:
                    throw new NotSupportedException($"{label}: content op {op?.GetType().Name ?? "(null)"} is not supported");
            }
        }
    }
}
