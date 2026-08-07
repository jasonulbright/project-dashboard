using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Hunk-level stage/unstage/discard (L-06): the fiddly one. Exercises the model-based
/// <see cref="GitService.BuildHunkPatch"/> and the byte-faithful raw
/// <see cref="GitService.ExtractHunkPatch"/> against real `git apply`.
/// </summary>
public class GitServiceHunkTests
{
    private readonly GitService _git = new();

    private const string FifteenLines =
        "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nl15\n";
    // First and last lines edited; 3 lines of context leaves two separate hunks.
    private const string FifteenEdited =
        "L1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n";

    private async Task<FileDiff> FileDiffAsync(TempRepo repo, bool staged)
    {
        var state = await _git.GetWorkingStateAsync(repo.Path);
        var file = state!.Files.First(f => f.Path == "file.txt");
        return (await _git.GetFileDiffAsync(repo.Path, file, staged))!;
    }

    private static int HunkCount(FileDiff diff) =>
        diff.Lines.Count(l => l.Kind == DiffLineKind.HunkHeader && l.Text.StartsWith("@@"));

    private static bool DiffTouches(FileDiff diff, string text) =>
        diff.Lines.Any(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed && l.Text == text);

    [Fact]
    public async Task StageHunk_StagesOnlyTheChosenHunkOfAMultiHunkFile()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-stage");
        repo.WriteFile("file.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        repo.WriteFile("file.txt", FifteenEdited);

        var diff = await FileDiffAsync(repo, staged: false);
        Assert.Equal(2, HunkCount(diff));

        var patch = GitService.BuildHunkPatch(diff, 0);
        Assert.True((await _git.StageHunkAsync(repo.Path, patch)).Success);

        // Staged side carries ONLY the first hunk (L1); the second (L15) stays unstaged.
        var staged = await FileDiffAsync(repo, staged: true);
        Assert.Equal(1, HunkCount(staged));
        Assert.True(DiffTouches(staged, "L1"));
        Assert.False(DiffTouches(staged, "L15"));

        var unstaged = await FileDiffAsync(repo, staged: false);
        Assert.True(DiffTouches(unstaged, "L15"));
        Assert.False(DiffTouches(unstaged, "L1"));
    }

    [Fact]
    public async Task UnstageHunk_RemovesAPreviouslyStagedHunk()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-unstage");
        repo.WriteFile("file.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        repo.WriteFile("file.txt", FifteenEdited);

        var patch = GitService.BuildHunkPatch(await FileDiffAsync(repo, staged: false), 0);
        Assert.True((await _git.StageHunkAsync(repo.Path, patch)).Success);
        Assert.Equal(1, HunkCount(await FileDiffAsync(repo, staged: true)));

        // Reverse the same patch out of the index.
        Assert.True((await _git.UnstageHunkAsync(repo.Path, patch)).Success);
        var state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Empty(state!.Staged);
        Assert.Equal(2, HunkCount(await FileDiffAsync(repo, staged: false)));
    }

    [Fact]
    public async Task DiscardHunk_RevertsOnlyThatHunkInTheWorkingTree()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-discard");
        repo.WriteFile("file.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        repo.WriteFile("file.txt", FifteenEdited);

        var patch = GitService.BuildHunkPatch(await FileDiffAsync(repo, staged: false), 0);
        Assert.True((await _git.DiscardHunkAsync(repo.Path, patch)).Success);

        // First hunk reverted on disk (line 1 back to l1), second edit (L15) kept.
        var content = repo.ReadFile("file.txt");
        Assert.StartsWith("l1\n", content);
        Assert.EndsWith("L15\n", content);
    }

    [Fact]
    public async Task StageHunk_FileWithoutTrailingNewline_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-nonl");
        repo.WriteFile("file.txt", "x1\nx2\nx3");   // no trailing newline
        await repo.CommitAllAsync("no trailing newline");
        repo.WriteFile("file.txt", "x1\nx2\nX3");   // still none

        var diff = await FileDiffAsync(repo, staged: false);
        var patch = GitService.BuildHunkPatch(diff, 0);
        // The builder must carry the "\ No newline at end of file" markers verbatim.
        Assert.Contains("\\ No newline at end of file", patch);

        Assert.True((await _git.StageHunkAsync(repo.Path, patch)).Success);
        var stagedBlob = await Git.RunAsync(repo.Path, "cat-file", "-p", ":file.txt");
        Assert.Equal("x1\nx2\nX3", stagedBlob);   // no trailing newline introduced
    }

    [Fact]
    public async Task StageHunk_CrlfFile_StagesBytesFaithfullyViaRawExtract()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-crlf");
        repo.WriteFile("file.txt", "c1\r\nc2\r\nc3\r\n");
        await repo.CommitAllAsync("crlf file");
        repo.WriteFile("file.txt", "C1\r\nc2\r\nc3\r\n");

        // FileDiff.ParseUnified strips the CR, so a model-built patch would fail plain
        // `git apply`; the raw extractor preserves the CR bytes.
        var raw = await _git.GetFileDiffRawAsync(repo.Path, "file.txt", staged: false);
        Assert.NotNull(raw);
        Assert.Contains("\r", raw);
        var patch = GitService.ExtractHunkPatch(raw!, 0);
        Assert.NotNull(patch);

        Assert.True((await _git.StageHunkAsync(repo.Path, patch!)).Success);

        // Staged blob keeps CRLF on every line — no LF corruption of the edited line.
        var stagedBlob = await Git.RunAsync(repo.Path, "cat-file", "-p", ":file.txt");
        Assert.Equal("C1\r\nc2\r\nc3\r\n", stagedBlob);
    }

    [Fact]
    public void BuildHunkPatch_ProducesGitApplyableHeadersAndBody()
    {
        var diff = new FileDiff { Path = "file.txt", OldPath = "file.txt" };
        diff.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = "@@ -1,3 +1,3 @@" });
        diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = "old" });
        diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = "new" });
        diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = "keep" });

        var patch = GitService.BuildHunkPatch(diff, 0);

        Assert.Equal(
            "diff --git a/file.txt b/file.txt\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,3 +1,3 @@\n" +
            "-old\n" +
            "+new\n" +
            " keep\n",
            patch);
    }

    [Fact]
    public void BuildHunkPatch_OutOfRangeIndex_Throws()
    {
        var diff = new FileDiff { Path = "f" };
        diff.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = "@@ -1 +1 @@" });
        Assert.Throws<ArgumentOutOfRangeException>(() => GitService.BuildHunkPatch(diff, 1));
    }

    [Fact]
    public void ExtractHunkPatch_SlicesSecondHunkWithPreamble()
    {
        const string raw =
            "diff --git a/f.txt b/f.txt\n" +
            "index 111..222 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -1,2 +1,2 @@\n" +
            "-a\n+A\n b\n" +
            "@@ -10,2 +10,2 @@\n" +
            " y\n-z\n+Z\n";

        var second = GitService.ExtractHunkPatch(raw, 1);
        Assert.Equal(
            "diff --git a/f.txt b/f.txt\n" +
            "index 111..222 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -10,2 +10,2 @@\n" +
            " y\n-z\n+Z\n",
            second);

        Assert.Null(GitService.ExtractHunkPatch(raw, 2));
    }
}
