using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

[Collection("app-data-sandbox")]
public class ProjectDiscoveryCacheTests
{
    public ProjectDiscoveryCacheTests()
    {
        TestSandbox.ResetDataDir();

        // Root with no git repos, gh pointed at a nonexistent exe: a fresh cache
        // must be served as-is, and even a regression to the rescan path stays
        // inside the sandbox.
        Directory.CreateDirectory(EmptyRoot);
        new SettingsService().Save(new AppSettings
        {
            ProjectsRootPath = EmptyRoot,
            GhPath = Path.Combine(EmptyRoot, "no-such-gh.exe"),
            RefreshIntervalSeconds = 7200
        });
    }

    private static string EmptyRoot => Path.Combine(TestEnv.Root, "empty-root");

    private static ProjectDiscoveryService NewService(ManifestStore store) =>
        NewService(store, new GitService());

    private static ProjectDiscoveryService NewService(ManifestStore store, GitService git)
    {
        var settings = new SettingsService();
        return new ProjectDiscoveryService(git, new GitHubService(settings), settings, store);
    }

    private static void WriteCache(params object[] projects) =>
        WriteCache(ProjectDiscoveryService.CacheSchemaVersion, DateTimeOffset.Now, projects);

    private static void WriteCache(int schemaVersion, DateTimeOffset cachedAt, params object[] projects)
    {
        var cache = new { SchemaVersion = schemaVersion, CachedAt = cachedAt, Projects = projects };
        File.WriteAllText(AppPaths.DiscoveryCacheFile,
            JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Counts git processes, which is what tells a served cache from a re-scan that found nothing.</summary>
    private sealed class CountingGitService : GitService
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Interlocked.Increment(ref _runs);
            return base.RunAsync(repoPath, args, environment, ct, timeout);
        }
    }

    [Fact]
    public async Task DiscoverAll_RemoteOnlyCacheEntry_ServesCacheInsteadOfRescanning()
    {
        WriteCache(new
        {
            DirectoryName = "cloud-repo",
            DisplayName = "cloud-repo",
            FullPath = "",
            IsRemoteOnly = true,
            RemoteSlug = "someone/cloud-repo",
            HasManifest = true
        });

        var results = await NewService(new ManifestStore()).DiscoverAllAsync();

        // A rescan of the empty root would return zero projects; serving the
        // cache returns exactly the remote-only entry.
        var project = Assert.Single(results);
        Assert.True(project.IsRemoteOnly);
        Assert.Equal("cloud-repo", project.DirectoryName);
        Assert.Equal("someone/cloud-repo", project.RemoteSlug);
    }

    [Fact]
    public async Task Refresh_EmptyPath_ReturnsNullInsteadOfRunningGitInCwd()
    {
        var service = NewService(new ManifestStore());

        Assert.Null(await service.RefreshProjectLocalAsync(""));
        Assert.Null(await service.RefreshProjectLocalAsync("   "));
        Assert.Null(await service.RefreshProjectAsync(new ProjectInfo { FullPath = "" }));
    }

    [Fact]
    public async Task SaveCache_SwapsAtomically_LeavesNoTmpAndNoBak()
    {
        var service = NewService(new ManifestStore());

        // Two saves: the second exercises the replace-over-existing path, which
        // must not retain a .bak (cache content is reconstructible by a re-scan).
        await service.ForceRefreshAllAsync();
        await service.ForceRefreshAllAsync();

        Assert.True(File.Exists(AppPaths.DiscoveryCacheFile));
        Assert.False(File.Exists(AppPaths.DiscoveryCacheFile + ".tmp"));
        Assert.False(File.Exists(AppPaths.DiscoveryCacheFile + ".bak"));
    }

    [Fact]
    public async Task DiscoverAll_CachedLocalEntry_StillReconciledFromManifestStore()
    {
        var localPath = Path.Combine(TestEnv.Root, "local-repo");
        var store = new ManifestStore();
        store.Save(localPath, new ProjectManifest { Description = "from-store" });

        WriteCache(
            new
            {
                DirectoryName = "cloud-repo",
                DisplayName = "cloud-repo",
                FullPath = "",
                IsRemoteOnly = true,
                RemoteSlug = "someone/cloud-repo"
            },
            new
            {
                DirectoryName = "local-repo",
                DisplayName = "local-repo",
                FullPath = localPath,
                IsRemoteOnly = false
            });

        var results = await NewService(store).DiscoverAllAsync();

        Assert.Equal(2, results.Count);
        var local = Assert.Single(results, p => !p.IsRemoteOnly);
        Assert.Equal("from-store", local.Manifest.Description);
        Assert.True(local.HasManifest);
    }

    /// <summary>
    /// A cached scan that found nothing is an answer, not the absence of one. The root holds a
    /// repository the cache does not mention, so a re-scan is the one thing that could not
    /// return empty — and it would have spawned git to find out.
    /// </summary>
    [Fact]
    public async Task DiscoverAll_FreshEmptyCache_IsServedInsteadOfRescanning()
    {
        await WithRepoInRootAsync("served-empty-cache", async () =>
        {
            var git = new CountingGitService();
            WriteCache();

            var results = await NewService(new ManifestStore(), git).DiscoverAllAsync();

            Assert.Empty(results);
            Assert.Equal(0, git.Runs);
        });
    }

    [Fact]
    public async Task DiscoverAll_ExpiredEmptyCache_StillRescans()
    {
        await WithRepoInRootAsync("expired-empty-cache", async () =>
        {
            WriteCache(ProjectDiscoveryService.CacheSchemaVersion, DateTimeOffset.Now.AddHours(-8));

            var results = await NewService(new ManifestStore()).DiscoverAllAsync();

            Assert.Contains(results, p => p.DirectoryName == "expired-empty-cache");
        });
    }

    /// <summary>Runs <paramref name="body"/> with one repository present under the configured root.</summary>
    private static async Task WithRepoInRootAsync(string name, Func<Task> body)
    {
        var repo = Path.Combine(EmptyRoot, name);
        Directory.CreateDirectory(repo);
        await Git.RunAsync(repo, "init", "-b", "main");
        try { await body(); }
        finally { TestEnv.TryDeleteTree(repo); }
    }

    [Fact]
    public async Task DiscoverAll_CacheFromAnotherSchema_IsAMiss()
    {
        WriteCache(ProjectDiscoveryService.CacheSchemaVersion - 1, DateTimeOffset.Now, new
        {
            DirectoryName = "stale-schema",
            DisplayName = "stale-schema",
            FullPath = "",
            IsRemoteOnly = true,
            RemoteSlug = "someone/stale-schema"
        });

        var results = await NewService(new ManifestStore()).DiscoverAllAsync();

        Assert.DoesNotContain(results, p => p.DirectoryName == "stale-schema");
    }
}
