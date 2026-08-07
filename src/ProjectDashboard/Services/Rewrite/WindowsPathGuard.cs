using System.Text.RegularExpressions;

namespace ProjectDashboard.Services.Rewrite;

/// <summary>
/// Judges whether a git tree path can ever exist in a Windows working tree. A bare
/// repository stores any byte sequence in a tree entry, so a rewritten history can
/// carry a path that no `reset --hard` could ever check out; this guard names such a
/// path before any real ref moves, so the swap refuses up front instead of failing
/// half-applied at checkout.
///
/// Each `/`-separated component is judged against the rules a Windows checkout enforces
/// plus git's own core.protectNTFS guards: characters the filesystem forbids, reserved
/// DOS device names, trailing dot or space, and the `.git`/8.3-shortname forms that
/// protectNTFS blocks to stop a tree from writing into the real repository directory.
/// </summary>
internal static partial class WindowsPathGuard
{
    // < > : " | ? * and every control character are illegal in an NTFS name; a backslash
    // inside a git path component (git separates with '/') would split into directories a
    // Windows checkout never intended.
    private static readonly char[] IllegalChars =
        ['<', '>', ':', '"', '|', '?', '*', '\\'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>NTFS 8.3 short form of ".git" (GIT~1, GIT~2, …) — protectNTFS blocks it so a tree cannot address the git dir via its shortname.</summary>
    [GeneratedRegex(@"^git~[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitShortName();

    /// <summary>The first path a Windows checkout could never realize, with why; null when every path is safe.</summary>
    public static (string Path, string Reason)? FirstUncheckoutable(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Reason(path) is { } reason)
                return (path, reason);
        }
        return null;
    }

    private static string? Reason(string path)
    {
        foreach (var component in path.Split('/'))
        {
            if (component.Length == 0)
                continue;

            foreach (var c in component)
            {
                if (c < 0x20)
                    return $"component '{component}' contains a control character (0x{(int)c:X2})";
                if (IllegalChars.Contains(c))
                    return $"component '{component}' contains the Windows-illegal character '{c}'";
            }

            if (component[^1] is ' ' or '.')
                return $"component '{component}' ends with a '{component[^1]}', which Windows strips and cannot round-trip";

            // The device-name check ignores any extension: `AUX.txt` is as unusable as `AUX`.
            var stem = component.Split('.', 2)[0];
            if (ReservedDeviceNames.Contains(stem))
                return $"component '{component}' is the reserved DOS device name '{stem}'";

            if (string.Equals(component, ".git", StringComparison.OrdinalIgnoreCase) || GitShortName().IsMatch(component))
                return $"component '{component}' resolves to the git directory (protectNTFS)";
        }
        return null;
    }
}
