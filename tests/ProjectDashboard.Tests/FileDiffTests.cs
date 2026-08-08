using ProjectDashboard.Models;

namespace ProjectDashboard.Tests;

public class FileDiffTests
{
    [Fact]
    public void SimpleHunk_ProducesCorrectLineNumberGutters()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/src/app.txt b/src/app.txt\n" +
            "index 83db48f..bf269f4 100644\n" +
            "--- a/src/app.txt\n" +
            "+++ b/src/app.txt\n" +
            "@@ -10,4 +10,5 @@ section heading\n" +
            " context one\n" +
            "-removed line\n" +
            "+added line\n" +
            "+another added\n" +
            " context two\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("src/app.txt", diff.Path);
        Assert.False(diff.IsBinary);
        Assert.False(diff.IsCombined);

        Assert.Equal(6, diff.Lines.Count);
        Assert.Equal(DiffLineKind.HunkHeader, diff.Lines[0].Kind);

        var context1 = diff.Lines[1];
        Assert.Equal(DiffLineKind.Context, context1.Kind);
        Assert.Equal("context one", context1.Text);
        Assert.Equal("10", context1.OldNumber);
        Assert.Equal("10", context1.NewNumber);

        var removed = diff.Lines[2];
        Assert.Equal(DiffLineKind.Removed, removed.Kind);
        Assert.Equal("11", removed.OldNumber);
        Assert.Equal("", removed.NewNumber);

        var added1 = diff.Lines[3];
        Assert.Equal(DiffLineKind.Added, added1.Kind);
        Assert.Equal("", added1.OldNumber);
        Assert.Equal("11", added1.NewNumber);

        var added2 = diff.Lines[4];
        Assert.Equal("12", added2.NewNumber);

        var context2 = diff.Lines[5];
        Assert.Equal("12", context2.OldNumber);
        Assert.Equal("13", context2.NewNumber);
    }

    [Fact]
    public void MultiFileInput_SplitsPerFile()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/first.txt b/first.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/first.txt\n" +
            "+++ b/first.txt\n" +
            "@@ -1 +1 @@\n" +
            "-one\n" +
            "+uno\n" +
            "diff --git a/second.txt b/second.txt\n" +
            "index 3333333..4444444 100644\n" +
            "--- a/second.txt\n" +
            "+++ b/second.txt\n" +
            "@@ -1 +1 @@\n" +
            "-two\n" +
            "+dos\n");

        Assert.Equal(2, diffs.Count);
        Assert.Equal("first.txt", diffs[0].Path);
        Assert.Equal("second.txt", diffs[1].Path);
        Assert.All(diffs, d => Assert.Equal(3, d.Lines.Count));
    }

    [Fact]
    public void Rename_CarriesOldAndNewPath()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/old-name.txt b/new-name.txt\n" +
            "similarity index 90%\n" +
            "rename from old-name.txt\n" +
            "rename to new-name.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/old-name.txt\n" +
            "+++ b/new-name.txt\n" +
            "@@ -1,2 +1,2 @@\n" +
            " keep\n" +
            "-drop\n" +
            "+swap\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("new-name.txt", diff.Path);
        Assert.Equal("old-name.txt", diff.OldPath);
    }

    [Fact]
    public void BinaryFile_SetsFlagWithNoRows()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/logo.png b/logo.png\n" +
            "index 1111111..2222222 100644\n" +
            "Binary files a/logo.png and b/logo.png differ\n");

        var diff = Assert.Single(diffs);
        Assert.True(diff.IsBinary);
        Assert.Equal("logo.png", diff.Path);
        Assert.Empty(diff.Lines);
    }

    [Fact]
    public void ModeOnlyChange_NamesFileFromHeaderAndKeepsModeRows()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/tool.sh b/tool.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("tool.sh", diff.Path);
        Assert.Equal(2, diff.Lines.Count);
        Assert.All(diff.Lines, l => Assert.Equal(DiffLineKind.HunkHeader, l.Kind));
        Assert.Equal("old mode 100644", diff.Lines[0].Text);
        Assert.Equal("new mode 100755", diff.Lines[1].Text);
    }

    [Fact]
    public void SpaceBSlashInsideFileName_ResolvesByLengthNotBySearch()
    {
        // Path contains " b/" twice; a first-match split on " b/" picks the wrong point.
        var diffs = FileDiff.ParseUnified(
            "diff --git a/dir b/tool b/x.sh b/dir b/tool b/x.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("dir b/tool b/x.sh", diff.Path);
    }

    [Fact]
    public void CombinedDiff_MarksCombinedAndSkipsColumnParse()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --cc conflicted.txt\n" +
            "index 1111111,2222222..3333333\n" +
            "--- a/conflicted.txt\n" +
            "+++ b/conflicted.txt\n" +
            "@@@ -1,3 -1,3 +1,5 @@@\n" +
            "  shared line\n" +
            "++<<<<<<< HEAD\n" +
            " +ours\n" +
            "++=======\n" +
            "+ theirs\n" +
            "++>>>>>>> feature\n");

        var diff = Assert.Single(diffs);
        Assert.True(diff.IsCombined);
        Assert.Equal("conflicted.txt", diff.Path);

        // index/---/+++ between the file header and @@@ are metadata: they must not
        // surface as Removed/Added/Context rows, and the blank artifact of the
        // trailing newline must not become a row either.
        Assert.DoesNotContain(diff.Lines, l => l.Text.StartsWith("--- ", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Lines, l => l.Text.StartsWith("+++ ", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Lines, l => l.Text.StartsWith("index ", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Lines, l => l.Text.Length == 0);

        var hunk = Assert.Single(diff.Lines, l => l.Kind == DiffLineKind.HunkHeader);
        Assert.StartsWith("@@@", hunk.Text);
        Assert.Same(hunk, diff.Lines[0]);
        Assert.Equal(7, diff.Lines.Count);

        // Body rows after the hunk header: two-column status prefixes are kept verbatim,
        // classified by first column only, with no line-number gutters.
        var body = diff.Lines.Skip(1).ToList();
        Assert.Equal(6, body.Count);
        Assert.Equal(DiffLineKind.Context, body[0].Kind);
        Assert.Equal("  shared line", body[0].Text);
        Assert.Contains(body, l => l.Kind == DiffLineKind.Added && l.Text.Contains("<<<<<<<"));
        Assert.Contains(body, l => l.Kind == DiffLineKind.Context && l.Text == " +ours");
        Assert.Contains(body, l => l.Kind == DiffLineKind.Added && l.Text == "+ theirs");
        Assert.All(diff.Lines, l =>
        {
            Assert.Equal("", l.OldNumber);
            Assert.Equal("", l.NewNumber);
        });
    }

    [Fact]
    public void CombinedDiffBinaryMarker_SetsIsBinary()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --cc logo.png\n" +
            "index 1111111,2222222..3333333\n" +
            "Binary files differ\n");

        var diff = Assert.Single(diffs);
        Assert.True(diff.IsCombined);
        Assert.True(diff.IsBinary);
    }

    [Fact]
    public void NewFile_HasNoOldPathAndNumbersFromOne()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/fresh.txt b/fresh.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..e69de29\n" +
            "--- /dev/null\n" +
            "+++ b/fresh.txt\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+alpha\n" +
            "+beta\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("fresh.txt", diff.Path);
        Assert.Null(diff.OldPath);
        Assert.Equal("1", diff.Lines[1].NewNumber);
        Assert.Equal("2", diff.Lines[2].NewNumber);
    }

    [Fact]
    public void DeletedFile_KeepsNameFromOldSide()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/gone.txt b/gone.txt\n" +
            "deleted file mode 100644\n" +
            "index e69de29..0000000\n" +
            "--- a/gone.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1,1 +0,0 @@\n" +
            "-last words\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("gone.txt", diff.Path);
        Assert.Equal("gone.txt", diff.OldPath);
    }

    [Fact]
    public void NoNewlineMarker_RendersAsUnnumberedContext()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/x.txt b/x.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/x.txt\n" +
            "+++ b/x.txt\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n" +
            "\\ No newline at end of file\n");

        var diff = Assert.Single(diffs);
        var marker = diff.Lines[^1];
        Assert.Equal(DiffLineKind.Context, marker.Kind);
        Assert.StartsWith("\\ No newline", marker.Text);
        Assert.Equal("", marker.OldNumber);
        Assert.Equal("", marker.NewNumber);
        Assert.True(marker.IsNoNewlineMarker);
        Assert.DoesNotContain(diff.Lines.Take(diff.Lines.Count - 1), l => l.IsNoNewlineMarker);
    }

    [Fact]
    public void EmptyInput_YieldsNoFiles()
    {
        Assert.Empty(FileDiff.ParseUnified(""));
    }

    [Fact]
    public void DeletedBodyLineStartingWithTwoDashes_StaysARemovedRow()
    {
        // Deleting "-- old comment" arrives as "--- old comment": marker plus
        // content, not a header — the row must render and OldPath must survive.
        var diffs = FileDiff.ParseUnified(
            "diff --git a/notes.txt b/notes.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/notes.txt\n" +
            "+++ b/notes.txt\n" +
            "@@ -1,3 +1,2 @@\n" +
            " keep\n" +
            "--- old comment\n" +
            " tail\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("notes.txt", diff.Path);
        Assert.Equal("notes.txt", diff.OldPath);

        Assert.Equal(4, diff.Lines.Count);
        var removed = diff.Lines[2];
        Assert.Equal(DiffLineKind.Removed, removed.Kind);
        Assert.Equal("-- old comment", removed.Text);
        Assert.Equal("2", removed.OldNumber);
        Assert.Equal("", removed.NewNumber);

        // The gutter numbering after the swallowed-row candidate stays aligned.
        var tail = diff.Lines[3];
        Assert.Equal("3", tail.OldNumber);
        Assert.Equal("2", tail.NewNumber);
    }

    [Fact]
    public void AddedBodyLineStartingWithTwoPluses_StaysAnAddedRow()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/inc.txt b/inc.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/inc.txt\n" +
            "+++ b/inc.txt\n" +
            "@@ -1,1 +1,2 @@\n" +
            " keep\n" +
            "+++ x\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("inc.txt", diff.Path);

        var added = diff.Lines[2];
        Assert.Equal(DiffLineKind.Added, added.Kind);
        Assert.Equal("++ x", added.Text);
        Assert.Equal("2", added.NewNumber);
    }

    [Fact]
    public void ModeChangeWithContent_ParsesHeadersAfterModeRows()
    {
        // old/new mode rows land in Lines BEFORE ---/+++ arrive, so header
        // recognition must key on not-yet-seen-@@, not on Lines being empty.
        var diffs = FileDiff.ParseUnified(
            "diff --git a/tool.sh b/tool.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n" +
            "index 1111111..2222222\n" +
            "--- a/tool.sh\n" +
            "+++ b/tool.sh\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n");

        var diff = Assert.Single(diffs);
        Assert.Equal("tool.sh", diff.Path);
        Assert.Equal("tool.sh", diff.OldPath);

        Assert.Equal(5, diff.Lines.Count);
        Assert.Equal("old mode 100644", diff.Lines[0].Text);
        Assert.Equal("new mode 100755", diff.Lines[1].Text);
        Assert.Equal(DiffLineKind.HunkHeader, diff.Lines[2].Kind);
        Assert.Equal(DiffLineKind.Removed, diff.Lines[3].Kind);
        Assert.Equal("1", diff.Lines[3].OldNumber);
        Assert.Equal(DiffLineKind.Added, diff.Lines[4].Kind);
        Assert.Equal("1", diff.Lines[4].NewNumber);
        Assert.DoesNotContain(diff.Lines, l => l.Text.StartsWith("index ", StringComparison.Ordinal));
    }

    [Fact]
    public void SecondFileAfterAHunk_ParsesItsHeadersAgain()
    {
        // seenHunk is per-file: file two's ---/+++ must be headers even though
        // file one already emitted a hunk.
        var diffs = FileDiff.ParseUnified(
            "diff --git a/one.txt b/one.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/one.txt\n" +
            "+++ b/one.txt\n" +
            "@@ -1 +1 @@\n" +
            "--- dashes\n" +
            "+++ pluses\n" +
            "diff --git a/two.txt b/two.txt\n" +
            "index 3333333..4444444 100644\n" +
            "--- a/two.txt\n" +
            "+++ b/two.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n");

        Assert.Equal(2, diffs.Count);
        Assert.Equal("one.txt", diffs[0].Path);
        Assert.Equal("-- dashes", diffs[0].Lines[1].Text);
        Assert.Equal("++ pluses", diffs[0].Lines[2].Text);
        Assert.Equal("two.txt", diffs[1].Path);
        Assert.Equal("two.txt", diffs[1].OldPath);
        Assert.Equal(3, diffs[1].Lines.Count);
    }

    // ── Hunk index (the handle the staging UI passes to ExtractHunkPatch) ────────

    /// <summary>
    /// The index counts hunks WITHIN a file. Counted across the whole diff text it would name a
    /// hunk of the wrong file, and the patch sliced at it would stage somebody else's change.
    /// </summary>
    [Fact]
    public void HunkIndex_RestartsAtEachFile()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/one.txt b/one.txt\n--- a/one.txt\n+++ b/one.txt\n" +
            "@@ -1,2 +1,2 @@\n-a\n+A\n b\n" +
            "@@ -20,2 +20,2 @@\n y\n-z\n+Z\n" +
            "diff --git a/two.txt b/two.txt\n--- a/two.txt\n+++ b/two.txt\n" +
            "@@ -1,1 +1,1 @@\n-p\n+P\n");

        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1], diffs[0].Lines.Select(l => l.HunkIndex));
        Assert.Equal([0, 0, 0], diffs[1].Lines.Select(l => l.HunkIndex));
        Assert.Equal([0, 1], diffs[0].Lines.Where(l => l.IsHunkStart).Select(l => l.HunkIndex));
        Assert.Single(diffs[1].Lines, l => l.IsHunkStart);
    }

    /// <summary>
    /// A mode-change row is rendered as a header but is not a hunk: no patch can be sliced at it,
    /// so it must not offer the staging actions a real hunk header does.
    /// </summary>
    [Fact]
    public void HunkIndex_IsNegativeForRowsBeforeTheFirstHunk()
    {
        var diffs = FileDiff.ParseUnified(
            "diff --git a/exec.sh b/exec.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n" +
            "--- a/exec.sh\n+++ b/exec.sh\n" +
            "@@ -1 +1 @@\n-a\n+A\n");

        var file = Assert.Single(diffs);
        Assert.Equal(-1, file.Lines[0].HunkIndex);
        Assert.Equal(-1, file.Lines[1].HunkIndex);
        Assert.False(file.Lines[0].IsHunkStart);
        Assert.Equal(0, file.Lines[2].HunkIndex);
        Assert.True(file.Lines[2].IsHunkStart);
    }

    /// <summary>
    /// The rendered row and the patch builder must agree on what index N names, or the reader
    /// stages a hunk other than the one selected. Both count column-0 "@@" headers in order and
    /// both restart the count at each file, so the agreement survives text carrying more than
    /// one of them.
    /// </summary>
    [Fact]
    public void HunkIndex_NamesTheSameHunkExtractHunkPatchSlices()
    {
        const string raw =
            "diff --git a/f.txt b/f.txt\n" +
            "index 111..222 100644\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -1,2 +1,2 @@\n-a\n+A\n b\n" +
            "@@ -10,2 +10,2 @@ tail section\n y\n-z\n+Z\n" +
            "diff --git a/g.txt b/g.txt\n" +
            "index 333..444 100644\n" +
            "--- a/g.txt\n" +
            "+++ b/g.txt\n" +
            "@@ -5,2 +5,2 @@ other file\n p\n-q\n+Q\n";

        foreach (var file in FileDiff.ParseUnified(raw))
            foreach (var header in file.Lines.Where(l => l.IsHunkStart))
            {
                var patch = Services.GitService.ExtractHunkPatch(raw, file.Path, header.HunkIndex);
                Assert.NotNull(patch);
                Assert.Contains(header.Text + "\n", patch);
                Assert.Contains($"diff --git a/{file.Path} ", patch);
            }
    }
}
