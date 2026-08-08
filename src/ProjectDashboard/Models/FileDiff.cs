namespace ProjectDashboard.Models;

public enum DiffLineKind { Context, Added, Removed, HunkHeader }

/// <summary>One rendered row of a unified diff.</summary>
public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Line number in the old file ("" for added/hunk rows).</summary>
    public string OldNumber { get; init; } = "";
    /// <summary>Line number in the new file ("" for removed/hunk rows).</summary>
    public string NewNumber { get; init; } = "";

    /// <summary>
    /// Zero-based position of the hunk this row belongs to WITHIN ITS FILE, counted over the
    /// same column-0 "@@" headers <see cref="Services.GitService.ExtractHunkPatch"/> counts, so
    /// the two agree on which hunk an index names. Negative for a row that precedes the file's
    /// first hunk and for a synthesized diff, which has no hunk a patch could be sliced at.
    /// </summary>
    public int HunkIndex { get; init; } = -1;

    /// <summary>True for the header row of a hunk that can be sliced out of the raw diff.</summary>
    public bool IsHunkStart => Kind == DiffLineKind.HunkHeader && HunkIndex >= 0;
}

/// <summary>Parsed diff for one file (hunk headers flattened in as rows).</summary>
public sealed class FileDiff
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public bool IsBinary { get; set; }
    /// <summary>True for a merge/combined diff (git diff --cc) — rendered read-only, not column-parsed.</summary>
    public bool IsCombined { get; set; }
    public List<DiffLine> Lines { get; } = [];

    /// <summary>
    /// Parses `git diff --no-color` unified output (one or many files).
    /// Handles renames, binary markers, and \ No newline markers.
    /// </summary>
    public static List<FileDiff> ParseUnified(string diffText)
    {
        var files = new List<FileDiff>();
        FileDiff? current = null;
        int oldNo = 0, newNo = 0;
        // ---/+++/index are headers only before the file's first @@. Past it, a
        // deleted body line "-- x" arrives as "--- x" (marker + content) and an
        // added "++ x" as "+++ x"; consuming those as headers drops the row and
        // clobbers OldPath/Path. A Lines.Count gate cannot stand in for this
        // flag: mode-change diffs add old/new mode rows before ---/+++.
        var seenHunk = false;
        // Per FILE, not per diff text: a patch is sliced out of one file's raw diff, so an
        // index counted across files would name a hunk of the wrong file.
        var hunkIndex = -1;

        foreach (var raw in diffText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = new FileDiff();
                files.Add(current);
                // Seed Path from the header so a mode-only change (no ---/+++ lines) still names the file.
                current.Path = PathFromDiffGit(line);
                oldNo = newNo = 0;
                seenHunk = false;
                hunkIndex = -1;
                continue;
            }
            if (line.StartsWith("diff --cc ", StringComparison.Ordinal) ||
                line.StartsWith("diff --combined ", StringComparison.Ordinal))
            {
                // Merge/combined diff: no a/ b/ prefixes, content lines carry 2 status columns.
                current = new FileDiff { IsCombined = true };
                files.Add(current);
                var sp = line.IndexOf(' ', 8);
                current.Path = sp > 0 ? line[(sp + 1)..].Trim() : line["diff --cc ".Length..].Trim();
                oldNo = newNo = 0;
                seenHunk = false;
                hunkIndex = -1;
                continue;
            }
            if (current is null) continue;

            if (line.StartsWith("old mode ", StringComparison.Ordinal))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = line, HunkIndex = hunkIndex });
                continue;
            }
            if (line.StartsWith("new mode ", StringComparison.Ordinal))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = line, HunkIndex = hunkIndex });
                continue;
            }

            if (current.IsCombined)
            {
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                    line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    current.IsBinary = true;
                    continue;
                }
                // Combined-diff body: keep it readable rather than mis-counting columns.
                // Headers (index, mode, --- / +++) appear only before the first @@@;
                // past it, a body line can itself begin with "---"/"+++" (two status
                // columns plus content), so headers are skipped by position, not prefix.
                if (line.StartsWith("@@@", StringComparison.Ordinal))
                    current.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = line, HunkIndex = ++hunkIndex });
                else if (current.Lines.Count == 0 || line.Length == 0)
                {
                    // Pre-hunk metadata, or the blank artifact of a trailing newline.
                }
                else if (line.StartsWith('+'))
                    current.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = line, HunkIndex = hunkIndex });
                else if (line.StartsWith('-'))
                    current.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = line, HunkIndex = hunkIndex });
                else
                    current.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = line, HunkIndex = hunkIndex });
                continue;
            }

            if (!seenHunk && line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var p = line[4..];
                if (p != "/dev/null") current.OldPath = StripPrefix(p);
                continue;
            }
            if (!seenHunk && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = line[4..];
                current.Path = p == "/dev/null" ? current.OldPath ?? "" : StripPrefix(p);
                continue;
            }
            if (!seenHunk && line.StartsWith("index ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                current.IsBinary = true;
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var at = ParseHunkHeader(line);
                oldNo = at.oldStart;
                newNo = at.newStart;
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = line, HunkIndex = ++hunkIndex });
                seenHunk = true;
                continue;
            }
            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                current.OldPath = line["rename from ".Length..];
                continue;
            }
            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                current.Path = line["rename to ".Length..];
                continue;
            }
            // Other metadata (index, mode, similarity) — skip.
            if (current.Lines.Count == 0 && !line.StartsWith('+') && !line.StartsWith('-') && !line.StartsWith(' '))
                continue;

            if (line.StartsWith('+'))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = line[1..], NewNumber = (newNo++).ToString(), HunkIndex = hunkIndex });
            }
            else if (line.StartsWith('-'))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = line[1..], OldNumber = (oldNo++).ToString(), HunkIndex = hunkIndex });
            }
            else if (line.StartsWith(' '))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = line[1..], OldNumber = (oldNo++).ToString(), NewNumber = (newNo++).ToString(), HunkIndex = hunkIndex });
            }
            else if (line.StartsWith('\\'))
            {
                // "\ No newline at end of file"
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = line, HunkIndex = hunkIndex });
            }
        }

        return files;
    }

    /// <summary>
    /// Path from a "diff --git a/P b/P" header. For an unrenamed change git emits the
    /// identical path twice, so P = the front half of "P b/P" — computed by length rather
    /// than by finding " b/" (which a path containing that substring would break).
    /// </summary>
    private static string PathFromDiffGit(string line)
    {
        var rest = line["diff --git ".Length..].Trim();
        if (!rest.StartsWith("a/", StringComparison.Ordinal)) return "";
        var body = rest[2..]; // "P b/P"
        // body == P + " b/" + P  =>  len(body) = 2*len(P) + 3
        if ((body.Length - 3) % 2 != 0) return "";
        var pLen = (body.Length - 3) / 2;
        if (pLen <= 0) return "";
        var p = body[..pLen];
        return body == $"{p} b/{p}" ? p : "";
    }

    private static string StripPrefix(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;

    private static (int oldStart, int newStart) ParseHunkHeader(string line)
    {
        // @@ -12,5 +13,6 @@ optional section
        try
        {
            var parts = line.Split(' ');
            var oldPart = parts[1].TrimStart('-').Split(',')[0];
            var newPart = parts[2].TrimStart('+').Split(',')[0];
            return (int.Parse(oldPart), int.Parse(newPart));
        }
        catch
        {
            return (0, 0);
        }
    }
}
