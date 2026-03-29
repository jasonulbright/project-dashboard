using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class ProjectDiscoveryService(GitService gitService, GitHubService gitHubService, SettingsService settingsService)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectDashboard", "discovery-cache.json");

    /// <summary>
    /// Loads from cache if fresh, otherwise runs full discovery and updates cache.
    /// </summary>
    public async Task<List<ProjectInfo>> DiscoverAllAsync(CancellationToken ct = default)
    {
        var settings = settingsService.Load();

        // Try cache first
        var cached = LoadCache(settings.RefreshIntervalSeconds);
        if (cached is not null)
            return cached;

        // Full discovery
        var results = await DiscoverFromDiskAsync(settings, ct);

        // Save cache
        SaveCache(results);

        return results;
    }

    /// <summary>
    /// Forces a full re-scan, ignoring cache.
    /// </summary>
    public async Task<List<ProjectInfo>> ForceRefreshAllAsync(CancellationToken ct = default)
    {
        var settings = settingsService.Load();
        var results = await DiscoverFromDiskAsync(settings, ct);
        SaveCache(results);
        return results;
    }

    public async Task<ProjectInfo> RefreshProjectAsync(ProjectInfo project, CancellationToken ct = default)
    {
        var ghAvailable = await gitHubService.IsAvailableAsync(ct);
        return await BuildProjectInfoAsync(project.FullPath, ghAvailable, ct);
    }

    public async Task SaveManifestAsync(string repoPath, ProjectManifest manifest, CancellationToken ct = default)
    {
        var path = Path.Combine(repoPath, "project-manifest.json");
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<List<ProjectInfo>> DiscoverFromDiskAsync(AppSettings settings, CancellationToken ct)
    {
        var rootPath = settings.ProjectsRootPath;
        var excluded = new HashSet<string>(settings.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
            return [];

        var dirs = Directory.GetDirectories(rootPath)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return !excluded.Contains(name) && Directory.Exists(Path.Combine(d, ".git"));
            })
            .ToList();

        var ghAvailable = await gitHubService.IsAvailableAsync(ct);

        var semaphore = new SemaphoreSlim(6);
        var tasks = dirs.Select(async dir =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await BuildProjectInfoAsync(dir, ghAvailable, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        return results
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ProjectInfo> BuildProjectInfoAsync(string dirPath, bool ghAvailable, CancellationToken ct)
    {
        var dirName = Path.GetFileName(dirPath);
        var readmePath = Path.Combine(dirPath, "README.md");
        var changelogPath = Path.Combine(dirPath, "CHANGELOG.md");
        var manifestPath = Path.Combine(dirPath, "project-manifest.json");

        var project = new ProjectInfo
        {
            DirectoryName = dirName,
            FullPath = dirPath,
            HasReadme = File.Exists(readmePath),
            HasChangelog = File.Exists(changelogPath),
            HasManifest = File.Exists(manifestPath)
        };

        // Git status
        project.GitStatus = await gitService.GetStatusAsync(dirPath, ct);

        // Recent commits
        project.RecentCommits = await gitService.GetRecentCommitsAsync(dirPath, 20, ct);

        // README
        if (project.HasReadme)
        {
            project.ReadmeContent = MarkdownService.ReadFileHead(readmePath, 80);
            project.DisplayName = MarkdownService.ExtractTitle(project.ReadmeContent);
            project.Description = MarkdownService.ExtractDescription(project.ReadmeContent);
        }

        if (string.IsNullOrWhiteSpace(project.DisplayName))
            project.DisplayName = dirName;

        // CHANGELOG
        if (project.HasChangelog)
        {
            project.ChangelogContent = MarkdownService.ReadFileHead(changelogPath, 80);
            project.LatestVersion = MarkdownService.ExtractLatestVersion(project.ChangelogContent);
        }

        // Manifest
        if (project.HasManifest)
        {
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct);
                project.Manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions) ?? new ProjectManifest();
            }
            catch
            {
                project.Manifest = new ProjectManifest();
            }
        }

        // GitHub data
        if (ghAvailable && !string.IsNullOrEmpty(project.GitHubSlug))
        {
            project.OpenIssueCount = await gitHubService.GetOpenIssueCountAsync(project.GitHubSlug, ct);
            project.Issues = await gitHubService.GetIssuesAsync(project.GitHubSlug, "open", ct);
            project.GitStatus.Visibility = await gitHubService.GetRepoVisibilityAsync(project.GitHubSlug, ct);
        }

        return project;
    }

    // ── Cache ──────────────────────────────────────────────────────

    private sealed class DiscoveryCache
    {
        public DateTimeOffset CachedAt { get; set; }
        public List<ProjectInfo> Projects { get; set; } = [];
    }

    private static List<ProjectInfo>? LoadCache(int maxAgeSeconds)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;

            var json = File.ReadAllText(CachePath);
            var cache = JsonSerializer.Deserialize<DiscoveryCache>(json, JsonOptions);
            if (cache is null) return null;

            var age = DateTimeOffset.Now - cache.CachedAt;
            if (age.TotalSeconds > maxAgeSeconds) return null;

            return cache.Projects.Count > 0 ? cache.Projects : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(List<ProjectInfo> projects)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var cache = new DiscoveryCache
            {
                CachedAt = DateTimeOffset.Now,
                Projects = projects
            };

            var json = JsonSerializer.Serialize(cache, JsonOptions);
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // Cache write failure is non-fatal
        }
    }
}
