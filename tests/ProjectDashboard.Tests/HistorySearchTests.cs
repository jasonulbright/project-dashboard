using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The history search: its results are their own list — the main Commits walk stays contiguous
/// and unfiltered for the surgery depth — and a filter the reader typed is either honored or
/// refused, never silently narrowed.
/// </summary>
public class HistorySearchTests
{
    private static async Task<TempRepo> RepoWithHistoryAsync()
    {
        var repo = await TempRepo.CreateWithCommitAsync("history-search");
        repo.WriteFile("src/core.txt", "one\n");
        await Git.RunAsync(repo.Path, "add", "-A");
        await Git.RunAsync(repo.Path, "-c", "user.name=Ada", "-c", "user.email=ada@example.test",
            "commit", "-m", "core: add the engine");
        repo.WriteFile("docs/readme.txt", "hello\n");
        await Git.RunAsync(repo.Path, "add", "-A");
        await Git.RunAsync(repo.Path, "-c", "user.name=Grace", "-c", "user.email=grace@example.test",
            "commit", "-m", "docs: explain the engine");
        return repo;
    }

    private static async Task<ProjectDetailViewModel> PageOnAsync(TempRepo repo)
    {
        // Seeded the way a scan seeds a card: the History walk comes in as RecentCommits.
        var recent = (await new GitService().GetCommitsPagedAsync(repo.Path, 0, 50)).Commits;
        var page = new ProjectDetailViewModel(null!, new GitService(), null!);
        await page.SetProjectAsync(new ProjectInfo
        {
            DirectoryName = "history-search",
            DisplayName = "history-search",
            FullPath = repo.Path,
            RecentCommits = recent,
        });
        return page;
    }

    [Fact]
    public async Task ASearchByMessageAuthorOrPath_ReturnsExactlyTheMatchingCommits()
    {
        using var repo = await RepoWithHistoryAsync();
        var page = await PageOnAsync(repo);

        page.HistorySearchMessage = "engine";
        await page.SearchHistoryCommand.ExecuteAsync(null);
        Assert.Equal(2, page.HistorySearchResults.Count);
        Assert.Contains("every match", page.HistorySearchStatus);

        page.HistorySearchAuthor = "Ada";
        await page.SearchHistoryCommand.ExecuteAsync(null);
        Assert.Single(page.HistorySearchResults);
        Assert.Contains("core: add the engine", page.HistorySearchResults[0].Message);

        page.HistorySearchAuthor = "";
        page.HistorySearchMessage = "";
        page.HistorySearchPath = "docs/readme.txt";
        await page.SearchHistoryCommand.ExecuteAsync(null);
        Assert.Single(page.HistorySearchResults);
        Assert.Contains("docs: explain the engine", page.HistorySearchResults[0].Message);
    }

    /// <summary>The Commits walk is untouched by a search; only the result list narrows.</summary>
    [Fact]
    public async Task ASearch_NeverFiltersTheMainCommitsWalk()
    {
        using var repo = await RepoWithHistoryAsync();
        var page = await PageOnAsync(repo);
        var walkBefore = page.Commits.Select(c => c.Ref).ToList();

        page.HistorySearchAuthor = "Ada";
        await page.SearchHistoryCommand.ExecuteAsync(null);

        Assert.Equal(walkBefore, page.Commits.Select(c => c.Ref).ToList());
    }

    [Fact]
    public async Task AnUnreadableDateOrAnEmptyFilter_IsRefusedWithoutRunningGit()
    {
        using var repo = await RepoWithHistoryAsync();
        var page = await PageOnAsync(repo);

        await page.SearchHistoryCommand.ExecuteAsync(null);
        Assert.Contains("at least one filter", page.HistorySearchStatus);

        page.HistorySearchSince = "not-a-date";
        await page.SearchHistoryCommand.ExecuteAsync(null);
        Assert.Contains("not a date", page.HistorySearchStatus);
        Assert.Empty(page.HistorySearchResults);
    }

    /// <summary>A bare until-date covers its whole day; midnight would exclude every commit made on it.</summary>
    [Fact]
    public async Task AWholeDayRange_FindsTheCommitsMadeOnThatDay()
    {
        using var repo = await RepoWithHistoryAsync();
        var page = await PageOnAsync(repo);
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        page.HistorySearchSince = today;
        page.HistorySearchUntil = today;
        await page.SearchHistoryCommand.ExecuteAsync(null);

        Assert.Equal(3, page.HistorySearchResults.Count);
    }

    [Fact]
    public async Task SelectingAResultInTheLoadedWindow_SelectsItAbove_AndADeeperOneSaysToPage()
    {
        using var repo = await RepoWithHistoryAsync();
        var page = await PageOnAsync(repo);

        page.HistorySearchAuthor = "Grace";
        await page.SearchHistoryCommand.ExecuteAsync(null);
        page.SelectHistorySearchResultCommand.Execute(page.HistorySearchResults[0]);
        Assert.Equal(page.HistorySearchResults[0].Ref, page.SelectedCommit?.Ref);

        var beyond = new GitCommit { Hash = new string('f', 40), ShortHash = "fffffff" };
        page.SelectHistorySearchResultCommand.Execute(beyond);
        Assert.Contains("Load older commits", page.HistorySearchStatus);
    }
}

/// <summary>
/// The streaming remote operations: git's own progress narration reaches the caller line by
/// line, and the flag that makes a piped git talk at all is on every streaming invocation.
/// </summary>
public class StreamingProgressTests
{
    /// <summary>A real clone from a file remote emits at least its opening stderr line live.</summary>
    [Fact]
    public async Task ACloneWithAProgressCallback_NarratesWhileItRuns()
    {
        using var source = await TempRepo.CreateWithCommitAsync("stream-src");
        using var bare = await TempRepo.CreateBareFromAsync(source, "stream-origin");
        var parent = TestEnv.NewDir("stream-clone");
        var lines = new List<string>();

        var error = await new GitService().CloneAsync(bare.FileUrl, parent, onProgress: lines.Add);

        Assert.Null(error);
        Assert.True(Directory.Exists(Path.Combine(parent, "remote")), "the clone did not land");
        Assert.Contains(lines, l => l.Contains("Cloning into", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordsProgressArgs : GitService
    {
        public List<List<string>> ProgressRuns { get; } = [];

        public override Task<ProcessResult> RunWithProgressAsync(
            string repoPath, IEnumerable<string> args, Action<string> onProgressLine,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            ProgressRuns.Add([.. args]);
            onProgressLine("Receiving objects:  42% (5/12)");
            return Task.FromResult(new ProcessResult(0, "", "", TimedOut: false));
        }
    }

    /// <summary>git volunteers progress only to a terminal; a piped run must ask with --progress.</summary>
    [Fact]
    public async Task EveryStreamingRemoteOperation_AsksGitForProgress()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stream-args");
        var git = new RecordsProgressArgs();

        await git.FetchProgressAsync(repo.Path, _ => { });
        await git.PullProgressAsync(repo.Path, _ => { });

        Assert.Equal(2, git.ProgressRuns.Count);
        Assert.All(git.ProgressRuns, args => Assert.Contains("--progress", args));
    }
}
