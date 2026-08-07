using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>File history across renames, blame porcelain parse, and paged/filtered history (L-04, L-05).</summary>
public class GitServiceFileHistoryTests
{
    private readonly GitService _git = new();

    private static Task CommitAsAsync(TempRepo repo, string author, string subject) =>
        repo.GitAsync("commit", "-m", subject, $"--author={author}");

    [Fact]
    public async Task GetFileHistory_FollowsAcrossRename()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hist-rename");
        repo.WriteFile("original.txt", "v1\n");
        await repo.CommitAllAsync("add original");
        await repo.GitAsync("mv", "original.txt", "renamed.txt");
        await repo.CommitAllAsync("rename original to renamed");
        repo.WriteFile("renamed.txt", "v2\n");
        await repo.CommitAllAsync("edit renamed");

        var history = await _git.GetFileHistoryAsync(repo.Path, "renamed.txt", 20);
        var subjects = history.Select(c => c.Message).ToList();
        Assert.Contains("edit renamed", subjects);
        Assert.Contains("rename original to renamed", subjects);
        // --follow reaches back to the commit that introduced the pre-rename path.
        Assert.Contains("add original", subjects);
    }

    [Fact]
    public async Task GetFileHistory_HonorsLimit()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hist-limit");
        for (var i = 0; i < 5; i++)
        {
            repo.WriteFile("f.txt", $"line {i}\n");
            await repo.CommitAllAsync($"edit {i}");
        }

        var history = await _git.GetFileHistoryAsync(repo.Path, "f.txt", 3);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task GetBlame_AttributesAuthorsAndMarksBoundary()
    {
        using var repo = TempRepo.CreateEmptyDir("blame");
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("code.txt", "alpha\nbeta\ngamma\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Alice <alice@example.test>", "root by alice");

        // Bob rewrites only the middle line.
        repo.WriteFile("code.txt", "alpha\nBETA\ngamma\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Bob <bob@example.test>", "bob edits middle");

        var blame = await _git.GetBlameAsync(repo.Path, "code.txt");
        Assert.Equal(3, blame.Count);

        Assert.Equal(1, blame[0].LineNumber);
        Assert.Equal("alpha", blame[0].Text);
        Assert.Equal("Alice", blame[0].Author);
        // The root commit is a blame boundary.
        Assert.True(blame[0].IsBoundary);
        Assert.NotNull(blame[0].Date);

        Assert.Equal("BETA", blame[1].Text);
        Assert.Equal("Bob", blame[1].Author);
        // Bob's commit has a parent, so it is not a boundary.
        Assert.False(blame[1].IsBoundary);

        Assert.Equal("gamma", blame[2].Text);
        Assert.Equal("Alice", blame[2].Author);
        Assert.True(blame[2].IsBoundary);
    }

    [Fact]
    public void ParseBlamePorcelain_ReusesCachedMetadataForRepeatedSha()
    {
        // Two lines from one commit: the metadata block appears only once, then the
        // second line repeats just the header. Both must resolve full author/boundary.
        const string porcelain =
            "1111111111111111111111111111111111111111 1 1 2\n" +
            "author Alice\n" +
            "author-mail <a@x>\n" +
            "author-time 1700000000\n" +
            "author-tz +0000\n" +
            "summary first\n" +
            "boundary\n" +
            "filename f.txt\n" +
            "\tfirst line\n" +
            "1111111111111111111111111111111111111111 2 2\n" +
            "\tsecond line\n";

        var lines = GitService.ParseBlamePorcelain(porcelain);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("Alice", l.Author));
        Assert.All(lines, l => Assert.True(l.IsBoundary));
        Assert.Equal(1, lines[0].LineNumber);
        Assert.Equal(2, lines[1].LineNumber);
        Assert.Equal("second line", lines[1].Text);
    }

    [Fact]
    public async Task GetCommitsPaged_PagesWithHasMoreSignal()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("paged");
        for (var i = 0; i < 6; i++)
        {
            repo.WriteFile("f.txt", $"content {i}\n");
            await repo.CommitAllAsync($"commit {i}");
        }
        // 7 commits total (initial + 6).

        var first = await _git.GetCommitsPagedAsync(repo.Path, skip: 0, count: 3);
        Assert.Equal(3, first.Commits.Count);
        Assert.True(first.HasMore);

        var last = await _git.GetCommitsPagedAsync(repo.Path, skip: 6, count: 3);
        Assert.Single(last.Commits);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task GetCommitsPaged_FiltersByGrepAuthorAndPath()
    {
        using var repo = TempRepo.CreateEmptyDir("paged-filter");
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("a.txt", "a\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Alice <alice@example.test>", "FEAT add a");
        repo.WriteFile("b.txt", "b\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Bob <bob@example.test>", "fix touch b");
        repo.WriteFile("a.txt", "a2\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Alice <alice@example.test>", "FEAT edit a again");

        var byGrep = await _git.GetCommitsPagedAsync(repo.Path, 0, 50, new CommitFilter { MessageGrep = "FEAT" });
        Assert.Equal(2, byGrep.Commits.Count);
        Assert.All(byGrep.Commits, c => Assert.Contains("FEAT", c.Message));

        var byAuthor = await _git.GetCommitsPagedAsync(repo.Path, 0, 50, new CommitFilter { Author = "Bob" });
        Assert.Single(byAuthor.Commits);
        Assert.Equal("Bob", byAuthor.Commits[0].Author);

        var byPath = await _git.GetCommitsPagedAsync(repo.Path, 0, 50, new CommitFilter { Path = "b.txt" });
        Assert.Single(byPath.Commits);
        Assert.Equal("fix touch b", byPath.Commits[0].Message);
    }
}
