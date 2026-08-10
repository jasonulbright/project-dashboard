using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

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
/// </summary>
public static class RepositoryWalk
{
    public static RootWalkResult Run(ProjectRoot root, CancellationToken ct)
    {
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
        var found = new List<string>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(rootPath))
            {
                ct.ThrowIfCancellationRequested();
                if (exclusions.Excludes(directory)) continue;
                if (GitService.IsGitRepo(directory)) found.Add(RepoPaths.Normalize(directory));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RootWalkResult(found, RootAvailability.Unreadable, true, ex.Message);
        }

        return new RootWalkResult(found, RootAvailability.Available, false, "");
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
