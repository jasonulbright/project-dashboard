using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class ProjectDiscoveryService(GitService gitService, GitHubService gitHubService, SettingsService settingsService, ManifestStore manifestStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string CachePath = AppPaths.DiscoveryCacheFile;

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
        var refreshed = await BuildProjectInfoAsync(project.FullPath, ct);
        if (await gitHubService.IsAvailableAsync(ct))
            await ApplyRemoteDataAsync([refreshed], ct);
        return refreshed;
    }

    public Task SaveManifestAsync(string repoPath, ProjectManifest manifest, CancellationToken ct = default)
    {
        // Manifests live out-of-source in %APPDATA%, not in the repo root.
        manifestStore.Save(repoPath, manifest);
        return Task.CompletedTask;
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
                return !excluded.Contains(name) && GitService.IsGitRepo(d);
            })
            .ToList();

        // Phase A: local git/file facts, parallel with a small concurrency cap.
        var semaphore = new SemaphoreSlim(6);
        var tasks = dirs.Select(async dir =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await BuildProjectInfoAsync(dir, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = (await Task.WhenAll(tasks))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Phase B: one batched gh call per ~25 GitHub repos (was 3 spawns per repo).
        if (await gitHubService.IsAvailableAsync(ct))
        {
            await ApplyRemoteDataAsync(results, ct);

            // Phase C (ROADMAP v1.1): surface GitHub repos not cloned locally as Cloud cards.
            if (settings.EnableGitHubDiscovery)
                await AppendRemoteOnlyAsync(results, ct);
        }

        return results
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Adds the signed-in user's repositories that have no local clone as remote-only
    /// ("Cloud") entries. A repo is "local" if any discovered project's GitHub slug or
    /// folder name matches it, so a repo cloned under a renamed folder isn't duplicated.
    /// </summary>
    private async Task AppendRemoteOnlyAsync(List<ProjectInfo> local, CancellationToken ct)
    {
        List<RemoteRepo> remotes;
        try { remotes = await gitHubService.GetUserReposAsync(ct); }
        catch (Exception ex) { Log.Warn("remote-only discovery skipped", ex); return; }
        if (remotes.Count == 0) return;

        var localSlugs = new HashSet<string>(
            local.Select(p => p.GitHubSlug).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        var localNames = new HashSet<string>(
            local.Select(p => p.DirectoryName), StringComparer.OrdinalIgnoreCase);

        foreach (var repo in remotes)
        {
            if (localSlugs.Contains(repo.NameWithOwner) || localNames.Contains(repo.Name))
                continue;

            var manifest = manifestStore.TryGet(repo.NameWithOwner, out var stored) && stored is not null
                ? stored
                : new ProjectManifest { Description = repo.Description };

            local.Add(new ProjectInfo
            {
                DirectoryName = repo.Name,
                DisplayName = repo.Name,
                FullPath = "",
                Description = repo.Description,
                IsRemoteOnly = true,
                RemoteSlug = repo.NameWithOwner,
                HasManifest = true,
                Manifest = manifest,
                GitStatus = new GitStatus
                {
                    Visibility = repo.Visibility,
                    RemoteUrl = $"https://github.com/{repo.NameWithOwner}",
                    LastCommitDate = repo.UpdatedAt == default ? null : repo.UpdatedAt
                }
            });
        }
    }

    /// <summary>Fetches visibility + open issue/PR counts for all GitHub-hosted projects in bulk.</summary>
    private async Task ApplyRemoteDataAsync(List<ProjectInfo> projects, CancellationToken ct)
    {
        var githubProjects = projects.Where(p => !string.IsNullOrEmpty(p.GitHubSlug)).ToList();
        if (githubProjects.Count == 0) return;

        var data = await gitHubService.GetRepoDataBatchAsync(
            githubProjects.Select(p => p.GitHubSlug).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        foreach (var project in githubProjects)
        {
            if (data.TryGetValue(project.GitHubSlug, out var remote))
            {
                project.GitStatus.Visibility = remote.Visibility;
                project.OpenIssueCount = remote.OpenIssues;
                project.OpenPrCount = remote.OpenPrs;
            }
            else
            {
                // Batch call itself failed — unknown, NOT zero and NOT "local".
                project.GitStatus.Visibility = "unknown";
                project.OpenIssueCount = null;
                project.OpenPrCount = null;
            }
        }
    }

    private async Task<ProjectInfo> BuildProjectInfoAsync(string dirPath, CancellationToken ct)
    {
        var dirName = Path.GetFileName(dirPath);
        var readmePath = Path.Combine(dirPath, "README.md");
        var changelogPath = Path.Combine(dirPath, "CHANGELOG.md");
        var legacyManifestPath = Path.Combine(dirPath, "project-manifest.json");

        var project = new ProjectInfo
        {
            DirectoryName = dirName,
            FullPath = dirPath,
            HasReadme = File.Exists(readmePath),
            HasChangelog = File.Exists(changelogPath)
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

        // Manifest: read from the out-of-source store. If a legacy repo-root
        // project-manifest.json still exists, import it into the store on the fly.
        if (manifestStore.TryGet(dirPath, out var stored) && stored is not null)
        {
            project.Manifest = stored;
            project.HasManifest = true;
        }
        else if (File.Exists(legacyManifestPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(legacyManifestPath, ct);
                var legacy = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions) ?? new ProjectManifest();
                manifestStore.Save(dirPath, legacy);
                project.Manifest = legacy;
                project.HasManifest = true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to import legacy manifest for {dirPath}", ex);
                project.Manifest = new ProjectManifest();
            }
        }

        // GitHub-side data (visibility, counts) is applied afterwards in one batch;
        // the issues LIST loads lazily when a detail view actually needs it.
        return project;
    }

    // ── Cache ──────────────────────────────────────────────────────

    private sealed class DiscoveryCache
    {
        public DateTimeOffset CachedAt { get; set; }
        public List<ProjectInfo> Projects { get; set; } = [];
    }

    private List<ProjectInfo>? LoadCache(int maxAgeSeconds)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;

            var json = File.ReadAllText(CachePath);
            var cache = JsonSerializer.Deserialize<DiscoveryCache>(json, JsonOptions);
            if (cache is null) return null;

            var age = DateTimeOffset.Now - cache.CachedAt;
            if (age.TotalSeconds > maxAgeSeconds) return null;
            if (cache.Projects.Count == 0) return null;

            // Manifests are the store's truth, never the cache's: a manifest saved
            // after the cache was written must not appear reverted on relaunch.
            foreach (var project in cache.Projects)
            {
                if (manifestStore.TryGet(project.FullPath, out var stored) && stored is not null)
                {
                    project.Manifest = stored;
                    project.HasManifest = true;
                }
            }

            return cache.Projects;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to read discovery cache (will re-scan)", ex);
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
        catch (Exception ex)
        {
            Log.Warn("Failed to write discovery cache (non-fatal)", ex);
        }
    }
}
