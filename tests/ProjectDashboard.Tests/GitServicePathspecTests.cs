using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Every path this service hands git names one file. A bare pathspec is a glob, so a path
/// holding a wildcard character or a bracket range also selects the other paths it happens to
/// match; the reads below would then describe, and the writes revert, a file the caller never
/// named.
///
/// The fix is per-pathspec <c>:(literal)</c> magic, not GIT_LITERAL_PATHSPECS: that variable
/// stops git parsing pathspec magic at all, which strips the rewrite scrub's own
/// <c>:(glob)</c>/<c>:(literal)</c> narrowing down to nothing and makes <c>git check-ignore</c>
/// exit 128 on every path. <see cref="NonInteractiveEnvironment_DoesNotForceLiteralPathspecs"/>
/// pins that.
/// </summary>
public class GitServicePathspecTests
{
    private readonly GitService _git = new();

    private const string BracketPath = "notes[1].txt";
    private const string SiblingPath = "notes1.txt";

    /// <summary>
    /// Two committed files whose names differ only by the bracket range: as a glob, the
    /// bracket one's own name matches the other.
    /// </summary>
    private static async Task<TempRepo> BracketRepoAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile(SiblingPath, "sibling one\n");
        repo.WriteFile(BracketPath, "bracket one\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "both files");
        repo.WriteFile(SiblingPath, "sibling TWO\n");
        repo.WriteFile(BracketPath, "bracket TWO\n");
        return repo;
    }

    [Fact]
    public async Task GetFileDiffRaw_ForABracketedName_ReadsOnlyThatFile()
    {
        using var repo = await BracketRepoAsync("pathspec-raw");

        var raw = await _git.GetFileDiffRawAsync(repo.Path, BracketPath, staged: false);

        Assert.NotNull(raw);
        Assert.Contains("bracket TWO", raw);
        Assert.DoesNotContain("sibling TWO", raw);
        Assert.Single(raw.Split("diff --git ").Skip(1));
    }

    [Fact]
    public async Task GetFileDiff_ForABracketedName_ParsesThatFileNotItsGlobSibling()
    {
        using var repo = await BracketRepoAsync("pathspec-parsed");
        var state = await _git.GetWorkingStateAsync(repo.Path);
        var file = state!.Files.First(f => f.Path == BracketPath);

        var diff = await _git.GetFileDiffAsync(repo.Path, file, staged: false);

        Assert.NotNull(diff);
        Assert.Equal(BracketPath, diff.Path);
        Assert.Contains(diff.Lines, l => l.Text == "bracket TWO");
    }

    /// <summary>
    /// The destructive one. `git restore` on a glob pathspec reverts every path the glob hits,
    /// so a discard aimed at the bracketed file would throw away the sibling's edit too.
    /// </summary>
    [Fact]
    public async Task Discard_ForABracketedName_LeavesTheGlobSiblingAlone()
    {
        using var repo = await BracketRepoAsync("pathspec-discard");
        var state = await _git.GetWorkingStateAsync(repo.Path);
        var file = state!.Files.First(f => f.Path == BracketPath);

        Assert.True((await _git.DiscardAsync(repo.Path, file)).Success);

        Assert.Equal("bracket one\n", repo.ReadFile(BracketPath));
        Assert.Equal("sibling TWO\n", repo.ReadFile(SiblingPath));
    }

    [Fact]
    public async Task Stage_ForABracketedName_StagesOnlyThatFile()
    {
        using var repo = await BracketRepoAsync("pathspec-stage");

        Assert.True((await _git.StageAsync(repo.Path, BracketPath)).Success);

        var state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Equal([BracketPath], state!.Staged.Select(f => f.Path));
    }

    [Fact]
    public async Task FileHistory_ForABracketedName_ExcludesTheGlobSiblingsCommits()
    {
        using var repo = TempRepo.CreateEmptyDir("pathspec-history");
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile(SiblingPath, "sibling\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "sibling only");
        repo.WriteFile(BracketPath, "bracket\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "bracket only");

        var history = await _git.GetFileHistoryAsync(repo.Path, BracketPath, 20);

        Assert.Equal(["bracket only"], history.Commits.Select(c => c.Message));
    }

    /// <summary>
    /// The rewrite scrub narrows `git grep` with magic pathspecs and runs it under this
    /// dictionary. GIT_LITERAL_PATHSPECS in it would make every one of those pathspecs match
    /// no path, and a grep that searched nothing reports a clean bill it did not earn.
    /// </summary>
    [Fact]
    public void NonInteractiveEnvironment_DoesNotForceLiteralPathspecs()
    {
        Assert.DoesNotContain("GIT_LITERAL_PATHSPECS", GitService.NonInteractiveEnvironment.Keys);
    }

    /// <summary>Magic pathspecs still narrow a grep run under the application-wide git environment.</summary>
    [Fact]
    public async Task GlobPathspecs_StillNarrowAGrepUnderTheApplicationEnvironment()
    {
        using var repo = TempRepo.CreateEmptyDir("pathspec-scrub");
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("keep.txt", "needle\n");
        repo.WriteFile("skip.md", "needle\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "two needles");

        var result = await ProcessRunner.RunAsync("git",
            ["grep", "-l", "needle", "HEAD", "--", ":(glob)**/*.txt"],
            repo.Path, TimeSpan.FromSeconds(30), GitService.NonInteractiveEnvironment);

        Assert.True(result.Success, result.FirstError);
        Assert.Contains("keep.txt", result.StdOut);
        Assert.DoesNotContain("skip.md", result.StdOut);
    }

    /// <summary>
    /// `git check-ignore` and `git blame` take a pathname, not a pathspec, and reject magic
    /// outright; both stay bare and both already resolve a bracketed name to itself.
    /// </summary>
    [Fact]
    public async Task CheckIgnoreAndBlame_ResolveABracketedNameToItself()
    {
        using var repo = await BracketRepoAsync("pathspec-pathname");
        // check-ignore skips tracked paths, so the ignore rule is proven on untracked ones.
        repo.WriteFile(".gitignore", "spare1.txt\n");
        repo.WriteFile("spare1.txt", "");
        repo.WriteFile("spare[1].txt", "");

        Assert.True(await _git.CheckIgnoreAsync(repo.Path, "spare1.txt"));
        Assert.False(await _git.CheckIgnoreAsync(repo.Path, "spare[1].txt"));

        var blame = await _git.GetBlameAsync(repo.Path, BracketPath);
        Assert.Equal(["bracket TWO"], blame.Lines.Select(l => l.Text));
    }
}
