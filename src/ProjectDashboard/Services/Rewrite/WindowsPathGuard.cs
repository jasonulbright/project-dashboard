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
/// DOS device names, trailing dot or space, the `.git`/8.3-shortname forms that
/// protectNTFS blocks to stop a tree from writing into the real repository directory, and
/// the NTFS 255-character cap on one name. Whole-path length is judged only against the
/// budget the caller supplies, because it depends on where the checkout lives and on
/// core.longpaths — a guard given no budget makes no claim about total length.
///
/// Content-only rewrites cannot introduce any of these: they change bytes inside a blob,
/// never a tree entry name. A purge, a path-scoped run over an already-illegal path, or a
/// source that only ever lived in a bare repository can.
/// </summary>
internal static partial class WindowsPathGuard
{
    /// <summary>NTFS caps one file or directory name at 255 UTF-16 code units, whatever core.longpaths says.</summary>
    public const int MaxComponentLength = 255;

    /// <summary>
    /// Longest absolute path a Windows checkout can create without core.longpaths: Win32
    /// MAX_PATH is 260 counting the terminating NUL, so 259 characters are usable.
    /// </summary>
    public const int MaxAbsolutePathLength = 259;

    /// <summary>
    /// Characters left for a repo-relative path under a working tree rooted at
    /// <paramref name="workingTreeRoot"/>, or <see cref="int.MaxValue"/> when
    /// <paramref name="longPathsEnabled"/> — git then uses the extended-length API and only the
    /// per-component cap remains. Negative when the root alone already exhausts MAX_PATH.
    /// </summary>
    public static int BudgetFor(string workingTreeRoot, bool longPathsEnabled) =>
        longPathsEnabled
            ? int.MaxValue
            : MaxAbsolutePathLength - (workingTreeRoot.TrimEnd('\\', '/').Length + 1);

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

    /// <summary>
    /// The first path a Windows checkout could never realize, with why; null when every path is
    /// safe. <paramref name="pathBudget"/> is the number of characters a repo-relative path may
    /// occupy — see <see cref="BudgetFor"/>; the default makes no whole-path length claim.
    /// </summary>
    public static (string Path, string Reason)? FirstUncheckoutable(
        IEnumerable<string> paths, int pathBudget = int.MaxValue)
    {
        foreach (var path in paths)
        {
            if (Reason(path, pathBudget) is { } reason)
                return (path, reason);
        }
        return null;
    }

    private static string? Reason(string path, int pathBudget)
    {
        if (path.Length > pathBudget)
            return $"the checked-out path would be {path.Length - pathBudget} character(s) past the " +
                   $"{MaxAbsolutePathLength}-character Windows limit (core.longpaths is off in this repository)";

        foreach (var component in path.Split('/'))
        {
            if (component.Length == 0)
                continue;

            if (component.Length > MaxComponentLength)
                return $"component '{Clip(component)}' is {component.Length} characters, past the NTFS {MaxComponentLength}-character name limit";

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

    /// <summary>Keeps an over-long component from filling the refusal message it appears in.</summary>
    private static string Clip(string component) =>
        component.Length <= 60 ? component : component[..60] + "…";
}
