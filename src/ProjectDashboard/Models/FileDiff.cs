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
}

/// <summary>Parsed diff for one file (hunk headers flattened in as rows).</summary>
public sealed class FileDiff
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public bool IsBinary { get; set; }
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

        foreach (var raw in diffText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = new FileDiff();
                files.Add(current);
                continue;
            }
            if (current is null) continue;

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var p = line[4..];
                if (p != "/dev/null") current.OldPath = StripPrefix(p);
                continue;
            }
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = line[4..];
                current.Path = p == "/dev/null" ? current.OldPath ?? "" : StripPrefix(p);
                continue;
            }
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
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = line });
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
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = line[1..], NewNumber = (newNo++).ToString() });
            }
            else if (line.StartsWith('-'))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = line[1..], OldNumber = (oldNo++).ToString() });
            }
            else if (line.StartsWith(' '))
            {
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = line[1..], OldNumber = (oldNo++).ToString(), NewNumber = (newNo++).ToString() });
            }
            else if (line.StartsWith('\\'))
            {
                // "\ No newline at end of file"
                current.Lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = line });
            }
        }

        return files;
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
