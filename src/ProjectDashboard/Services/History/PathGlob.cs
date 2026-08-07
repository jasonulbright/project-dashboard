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
/// Matching is anchored at both ends; a leading '/' is stripped (paths are already
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
        _regex = new Regex(Compile(pattern), RegexOptions.CultureInvariant);
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

    private static string Compile(string glob)
    {
        glob = glob.Replace('\\', '/');
        if (glob.StartsWith('/')) glob = glob[1..];
        // A trailing slash means "everything under this directory".
        if (glob.EndsWith('/')) glob += "**";

        var sb = new StringBuilder("^");
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
        sb.Append('$');
        return sb.ToString();
    }
}
