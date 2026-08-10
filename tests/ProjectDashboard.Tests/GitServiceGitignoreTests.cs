using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>.gitignore read/save/append and check-ignore.</summary>
public class GitServiceGitignoreTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task GetGitignore_Absent_IsNull()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-absent");
        Assert.Null(await _git.GetGitignoreAsync(repo.Path));
    }

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-save");
        await _git.SaveGitignoreAsync(repo.Path, "bin/\nobj/\n");
        Assert.Equal("bin/\nobj/\n", await _git.GetGitignoreAsync(repo.Path));
    }

    [Fact]
    public async Task AppendIgnoreEntry_AddsOnceAndIsIdempotent()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-append");
        await _git.SaveGitignoreAsync(repo.Path, "bin/\n");

        await _git.AppendIgnoreEntryAsync(repo.Path, "*.log");
        await _git.AppendIgnoreEntryAsync(repo.Path, "*.log");   // already present — no-op

        var content = await _git.GetGitignoreAsync(repo.Path);
        var occurrences = content!.Split('\n').Count(l => l.Trim() == "*.log");
        Assert.Equal(1, occurrences);
        Assert.StartsWith("bin/\n", content);
    }

    [Fact]
    public async Task AppendIgnoreEntry_CreatesFileWhenAbsent()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-create");
        await _git.AppendIgnoreEntryAsync(repo.Path, "node_modules/");
        Assert.Equal("node_modules/\n", await _git.GetGitignoreAsync(repo.Path));
    }

    /// <summary>
    /// The dedupe is silent in the file, so the call is what has to say whether anything was
    /// written — a caller reporting "ignored" over a no-op would tell a reader a rule was added
    /// that was already there.
    /// </summary>
    [Fact]
    public async Task AppendIgnoreEntry_SaysWhetherItWrote()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-append-reports");

        Assert.True(await _git.AppendIgnoreEntryAsync(repo.Path, "*.log"));
        Assert.False(await _git.AppendIgnoreEntryAsync(repo.Path, "*.log"));
        Assert.False(await _git.AppendIgnoreEntryAsync(repo.Path, "  *.log  "));
    }

    // ── Composing a line for one path ───────────────────────────────────────

    [Theory]
    [InlineData("notes.txt", "/notes.txt")]
    [InlineData("src/inner.txt", "/src/inner.txt")]
    [InlineData(@"src\inner.txt", "/src/inner.txt")]
    // Only '[' opens a class, so ']' is literal and is left alone.
    [InlineData("a[1].txt", @"/a\[1].txt")]
    [InlineData("star*.txt", @"/star\*.txt")]
    [InlineData("what?.txt", @"/what\?.txt")]
    [InlineData(@"back\slash/x.txt", "/back/slash/x.txt")]
    [InlineData("trailing ", @"/trailing\ ")]
    [InlineData("two  ", @"/two\ \ ")]
    public void IgnoreLineForPath_AnchorsAtTheRootAndEscapesEveryGlobCharacter(string path, string expected) =>
        Assert.Equal(expected, GitService.IgnoreLineForPath(path));

    [Theory]
    [InlineData("log", "*.log")]
    [InlineData(".log", "*.log")]
    [InlineData("c[1]", @"*.c\[1]")]
    public void IgnoreLineForExtension_IsAGlobOverTheExtensionAlone(string extension, string expected) =>
        Assert.Equal(expected, GitService.IgnoreLineForExtension(extension));

    /// <summary>
    /// A path holding a bracket is a character class when it is written to .gitignore verbatim,
    /// so the rule would miss the file it was added for and catch a different one. git itself is
    /// the judge of whether the escaping worked.
    /// </summary>
    [Fact]
    public async Task IgnoreLineForPath_APathHoldingGlobCharacters_IgnoresThatPathAndNoOther()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-escape");
        repo.WriteFile("a[1].txt", "x\n");
        repo.WriteFile("a1.txt", "x\n");

        await _git.AppendIgnoreEntryAsync(repo.Path, GitService.IgnoreLineForPath("a[1].txt"));

        Assert.Equal(IgnoreState.Ignored, (await _git.CheckIgnoreAsync(repo.Path, "a[1].txt")).State);
        // Unescaped, "a[1].txt" is a character class that matches this one instead.
        Assert.Equal(IgnoreState.NotIgnored, (await _git.CheckIgnoreAsync(repo.Path, "a1.txt")).State);
    }

    /// <summary>
    /// git strips trailing whitespace from a .gitignore line unless a backslash quotes it, so an
    /// unescaped rule for a name ending in a space becomes a rule for the trimmed name — it misses
    /// the file it was written for and catches a different one. The control below is git deciding
    /// that, not an assumption about it.
    /// </summary>
    [Fact]
    public async Task IgnoreLineForPath_ANameEndingInASpace_IgnoresThatNameAndNotTheTrimmedOne()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-trailing-space");
        WriteWithExactName(repo.Path, "notes ", "x\n");
        repo.WriteFile("notes", "x\n");

        await _git.AppendIgnoreEntryAsync(repo.Path, GitService.IgnoreLineForPath("notes "));

        Assert.Equal("/notes\\ \n", await _git.GetGitignoreAsync(repo.Path));
        Assert.Equal(IgnoreState.Ignored, (await _git.CheckIgnoreAsync(repo.Path, "notes ")).State);
        Assert.Equal(IgnoreState.NotIgnored, (await _git.CheckIgnoreAsync(repo.Path, "notes")).State);
    }

    /// <summary>The control: without the escape git reads the same intent as a rule for another file.</summary>
    [Fact]
    public async Task AnUnescapedTrailingSpace_IsTheRuleGitStripsIntoADifferentOne()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-trailing-raw");
        await _git.SaveGitignoreAsync(repo.Path, "/notes \n");

        Assert.Equal(IgnoreState.NotIgnored, (await _git.CheckIgnoreAsync(repo.Path, "notes ")).State);
        Assert.Equal(IgnoreState.Ignored, (await _git.CheckIgnoreAsync(repo.Path, "notes")).State);
    }

    /// <summary>
    /// Win32 strips a trailing space from a path it normalizes, so the extended-length form is the
    /// only way to put one on disk under the name the test is about.
    /// </summary>
    private static void WriteWithExactName(string repoPath, string name, string content)
    {
        var full = Path.Combine(repoPath, name);
        File.WriteAllText(@"\\?\" + Path.GetFullPath(repoPath) + "\\" + name, content);
        Assert.Contains(name, Directory.GetFiles(repoPath).Select(Path.GetFileName));
        Assert.False(File.Exists(full) && Path.GetFileName(full) != name);
    }

    /// <summary>
    /// Without the leading slash a bare name matches at every depth, so ignoring one file would
    /// also ignore its namesakes in subdirectories the reader never looked at.
    /// </summary>
    [Fact]
    public async Task IgnoreLineForPath_IgnoresTheNamedFileAndNotItsNamesakeInASubdirectory()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-anchor");
        repo.WriteFile("notes.txt", "x\n");
        repo.WriteFile("sub/notes.txt", "x\n");

        await _git.AppendIgnoreEntryAsync(repo.Path, GitService.IgnoreLineForPath("notes.txt"));

        Assert.Equal(IgnoreState.Ignored, (await _git.CheckIgnoreAsync(repo.Path, "notes.txt")).State);
        Assert.Equal(IgnoreState.NotIgnored, (await _git.CheckIgnoreAsync(repo.Path, "sub/notes.txt")).State);
    }

    [Fact]
    public async Task CheckIgnore_ReflectsPatterns()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-check");
        await _git.SaveGitignoreAsync(repo.Path, "*.log\n");

        Assert.Equal(new IgnoreAnswer(IgnoreState.Ignored, false, ""),
            await _git.CheckIgnoreAsync(repo.Path, "debug.log"));
        Assert.Equal(new IgnoreAnswer(IgnoreState.NotIgnored, false, ""),
            await _git.CheckIgnoreAsync(repo.Path, "notes.txt"));
    }

    /// <summary>
    /// check-ignore consults the index, so a tracked path exits 1 — "not ignored" — even while a
    /// rule matches it. Trackedness is what separates that from a path no rule matches.
    /// </summary>
    [Fact]
    public async Task CheckIgnore_ATrackedPathIsReportedNotIgnoredAndTracked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-check-tracked");
        repo.WriteFile("kept.log", "x\n");
        await repo.GitAsync("add", "--force", "--", "kept.log");
        await repo.CommitAllAsync("track a log");
        await _git.SaveGitignoreAsync(repo.Path, "*.log\n");

        var answer = await _git.CheckIgnoreAsync(repo.Path, "kept.log");

        Assert.Equal(IgnoreState.NotIgnored, answer.State);
        Assert.True(answer.Tracked);
        Assert.True(await _git.IsTrackedAsync(repo.Path, "kept.log"));
        Assert.False(await _git.IsTrackedAsync(repo.Path, "never-added.log"));
    }

    /// <summary>
    /// `ls-files` prints every index entry a pathspec covers, so a directory pathspec prints the
    /// files UNDER it. Treating any output as a hit made a directory holding tracked files read
    /// as tracked itself, and the probe then told the reader the index outranks the ignore rules
    /// for a path the index does not hold.
    /// </summary>
    [Fact]
    public async Task IsTracked_ADirectoryHoldingTrackedFiles_IsNotItselfTracked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-tracked-dir");
        repo.WriteFile("lib/inner.txt", "x\n");
        await repo.GitAsync("add", "--", "lib/inner.txt");
        await repo.CommitAllAsync("track a file under a directory");

        Assert.True(await _git.IsTrackedAsync(repo.Path, "lib/inner.txt"));
        Assert.False(await _git.IsTrackedAsync(repo.Path, "lib"));
        Assert.False(await _git.IsTrackedAsync(repo.Path, "lib/"));

        // The probe reports what the index holds, so the directory gets the plain answer.
        var answer = await _git.CheckIgnoreAsync(repo.Path, "lib");
        Assert.Equal(IgnoreState.NotIgnored, answer.State);
        Assert.False(answer.Tracked);
    }

    /// <summary>A path typed with the platform separator asks about the entry git records.</summary>
    [Fact]
    public async Task IsTracked_AcceptsTheWindowsSeparatorForAPathTheIndexHolds()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-tracked-sep");
        repo.WriteFile("lib/inner.txt", "x\n");
        await repo.GitAsync("add", "--", "lib/inner.txt");
        await repo.CommitAllAsync("track a file under a directory");

        Assert.True(await _git.IsTrackedAsync(repo.Path, @"lib\inner.txt"));
    }

    /// <summary>Exit 128 is git refusing the question; answering "not ignored" would invent an answer.</summary>
    [Fact]
    public async Task CheckIgnore_APathGitRefuses_IsUnknownRatherThanNotIgnored()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-check-outside");
        await _git.SaveGitignoreAsync(repo.Path, "*.log\n");

        var answer = await _git.CheckIgnoreAsync(repo.Path, "../outside.log");

        Assert.Equal(IgnoreState.Unknown, answer.State);
        Assert.NotEqual("", answer.Error);
    }
}
