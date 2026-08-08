using System.Text;
using System.Text.RegularExpressions;

namespace ProjectDashboard.Services.History;

/// <summary>
/// Repo-relative path glob matcher over '/'-separated paths. Supported subset:
///   <c>*</c>   matches any run of characters except '/'.
///   <c>?</c>   matches a single character except '/'.
///   <c>**</c>  matches any run of characters including '/'.
///   <c>**/</c> matches zero or more leading path segments (so <c>**/x</c> hits <c>x</c> and <c>a/b/x</c>).
///   a trailing <c>/</c> matches the whole directory subtree (equivalent to appending <c>**</c>).
/// A pattern carrying none of these wildcards matches its path exactly. Unsupported and
/// documented as such: character classes <c>[...]</c>, negation <c>!</c>, and brace
/// expansion <c>{a,b}</c> — those characters are treated literally, never as operators.
/// Matching is anchored at the true start and end of the path — a path ending in a newline
/// is not matched by a pattern naming it without one, and wildcards span newlines, both of
/// which are legal bytes in a git path. A leading '/' is stripped (paths are already
/// root-relative). Input paths are normalized: backslashes become '/', a leading './' or
/// '/' is dropped.
/// </summary>
public sealed class PathGlob
{
    private readonly Regex _regex;

    public string Pattern { get; }

    public PathGlob(string pattern)
    {
        Pattern = pattern;
        // Singleline so '.' spans an LF, which is a legal byte in a git path. Without it
        // '**' stops at the newline and an in-scope path is silently missed.
        _regex = new Regex(Compile(pattern), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public bool IsMatch(string path) => _regex.IsMatch(Normalize(path));

    /// <summary>Backslash-to-slash, drop a leading './' or '/'. Paths from git are already slash-separated and root-relative; this only hardens against caller input.</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        if (p.StartsWith('/')) p = p[1..];
        return p;
    }

    /// <summary>
    /// The same pattern as a git `:(glob)` pathspec, or null when git's wildmatch cannot express
    /// it. The scrub narrows `git grep` with this, so anything but an exact translation would
    /// verify a different set of paths than the preview scoped — under-matching most dangerously,
    /// since a needle at an unsearched path then reads as scrubbed.
    ///
    /// Two constructs need work. Brackets are literal here and a character class to wildmatch, and
    /// a backslash is not an escape in a pathspec (git's own tests rely on that), so `[` and `]`
    /// are written as the single-character classes `[[]` and `[]]`, which wildmatch reads as those
    /// literal characters. And wildmatch rejects a `**` that is not bounded by slashes or by the
    /// ends of the pattern — `a**b` is a parse error there and an any-run wildcard here — so a
    /// pattern using one has no translation at all.
    /// </summary>
    public static string? ToGitPathspec(string pattern)
    {
        var glob = NormalizePattern(pattern);
        var sb = new StringBuilder(":(glob)");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var start = i;
                while (i + 1 < glob.Length && glob[i + 1] == '*') i++;
                var startsSegment = start == 0 || glob[start - 1] == '/';
                var endsSegment = i + 1 == glob.Length || glob[i + 1] == '/';
                if (!startsSegment || !endsSegment) return null;
                sb.Append("**");
                continue;
            }
            sb.Append(c switch { '[' => "[[]", ']' => "[]]", _ => c.ToString() });
        }
        return sb.ToString();
    }

    /// <summary>Backslash-to-slash, drop a leading '/', and expand a trailing '/' into a whole-subtree match — the shape <see cref="Compile"/> works from.</summary>
    private static string NormalizePattern(string pattern)
    {
        var glob = pattern.Replace('\\', '/');
        if (glob.StartsWith('/')) glob = glob[1..];
        if (glob.EndsWith('/')) glob += "**";
        return glob;
    }

    private static string Compile(string pattern)
    {
        var glob = NormalizePattern(pattern);

        // \A and \z, never ^ and $: '$' also matches before a trailing LF, so a path ending
        // in a newline would match a pattern that does not name it.
        var sb = new StringBuilder(@"\A");
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i += 2;
                    // '**/' spans zero or more whole segments; a bare '**' spans anything.
                    if (i < glob.Length && glob[i] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    i++;
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                i++;
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        sb.Append(@"\z");
        return sb.ToString();
    }
}
