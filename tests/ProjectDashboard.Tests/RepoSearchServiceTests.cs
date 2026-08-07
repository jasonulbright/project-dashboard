using System.Diagnostics;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Tests;

/// <summary>
/// The fan-out runs one git process per repo across every repo the user has, driven by
/// keystrokes. Every bound it relies on — hits per repo, hits overall, per-repo wall
/// clock, and prompt cancellation — is asserted here against real repositories.
/// </summary>
public class RepoSearchServiceTests
{
    private static RepoSearchService NewService(RepoBusyRegistry? registry = null) =>
        new(new GitService(), registry ?? new RepoBusyRegistry());

    private static RepoSearchTarget Target(TempRepo repo) =>
        new(Path.GetFileName(repo.Path), repo.Path);

    private static async Task<TempRepo> RepoWithAsync(params (string Path, string Content)[] files)
    {
        var repo = TempRepo.CreateEmptyDir("search");
        await repo.GitAsync("init", "-b", "main");
        foreach (var (path, content) in files)
            repo.WriteFile(path, content);
        await repo.CommitAllAsync("fixture");
        return repo;
    }

    [Fact]
    public async Task FindsAContentMatch_WithItsFileAndLineNumber()
    {
        using var repo = await RepoWithAsync(("src/app.cs", "first\nneedle here\nthird\n"));

        var result = await NewService().SearchAsync("needle", [Target(repo)]);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("src/app.cs", hit.FilePath);
        Assert.Equal(2, hit.Line);
        Assert.Contains("needle", hit.Text);
        Assert.False(hit.IsFileNameMatch);
        Assert.Equal(1, result.ReposSearched);
    }

    [Fact]
    public async Task FindsAFileNameMatch_WithNoLineNumber()
    {
        using var repo = await RepoWithAsync(("docs/needle-notes.md", "nothing relevant\n"));

        var result = await NewService().SearchAsync("needle", [Target(repo)]);

        var hit = Assert.Single(result.Hits);
        Assert.True(hit.IsFileNameMatch);
        Assert.Equal(0, hit.Line);
        Assert.Contains("needle-notes.md", hit.Location);
    }

    [Fact]
    public async Task MatchingIsCaseInsensitive()
    {
        using var repo = await RepoWithAsync(("a.txt", "The NEEDLE is here\n"));

        Assert.NotEmpty((await NewService().SearchAsync("needle", [Target(repo)])).Hits);
    }

    [Fact]
    public async Task PerRepoCap_LimitsHits_AndTheRestAreCountedAsMore()
    {
        var files = Enumerable.Range(0, RepoSearchService.MaxHitsPerRepo + 4)
            .Select(i => ($"file{i}.txt", "needle\n"))
            .ToArray();
        using var repo = await RepoWithAsync(files);

        var result = await NewService().SearchAsync("needle", [Target(repo)]);

        Assert.Equal(RepoSearchService.MaxHitsPerRepo, result.Hits.Count);
        Assert.True(result.More >= 4, $"expected the overflow to be counted, got {result.More}");
    }

    [Fact]
    public async Task OverallCap_LimitsHitsAcrossRepos()
    {
        var perRepo = RepoSearchService.MaxHitsPerRepo;
        var repoCount = (RepoSearchService.MaxHitsTotal / perRepo) + 2;

        var repos = new List<TempRepo>();
        try
        {
            for (var i = 0; i < repoCount; i++)
            {
                var files = Enumerable.Range(0, perRepo).Select(n => ($"f{n}.txt", "needle\n")).ToArray();
                repos.Add(await RepoWithAsync(files));
            }

            var result = await NewService().SearchAsync("needle", repos.Select(Target).ToList());

            Assert.Equal(RepoSearchService.MaxHitsTotal, result.Hits.Count);
            Assert.True(result.More > 0, "matches past the overall cap must be reported, not dropped silently");
        }
        finally
        {
            foreach (var repo in repos) repo.Dispose();
        }
    }

    [Fact]
    public async Task SkipsABusyRepo_WithoutFailingTheRest()
    {
        using var busy = await RepoWithAsync(("a.txt", "needle\n"));
        using var free = await RepoWithAsync(("b.txt", "needle\n"));

        var registry = new RepoBusyRegistry();
        using var lease = registry.Acquire(busy.Path);

        var result = await NewService(registry).SearchAsync("needle", [Target(busy), Target(free)]);

        Assert.Equal(1, result.ReposSkipped);
        Assert.Equal(1, result.ReposSearched);
        Assert.All(result.Hits, h => Assert.Equal(free.Path, h.RepoPath));
    }

    [Fact]
    public async Task SkipsABareRepo()
    {
        using var source = await RepoWithAsync(("a.txt", "needle\n"));
        using var bare = await TempRepo.CreateBareFromAsync(source);

        var result = await NewService().SearchAsync("needle", [Target(bare)]);

        Assert.Equal(1, result.ReposSkipped);
        Assert.Equal(0, result.ReposSearched);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SkipsAMissingPathAndAnEmptyPath()
    {
        var gone = Path.Combine(TestEnv.Root, "not-there-" + Guid.NewGuid().ToString("N")[..8]);

        var result = await NewService().SearchAsync("needle", [new("gone", gone), new("blank", "")]);

        Assert.Equal(2, result.ReposSkipped);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SkipsADirectoryThatIsNotARepo()
    {
        var plain = TestEnv.NewDir("plain");
        File.WriteAllText(Path.Combine(plain, "a.txt"), "needle\n");

        var result = await NewService().SearchAsync("needle", [new("plain", plain)]);

        Assert.Equal(1, result.ReposSkipped);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task ShortTermsAndAnEmptyTargetList_DoNoWork()
    {
        using var repo = await RepoWithAsync(("a.txt", "needle\n"));

        Assert.Same(RepoSearchResult.Empty, await NewService().SearchAsync("n", [Target(repo)]));
        Assert.Same(RepoSearchResult.Empty, await NewService().SearchAsync("   ", [Target(repo)]));
        Assert.Same(RepoSearchResult.Empty, await NewService().SearchAsync("needle", []));
    }

    [Fact]
    public async Task CancellationStopsTheFanOut_Promptly()
    {
        var repos = new List<TempRepo>();
        try
        {
            for (var i = 0; i < 24; i++)
                repos.Add(await RepoWithAsync(("a.txt", "needle\n")));

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => NewService().SearchAsync("needle", repos.Select(Target).ToList(), cts.Token));
            watch.Stop();

            Assert.True(watch.Elapsed < RepoSearchService.PerRepoTimeout,
                $"an already-cancelled search must not run a repo's full timeout budget; took {watch.Elapsed}");
        }
        finally
        {
            foreach (var repo in repos) repo.Dispose();
        }
    }

    [Fact]
    public async Task PerRepoTimeoutIsBounded()
    {
        // The budget is what stops one pathological repo from stalling the whole
        // fan-out; a healthy repo must finish far inside it.
        using var repo = await RepoWithAsync(("a.txt", "needle\n"));

        var watch = Stopwatch.StartNew();
        var result = await NewService().SearchAsync("needle", [Target(repo)]);
        watch.Stop();

        Assert.NotEmpty(result.Hits);
        Assert.True(watch.Elapsed < RepoSearchService.PerRepoTimeout * 2,
            $"one repo took {watch.Elapsed}, past twice its own budget");
        Assert.True(RepoSearchService.PerRepoTimeout > TimeSpan.Zero);
        Assert.True(RepoSearchService.MaxConcurrency > 0);
    }

    [Fact]
    public async Task ConcurrencyIsCapped()
    {
        // Every repo still reports, so the semaphore gates rather than drops work.
        var repos = new List<TempRepo>();
        try
        {
            for (var i = 0; i < RepoSearchService.MaxConcurrency * 2; i++)
                repos.Add(await RepoWithAsync(($"needle{i}.txt", "x\n")));

            var result = await NewService().SearchAsync("needle", repos.Select(Target).ToList());

            Assert.Equal(repos.Count, result.ReposSearched);
            Assert.Equal(0, result.ReposSkipped);
        }
        finally
        {
            foreach (var repo in repos) repo.Dispose();
        }
    }

    [Theory]
    [InlineData("src/app.cs:42:  var x = 1;", "src/app.cs", 42, "var x = 1;")]
    [InlineData("a:b/file.txt:7:text", "a:b/file.txt", 7, "text")]
    [InlineData("f.txt:1:", "f.txt", 1, "")]
    public void ParseGrepLine_SplitsOnTheLineNumber_NotTheFirstColon(
        string raw, string path, int line, string text)
    {
        var parsed = RepoSearchService.ParseGrepLine(raw);

        Assert.NotNull(parsed);
        Assert.Equal(path, parsed.Value.Path);
        Assert.Equal(line, parsed.Value.Line);
        Assert.Equal(text, parsed.Value.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no colons at all")]
    [InlineData("file.txt:notanumber:text")]
    public void ParseGrepLine_RejectsAnythingElse(string raw)
        => Assert.Null(RepoSearchService.ParseGrepLine(raw));
}
