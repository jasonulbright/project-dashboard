using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// How many git processes one repository's card costs, and that cutting them changed no answer.
///
/// A dashboard scan pays every one of these per repository, times the whole projects root, so a
/// read that could have ridden along with another is the scan's dominant cost. The counts below
/// are asserted, not described: a reader that quietly grows a second process reintroduces the
/// cost across the whole grid, and nothing else would notice.
/// </summary>
public class GitServiceScanCostTests
{
    /// <summary>Records every git invocation this service makes, in order.</summary>
    private sealed class CountingGitService : GitService
    {
        public List<string> Invocations { get; } = [];

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var listed = args.ToList();
            var result = await base.RunAsync(repoPath, listed, environment, ct, timeout);
            lock (Invocations) Invocations.Add(string.Join(' ', listed));
            return result;
        }
    }

    [Fact]
    public async Task WorkingState_OnAPrimaryCheckout_CostsOneGitProcess()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("state-cost");
        var git = new CountingGitService();

        var state = await git.GetWorkingStateAsync(repo.Path);

        Assert.NotNull(state);
        Assert.Equal("main", state.Branch);
        Assert.Equal(RepoActivity.None, state.Activity);
        Assert.Single(git.Invocations);
    }

    /// <summary>
    /// The layout shortcut must resolve the same directory `rev-parse --git-dir` would, or the
    /// banner for a merge, rebase, cherry-pick, revert or bisect stops appearing.
    /// </summary>
    [Fact]
    public async Task WorkingState_OnAPrimaryCheckout_StillReadsTheActivityMarker()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("state-marker");
        File.WriteAllText(Path.Combine(repo.Path, ".git", "MERGE_HEAD"), "0123456789abcdef\n");

        var state = await new GitService().GetWorkingStateAsync(repo.Path);

        Assert.NotNull(state);
        Assert.Equal(RepoActivity.Merging, state.Activity);
    }

    /// <summary>
    /// A linked worktree's .git is a FILE naming a directory elsewhere, so the layout shortcut
    /// declines and git is asked — which is the only thing that finds the per-worktree state dir.
    /// </summary>
    [Fact]
    public async Task WorkingState_OnALinkedWorktree_AsksGitForTheStateDirectory()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("state-worktree");
        var linked = Path.Combine(TestEnv.NewDir("linked"), "wt");
        await repo.GitAsync("worktree", "add", "-b", "wt", linked);

        var git = new CountingGitService();
        var gitDir = await git.ResolveGitDirAsync(linked);
        Assert.NotNull(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "0123456789abcdef\n");
        git.Invocations.Clear();

        var state = await git.GetWorkingStateAsync(linked);

        Assert.NotNull(state);
        Assert.Equal(RepoActivity.CherryPicking, state.Activity);
        Assert.Equal(2, git.Invocations.Count);
    }

    [Fact]
    public async Task CardState_ReadsOneRepositoryInFourGitProcesses()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("card-cost");
        var git = new CountingGitService();

        var card = await git.GetCardStateAsync(repo.Path, 20);

        Assert.False(card.Status.HasError);
        Assert.Single(card.RecentCommits);
        Assert.Equal(4, git.Invocations.Count);
    }

    /// <summary>
    /// The window's first row is the tip, so taking the summary's last-commit facts from it must
    /// give the same answer the standalone status read gives.
    /// </summary>
    [Fact]
    public async Task CardState_TipFacts_MatchTheStandaloneStatusRead()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("card-tip");
        repo.WriteFile("second.txt", "more\n");
        await repo.CommitAllAsync("the newest subject");
        var git = new GitService();

        var card = await git.GetCardStateAsync(repo.Path, 20);
        var standalone = await git.GetStatusAsync(repo.Path);

        Assert.Equal("the newest subject", card.Status.LastCommitMessage);
        Assert.Equal(standalone.LastCommitMessage, card.Status.LastCommitMessage);
        Assert.Equal(standalone.LastCommitDate, card.Status.LastCommitDate);
    }

    [Fact]
    public async Task CardState_OnARepositoryWithNoCommits_LeavesTheTipFactsBlank()
    {
        var dir = TestEnv.NewDir("card-empty");
        await Git.RunAsync(dir, "init", "-b", "main");
        var git = new GitService();

        var card = await git.GetCardStateAsync(dir, 20);

        Assert.False(card.Status.HasError);
        Assert.Empty(card.RecentCommits);
        Assert.Null(card.Status.LastCommitDate);
        Assert.Equal("", card.Status.LastCommitMessage);

        var standalone = await git.GetStatusAsync(dir);
        Assert.Null(standalone.LastCommitDate);
        Assert.Equal("", standalone.LastCommitMessage);
    }

    /// <summary>
    /// A window walk that dies partway through — an object missing from the middle of history —
    /// reports no commits, which must not be read as a repository that has none. The summary's
    /// last-commit facts fall back to their own tip read, so a damaged repository still shows the
    /// commit it is parked on instead of presenting as commitless.
    /// </summary>
    [Fact]
    public async Task CardState_WhenTheWindowWalkFails_StillReportsTheTipFacts()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("card-damaged");
        for (var i = 2; i <= 6; i++)
        {
            repo.WriteFile("file.txt", $"line {i}\n");
            await repo.CommitAllAsync($"subject {i}");
        }
        var missing = (await repo.GitAsync("rev-parse", "HEAD~3")).Trim();
        var loose = Path.Combine(repo.Path, ".git", "objects", missing[..2], missing[2..]);
        // git writes loose objects read-only.
        File.SetAttributes(loose, FileAttributes.Normal);
        File.Delete(loose);

        // The damage this fixture depends on: the tip reads fine, the window walk does not.
        Assert.Equal(0, (await Git.TryRunAsync(repo.Path, "log", "-1", "--format=%s")).ExitCode);
        Assert.NotEqual(0, (await Git.TryRunAsync(repo.Path, "log", "-n", "20", "--format=%s")).ExitCode);

        var card = await new GitService().GetCardStateAsync(repo.Path, 20);

        Assert.Equal("subject 6", card.Status.LastCommitMessage);
        Assert.NotNull(card.Status.LastCommitDate);
    }

    /// <summary>
    /// A window of zero rows is a window nobody asked for, not a repository with no commits: the
    /// summary's last-commit facts still come from a tip read of their own.
    /// </summary>
    [Fact]
    public async Task CardState_WithAZeroCommitWindow_StillReportsTheTipFacts()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("card-zero-window");

        var card = await new GitService().GetCardStateAsync(repo.Path, 0);

        Assert.Empty(card.RecentCommits);
        Assert.Equal("initial commit", card.Status.LastCommitMessage);
        Assert.NotNull(card.Status.LastCommitDate);
    }

    /// <summary>
    /// The commit log's fields are separated by a control character no author name or subject can
    /// contain. A printable delimiter splits a name carrying it, and every field after that one
    /// shifts — the subject shown against a commit then belongs to no commit at all.
    /// </summary>
    [Fact]
    public async Task RecentCommits_AuthorAndSubjectCarryingTheOldDelimiter_ParseIntact()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("delimiter");
        repo.WriteFile("pipe.txt", "content\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync(
            "-c", "user.name=Ada|Lovelace", "-c", "user.email=ada@example.invalid",
            "commit", "-m", "fix a|b parsing");

        var commits = await new GitService().GetRecentCommitsAsync(repo.Path, 5);

        Assert.Equal("Ada|Lovelace", commits[0].Author);
        Assert.Equal("fix a|b parsing", commits[0].Message);
    }

    /// <summary>
    /// `git config --get` answers a multiply-set key with its LAST value, and the single-read
    /// resolution must agree — a repository whose remote URL was appended to must not resolve to
    /// the superseded one.
    /// </summary>
    [Fact]
    public async Task DefaultRemote_WithAMultiplySetUrl_TakesTheLastValue()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("multi-url");
        await repo.GitAsync("remote", "add", "origin", "https://example.invalid/first.git");
        await repo.GitAsync("config", "--add", "remote.origin.url", "https://example.invalid/second.git");
        var git = new GitService();

        Assert.Equal("origin", await git.ResolveDefaultRemoteAsync(repo.Path));
        var status = await git.GetStatusAsync(repo.Path);
        Assert.Equal("https://example.invalid/second.git", status.RemoteUrl);
    }

    /// <summary>A push URL is not a remote URL; the pattern must not let one stand in for the other.</summary>
    [Fact]
    public async Task DefaultRemote_WithOnlyAPushUrl_ResolvesToNothing()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("pushurl-only");
        await repo.GitAsync("config", "remote.origin.pushurl", "https://example.invalid/push.git");
        var git = new GitService();

        Assert.Null(await git.ResolveDefaultRemoteAsync(repo.Path));
        Assert.Equal("", (await git.GetStatusAsync(repo.Path)).RemoteUrl);
    }

    /// <summary>A remote whose own name ends in ".url" still parses out of the config key.</summary>
    [Fact]
    public async Task DefaultRemote_WithANameEndingInUrl_ParsesTheNameOut()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("dotted-remote");
        await repo.GitAsync("remote", "add", "mirror.url", "https://example.invalid/mirror.git");
        var git = new GitService();

        Assert.Equal("mirror.url", await git.ResolveDefaultRemoteAsync(repo.Path));
        Assert.Equal("https://example.invalid/mirror.git", (await git.GetStatusAsync(repo.Path)).RemoteUrl);
    }
}
