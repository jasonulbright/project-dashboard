using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Hunk-level stage/unstage/discard: the fiddly one. Every patch is sliced out of
/// the RAW `git diff` bytes by <see cref="GitService.ExtractHunkPatch"/> and applied by
/// real `git apply`.
///
/// There is deliberately no second, model-based builder. A patch rebuilt from a parsed
/// <see cref="FileDiff"/> is lossy in two ways that `git apply` either rejects or, worse,
/// accepts with the wrong bytes: the parser strips the CR of every CRLF line, and it
/// cannot tell the "\ No newline at end of file" marker from a context line whose own
/// content starts with a backslash. Both are exercised below through the raw path.
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

    /// <summary>The patch the staging UI sends: raw diff bytes, sliced at one hunk.</summary>
    private async Task<string> HunkPatchAsync(TempRepo repo, int hunkIndex, bool staged = false)
    {
        var raw = await _git.GetFileDiffRawAsync(repo.Path, "file.txt", staged);
        Assert.NotNull(raw);
        var patch = GitService.ExtractHunkPatch(raw.Text, "file.txt", hunkIndex);
        Assert.NotNull(patch);
        return patch;
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

        Assert.Equal(2, HunkCount(await FileDiffAsync(repo, staged: false)));

        var patch = await HunkPatchAsync(repo, 0);
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

        var patch = await HunkPatchAsync(repo, 0);
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

        var patch = await HunkPatchAsync(repo, 0);
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

        var patch = await HunkPatchAsync(repo, 0);
        // The marker travels verbatim; a patch without it appends a newline nobody asked for.
        Assert.Contains("\\ No newline at end of file", patch);

        Assert.True((await _git.StageHunkAsync(repo.Path, patch)).Success);
        var stagedBlob = await Git.RunAsync(repo.Path, "cat-file", "-p", ":file.txt");
        Assert.Equal("x1\nx2\nX3", stagedBlob);   // no trailing newline introduced
    }

    [Fact]
    public async Task StageHunk_CrlfFile_StagesBytesFaithfully()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-crlf");
        repo.WriteFile("file.txt", "c1\r\nc2\r\nc3\r\n");
        await repo.CommitAllAsync("crlf file");
        repo.WriteFile("file.txt", "C1\r\nc2\r\nc3\r\n");

        var raw = await _git.GetFileDiffRawAsync(repo.Path, "file.txt", staged: false);
        Assert.NotNull(raw);
        Assert.Contains("\r", raw.Text);
        // FileDiff.ParseUnified drops these CRs, which is why it is not the staging path.
        Assert.DoesNotContain("\r", string.Concat((await FileDiffAsync(repo, false)).Lines.Select(l => l.Text)));

        var patch = GitService.ExtractHunkPatch(raw.Text, "file.txt", 0);
        Assert.NotNull(patch);
        Assert.True((await _git.StageHunkAsync(repo.Path, patch)).Success);

        // Staged blob keeps CRLF on every line — no LF corruption of the edited line.
        var stagedBlob = await Git.RunAsync(repo.Path, "cat-file", "-p", ":file.txt");
        Assert.Equal("C1\r\nc2\r\nc3\r\n", stagedBlob);
    }

    /// <summary>
    /// A context line whose own content begins with "\ " is indistinguishable from the
    /// no-newline marker once the parser has stripped the diff's leading space, so a
    /// model-built patch emits it with no prefix and `git apply` reads it as a marker.
    /// Sliced from the raw bytes the distinction never has to be made.
    /// </summary>
    [Fact]
    public async Task StageHunk_ContextLineStartingWithABackslash_IsNotReadAsANoNewlineMarker()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("hunk-backslash");
        repo.WriteFile("file.txt", "a1\n\\ No newline at end of file\na3\n");
        await repo.CommitAllAsync("backslash content line");
        repo.WriteFile("file.txt", "A1\n\\ No newline at end of file\na3\n");

        var patch = await HunkPatchAsync(repo, 0);
        // The content line keeps the unified diff's context space; only a real marker
        // sits at column 0.
        Assert.Contains(" \\ No newline at end of file", patch);

        Assert.True((await _git.StageHunkAsync(repo.Path, patch)).Success);
        var stagedBlob = await Git.RunAsync(repo.Path, "cat-file", "-p", ":file.txt");
        Assert.Equal("A1\n\\ No newline at end of file\na3\n", stagedBlob);
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

        var second = GitService.ExtractHunkPatch(raw, "f.txt", 1);
        Assert.Equal(
            "diff --git a/f.txt b/f.txt\n" +
            "index 111..222 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -10,2 +10,2 @@\n" +
            " y\n-z\n+Z\n",
            second);
    }

    /// <summary>
    /// An index past the last hunk yields nothing rather than the last hunk: a caller
    /// asking for a hunk that is not there must not stage a different one.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(99)]
    public void ExtractHunkPatch_IndexOutsideTheDiff_YieldsNothing(int hunkIndex)
    {
        const string raw =
            "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n" +
            "@@ -1,2 +1,2 @@\n-a\n+A\n b\n" +
            "@@ -10,2 +10,2 @@\n y\n-z\n+Z\n";

        Assert.Null(GitService.ExtractHunkPatch(raw, "f.txt", hunkIndex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("diff --git a/f.txt b/f.txt\nindex 111..222 100644\n")]
    public void ExtractHunkPatch_DiffWithNoHunk_YieldsNothing(string raw)
        => Assert.Null(GitService.ExtractHunkPatch(raw, "f.txt", 0));

    /// <summary>
    /// Raw diff text carrying two files, as a pathspec that globs onto a sibling produces. The
    /// index names a hunk WITHIN the file asked for, so the count starts again at its own
    /// section — counting across the whole text slices the other file's change under this
    /// file's name.
    /// </summary>
    private const string TwoFileRaw =
        "diff --git a/notes1.txt b/notes1.txt\n" +
        "index aaa..bbb 100644\n" +
        "--- a/notes1.txt\n" +
        "+++ b/notes1.txt\n" +
        "@@ -1,1 +1,1 @@\n" +
        "-sibling one\n+SIBLING TWO\n" +
        "diff --git a/notes[1].txt b/notes[1].txt\n" +
        "index ccc..ddd 100644\n" +
        "--- a/notes[1].txt\n" +
        "+++ b/notes[1].txt\n" +
        "@@ -1,1 +1,1 @@\n" +
        "-bracket one\n+BRACKET TWO\n";

    [Fact]
    public void ExtractHunkPatch_CountsHunksWithinTheNamedFilesOwnSection()
    {
        var patch = GitService.ExtractHunkPatch(TwoFileRaw, "notes[1].txt", 0);

        Assert.Equal(
            "diff --git a/notes[1].txt b/notes[1].txt\n" +
            "index ccc..ddd 100644\n" +
            "--- a/notes[1].txt\n" +
            "+++ b/notes[1].txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-bracket one\n+BRACKET TWO\n",
            patch);
    }

    [Fact]
    public void ExtractHunkPatch_StopsTheLastHunkAtTheNextFilesSection()
    {
        var patch = GitService.ExtractHunkPatch(TwoFileRaw, "notes1.txt", 0);

        Assert.NotNull(patch);
        Assert.EndsWith("-sibling one\n+SIBLING TWO\n", patch);
        Assert.DoesNotContain("bracket", patch);
    }

    [Fact]
    public void ExtractHunkPatch_PathTheDiffDoesNotDescribe_YieldsNothing()
    {
        Assert.Null(GitService.ExtractHunkPatch(TwoFileRaw, "absent.txt", 0));
        Assert.Null(GitService.ExtractHunkPatch(TwoFileRaw, "", 0));
    }

    /// <summary>A rename's section is found under the path the pane names it by, which is the new one.</summary>
    [Fact]
    public void ExtractHunkPatch_FindsARenamedFileByItsNewPath()
    {
        const string raw =
            "diff --git a/before.txt b/after.txt\n" +
            "similarity index 86%\n" +
            "rename from before.txt\n" +
            "rename to after.txt\n" +
            "index aaa..bbb 100644\n" +
            "--- a/before.txt\n" +
            "+++ b/after.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-l1\n+L1\n";

        Assert.NotNull(GitService.ExtractHunkPatch(raw, "after.txt", 0));
        Assert.Null(GitService.ExtractHunkPatch(raw, "before.txt", 0));
    }
}
