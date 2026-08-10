using System.IO;

namespace ProjectDashboard.Services;

/// <summary>
/// One spelling for a repository path. A trailing separator, a relative spelling, or a
/// differently-cased drive letter must not make one repository look like two to the watcher
/// signal, the pin list, or the card lookup that matches them. An unparseable path keys as
/// itself rather than throwing — a damaged settings entry is inert, not fatal.
/// </summary>
public static class RepoPaths
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"unusable repository path: {path}", ex);
            return path.Trim();
        }
    }

    public static bool Equal(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="ancestor"/> itself or lies under
    /// it. Compares whole segments: a prefix test alone puts <c>C:\projects2</c> under
    /// <c>C:\projects</c>.
    /// </summary>
    public static bool IsAtOrUnder(string candidate, string ancestor)
    {
        var child = Normalize(candidate);
        var parent = Normalize(ancestor);
        if (parent.Length == 0 || child.Length == 0) return false;
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
        if (child.Length <= parent.Length) return false;
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;

        // A drive root keeps its separator through normalization and so carries the boundary
        // already; every other path needs one at the join, or C:\projects2 reads as being
        // under C:\projects.
        return IsSeparator(parent[^1]) || IsSeparator(child[parent.Length]);
    }

    private static bool IsSeparator(char c) =>
        c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
