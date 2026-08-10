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
        if (child.Length == parent.Length)
            return string.Equals(child, parent, StringComparison.OrdinalIgnoreCase);
        return child.Length > parent.Length
            && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && (child[parent.Length] == Path.DirectorySeparatorChar
                || child[parent.Length] == Path.AltDirectorySeparatorChar);
    }
}
