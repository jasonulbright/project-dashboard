using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>Which file an inventory export writes.</summary>
public enum PortfolioFormat
{
    Csv,
    Json,
    Html,
}

/// <summary>
/// One project as the export describes it. Declaration order is the column order, and every
/// format is built from these same rows, so a CSV, a JSON and an HTML export of one dashboard
/// describe the same inventory in the same order.
/// </summary>
public sealed record PortfolioRow(
    string Name,
    string Path,
    string Type,
    string Status,
    string Category,
    string Version,
    string LastCommitDate,
    string LastCommitSha,
    string Branch,
    bool Dirty,
    int Ahead,
    int Behind,
    string RemoteSlug,
    int NoteCount);

/// <summary>
/// Renders the discovered inventory to a file. Every value comes from what discovery already
/// holds: the export runs no git command of its own, so exporting a hundred repositories
/// costs no process launches and reports the picture the cards are showing.
/// </summary>
public static class PortfolioExport
{
    /// <summary>Column headings, in the order every export writes them.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "Name", "Path", "Type", "Status", "Category", "Version",
        "LastCommitDate", "LastCommitSha", "Branch", "Dirty", "Ahead", "Behind",
        "RemoteSlug", "NoteCount",
    ];

    // Relaxed escaping keeps the file the UTF-8 text its encoding claims: the default
    // encoder would render a non-ASCII project name as \uXXXX escapes.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One row per project, ordered by display name so two exports of one inventory agree
    /// byte for byte whatever order discovery returned.
    /// </summary>
    public static List<PortfolioRow> Rows(IEnumerable<ProjectInfo> projects) =>
    [
        .. projects
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(ToRow)
    ];

    public static string ToCsv(IEnumerable<ProjectInfo> projects)
    {
        var sb = new StringBuilder();
        // RFC 4180 line terminator, so a field's own embedded newline stays distinguishable
        // from the end of a record.
        sb.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var row in Rows(projects))
            sb.Append(string.Join(',', Fields(row).Select(Escape))).Append("\r\n");
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<ProjectInfo> projects) =>
        JsonSerializer.Serialize(Rows(projects), JsonOptions) + "\n";

    /// <summary>
    /// A standalone page: the stylesheet is inline and no element references a second file,
    /// so the export opens the same from a mail attachment as from the folder it was written
    /// to. Colours are inherited rather than stated, so the table stays legible under either
    /// browser theme.
    /// </summary>
    public static string ToHtml(IEnumerable<ProjectInfo> projects)
    {
        var rows = Rows(projects);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<title>").Append(HtmlEscape(AppName)).Append(" — project inventory</title>\n")
          .Append("<style>\n").Append(HtmlStyle).Append("</style>\n</head>\n<body>\n")
          .Append("<h1>").Append(HtmlEscape(AppName)).Append(" — project inventory</h1>\n")
          .Append("<p class=\"meta\">")
          .Append(HtmlEscape($"{rows.Count} {(rows.Count == 1 ? "project" : "projects")}"))
          .Append(" · exported ")
          .Append(HtmlEscape(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
          .Append("</p>\n<table>\n<thead>\n<tr>");

        foreach (var column in Columns)
            sb.Append("<th>").Append(HtmlEscape(column)).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var cell in Fields(row))
                sb.Append("<td>").Append(HtmlEscape(cell)).Append("</td>");
            sb.Append("</tr>\n");
        }

        return sb.Append("</tbody>\n</table>\n</body>\n</html>\n").ToString();
    }

    public static string Render(IEnumerable<ProjectInfo> projects, PortfolioFormat format) => format switch
    {
        PortfolioFormat.Json => ToJson(projects),
        PortfolioFormat.Html => ToHtml(projects),
        _ => ToCsv(projects),
    };

    public static Task WriteAsync(
        string path, PortfolioFormat format, IEnumerable<ProjectInfo> projects, CancellationToken ct = default)
        => AtomicFile.WriteAllTextAsync(path, Render(projects, format), ct);

    /// <summary>
    /// The format a chosen destination gets. A named extension outranks the picker's filter:
    /// the save dialog keeps a typed ".json" while the CSV filter is selected, and writing
    /// CSV into it would contradict the name the reader gave the file.
    /// <paramref name="filterIndex"/> is the dialog's own one-based index.
    /// </summary>
    public static PortfolioFormat FormatFor(string path, int filterIndex) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => PortfolioFormat.Json,
            ".html" or ".htm" => PortfolioFormat.Html,
            ".csv" => PortfolioFormat.Csv,
            _ => filterIndex switch
            {
                2 => PortfolioFormat.Json,
                3 => PortfolioFormat.Html,
                _ => PortfolioFormat.Csv,
            },
        };

    private static PortfolioRow ToRow(ProjectInfo p) => new(
        Name: p.DisplayName,
        Path: p.FullPath,
        Type: p.Manifest.ProjectType,
        Status: p.Manifest.Status,
        Category: p.Manifest.Category,
        Version: p.LatestVersion,
        // Offset-preserving ISO 8601: a bare local timestamp cannot be compared across
        // machines in different zones.
        LastCommitDate: p.GitStatus.LastCommitDate?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) ?? "",
        LastCommitSha: p.RecentCommits.Count > 0 ? p.RecentCommits[0].Hash : "",
        Branch: p.GitStatus.Branch,
        Dirty: p.GitStatus.IsDirty,
        Ahead: p.GitStatus.AheadBy,
        Behind: p.GitStatus.BehindBy,
        RemoteSlug: SlugOf(p),
        NoteCount: NoteCount(p.Manifest.Notes));

    /// <summary>owner/repo on any host, not only GitHub; empty when there is no remote.</summary>
    private static string SlugOf(ProjectInfo p)
    {
        if (p.IsRemoteOnly) return p.RemoteSlug;
        var remote = GitRemote.Parse(p.GitStatus.RemoteUrl);
        return remote is null ? "" : $"{remote.Owner}/{remote.Repo}";
    }

    /// <summary>Note lines that carry text; a blank line is not a note.</summary>
    private static int NoteCount(string notes) =>
        string.IsNullOrWhiteSpace(notes) ? 0 : notes.Split('\n').Count(l => l.Trim().Length > 0);

    /// <summary>
    /// The row's cells in column order. Kept beside <see cref="Columns"/>: a cell added to one
    /// and not the other shifts every following column's heading off its values.
    /// </summary>
    private static string[] Fields(PortfolioRow r) =>
    [
        r.Name,
        r.Path,
        r.Type,
        r.Status,
        r.Category,
        r.Version,
        r.LastCommitDate,
        r.LastCommitSha,
        r.Branch,
        r.Dirty ? "true" : "false",
        r.Ahead.ToString(CultureInfo.InvariantCulture),
        r.Behind.ToString(CultureInfo.InvariantCulture),
        r.RemoteSlug,
        r.NoteCount.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// Leading characters a spreadsheet reads as the start of a formula rather than as text.
    /// A branch or project name may legitimately begin with any of them.
    /// </summary>
    private static readonly char[] FormulaLeaders = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// RFC 4180 field: quoted when it holds a comma, a quote, or a line break, with embedded
    /// quotes doubled. A project path may hold every one of those. A value whose first
    /// character would start a formula gains a leading apostrophe first, so opening the file
    /// in a spreadsheet displays the value instead of evaluating it.
    /// </summary>
    private static string Escape(string value)
    {
        if (value.Length > 0 && Array.IndexOf(FormulaLeaders, value[0]) >= 0)
            value = "'" + value;

        return value.IndexOfAny(['"', ',', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private const string AppName = "Project Dashboard";

    private const string HtmlStyle = """
        :root { color-scheme: light dark; }
        body { font-family: "Segoe UI", system-ui, sans-serif; margin: 2rem; line-height: 1.4; }
        h1 { font-size: 1.25rem; margin: 0 0 .25rem; }
        .meta { margin: 0 0 1.25rem; opacity: .7; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid; padding: .35rem .6rem; text-align: left; vertical-align: top; }
        th { white-space: nowrap; }
        tbody tr:nth-child(even) { background: rgba(127, 127, 127, .12); }
        """;

    /// <summary>
    /// Markup-safe text. The ampersand is replaced first: doing it after the others would
    /// re-escape the ampersands they just introduced.
    /// </summary>
    private static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal);
}
