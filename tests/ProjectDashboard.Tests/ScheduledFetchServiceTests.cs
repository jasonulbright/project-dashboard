using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The background fetch against real file-protocol remotes: what it runs, what it skips, and how
/// it classifies a refusal. State lives under the app-data sandbox.
/// </summary>
[Collection("app-data-sandbox")]
public class ScheduledFetchServiceTests
{
    public ScheduledFetchServiceTests() => TestSandbox.ResetDataDir();

    private static readonly TimeSpan Hour = TimeSpan.FromMinutes(60);

    private static ScheduledFetchService NewService(GitService? git = null, RepoBusyRegistry? busy = null)
    {
        var service = new ScheduledFetchService(git ?? new GitService(), busy ?? new RepoBusyRegistry());
        // The tick must decide from its own gates, not from the machine's adapters.
        service.NetworkAvailable = () => true;
        return service;
    }

    private static FetchCandidate CandidateFor(TempRepo clone, TempRepo bare) =>
        new(clone.Path, bare.FileUrl);

    [Fact]
    public async Task ATick_FetchesAndTheBehindCountBecomesReadable()
    {
        using var source = await TempRepo.CreateWithCommitAsync("schedfetch-src");
        using var bare = await TempRepo.CreateBareFromAsync(source, "schedfetch-origin");
        using var clone = await TempRepo.CloneFromAsync(bare, "schedfetch-clone");
        // A commit lands upstream that the clone has never fetched.
        source.WriteFile("news.txt", "fresh\n");
        await source.CommitAllAsync("news");
        await Git.RunAsync(source.Path, "push", bare.FileUrl, "HEAD:main");

        var busy = new RepoBusyRegistry();
        var service = NewService(busy: busy);
        var leaseHeldDuringEvent = true;
        service.RepoFetched += repo => leaseHeldDuringEvent = busy.IsBusy(repo);

        var report = await service.RunTickAsync([CandidateFor(clone, bare)], Hour);

        Assert.Equal(1, report.Fetched);
        Assert.Equal(0, report.Failed);
        Assert.False(leaseHeldDuringEvent);
        var behind = await Git.RunAsync(clone.Path, "rev-list", "--count", "HEAD..@{u}");
        Assert.Equal("1", behind.Trim());
        Assert.Contains("as of the last fetch", service.DescribeRepo(clone.Path));
    }

    /// <summary>Records every git invocation; the scheduler's one permitted verb is asserted on it.</summary>
    private sealed class RecordingGitService : GitService
    {
        public List<string> Invocations { get; } = [];

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Invocations.Add(string.Join(" ", args));
            return base.RunAsync(repoPath, args, environment, ct, timeout);
        }
    }

    [Fact]
    public async Task TheSchedulersOnlyGitVerb_IsFetchPrune()
    {
        using var source = await TempRepo.CreateWithCommitAsync("schedfetch-verb");
        using var bare = await TempRepo.CreateBareFromAsync(source, "schedfetch-verb-origin");
        using var clone = await TempRepo.CloneFromAsync(bare, "schedfetch-verb-clone");

        var git = new RecordingGitService();
        var service = NewService(git);
        await service.RunTickAsync([CandidateFor(clone, bare)], Hour);

        var invocation = Assert.Single(git.Invocations);
        Assert.Equal("fetch --prune", invocation);
    }

    [Fact]
    public async Task ALeasedRepository_IsSkippedWithNoProcessSpawned()
    {
        using var source = await TempRepo.CreateWithCommitAsync("schedfetch-leased");
        using var bare = await TempRepo.CreateBareFromAsync(source, "schedfetch-leased-origin");
        using var clone = await TempRepo.CloneFromAsync(bare, "schedfetch-leased-clone");

        var git = new RecordingGitService();
        var busy = new RepoBusyRegistry();
        var service = NewService(git, busy);
        using var lease = busy.Acquire(clone.Path);

        var report = await service.RunTickAsync([CandidateFor(clone, bare)], Hour);

        Assert.Equal(0, report.Fetched);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task ARepositoryFetchedInsideTheInterval_IsNotFetchedAgain()
    {
        using var source = await TempRepo.CreateWithCommitAsync("schedfetch-interval");
        using var bare = await TempRepo.CreateBareFromAsync(source, "schedfetch-interval-origin");
        using var clone = await TempRepo.CloneFromAsync(bare, "schedfetch-interval-clone");

        var git = new RecordingGitService();
        var service = NewService(git);
        var first = await service.RunTickAsync([CandidateFor(clone, bare)], Hour);
        var second = await service.RunTickAsync([CandidateFor(clone, bare)], Hour);

        Assert.Equal(1, first.Fetched);
        Assert.Equal(0, second.Fetched);
        Assert.Equal(1, second.Skipped);
        Assert.Single(git.Invocations);
    }

    /// <summary>Answers every fetch with a credential refusal without spawning git.</summary>
    private sealed class RefusesAuthGitService : GitService
    {
        public int Calls { get; private set; }

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Calls++;
            return Task.FromResult(new ProcessResult(
                128, "", "fatal: Authentication failed for 'https://example.invalid/owner/repo.git/'",
                TimedOut: false));
        }
    }

    [Fact]
    public async Task ACredentialRefusal_ParksTheRepositoryInsteadOfRetryingForever()
    {
        using var source = await TempRepo.CreateWithCommitAsync("schedfetch-auth");
        using var bare = await TempRepo.CreateBareFromAsync(source, "schedfetch-auth-origin");
        using var clone = await TempRepo.CloneFromAsync(bare, "schedfetch-auth-clone");

        var git = new RefusesAuthGitService();
        var service = NewService(git);
        var candidate = CandidateFor(clone, bare);

        var first = await service.RunTickAsync([candidate], Hour);
        var second = await service.RunTickAsync([candidate], Hour);

        Assert.Equal(1, first.Failed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, git.Calls);
        Assert.Contains("parked", service.DescribeRepo(clone.Path));

        // Toggling the feature is the user acting; the next tick gets a fresh verdict.
        service.ClearParked();
        await service.RunTickAsync([candidate], Hour);
        Assert.Equal(2, git.Calls);
    }

    /// <summary>A kill on timeout is no verdict on the repository; parking it would freeze a healthy one.</summary>
    [Fact]
    public void FailureClassification_SeparatesCredentialVerdictsFromUnansweredQuestions()
    {
        Assert.NotNull(ScheduledFetchService.NonTransientReason(
            new ProcessResult(128, "", "remote: Repository not found.", TimedOut: false)));
        Assert.NotNull(ScheduledFetchService.NonTransientReason(
            new ProcessResult(128, "", "fatal: could not read Username for 'https://github.com'", TimedOut: false)));
        Assert.Null(ScheduledFetchService.NonTransientReason(
            new ProcessResult(-1, "", "fatal: Authentication failed", TimedOut: true)));
        Assert.Null(ScheduledFetchService.NonTransientReason(
            new ProcessResult(128, "", "fatal: unable to access: Could not resolve host", TimedOut: false)));
    }

    /// <summary>
    /// A fetch writes refs/remotes/* and FETCH_HEAD; neither may refresh a card, or every
    /// scheduled fetch becomes a refresh storm. The filter change that would break this fails
    /// here rather than in a live watcher's debounce window.
    /// </summary>
    [Fact]
    public void AWriteUnderRemoteTrackingRefs_SignalsNoRefresh()
    {
        const string root = @"C:\projects";
        Assert.False(ProjectWatcherService.SignalsRefresh(root, root + @"\repo\.git\refs\remotes\origin\main"));
        Assert.False(ProjectWatcherService.SignalsRefresh(root, root + @"\repo\.git\FETCH_HEAD"));
        Assert.True(ProjectWatcherService.SignalsRefresh(root, root + @"\repo\.git\HEAD"));
        Assert.True(ProjectWatcherService.SignalsRefresh(root, root + @"\repo\src\program.cs"));
    }
}
