using ProjectDashboard.Models;

namespace ProjectDashboard.Tests;

/// <summary>
/// The two-column rendering. It is built from the rows the unified pane already holds,
/// so what has to hold is that nothing is invented and nothing is lost: every line lands on
/// exactly one row, on the side the file it belongs to, under the hunk index the unified rows
/// give it — the index every hunk action slices a patch at.
/// </summary>
public class SideBySideDiffTests
{
    private static List<DiffLine> Parse(string diff) => FileDiff.ParseUnified(diff).Single().Lines;

    private const string OneForOne = """
        diff --git a/file.txt b/file.txt
        --- a/file.txt
        +++ b/file.txt
        @@ -1,3 +1,3 @@
         first
        -second
        +SECOND
         third
        """;

    [Fact]
    public void APairedEdit_PutsTheOldAndNewLinesOnOneRow()
    {
        var rows = SideBySideDiff.Build(Parse(OneForOne));

        var changed = rows.Single(r => r.LeftRemoved);
        Assert.Equal("second", changed.Left!.Text);
        Assert.Equal("SECOND", changed.Right!.Text);
        Assert.Equal("2", changed.LeftNumber);
        Assert.Equal("2", changed.RightNumber);
        Assert.False(changed.LeftAbsent);
        Assert.False(changed.RightAbsent);
    }

    [Fact]
    public void AContextLine_RendersOnBothSidesWithBothNumbers()
    {
        var rows = SideBySideDiff.Build(Parse(OneForOne));

        var context = rows.First(r => !r.IsHeader && r.Left?.Kind == DiffLineKind.Context);
        Assert.Equal("first", context.Left!.Text);
        Assert.Same(context.Left, context.Right);
        Assert.Equal("1", context.LeftNumber);
        Assert.Equal("1", context.RightNumber);
    }

    /// <summary>
    /// Position is the only alignment a unified diff carries. Three removed lines against one
    /// added line pair the first and leave the rest one-sided, rather than inventing a
    /// counterpart git never matched them to.
    /// </summary>
    [Fact]
    public void AnUnequalRun_LeavesTheSurplusLinesOneSided()
    {
        var rows = SideBySideDiff.Build(Parse("""
            diff --git a/file.txt b/file.txt
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,1 @@
            -one
            -two
            -three
            +only
            """));

        var body = rows.Where(r => !r.IsHeader).ToList();
        Assert.Equal(3, body.Count);
        Assert.Equal("only", body[0].Right!.Text);
        Assert.True(body[1].RightAbsent);
        Assert.True(body[2].RightAbsent);
        Assert.Equal(["one", "two", "three"], body.Select(r => r.Left!.Text));
    }

    /// <summary>
    /// A hunk boundary ends a run. Pairing across one would show the last line of a hunk beside
    /// the first line of the next, and the hunk a row names is what every hunk action slices at.
    /// </summary>
    [Fact]
    public void ARemovedLineEndingAHunk_IsNeverPairedWithTheNextHunksAddedLine()
    {
        var rows = SideBySideDiff.Build(Parse("""
            diff --git a/file.txt b/file.txt
            --- a/file.txt
            +++ b/file.txt
            @@ -1,1 +1,1 @@
            -gone
            @@ -9,1 +9,1 @@
            +new
            """));

        var body = rows.Where(r => !r.IsHeader).ToList();
        Assert.Equal(2, body.Count);
        Assert.True(body[0].RightAbsent);
        Assert.Equal(0, body[0].HunkIndex);
        Assert.True(body[1].LeftAbsent);
        Assert.Equal(1, body[1].HunkIndex);
    }

    /// <summary>
    /// Git reports a missing final newline with a marker line inside the hunk, once per side.
    /// The marker is not a line of either file, so it pairs with nothing: read as context it
    /// ends the run of changed lines, which leaves the edited last line one-sided and prints the
    /// marker's text into both columns as though the file held it.
    /// </summary>
    private const string LastLineWithoutNewline = """
        diff --git a/file.txt b/file.txt
        --- a/file.txt
        +++ b/file.txt
        @@ -1,2 +1,2 @@
         first
        -second
        \ No newline at end of file
        +SECOND
        \ No newline at end of file
        """;

    [Fact]
    public void AnEditedLastLineWithNoTrailingNewline_StillPairs()
    {
        var rows = SideBySideDiff.Build(Parse(LastLineWithoutNewline));

        var changed = rows.Single(r => r.LeftRemoved);
        Assert.Equal("second", changed.Left!.Text);
        Assert.Equal("SECOND", changed.Right!.Text);
        Assert.False(changed.LeftAbsent);
        Assert.False(changed.RightAbsent);
    }

    /// <summary>
    /// The marker states something about the file, like a hunk header, so it renders once across
    /// both columns — never as text one side of the file contains.
    /// </summary>
    [Fact]
    public void TheNoNewlineMarker_RendersAsAHeaderRow()
    {
        var rows = SideBySideDiff.Build(Parse(LastLineWithoutNewline));

        var markers = rows.Where(r => r.HeaderText.StartsWith('\\')).ToList();
        Assert.NotEmpty(markers);
        Assert.All(markers, r =>
        {
            Assert.True(r.IsHeader);
            Assert.Null(r.Left);
            Assert.Null(r.Right);
        });
        Assert.DoesNotContain(rows, r =>
            r.LeftSegments.Concat(r.RightSegments).Any(s => s.Text.StartsWith('\\')));
    }

    /// <summary>The marker follows the lines it is about, not the run it interrupted.</summary>
    [Fact]
    public void TheNoNewlineMarker_FollowsThePairItAnnotates()
    {
        var rows = SideBySideDiff.Build(Parse(LastLineWithoutNewline));

        Assert.True(rows.FindIndex(r => r.LeftRemoved) < rows.FindIndex(r => r.HeaderText.StartsWith('\\')));
    }

    [Fact]
    public void NoLineOfANoNewlineDiff_IsDroppedOrDuplicated()
    {
        var lines = Parse(LastLineWithoutNewline);
        var rows = SideBySideDiff.Build(lines);

        foreach (var line in lines)
            Assert.Single(rows, r => r.Covers(line));
    }

    /// <summary>
    /// The parser strips a context row's leading status space, so a line of the file can itself
    /// begin with a backslash. Such a line belongs to both files: it keeps its position and its
    /// two line numbers instead of being gathered up as a marker and reprinted as a header.
    /// </summary>
    private const string BackslashContextLines = """
        diff --git a/paper.tex b/paper.tex
        --- a/paper.tex
        +++ b/paper.tex
        @@ -1,4 +1,4 @@
         \documentclass{article}
         \begin{document}
        -\section{Old}
        +\section{New}
         \end{document}
        """;

    [Fact]
    public void AContextLineBeginningWithABackslash_KeepsItsPositionAndLineNumbers()
    {
        var rows = SideBySideDiff.Build(Parse(BackslashContextLines));

        var body = rows.Where(r => !r.IsHeader).ToList();
        Assert.Equal(
            ["\\documentclass{article}", "\\begin{document}", "\\section{Old}", "\\end{document}"],
            body.Select(r => r.Source.Text));
        Assert.DoesNotContain(rows, r => r.IsHeader && r.HeaderText.StartsWith('\\'));

        var first = body[0];
        Assert.Same(first.Left, first.Right);
        Assert.Equal("1", first.LeftNumber);
        Assert.Equal("1", first.RightNumber);
    }

    /// <summary>
    /// A context line ends the run of changed lines that precedes it whatever it begins with.
    /// Skipping one pairs a removed line with an added line git never matched it to, and word-
    /// diffs two lines that share no edit.
    /// </summary>
    [Fact]
    public void AContextLineBeginningWithABackslash_EndsTheRunOfChangedLines()
    {
        var rows = SideBySideDiff.Build(Parse("""
            diff --git a/paper.tex b/paper.tex
            --- a/paper.tex
            +++ b/paper.tex
            @@ -1,2 +1,2 @@
            -Hello
             \bigskip
            +Goodbye
            """));

        var body = rows.Where(r => !r.IsHeader).ToList();
        Assert.Equal(3, body.Count);
        Assert.Equal("Hello", body[0].Left!.Text);
        Assert.True(body[0].RightAbsent);
        Assert.Equal("\\bigskip", body[1].Left!.Text);
        Assert.Same(body[1].Left, body[1].Right);
        Assert.True(body[2].LeftAbsent);
        Assert.Equal("Goodbye", body[2].Right!.Text);
    }

    [Fact]
    public void EveryRow_CarriesTheHunkIndexOfTheLineItWasBuiltFrom()
    {
        var lines = Parse(OneForOne);
        var rows = SideBySideDiff.Build(lines);

        Assert.Equal(lines.Count(l => l.IsHunkStart), rows.Count(r => r.IsHunkStart));
        foreach (var row in rows)
            Assert.Equal(row.Source.HunkIndex, row.HunkIndex);
    }

    /// <summary>Every parsed line reaches exactly one row, on the side its file owns it.</summary>
    [Fact]
    public void NoLineIsDroppedAndNoneIsDuplicated()
    {
        var lines = Parse(OneForOne);
        var rows = SideBySideDiff.Build(lines);

        foreach (var line in lines)
            Assert.Single(rows, r => r.Covers(line));
    }

    /// <summary>
    /// A mode-only change parses to header rows and no body: the pane says what changed
    /// instead of showing two empty columns.
    /// </summary>
    [Fact]
    public void AModeChange_RendersAsHeaderRows()
    {
        var rows = SideBySideDiff.Build(Parse("""
            diff --git a/run.sh b/run.sh
            old mode 100644
            new mode 100755
            """));

        Assert.All(rows, r => Assert.True(r.IsHeader));
        Assert.Contains(rows, r => r.HeaderText == "new mode 100755");
    }

    /// <summary>A binary file parses to no rows at all; the pane's binary note stands alone.</summary>
    [Fact]
    public void ABinaryFile_ProducesNoRows()
    {
        var file = FileDiff.ParseUnified("""
            diff --git a/logo.png b/logo.png
            Binary files a/logo.png and b/logo.png differ
            """).Single();

        Assert.True(file.IsBinary);
        Assert.Empty(SideBySideDiff.Build(file.Lines));
    }

    /// <summary>
    /// An untracked file's synthesized diff is all-added, so its old column is absent on every
    /// row — the honest rendering of a file that has no old side.
    /// </summary>
    [Fact]
    public void AnAllAddedFile_LeavesTheOldColumnAbsentThroughout()
    {
        var lines = new List<DiffLine>
        {
            new() { Kind = DiffLineKind.HunkHeader, Text = "@@ new file: 2 lines @@" },
            new() { Kind = DiffLineKind.Added, Text = "one", NewNumber = "1" },
            new() { Kind = DiffLineKind.Added, Text = "two", NewNumber = "2" }
        };

        var rows = SideBySideDiff.Build(lines);

        Assert.All(rows.Where(r => !r.IsHeader), r =>
        {
            Assert.True(r.LeftAbsent);
            Assert.True(r.RightAdded);
        });
    }

    [Fact]
    public void APairedLine_HighlightsOnlyTheWordsThatDiffer()
    {
        var (left, right) = SideBySideDiff.Highlight("var total = count + 1;", "var total = count + 2;");

        Assert.Equal("1", string.Concat(left.Where(s => s.Changed).Select(s => s.Text)));
        Assert.Equal("2", string.Concat(right.Where(s => s.Changed).Select(s => s.Text)));
        Assert.Equal("var total = count + 1;", string.Concat(left.Select(s => s.Text)));
        Assert.Equal("var total = count + 2;", string.Concat(right.Select(s => s.Text)));
    }

    /// <summary>
    /// Two lines with nothing in common are left unmarked: highlighting every word repeats
    /// what the row colour already says and hides the rows where a word really did move.
    /// </summary>
    [Fact]
    public void AWhollyRewrittenLine_CarriesNoWordHighlight()
    {
        var (left, right) = SideBySideDiff.Highlight("alpha beta", "gamma delta");

        Assert.DoesNotContain(left, s => s.Changed);
        Assert.DoesNotContain(right, s => s.Changed);
    }

    [Fact]
    public void AnIdenticalPair_CarriesNoWordHighlight()
    {
        var (left, right) = SideBySideDiff.Highlight("same", "same");

        Assert.DoesNotContain(left, s => s.Changed);
        Assert.DoesNotContain(right, s => s.Changed);
    }

    /// <summary>A generated line is not word-diffed, and it still renders in full.</summary>
    [Fact]
    public void AVeryLongLine_IsRenderedWholeWithoutWordDiffing()
    {
        var old = new string('a', 5000);
        var (left, right) = SideBySideDiff.Highlight(old, old + "b");

        Assert.Equal(old, string.Concat(left.Select(s => s.Text)));
        Assert.Equal(old + "b", string.Concat(right.Select(s => s.Text)));
        Assert.DoesNotContain(left, s => s.Changed);
    }

    /// <summary>
    /// Splitting on whitespace alone would report a whole expression as changed when one
    /// operator inside it moved.
    /// </summary>
    [Fact]
    public void AChangeInsideAToken_HighlightsOnlyThatPart()
    {
        var (_, right) = SideBySideDiff.Highlight("a.b(c)", "a.b(d)");

        Assert.Equal("d", string.Concat(right.Where(s => s.Changed).Select(s => s.Text)));
    }
}
