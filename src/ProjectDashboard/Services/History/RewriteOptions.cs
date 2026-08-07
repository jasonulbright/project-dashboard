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

/// <summary>Which files a rewrite touches. Path, glob, and commit scoping are not part of this stage.</summary>
public enum RewriteScope
{
    AllFiles
}

public sealed class RewriteOptions
{
    public required IReadOnlyList<ContentOp> ContentOps { get; init; }

    /// <summary>Commit-message rewriting is not implemented at this stage; true is refused.</summary>
    public bool ReplaceInCommitMessages { get; init; }

    public RewriteScope Scope { get; init; } = RewriteScope.AllFiles;

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
            throw new NotSupportedException("commit-message rewriting is not supported by this stage");
        if (ContentOps is null || ContentOps.Count == 0)
            throw new ArgumentException("a rewrite with no content operations is a mistake, not a no-op — supply at least one op");

        foreach (var op in ContentOps)
        {
            switch (op)
            {
                case LiteralReplace literal:
                    if (literal.Find is null || literal.Find.Length == 0)
                        throw new ArgumentException("LiteralReplace.Find must be at least one byte");
                    if (literal.Replace is null)
                        throw new ArgumentException("LiteralReplace.Replace must be non-null (empty means deletion)");
                    break;
                case RegexReplace regex:
                    if (string.IsNullOrEmpty(regex.Pattern))
                        throw new ArgumentException("RegexReplace.Pattern must be non-empty");
                    if (regex.Replacement is null)
                        throw new ArgumentException("RegexReplace.Replacement must be non-null");
                    // A malformed pattern must fail here, not mid-rewrite after export work.
                    _ = new Regex(regex.Pattern, regex.Options);
                    break;
                default:
                    throw new NotSupportedException($"content op {op?.GetType().Name ?? "(null)"} is not supported");
            }
        }
    }
}
