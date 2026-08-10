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

    private static SearchScope Scope(SearchContentScope content) => new(content, SearchBreadth.Portfolio);

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

        var result = await NewService().SearchAsync("needle", [Target(repo)], SearchScope.Default);

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

        var result = await NewService().SearchAsync("needle", [Target(repo)], SearchScope.Default);

        var hit = Assert.Single(result.Hits);
        Assert.True(hit.IsFileNameMatch);
        Assert.Equal(0, hit.Line);
        Assert.Contains("needle-notes.md", hit.Location);
    }

    [Fact]
    public async Task MatchingIsCaseInsensitive()
    {
        using var repo = await RepoWithAsync(("a.txt", "The NEEDLE is here\n"));

        Assert.NotEmpty((await NewService().SearchAsync("needle", [Target(repo)], SearchScope.Default)).Hits);
    }

    [Fact]
    public async Task PerRepoCap_LimitsHits_AndTheRestAreCountedAsMore()
    {
        var files = Enumerable.Range(0, RepoSearchService.MaxHitsPerRepo + 4)
            .Select(i => ($"file{i}.txt", "needle\n"))
            .ToArray();
        using var repo = await RepoWithAsync(files);

        var result = await NewService().SearchAsync("needle", [Target(repo)], SearchScope.Default);

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

            var result = await NewService().SearchAsync(
                "needle", repos.Select(Target).ToList(), SearchScope.Default);

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

        var result = await NewService(registry).SearchAsync(
            "needle", [Target(busy), Target(free)], SearchScope.Default);

        Assert.Equal(1, result.ReposSkipped);
        Assert.Equal(1, result.ReposSearched);
        Assert.All(result.Hits, h => Assert.Equal(free.Path, h.RepoPath));
    }

    [Fact]
    public async Task SkipsABareRepo()
    {
        using var source = await RepoWithAsync(("a.txt", "needle\n"));
        using var bare = await TempRepo.CreateBareFromAsync(source);

        var result = await NewService().SearchAsync("needle", [Target(bare)], SearchScope.Default);

        Assert.Equal(1, result.ReposSkipped);
        Assert.Equal(0, result.ReposSearched);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SkipsAMissingPathAndAnEmptyPath()
    {
        var gone = Path.Combine(TestEnv.Root, "not-there-" + Guid.NewGuid().ToString("N")[..8]);

        var result = await NewService().SearchAsync(
            "needle", [new("gone", gone), new("blank", "")], SearchScope.Default);

        Assert.Equal(2, result.ReposSkipped);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SkipsADirectoryThatIsNotARepo()
    {
        var plain = TestEnv.NewDir("plain");
        File.WriteAllText(Path.Combine(plain, "a.txt"), "needle\n");

        var result = await NewService().SearchAsync("needle", [new("plain", plain)], SearchScope.Default);

        Assert.Equal(1, result.ReposSkipped);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task ShortTermsAndAnEmptyTargetList_DoNoWork()
    {
        using var repo = await RepoWithAsync(("a.txt", "needle\n"));

        Assert.Same(RepoSearchResult.Empty,
            await NewService().SearchAsync("n", [Target(repo)], SearchScope.Default));
        Assert.Same(RepoSearchResult.Empty,
            await NewService().SearchAsync("   ", [Target(repo)], SearchScope.Default));
        Assert.Same(RepoSearchResult.Empty,
            await NewService().SearchAsync("needle", [], SearchScope.Default));
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
                () => NewService().SearchAsync(
                    "needle", repos.Select(Target).ToList(), SearchScope.Default, cts.Token));
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
        // fan-out. It covers the repo, not each git invocation inside it: a healthy
        // repo must finish inside one budget, not two.
        using var repo = await RepoWithAsync(("a.txt", "needle\n"));

        var watch = Stopwatch.StartNew();
        var result = await NewService().SearchAsync("needle", [Target(repo)], SearchScope.Default);
        watch.Stop();

        Assert.NotEmpty(result.Hits);
        Assert.True(watch.Elapsed < RepoSearchService.PerRepoTimeout,
            $"one repo took {watch.Elapsed}, past its own budget");
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

            var result = await NewService().SearchAsync(
                "needle", repos.Select(Target).ToList(), SearchScope.Default);

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

    // ── Scope ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// One repository holding one file of each kind, each with a term nothing else carries, so a
    /// scope's answer is exactly the subset it is supposed to read. The ignore rule names an
    /// extension rather than a filename: an ignore file quoting a search term would put that term
    /// in tracked content and give every scope a hit it did not earn.
    /// </summary>
    private static async Task<TempRepo> ThreeKindsRepoAsync()
    {
        var repo = TempRepo.CreateEmptyDir("search-scope");
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile(".gitignore", "*.hidden\n");
        repo.WriteFile("alpha-file.txt", "needlealpha lives here\n");
        await repo.CommitAllAsync("fixture");
        repo.WriteFile("beta-file.txt", "needlebeta lives here\n");
        repo.WriteFile("gamma-file.hidden", "needlegamma lives here\n");
        return repo;
    }

    /// <summary>
    /// The content half, scope by scope. Each row is one of the three files and one of the three
    /// scopes: tracked reads the index, +untracked adds what the ignore rules let through, and
    /// all files adds what they exclude.
    /// </summary>
    [Theory]
    [InlineData(SearchContentScope.Tracked, "needlealpha", SearchFileScope.Tracked)]
    [InlineData(SearchContentScope.Tracked, "needlebeta", null)]
    [InlineData(SearchContentScope.Tracked, "needlegamma", null)]
    [InlineData(SearchContentScope.WithUntracked, "needlealpha", SearchFileScope.Tracked)]
    [InlineData(SearchContentScope.WithUntracked, "needlebeta", SearchFileScope.Untracked)]
    [InlineData(SearchContentScope.WithUntracked, "needlegamma", null)]
    [InlineData(SearchContentScope.Everything, "needlealpha", SearchFileScope.Tracked)]
    [InlineData(SearchContentScope.Everything, "needlebeta", SearchFileScope.Untracked)]
    [InlineData(SearchContentScope.Everything, "needlegamma", SearchFileScope.Ignored)]
    public async Task EachScopeReadsItsOwnSubsetOfContent_AndLabelsWhatItFound(
        SearchContentScope scope, string term, SearchFileScope? expected)
    {
        using var repo = await ThreeKindsRepoAsync();

        var result = await NewService().SearchAsync(term, [Target(repo)], Scope(scope));

        if (expected is null)
        {
            Assert.Empty(result.Hits);
            return;
        }

        var hit = Assert.Single(result.Hits);
        Assert.False(hit.IsFileNameMatch);
        Assert.Equal(expected, hit.FileScope);
    }

    /// <summary>The filename half takes the same three scopes, from the same listings.</summary>
    [Theory]
    [InlineData(SearchContentScope.Tracked, "alpha-file", SearchFileScope.Tracked)]
    [InlineData(SearchContentScope.Tracked, "beta-file", null)]
    [InlineData(SearchContentScope.Tracked, "gamma-file", null)]
    [InlineData(SearchContentScope.WithUntracked, "beta-file", SearchFileScope.Untracked)]
    [InlineData(SearchContentScope.WithUntracked, "gamma-file", null)]
    [InlineData(SearchContentScope.Everything, "beta-file", SearchFileScope.Untracked)]
    [InlineData(SearchContentScope.Everything, "gamma-file", SearchFileScope.Ignored)]
    public async Task EachScopeReadsItsOwnSubsetOfFileNames_AndLabelsWhatItFound(
        SearchContentScope scope, string term, SearchFileScope? expected)
    {
        using var repo = await ThreeKindsRepoAsync();

        var result = await NewService().SearchAsync(term, [Target(repo)], Scope(scope));

        if (expected is null)
        {
            Assert.Empty(result.Hits);
            return;
        }

        var hit = Assert.Single(result.Hits);
        Assert.True(hit.IsFileNameMatch);
        Assert.Equal(expected, hit.FileScope);
    }

    /// <summary>
    /// A row from an ignored file has to say so in the row, not only in a scope switch somewhere
    /// above it: a hit in obj/ drawn like a hit in src/ reads as source.
    /// </summary>
    [Fact]
    public async Task AnIgnoredRowSaysItIsIgnored_AndATrackedRowClaimsNothing()
    {
        using var repo = await ThreeKindsRepoAsync();
        var service = NewService();

        var ignored = Assert.Single(
            (await service.SearchAsync("needlegamma", [Target(repo)], Scope(SearchContentScope.Everything))).Hits);
        var tracked = Assert.Single(
            (await service.SearchAsync("needlealpha", [Target(repo)], Scope(SearchContentScope.Everything))).Hits);

        Assert.Equal("ignored", ignored.ScopeLabel);
        Assert.Contains("ignored", ignored.LocationWithScope);
        Assert.Equal("", tracked.ScopeLabel);
        Assert.Equal(tracked.Location, tracked.LocationWithScope);
    }

    /// <summary>
    /// The flags are git's own, and they are what the three scopes are: without --untracked git
    /// reads the index, with it the working tree, and --no-exclude-standard is what drops the
    /// ignore rules. Pinned as arguments because a scope that silently lost a flag would return a
    /// narrower answer under a wider label.
    /// </summary>
    [Fact]
    public void TheScopeSwitchesAreGitsOwnFlags()
    {
        Assert.DoesNotContain("--untracked", RepoSearchService.GrepArgs(SearchContentScope.Tracked, "t"));
        Assert.DoesNotContain("--no-exclude-standard", RepoSearchService.GrepArgs(SearchContentScope.Tracked, "t"));

        var withUntracked = RepoSearchService.GrepArgs(SearchContentScope.WithUntracked, "t");
        Assert.Contains("--untracked", withUntracked);
        Assert.DoesNotContain("--no-exclude-standard", withUntracked);

        var everything = RepoSearchService.GrepArgs(SearchContentScope.Everything, "t");
        Assert.Contains("--untracked", everything);
        Assert.Contains("--no-exclude-standard", everything);

        // -I and -m 1 matter more as the scope widens, not less: the widest one reads binaries
        // and generated files that would otherwise each contribute a page of matches.
        foreach (var scope in Enum.GetValues<SearchContentScope>())
        {
            var args = RepoSearchService.GrepArgs(scope, "t");
            Assert.Contains("-I", args);
            Assert.Contains("-m", args);
        }

        Assert.Contains("--exclude-standard", RepoSearchService.UntrackedListArgs);
        Assert.DoesNotContain("--exclude-standard", RepoSearchService.IgnoredListArgs);
    }

    /// <summary>
    /// The widest scope walks build output, so it takes a shorter budget and a lower hit cap than
    /// the tracked default. Both are asserted as an ordering rather than as numbers: the point is
    /// that widening the scope never buys a bigger share of the fan-out.
    /// </summary>
    [Fact]
    public void TheWidestScopeCostsLessThanTheDefault_NotMore()
    {
        Assert.True(RepoSearchService.WidePerRepoTimeout < RepoSearchService.PerRepoTimeout);
        Assert.True(RepoSearchService.WideMaxHitsPerRepo < RepoSearchService.MaxHitsPerRepo);

        Assert.Equal(RepoSearchService.WidePerRepoTimeout,
            RepoSearchService.TimeoutFor(SearchContentScope.Everything));
        Assert.Equal(RepoSearchService.PerRepoTimeout,
            RepoSearchService.TimeoutFor(SearchContentScope.Tracked));
        Assert.Equal(RepoSearchService.PerRepoTimeout,
            RepoSearchService.TimeoutFor(SearchContentScope.WithUntracked));

        Assert.Equal(RepoSearchService.WideMaxHitsPerRepo,
            RepoSearchService.HitsPerRepoFor(SearchContentScope.Everything));
        Assert.Equal(RepoSearchService.MaxHitsPerRepo,
            RepoSearchService.HitsPerRepoFor(SearchContentScope.Tracked));
    }

    /// <summary>
    /// A built repository's ignored tree is most of what the widest scope can see. The cap is
    /// spent on the tracked file first and the rest is reported as overflow, so the one match in
    /// source is not pushed off the list by fifty in obj/.
    /// </summary>
    [Fact]
    public async Task TheWidestScope_SpendsItsCapOnTrackedFilesAndReportsTheOverflow()
    {
        var repo = TempRepo.CreateEmptyDir("search-wide");
        try
        {
            await repo.GitAsync("init", "-b", "main");
            repo.WriteFile(".gitignore", "obj/\n");
            repo.WriteFile("src/app.cs", "needlewide in source\n");
            await repo.CommitAllAsync("fixture");
            for (var i = 0; i < 40; i++)
                repo.WriteFile($"obj/Debug/gen{i}.cs", "needlewide in build output\n");

            var watch = Stopwatch.StartNew();
            var result = await NewService().SearchAsync(
                "needlewide", [Target(repo)], Scope(SearchContentScope.Everything));
            watch.Stop();

            Assert.Equal(RepoSearchService.WideMaxHitsPerRepo, result.Hits.Count);
            Assert.Equal(SearchFileScope.Tracked, result.Hits[0].FileScope);
            Assert.Equal("src/app.cs", result.Hits[0].FilePath);
            Assert.True(result.More > 0, "the matches past the cap must be counted, not dropped silently");
            Assert.True(watch.Elapsed < RepoSearchService.WidePerRepoTimeout * 2,
                $"the widest scope took {watch.Elapsed} against a {RepoSearchService.WidePerRepoTimeout} budget");
        }
        finally
        {
            repo.Dispose();
        }
    }

    [Fact]
    public async Task CancellationStopsTheWidestScope_Promptly()
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
                () => NewService().SearchAsync(
                    "needle", repos.Select(Target).ToList(), Scope(SearchContentScope.Everything), cts.Token));
            watch.Stop();

            Assert.True(watch.Elapsed < RepoSearchService.WidePerRepoTimeout,
                $"an already-cancelled wide search must not run a repo's full budget; took {watch.Elapsed}");
        }
        finally
        {
            foreach (var repo in repos) repo.Dispose();
        }
    }

    // ── Nested repositories ─────────────────────────────────────────────────────

    /// <summary>
    /// git stops at a directory that holds its own repository — it is listed, never descended
    /// into — so the parent's search cannot report the child's content at any scope. The child is
    /// its own target with its own invocation, which is where its matches come from. Asserted
    /// rather than assumed: the widest scope drops the ignore rules, and if it also descended,
    /// every match inside a nested checkout would be attributed to the repository above it.
    /// </summary>
    [Theory]
    [InlineData(SearchContentScope.Tracked)]
    [InlineData(SearchContentScope.WithUntracked)]
    [InlineData(SearchContentScope.Everything)]
    public async Task ANestedRepositorysContentNeverComesThroughItsParent(SearchContentScope scope)
    {
        using var outer = await RepoWithAsync(("outer.txt", "nothing to see\n"));
        var inner = Path.Combine(outer.Path, "nested");
        Directory.CreateDirectory(inner);
        await Git.RunAsync(inner, "init", "-b", "main");
        File.WriteAllText(Path.Combine(inner, "inner.txt"), "needlenested lives here\n");
        await Git.RunAsync(inner, "add", "-A");
        await Git.RunAsync(inner, "commit", "-m", "inner");

        var fromParent = await NewService().SearchAsync("needlenested", [Target(outer)], Scope(scope));
        Assert.Empty(fromParent.Hits);

        var fromItself = await NewService().SearchAsync(
            "needlenested", [new RepoSearchTarget("nested", inner)], Scope(scope));
        var hit = Assert.Single(fromItself.Hits);
        Assert.Equal(inner, hit.RepoPath);
    }

    // ── Outcome states ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs git for real, and answers the named subcommand the way <paramref name="answer"/> says —
    /// in <paramref name="inRepo"/> only, so one repository's failure can be told apart from the
    /// fan-out's.
    /// </summary>
    private sealed class AnsweringGitService(
        string command, string inRepo, Func<ProcessResult, ProcessResult> answer) : GitService
    {
        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            var result = await base.RunAsync(repoPath, list, environment, ct, timeout);
            var mine = string.Equals(repoPath, inRepo, StringComparison.OrdinalIgnoreCase);
            return mine && list.Contains(command) ? answer(result) : result;
        }
    }

    /// <summary>
    /// A repository whose read errored is not a repository with no matches, and it is not a
    /// repository that was never opened either. It reports as its own state, and the repositories
    /// beside it still report theirs.
    /// </summary>
    [Fact]
    public async Task ARepoWhoseReadFailed_IsCountedApartFromASkipAndDoesNotHideTheOthers()
    {
        using var broken = await RepoWithAsync(("needle-a.txt", "needle\n"));
        using var fine = await RepoWithAsync(("needle-b.txt", "needle\n"));

        var service = new RepoSearchService(
            new AnsweringGitService("grep", broken.Path,
                r => r with { ExitCode = 128, StdErr = "fatal: bad revision" }),
            new RepoBusyRegistry());

        var result = await service.SearchAsync(
            "needle", [Target(broken), Target(fine)], SearchScope.Default);

        Assert.Equal(1, result.ReposFailed);
        Assert.Equal(1, result.ReposSearched);
        Assert.Equal(0, result.ReposSkipped);
        // The healthy repository's content hit is untouched by its neighbour's failure, and the
        // filename half of the broken one ran before the failure, so what it found still reports.
        Assert.Contains(result.Hits, h => h.RepoPath == fine.Path && !h.IsFileNameMatch);
        Assert.Contains(result.Hits, h => h.RepoPath == broken.Path && h.IsFileNameMatch);
        Assert.True(result.IsPartial);
    }

    /// <summary>
    /// A read the budget cut off leaves a prefix of an answer. The repository is reported as cut
    /// short rather than as searched: a partial result presented as a complete one is the failure
    /// this whole surface is designed against.
    /// </summary>
    [Fact]
    public async Task ARepoWhoseBudgetRanOut_IsReportedAsCutShortRatherThanSearched()
    {
        using var repo = await RepoWithAsync(("a.txt", "needle\n"));

        var service = new RepoSearchService(
            new AnsweringGitService("grep", repo.Path, r => r with { TimedOut = true }),
            new RepoBusyRegistry());

        var result = await service.SearchAsync("needle", [Target(repo)], SearchScope.Default);

        Assert.Equal(1, result.ReposTruncated);
        Assert.Equal(0, result.ReposSearched);
        Assert.Equal(0, result.ReposFailed);
        Assert.True(result.IsPartial);
    }

    /// <summary>A capture the budget cut short is a partial listing, and it is reported the same way.</summary>
    [Fact]
    public async Task ARepoWhoseListingWasTruncated_IsReportedAsCutShort()
    {
        using var repo = await RepoWithAsync(("needle-a.txt", "x\n"));

        var service = new RepoSearchService(
            new AnsweringGitService("ls-files", repo.Path, r => r with { Truncated = true }),
            new RepoBusyRegistry());

        var result = await service.SearchAsync("needle", [Target(repo)], SearchScope.Default);

        Assert.Equal(1, result.ReposTruncated);
        Assert.Equal(0, result.ReposSearched);
    }

    /// <summary>
    /// A listing cut short is a prefix, and a prefix cannot say what a path outside it is. Under a
    /// scope that reads the label off those listings, a content pass over an incomplete one would
    /// put "ignored" beside tracked source — so the pass does not run and the repository reports as
    /// cut short instead of guessing.
    /// </summary>
    [Fact]
    public async Task AnIncompleteListing_StopsTheContentPassRatherThanMislabelItsRows()
    {
        using var repo = await ThreeKindsRepoAsync();

        var service = new RepoSearchService(
            new AnsweringGitService("ls-files", repo.Path, r => r with { Truncated = true }),
            new RepoBusyRegistry());

        var wide = await service.SearchAsync(
            "needlealpha", [Target(repo)], Scope(SearchContentScope.Everything));

        Assert.Empty(wide.Hits);
        Assert.Equal(1, wide.ReposTruncated);
        Assert.Equal(0, wide.ReposSearched);

        // The narrowest scope reads the index and nothing else, so every row it returns is tracked
        // by construction and a short listing cannot mislabel one. It still reports as cut short.
        var narrow = await service.SearchAsync("needlealpha", [Target(repo)], SearchScope.Default);

        var hit = Assert.Single(narrow.Hits);
        Assert.Equal(SearchFileScope.Tracked, hit.FileScope);
        Assert.Equal(1, narrow.ReposTruncated);
    }

    /// <summary>A whole answer claims nothing about time it did not run out of.</summary>
    [Fact]
    public async Task AWholeAnswerIsNotPartial()
    {
        using var repo = await RepoWithAsync(("a.txt", "needle\n"));

        var result = await NewService().SearchAsync("needle", [Target(repo)], SearchScope.Default);

        Assert.Equal(1, result.ReposSearched);
        Assert.Equal(0, result.ReposTruncated);
        Assert.Equal(0, result.ReposFailed);
        Assert.False(result.IsPartial);
    }
}
