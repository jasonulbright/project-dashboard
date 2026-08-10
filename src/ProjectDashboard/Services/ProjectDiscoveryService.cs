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
    /// When the project list this service last handed out was read from disk — the cache's own
    /// stamp when the list was served from cache, the scan's own time when it was not. Null until
    /// either has happened, which is not the same fact as a list of age zero.
    /// </summary>
    public DateTimeOffset? LastDiscoveryAt { get; private set; }

    /// <summary>
    /// What each configured root was, the last time a list was handed out — served from the
    /// cache alongside the projects it describes. Empty until a list has been handed out, which
    /// is not the same fact as no roots being configured.
    /// </summary>
    public IReadOnlyList<RootStatus> LastRootStatuses { get; private set; } = [];

    /// <summary>A discovered repository and the configured root the walk found it under.</summary>
    private readonly record struct RepoCandidate(string Path, string RootPath);

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
    /// Forces a full re-scan, ignoring cache. Virtual for the substituting double the
    /// scan-drain tests park a fan-out in; sealing it leaves those tests unable to hold a
    /// scan in flight.
    /// </summary>
    public virtual async Task<List<ProjectInfo>> ForceRefreshAllAsync(CancellationToken ct = default)
    {
        var settings = settingsService.Load();
        var results = await DiscoverFromDiskAsync(settings, ct);
        SaveCache(results);
        return results;
    }

    /// <summary>Null when the project carries no usable path (remote-only stubs).</summary>
    public async Task<ProjectInfo?> RefreshProjectAsync(ProjectInfo project, CancellationToken ct = default)
    {
        var refreshed = await BuildProjectInfoAsync(project.FullPath, ct);
        if (refreshed is null) return null;
        if (await gitHubService.IsAvailableAsync(ct))
            await ApplyRemoteDataAsync([refreshed], ct);
        return refreshed;
    }

    /// <summary>
    /// Cheap local-only refresh of one repo (git status + commits, no gh) — used by the
    /// file watcher, which fires on every save and must not spawn network calls.
    /// Null when the path is empty.
    /// </summary>
    public Task<ProjectInfo?> RefreshProjectLocalAsync(string repoPath, CancellationToken ct = default)
        => BuildProjectInfoAsync(repoPath, ct);

    /// <summary>
    /// Persists a project's manifest. False when the write did not reach disk — the caller still
    /// holds the only copy of the edit and must not present it as stored.
    /// </summary>
    public virtual Task<bool> SaveManifestAsync(string repoPath, ProjectManifest manifest, CancellationToken ct = default)
        // Manifests live out-of-source under AppPaths.RoamingDir, not in the repo root.
        => Task.FromResult(manifestStore.Save(repoPath, manifest));

    private async Task<List<ProjectInfo>> DiscoverFromDiskAsync(AppSettings settings, CancellationToken ct)
    {
        // Off the calling thread before anything touches the disk: probing a disconnected UNC
        // root blocks for the SMB timeout, and this is awaited from the dispatcher.
        var (candidates, statuses) = await Task.Run(() => WalkRoots(settings, ct), ct);
        LastRootStatuses = statuses;

        // Phase A: local git/file facts, parallel with a small concurrency cap. One semaphore
        // over the COMBINED candidate list — one per root would multiply the cap, and the git
        // process count with it, by the number of roots.
        var semaphore = new SemaphoreSlim(6);
        var tasks = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var project = await BuildProjectInfoAsync(candidate.Path, ct);
                if (project is not null) project.RootPath = candidate.RootPath;
                return project;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = (await Task.WhenAll(tasks))
            .OfType<ProjectInfo>() // scan paths are never empty; keeps the guard's contract explicit
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Phase B: one batched gh call per ~25 GitHub repos (was 3 spawns per repo).
        if (await gitHubService.IsAvailableAsync(ct))
        {
            await ApplyRemoteDataAsync(results, ct);

            // Phase C: surface GitHub repos not cloned locally as Cloud cards.
            if (settings.EnableGitHubDiscovery)
                await AppendRemoteOnlyAsync(results, ct);
        }

        ApplyLocationHints(results);

        return results
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Marks the cards whose display name another card also carries. Recursion and multiple roots
    /// both make duplicate names ordinary, and two cards that read identically describe two
    /// different working trees with nothing on screen to tell them apart. The hint is the
    /// repository's place relative to its root, which is the shortest thing that distinguishes
    /// them; a name nothing else shares gets none, so the grid stays quiet in the common case.
    /// </summary>
    internal static void ApplyLocationHints(IReadOnlyList<ProjectInfo> projects)
    {
        foreach (var sharing in projects.GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var shared = sharing.Count() > 1;
            foreach (var project in sharing)
                project.LocationHint = shared ? LocationOf(project) : "";
        }
    }

    private static string LocationOf(ProjectInfo project)
    {
        if (project.IsRemoteOnly) return project.RemoteSlug;
        if (project.FullPath.Length == 0) return "";

        var root = RepoPaths.Normalize(project.RootPath);
        var full = RepoPaths.Normalize(project.FullPath);
        if (root.Length > 0 && full.Length > root.Length + 1 && RepoPaths.IsAtOrUnder(full, root))
        {
            var relative = Path.GetDirectoryName(full[(root.Length + 1)..]) ?? "";
            return relative.Length > 0 ? $"{Path.GetFileName(root)}\\{relative}" : Path.GetFileName(root);
        }
        return Path.GetDirectoryName(full) ?? full;
    }

    /// <summary>
    /// Walks every configured root in order and collects both the candidates and what each root
    /// turned out to be. A root that is missing or unreadable contributes no candidates and does
    /// not fault the scan: the union of what succeeded is still a better answer than a blank
    /// grid, provided the failures travel with it.
    ///
    /// Candidates are deduplicated by normalized path, so a root nested inside another — or the
    /// same path listed twice — produces one card rather than two.
    /// </summary>
    private static (List<RepoCandidate> Candidates, List<RootStatus> Statuses) WalkRoots(
        AppSettings settings, CancellationToken ct)
    {
        var candidates = new List<RepoCandidate>();
        var statuses = new List<RootStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ProjectRootSettings.Clean(ProjectRootSettings.Effective(settings)))
        {
            ct.ThrowIfCancellationRequested();

            if (!root.Enabled)
            {
                statuses.Add(RootStatus.For(root, RootAvailability.Disabled));
                continue;
            }

            var walk = RepositoryWalk.Run(root, ct);
            var added = 0;
            foreach (var repo in walk.Repositories)
                if (seen.Add(repo))
                {
                    candidates.Add(new RepoCandidate(repo, root.Path));
                    added++;
                }

            statuses.Add(RootStatus.For(root, walk.Availability, added, walk.Truncated, walk.Detail));
        }

        return (candidates, statuses);
    }

    /// <summary>
    /// The account's repositories that no discovered project is a clone of. Identity is the
    /// canonical GitHub slug and nothing else: a folder name names no repository, so an
    /// unrelated local "api" — one with no remote, or one on another host — would otherwise
    /// hide the account's own owner/api behind a card that describes a different tree. A clone
    /// under a renamed folder is still matched, by the slug its remote carries.
    /// </summary>
    internal static List<RemoteRepo> RemotesWithoutALocalClone(
        IReadOnlyList<ProjectInfo> local, IReadOnlyList<RemoteRepo> remotes)
    {
        var localSlugs = new HashSet<string>(
            local.Select(p => p.GitHubSlug).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

        return remotes.Where(r => !localSlugs.Contains(r.NameWithOwner)).ToList();
    }

    /// <summary>
    /// Adds the signed-in user's repositories that have no local clone as remote-only
    /// ("Cloud") entries.
    /// </summary>
    private async Task AppendRemoteOnlyAsync(List<ProjectInfo> local, CancellationToken ct)
    {
        List<RemoteRepo> remotes;
        try { remotes = await gitHubService.GetUserReposAsync(ct); }
        catch (Exception ex) { Log.Warn("remote-only discovery skipped", ex); return; }
        if (remotes.Count == 0) return;

        foreach (var repo in RemotesWithoutALocalClone(local, remotes))
        {
            // A remote-only card has no local path, so there's no manifest to key on —
            // synthesize one from the repo description (the manifest editor is unavailable
            // until it's cloned anyway).
            var manifest = new ProjectManifest { Description = repo.Description };

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

    private async Task<ProjectInfo?> BuildProjectInfoAsync(string dirPath, CancellationToken ct)
    {
        // An empty path would run every git call in the process cwd and key the
        // manifest lookup on "": refuse it here rather than trusting each caller.
        if (string.IsNullOrWhiteSpace(dirPath))
        {
            Log.Warn("project refresh skipped: empty repo path");
            return null;
        }

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

        var card = await gitService.GetCardStateAsync(dirPath, 20, ct);
        project.GitStatus = card.Status;
        project.RecentCommits = card.RecentCommits;

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

        // A non-GitHub remote (GitLab, self-hosted, …) has a remote but no visibility
        // we can classify — "remote", not the misleading "local" (which means no-remote
        // and draws the cloud-off icon). GitHub repos get their real visibility in the batch.
        if (!string.IsNullOrEmpty(project.GitStatus.RemoteUrl) && string.IsNullOrEmpty(project.GitHubSlug))
            project.GitStatus.Visibility = "remote";

        // GitHub-side data (visibility, counts) is applied afterwards in one batch;
        // the issues LIST loads lazily when a detail view actually needs it.
        return project;
    }

    // ── Cache ──────────────────────────────────────────────────────

    /// <summary>
    /// The shape this build writes and is willing to read back. A cache written by a build that
    /// recorded fewer facts per project deserializes without complaint and every card comes back
    /// missing them, so a mismatch is a miss rather than a partial answer.
    /// </summary>
    internal const int CacheSchemaVersion = 2;

    private sealed class DiscoveryCache
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset CachedAt { get; set; }
        public List<ProjectInfo> Projects { get; set; } = [];

        /// <summary>
        /// What each root was when this list was produced. Served with it, or an unreachable root
        /// reads as an empty one for as long as the cache lasts.
        /// </summary>
        public List<RootStatus> Roots { get; set; } = [];
    }

    /// <summary>
    /// The cached scan, or null when there is none to serve. A scan that found no projects is an
    /// answer and is served as one: counting it as absent sends every load and every timer tick
    /// down the full-scan path — a gh availability probe and a remote-repo list included — for a
    /// root that is empty, or that times out because it is unreachable.
    /// </summary>
    private List<ProjectInfo>? LoadCache(int maxAgeSeconds)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;

            var json = File.ReadAllText(CachePath);
            var cache = JsonSerializer.Deserialize<DiscoveryCache>(json, JsonOptions);
            if (cache is null) return null;
            if (cache.SchemaVersion != CacheSchemaVersion) return null;

            var age = DateTimeOffset.Now - cache.CachedAt;
            if (age.TotalSeconds > maxAgeSeconds) return null;

            // Manifests are the store's truth, never the cache's: a manifest saved
            // after the cache was written must not appear reverted on relaunch.
            foreach (var project in cache.Projects)
            {
                // Remote-only entries have no local path, hence no manifest key;
                // reconciling one would invalidate the whole cache on every load.
                if (project.IsRemoteOnly || string.IsNullOrWhiteSpace(project.FullPath))
                    continue;

                if (manifestStore.TryGet(project.FullPath, out var stored) && stored is not null)
                {
                    project.Manifest = stored;
                    project.HasManifest = true;
                }
            }

            LastDiscoveryAt = cache.CachedAt;
            LastRootStatuses = cache.Roots;
            return cache.Projects;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to read discovery cache (will re-scan)", ex);
            return null;
        }
    }

    private void SaveCache(List<ProjectInfo> projects)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var cache = new DiscoveryCache
            {
                SchemaVersion = CacheSchemaVersion,
                CachedAt = DateTimeOffset.Now,
                Projects = projects,
                Roots = [.. LastRootStatuses]
            };
            LastDiscoveryAt = cache.CachedAt;

            // tmp+swap so a crash mid-write cannot truncate the live cache, but no
            // .bak: the cache is fully reconstructible by a re-scan, and LoadCache
            // already falls back to one on any parse failure — a backup would only
            // re-serve stale projects.
            DurableJsonFile.Write(CachePath, JsonSerializer.Serialize(cache, JsonOptions), keepBackup: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to write discovery cache (non-fatal)", ex);
        }
    }
}
