using System.Globalization;
using System.Text;
using System.Windows.Data;
using ProjectDashboard.Models;

namespace ProjectDashboard.Helpers;

/// <summary>
/// Spells a diff row out for a reader. Added and removed rows are distinguished on screen by a
/// tinted background and by which line-number gutter is filled; neither reaches a screen reader,
/// and the parser strips the +/- status column from the row's text, so the row's kind has to be
/// stated in words here or it is unavailable non-visually.
/// </summary>
public sealed class DiffLineNarrator : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DiffLine line ? Narrate(line) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static string Narrate(DiffLine line)
    {
        if (line.IsNoNewlineMarker) return "No newline at end of file";
        return line.Kind switch
        {
            DiffLineKind.HunkHeader => $"Hunk header {line.Text}",
            DiffLineKind.Added => Row("Added line", line.NewNumber, line.Text),
            DiffLineKind.Removed => Row("Removed line", line.OldNumber, line.Text),
            _ => Row("Line", line.OldNumber.Length > 0 ? line.OldNumber : line.NewNumber, line.Text),
        };
    }

    private static string Row(string kind, string number, string text) =>
        number.Length > 0 ? $"{kind} {number}: {text}" : $"{kind}: {text}";
}

/// <summary>
/// Spells a two-column diff row out for a reader. A cell with no counterpart is a grey block on
/// screen and silence to a reader, so which side gained or lost the line is stated in words.
/// </summary>
public sealed class SideBySideRowNarrator : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SideBySideRow row ? Narrate(row) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static string Narrate(SideBySideRow row)
    {
        if (row.IsHeader) return $"Hunk header {row.HeaderText}";

        var left = Join(row.LeftSegments);
        var right = Join(row.RightSegments);

        if (row.LeftAbsent) return $"Added line {row.RightNumber}: {right}";
        if (row.RightAbsent) return $"Removed line {row.LeftNumber}: {left}";
        if (row.LeftRemoved && row.RightAdded)
            return $"Changed line {row.LeftNumber} to line {row.RightNumber}: was {left}, now {right}";
        return $"Line {row.LeftNumber}: {left}";
    }

    private static string Join(IReadOnlyList<DiffSegment> segments)
    {
        if (segments.Count == 1) return segments[0].Text;
        var text = new StringBuilder();
        foreach (var segment in segments) text.Append(segment.Text);
        return text.ToString();
    }
}
