using System.IO;
using ProjectDashboard.Services;

namespace ProjectDashboard.Models;

/// <summary>
/// The root list's shape on disk: how a settings file written before roots existed becomes one,
/// and what the singular <see cref="AppSettings.ProjectsRootPath"/> means afterwards.
///
/// The singular field stays written as the first enabled root so a downgrade to a build that
/// only knows that field still finds somewhere to scan. It is a mirror, never the truth.
/// </summary>
public static class ProjectRootSettings
{
    /// <summary>The root's own children. Reproduces the behaviour of every build before roots.</summary>
    public const int MinDepth = 1;

    /// <summary>
    /// Hard ceiling on scan depth. An unbounded walk over a large drive is how discovery turns
    /// into a hang, and the ceiling is enforced here rather than trusted from the file.
    /// </summary>
    public const int MaxDepth = 4;

    public static int ClampDepth(int depth) => Math.Clamp(depth, MinDepth, MaxDepth);

    /// <summary>
    /// The root list a settings object means, whether or not it has been migrated. A settings
    /// object built in code carries only the singular field, and a reader that took the list
    /// literally would scan nothing at all for it.
    /// </summary>
    public static ProjectRoot[] Effective(AppSettings settings) =>
        settings.ProjectRoots.Length > 0
            ? settings.ProjectRoots
            : settings.ProjectsRootPath.Trim().Length == 0
                ? []
                : [new ProjectRoot
                {
                    Path = settings.ProjectsRootPath,
                    ExcludedDirectories = [.. settings.ExcludedDirectories],
                }];

    /// <summary>
    /// Brings a loaded settings object up to the root list. Lossless: a file that carries only
    /// the singular root becomes one root with that path and the old global exclusions, and the
    /// singular field keeps its value.
    /// </summary>
    public static void Migrate(AppSettings settings)
    {
        settings.ProjectRoots = Clean(Effective(settings));

        if (Find(settings.ProjectRoots, settings.DefaultRootPath) is null)
            settings.DefaultRootPath = FirstUsableForWrites(settings.ProjectRoots);
    }

    /// <summary>
    /// Settles a write against what is already on disk, then re-derives the singular
    /// compatibility fields from the list.
    ///
    /// The singular fields are a live surface, not a write-only mirror: an external editor and
    /// every caller that still load-mutates <see cref="AppSettings.ProjectsRootPath"/> or
    /// <see cref="AppSettings.ExcludedDirectories"/> would otherwise have its edit silently
    /// discarded on save. An edit to the list itself is the richer expression of the same
    /// intent and wins when both changed.
    /// </summary>
    public static void Reconcile(AppSettings settings, AppSettings previous)
    {
        Migrate(settings);

        if (SameRoots(previous.ProjectRoots, settings.ProjectRoots)
            && Primary(settings) is { } edited)
        {
            if (!SamePath(settings.ProjectsRootPath, previous.ProjectsRootPath))
                edited.Path = RepoPaths.Normalize(settings.ProjectsRootPath);
            if (!SameNames(settings.ExcludedDirectories, previous.ExcludedDirectories))
                edited.ExcludedDirectories = CleanExclusions(settings.ExcludedDirectories);
            settings.ProjectRoots = Clean(settings.ProjectRoots);
        }

        if (Find(settings.ProjectRoots, settings.DefaultRootPath) is null)
            settings.DefaultRootPath = FirstUsableForWrites(settings.ProjectRoots);

        SyncLegacyFields(settings);
    }

    /// <summary>
    /// Re-derives the singular compatibility fields from the root list, so the file never
    /// carries a singular root the list disagrees with.
    /// </summary>
    public static void SyncLegacyFields(AppSettings settings)
    {
        if (Primary(settings) is not { } primary)
        {
            // An empty list clears the mirror. Left standing, the singular root is what
            // Effective synthesizes a root from, and removing every folder would not stick.
            settings.ProjectsRootPath = "";
            settings.ExcludedDirectories = [];
            return;
        }

        settings.ProjectsRootPath = primary.Path;
        settings.ExcludedDirectories = [.. primary.ExcludedDirectories];
    }

    /// <summary>The root the singular compatibility fields describe.</summary>
    public static ProjectRoot? Primary(AppSettings settings) =>
        settings.ProjectRoots.FirstOrDefault(r => r.Enabled) ?? settings.ProjectRoots.FirstOrDefault();

    private static bool SamePath(string left, string right) =>
        string.Equals(RepoPaths.Normalize(left), RepoPaths.Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static bool SameNames(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count == right.Count && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private static bool SameRoots(IReadOnlyList<ProjectRoot> left, IReadOnlyList<ProjectRoot> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!SamePath(left[i].Path, right[i].Path)) return false;
            if (left[i].Enabled != right[i].Enabled) return false;
            if (left[i].MaxDepth != right[i].MaxDepth) return false;
            if (!string.Equals(left[i].Label, right[i].Label, StringComparison.Ordinal)) return false;
            if (!SameNames(left[i].ExcludedDirectories, right[i].ExcludedDirectories)) return false;
        }
        return true;
    }

    /// <summary>Normalizes paths, clamps depths, and drops empty and duplicate entries, in order.</summary>
    public static ProjectRoot[] Clean(IEnumerable<ProjectRoot> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<ProjectRoot>();
        foreach (var root in roots)
        {
            if (root is null) continue;
            var path = RepoPaths.Normalize(root.Path);
            if (path.Length == 0 || !seen.Add(path)) continue;

            var copy = root.Copy();
            copy.Path = path;
            copy.Label = root.Label.Trim();
            copy.MaxDepth = ClampDepth(root.MaxDepth);
            copy.ExcludedDirectories = CleanExclusions(root.ExcludedDirectories);
            cleaned.Add(copy);
        }
        return [.. cleaned];
    }

    public static string[] CleanExclusions(IEnumerable<string> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (var entry in entries)
        {
            var trimmed = entry?.Trim().Trim('\\', '/') ?? "";
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            cleaned.Add(trimmed);
        }
        return [.. cleaned];
    }

    /// <summary>The roots a scan reads, in order. A disabled root is not one of them.</summary>
    public static IReadOnlyList<ProjectRoot> Scannable(AppSettings settings) =>
        [.. Clean(Effective(settings)).Where(r => r.Enabled)];

    public static ProjectRoot? Find(IEnumerable<ProjectRoot> roots, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = RepoPaths.Normalize(path);
        return roots.FirstOrDefault(r => string.Equals(RepoPaths.Normalize(r.Path), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The root a new project or a clone lands in, or empty when none can take one.</summary>
    public static string WriteTarget(AppSettings settings)
    {
        var chosen = Find(settings.ProjectRoots, settings.DefaultRootPath);
        if (chosen is { Enabled: true } && Directory.Exists(chosen.Path)) return chosen.Path;
        return "";
    }

    /// <summary>
    /// Why <see cref="WriteTarget"/> is empty, phrased for the surface that refused. Null when a
    /// target is available.
    /// </summary>
    public static string? WriteTargetRefusal(AppSettings settings)
    {
        if (settings.ProjectRoots.Length == 0)
            return "no projects folder is configured — add one in Settings.";

        var chosen = Find(settings.ProjectRoots, settings.DefaultRootPath);
        if (chosen is null)
            return "no default projects folder is chosen — pick one in Settings.";
        if (!chosen.Enabled)
            return $"the default projects folder {chosen.Path} is switched off — enable it or pick another in Settings.";
        if (!Directory.Exists(chosen.Path))
            return $"the default projects folder {chosen.Path} isn't there — reconnect it or pick another in Settings.";
        return null;
    }

    private static string FirstUsableForWrites(ProjectRoot[] roots) =>
        (roots.FirstOrDefault(r => r.Enabled) ?? roots.FirstOrDefault())?.Path ?? "";
}
