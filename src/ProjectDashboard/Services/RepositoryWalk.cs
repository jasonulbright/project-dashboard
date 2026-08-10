using System.Diagnostics;
using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Directory names that hold no project and whose churn changes no card: git internals, package
/// caches, and build output. One list, read by the scan that decides where not to look and by
/// the watcher that decides which events to drop — two lists would drift, and the pair would
/// then disagree about which directories the app even believes in.
/// </summary>
public static class ScanSkips
{
    public static readonly string[] Names =
        [".git", "node_modules", "bin", "obj", ".vs", "packages", "publish", "target", "dist", ".gradle", "vendor"];

    /// <summary>The same names as <c>\name\</c> segments, for matching inside a path.</summary>
    public static readonly string[] Segments = [.. Names.Select(n => $"\\{n}\\")];

    public static bool IsSkipped(string directoryName) =>
        Names.Contains(directoryName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// How far a single root's walk may go before it reports a floor instead of a total. An
/// unbounded walk over a large drive is how discovery becomes a hang.
/// </summary>
public sealed record WalkLimits(int MaxDirectories, TimeSpan Budget)
{
    public static WalkLimits Default { get; } = new(10_000, TimeSpan.FromSeconds(10));
}

/// <summary>What one root's walk found, and whether it finished.</summary>
public sealed record RootWalkResult(
    IReadOnlyList<string> Repositories,
    RootAvailability Availability,
    bool Truncated,
    string Detail);

/// <summary>
/// Finds the repositories under one configured root. Every path out reports what happened to
/// that root: a root that is gone, or that threw while being read, yields no repositories and
/// says so, because a scan that quietly returns nothing for an unreachable drive is
/// indistinguishable from a drive with nothing on it.
///
/// An explicit breadth-first walk rather than <see cref="SearchOption.AllDirectories"/>, which
/// throws on the first denied subdirectory and loses the whole walk with it.
/// </summary>
public static class RepositoryWalk
{
    public static RootWalkResult Run(ProjectRoot root, CancellationToken ct, WalkLimits? limits = null)
    {
        limits ??= WalkLimits.Default;

        var rootPath = RepoPaths.Normalize(root.Path);
        if (rootPath.Length == 0)
            return new RootWalkResult([], RootAvailability.Missing, false, "no path configured");

        try
        {
            if (!Directory.Exists(rootPath))
                return new RootWalkResult([], RootAvailability.Missing, false, "");
        }
        catch (Exception ex)
        {
            return new RootWalkResult([], RootAvailability.Unreadable, false, ex.Message);
        }

        var exclusions = new RootExclusions(rootPath, root.ExcludedDirectories);
        var maxDepth = ProjectRootSettings.ClampDepth(root.MaxDepth);

        var found = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Identity(rootPath) };
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));

        var clock = Stopwatch.StartNew();
        var examined = 0;
        var truncated = false;
        var deniedSubtrees = 0;
        var truncationReason = "";
        var firstDenial = "";

        while (queue.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            List<string> children;
            try
            {
                children = [.. Directory.EnumerateDirectories(directory)];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One denied subtree is not a failed root: the rest of the walk is still a
                // better answer than nothing, provided the skip travels with it.
                deniedSubtrees++;
                if (firstDenial.Length == 0) firstDenial = $"{directory} ({ex.Message})";
                if (depth == 0) return new RootWalkResult(found, RootAvailability.Unreadable, false, ex.Message);
                continue;
            }

            foreach (var child in children)
            {
                // Per directory, not per root: a token honoured only between roots leaves a
                // pathological one running to completion after the reader navigated away.
                ct.ThrowIfCancellationRequested();

                if (++examined > limits.MaxDirectories)
                {
                    truncated = true;
                    truncationReason = $"stopped after {limits.MaxDirectories} directories";
                    break;
                }
                if (clock.Elapsed > limits.Budget)
                {
                    truncated = true;
                    truncationReason = $"stopped after {limits.Budget.TotalSeconds:0.#}s";
                    break;
                }

                if (ScanSkips.IsSkipped(Path.GetFileName(child))) continue;
                if (exclusions.Excludes(child)) continue;

                // A repository is a leaf. Not descending into one is what keeps the walk cheap
                // — no git internals, no vendored trees — and a repository nested inside another
                // belongs to the card that already covers it.
                if (GitService.IsGitRepo(child))
                {
                    found.Add(RepoPaths.Normalize(child));
                    continue;
                }

                // Junctions and symlinks make the tree a graph. Walking THROUGH one is how a
                // link back to an ancestor becomes an unbounded walk; a repository AT one is
                // still recorded above.
                if (IsReparsePoint(child)) continue;
                if (!visited.Add(Identity(child))) continue;

                queue.Enqueue((RepoPaths.Normalize(child), depth + 1));
            }
        }

        var detail = string.Join("; ", new[]
        {
            truncationReason,
            deniedSubtrees > 0 ? $"{deniedSubtrees} folder(s) could not be read, first {firstDenial}" : "",
        }.Where(part => part.Length > 0));

        return new RootWalkResult(found, RootAvailability.Available, truncated, detail);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex)
        {
            // Unreadable attributes cannot be shown to be safe to walk through.
            Log.Warn($"could not read attributes of {path}", ex);
            return true;
        }
    }

    /// <summary>
    /// A directory's identity for the visited set: the final target of any link, so two paths
    /// that resolve to one directory are counted once.
    /// </summary>
    private static string Identity(string path)
    {
        try
        {
            var resolved = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return RepoPaths.Normalize(resolved?.FullName ?? path);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not resolve {path}", ex);
            return RepoPaths.Normalize(path);
        }
    }
}

/// <summary>
/// A root's exclusion list. An entry carrying a separator is a root-relative path prefix; one
/// without is a directory name matched at any depth, which is what the flat name list every
/// build before roots carried has always meant.
/// </summary>
public sealed class RootExclusions
{
    private readonly string _root;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _relativePaths = [];

    public RootExclusions(string rootPath, IEnumerable<string> entries)
    {
        _root = RepoPaths.Normalize(rootPath);
        foreach (var entry in entries)
        {
            var trimmed = entry?.Trim().Trim('\\', '/') ?? "";
            if (trimmed.Length == 0) continue;
            if (trimmed.Contains('\\') || trimmed.Contains('/'))
                _relativePaths.Add(trimmed.Replace('/', '\\'));
            else
                _names.Add(trimmed);
        }
    }

    public bool IsEmpty => _names.Count == 0 && _relativePaths.Count == 0;

    /// <summary>Whether the excluded set covers this directory, or an ancestor of it under the root.</summary>
    public bool Excludes(string directory)
    {
        if (IsEmpty) return false;

        var full = RepoPaths.Normalize(directory);
        if (_names.Count > 0)
        {
            var walk = full;
            while (walk.Length > _root.Length)
            {
                if (_names.Contains(Path.GetFileName(walk))) return true;
                var parent = Path.GetDirectoryName(walk);
                if (parent is null) break;
                walk = RepoPaths.Normalize(parent);
            }
        }

        if (_relativePaths.Count == 0) return false;
        if (full.Length <= _root.Length) return false;
        var relative = full[(_root.Length + 1)..];
        foreach (var prefix in _relativePaths)
        {
            if (relative.Length < prefix.Length) continue;
            if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.Length == prefix.Length || relative[prefix.Length] == '\\') return true;
        }
        return false;
    }

    /// <summary>The excluded names, for the surfaces that list what a root is hiding.</summary>
    public IReadOnlyCollection<string> Names => _names;
}
