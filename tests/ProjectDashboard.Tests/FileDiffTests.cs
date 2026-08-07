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

        var hunk = Assert.Single(diff.Lines, l => l.Kind == DiffLineKind.HunkHeader);
        Assert.StartsWith("@@@", hunk.Text);

        // Body rows after the hunk header: two-column status prefixes are kept verbatim,
        // classified by first column only, with no line-number gutters.
        var body = diff.Lines.Skip(diff.Lines.IndexOf(hunk) + 1).Where(l => l.Text.Length > 0).ToList();
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
    }

    [Fact]
    public void EmptyInput_YieldsNoFiles()
    {
        Assert.Empty(FileDiff.ParseUnified(""));
    }
}
