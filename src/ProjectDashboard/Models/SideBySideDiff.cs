namespace ProjectDashboard.Models;

/// <summary>One run of a rendered diff cell, flagged when it is part of what changed on that row.</summary>
public sealed record DiffSegment(string Text, bool Changed);

/// <summary>
/// One row of the two-column rendering: an old-side cell, a new-side cell, or a header that
/// spans both. A row carries the <see cref="DiffLine"/> instances it was built from, so the
/// hunk it names is the hunk the unified rows name — the two renderings share one
/// <see cref="DiffLine.HunkIndex"/> space and one selection.
/// </summary>
public sealed class SideBySideRow
{
    public DiffLine? Header { get; init; }
    public DiffLine? Left { get; init; }
    public DiffLine? Right { get; init; }

    /// <summary>The row a selection on this row resolves to. Never null.</summary>
    public DiffLine Source => Header ?? Left ?? Right!;

    public int HunkIndex => Source.HunkIndex;
    public bool IsHunkStart => Source.IsHunkStart;

    public bool IsHeader => Header is not null;
    public string HeaderText => Header?.Text ?? "";

    public bool HasLeft => Left is not null;
    public string LeftNumber => Left?.OldNumber ?? "";
    public bool LeftRemoved => Left?.Kind == DiffLineKind.Removed;

    public bool HasRight => Right is not null;
    public string RightNumber => Right?.NewNumber ?? "";
    public bool RightAdded => Right?.Kind == DiffLineKind.Added;

    /// <summary>
    /// A side with no counterpart on this row: the file gained or lost the line, and the cell
    /// renders as absent rather than as an empty line the file does not have.
    /// </summary>
    public bool LeftAbsent => !IsHeader && Left is null;
    public bool RightAbsent => !IsHeader && Right is null;

    /// <summary>The cell's text as runs. One unchanged run when nothing intra-line is highlighted.</summary>
    public IReadOnlyList<DiffSegment> LeftSegments { get; init; } = [];
    public IReadOnlyList<DiffSegment> RightSegments { get; init; } = [];

    /// <summary>Whether this row was built from <paramref name="line"/>, on either side.</summary>
    public bool Covers(DiffLine line) =>
        ReferenceEquals(line, Header) || ReferenceEquals(line, Left) || ReferenceEquals(line, Right);
}

/// <summary>
/// Builds the two-column rendering from the rows the unified pane already holds. No second
/// git call and no second parser: the same <see cref="FileDiff"/> feeds both, so a hunk index,
/// a line number, and a binary or mode-change marker mean the same thing in either mode.
/// </summary>
public static class SideBySideDiff
{
    /// <summary>
    /// Above this many characters a line is not word-diffed. The pairing is quadratic in
    /// nothing, but tokenizing a minified bundle line on every row rebuild is work the reader
    /// cannot see: the row colour already says the line changed.
    /// </summary>
    private const int WordDiffLineLimit = 4000;

    public static List<SideBySideRow> Build(IEnumerable<DiffLine> lines)
    {
        var rows = new List<SideBySideRow>();
        var removed = new List<DiffLine>();
        var added = new List<DiffLine>();
        var notes = new List<DiffLine>();

        foreach (var line in lines)
        {
            if (IsFileNote(line))
            {
                notes.Add(line);
                continue;
            }

            switch (line.Kind)
            {
                case DiffLineKind.Removed:
                    removed.Add(line);
                    continue;
                case DiffLineKind.Added:
                    added.Add(line);
                    continue;
            }

            // A context row or a header ends the run of changed lines that precedes it, and
            // a header also ends the hunk — so a pair is never made across two hunks.
            Flush(rows, removed, added, notes);
            if (line.Kind == DiffLineKind.HunkHeader)
                rows.Add(new SideBySideRow { Header = line });
            else
                rows.Add(new SideBySideRow
                {
                    Left = line,
                    Right = line,
                    LeftSegments = [new DiffSegment(line.Text, false)],
                    RightSegments = [new DiffSegment(line.Text, false)]
                });
        }

        Flush(rows, removed, added, notes);
        return rows;
    }

    /// <summary>
    /// A line git writes about the file rather than from it — "\ No newline at end of file",
    /// which the unified parser carries as context because it sits inside the hunk. It belongs
    /// to no side, so it neither pairs nor ends the run of changed lines it interrupts.
    /// </summary>
    private static bool IsFileNote(DiffLine line) =>
        line.Kind == DiffLineKind.Context && line.Text.StartsWith('\\');

    /// <summary>
    /// Pairs a run of removed lines with the run of added lines that follows it, in order.
    /// Position is the only alignment a unified diff carries; a run of unequal lengths leaves
    /// the surplus rows one-sided rather than paired with a line git never matched them to.
    /// Notes gathered inside the run follow it, spanning both columns like a header: they
    /// describe the lines above them and are text neither file contains.
    /// </summary>
    private static void Flush(List<SideBySideRow> rows, List<DiffLine> removed, List<DiffLine> added,
        List<DiffLine> notes)
    {
        for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
        {
            var left = i < removed.Count ? removed[i] : null;
            var right = i < added.Count ? added[i] : null;
            var (leftSegments, rightSegments) = left is not null && right is not null
                ? Highlight(left.Text, right.Text)
                : (Plain(left), Plain(right));

            rows.Add(new SideBySideRow
            {
                Left = left,
                Right = right,
                LeftSegments = leftSegments,
                RightSegments = rightSegments
            });
        }
        removed.Clear();
        added.Clear();

        foreach (var note in notes) rows.Add(new SideBySideRow { Header = note });
        notes.Clear();
    }

    private static IReadOnlyList<DiffSegment> Plain(DiffLine? line) =>
        line is null ? [] : [new DiffSegment(line.Text, false)];

    /// <summary>
    /// Splits two paired lines into unchanged and changed runs. The changed run is what is
    /// left after the words they open and close with agree — the edit a reader is looking for
    /// on a line that otherwise matches. A pair with nothing in common is left unhighlighted:
    /// marking the whole line adds nothing the row colour has not already said.
    /// </summary>
    internal static (IReadOnlyList<DiffSegment> Left, IReadOnlyList<DiffSegment> Right) Highlight(
        string oldText, string newText)
    {
        if (oldText.Length > WordDiffLineLimit || newText.Length > WordDiffLineLimit || oldText == newText)
            return ([new DiffSegment(oldText, false)], [new DiffSegment(newText, false)]);

        var oldTokens = Tokenize(oldText);
        var newTokens = Tokenize(newText);

        var prefix = 0;
        var shortest = Math.Min(oldTokens.Count, newTokens.Count);
        while (prefix < shortest && oldTokens[prefix] == newTokens[prefix]) prefix++;

        var suffix = 0;
        while (suffix < shortest - prefix
               && oldTokens[^(suffix + 1)] == newTokens[^(suffix + 1)]) suffix++;

        if (prefix == 0 && suffix == 0)
            return ([new DiffSegment(oldText, false)], [new DiffSegment(newText, false)]);

        return (Segments(oldTokens, prefix, suffix), Segments(newTokens, prefix, suffix));
    }

    /// <summary>Joins tokens back into at most three runs: shared head, changed middle, shared tail.</summary>
    private static IReadOnlyList<DiffSegment> Segments(List<string> tokens, int prefix, int suffix)
    {
        var segments = new List<DiffSegment>(3);
        Add(string.Concat(tokens.Take(prefix)), false);
        Add(string.Concat(tokens.Skip(prefix).Take(tokens.Count - prefix - suffix)), true);
        Add(string.Concat(tokens.Skip(tokens.Count - suffix)), false);
        return segments;

        void Add(string text, bool changed)
        {
            if (text.Length > 0) segments.Add(new DiffSegment(text, changed));
        }
    }

    /// <summary>
    /// Words, whitespace runs, and every other character on its own. Splitting on whitespace
    /// alone would report a whole expression as changed when one operator inside it moved.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            if (IsWordCharacter(text[index]))
                while (index < text.Length && IsWordCharacter(text[index])) index++;
            else if (char.IsWhiteSpace(text[index]))
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            else
                index++;
            tokens.Add(text[start..index]);
        }
        return tokens;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
