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
/// What the Path column carries. The path is the one exported value that names the machine —
/// drive layout and the Windows account name — so it is a three-way choice, not a toggle:
/// the full path for a private archive, the folder name alone for a file that leaves the
/// machine, or no column at all.
/// </summary>
public enum ExportPathMode
{
    Full,
    FolderName,
    Omit,
}

/// <summary>How the export dialog groups the column checklist.</summary>
public enum ExportColumnGroup
{
    Identity,
    GitState,
    GitHub,
    Housekeeping,
}

/// <summary>
/// One exportable column: its heading, where its value comes from, and how the dialog offers
/// it. <see cref="Raw"/> keeps a typed value (bool, int, string) so the JSON export writes the
/// type the value has; the text projection every other format uses is derived from it in one
/// place. Null is a fact the source could not fetch and stays distinct from zero.
/// </summary>
public sealed record PortfolioColumn(
    string Key,
    ExportColumnGroup Group,
    bool DefaultOn,
    Func<ProjectInfo, object?> Raw)
{
    public bool IsPath => Key == "Path";
}

/// <summary>
/// Everything an export run needs beyond the projects themselves. <see cref="ColumnKeys"/> is
/// the selection in registry order; the source collection is the caller's choice, so "current
/// view only" is decided before this record is built.
/// </summary>
public sealed record ExportChoices(
    IReadOnlyList<string> ColumnKeys,
    ExportPathMode PathMode,
    bool ExcludeHidden = false,
    bool ExcludeRemoteOnly = false,
    string VisibilityFilter = "",
    string TypeFilter = "",
    string StatusFilter = "",
    string CategoryFilter = "")
{
    public static ExportChoices Default => new(
        [.. PortfolioExport.Registry.Where(c => c.DefaultOn).Select(c => c.Key)],
        ExportPathMode.FolderName);
}

/// <summary>
/// Renders the discovered inventory to a file. Every value comes from what discovery already
/// holds: the export runs no git command of its own, so exporting a hundred repositories
/// costs no process launches and reports the picture the cards are showing.
///
/// One column registry feeds every format: CSV headings, JSON keys, and HTML header cells are
/// the same selected list in the same order by construction, so a column added to one cannot
/// shift another format's headings off its values.
/// </summary>
public static class PortfolioExport
{
    /// <summary>
    /// Every exportable column, in the order any selection of them is written. The first
    /// fourteen are the original fixed set, in their original order — the default selection
    /// with the full path mode reproduces the old export byte for byte.
    /// </summary>
    public static IReadOnlyList<PortfolioColumn> Registry { get; } =
    [
        new("Name", ExportColumnGroup.Identity, DefaultOn: true, p => p.DisplayName),
        new("Path", ExportColumnGroup.Identity, DefaultOn: true, p => p.FullPath),
        new("Type", ExportColumnGroup.Identity, DefaultOn: true, p => p.Manifest.ProjectType),
        new("Status", ExportColumnGroup.Identity, DefaultOn: true, p => p.Manifest.Status),
        new("Category", ExportColumnGroup.Identity, DefaultOn: true, p => p.Manifest.Category),
        new("Version", ExportColumnGroup.Identity, DefaultOn: true, p => p.LatestVersion),
        // Offset-preserving ISO 8601: a bare local timestamp cannot be compared across
        // machines in different zones.
        new("LastCommitDate", ExportColumnGroup.GitState, DefaultOn: true,
            p => p.GitStatus.LastCommitDate?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) ?? ""),
        new("LastCommitSha", ExportColumnGroup.GitState, DefaultOn: true,
            p => p.RecentCommits.Count > 0 ? p.RecentCommits[0].Hash : ""),
        new("Branch", ExportColumnGroup.GitState, DefaultOn: true, p => p.GitStatus.Branch),
        new("Dirty", ExportColumnGroup.GitState, DefaultOn: true, p => p.GitStatus.IsDirty),
        new("Ahead", ExportColumnGroup.GitState, DefaultOn: true, p => p.GitStatus.AheadBy),
        new("Behind", ExportColumnGroup.GitState, DefaultOn: true, p => p.GitStatus.BehindBy),
        new("RemoteSlug", ExportColumnGroup.GitHub, DefaultOn: true, SlugOf),
        new("NoteCount", ExportColumnGroup.Housekeeping, DefaultOn: true, p => NoteCount(p.Manifest.Notes)),

        new("Description", ExportColumnGroup.Identity, DefaultOn: false, p => p.Manifest.Description),
        new("Visibility", ExportColumnGroup.GitHub, DefaultOn: false, p => p.GitStatus.Visibility),
        new("RemoteUrl", ExportColumnGroup.GitHub, DefaultOn: false, p => ScrubbedRemoteUrl(p.GitStatus.RemoteUrl)),
        new("OpenIssueCount", ExportColumnGroup.GitHub, DefaultOn: false, p => p.OpenIssueCount),
        new("OpenPrCount", ExportColumnGroup.GitHub, DefaultOn: false, p => p.OpenPrCount),
        new("ModifiedCount", ExportColumnGroup.GitState, DefaultOn: false, p => p.GitStatus.ModifiedCount),
        new("UntrackedCount", ExportColumnGroup.GitState, DefaultOn: false, p => p.GitStatus.UntrackedCount),
        new("HasConflicts", ExportColumnGroup.GitState, DefaultOn: false, p => p.GitStatus.HasConflicts),
        new("Activity", ExportColumnGroup.GitState, DefaultOn: false, p => p.GitStatus.ActivityLabel),
        new("IsPinned", ExportColumnGroup.Housekeeping, DefaultOn: false, p => p.IsPinned),
        new("IsHidden", ExportColumnGroup.Housekeeping, DefaultOn: false, p => p.IsHidden),
    ];

    /// <summary>
    /// The columns an export actually writes: the selection in registry order, with the Path
    /// column swapped or dropped per the mode. Omit removes the column outright — a heading
    /// over uniformly empty cells reads as data that went missing, not as a choice.
    /// </summary>
    public static List<PortfolioColumn> Selected(ExportChoices choices)
    {
        var wanted = choices.ColumnKeys.ToHashSet(StringComparer.Ordinal);
        var columns = new List<PortfolioColumn>();
        foreach (var column in Registry)
        {
            if (!wanted.Contains(column.Key)) continue;
            if (!column.IsPath)
            {
                columns.Add(column);
                continue;
            }
            switch (choices.PathMode)
            {
                case ExportPathMode.Full: columns.Add(column); break;
                case ExportPathMode.FolderName:
                    columns.Add(column with { Raw = p => p.DirectoryName });
                    break;
                // Omit: dropped.
            }
        }
        return columns;
    }

    /// <summary>
    /// The rows an export describes, ordered by display name so two exports of one inventory
    /// agree byte for byte whatever order discovery returned. Filters are conjunctive; an empty
    /// filter matches everything.
    /// </summary>
    public static List<ProjectInfo> Filtered(IEnumerable<ProjectInfo> projects, ExportChoices choices) =>
    [
        .. projects
            .Where(p => !choices.ExcludeHidden || !p.IsHidden)
            .Where(p => !choices.ExcludeRemoteOnly || !p.IsRemoteOnly)
            .Where(p => Matches(choices.VisibilityFilter, p.GitStatus.Visibility))
            .Where(p => Matches(choices.TypeFilter, p.Manifest.ProjectType))
            .Where(p => Matches(choices.StatusFilter, p.Manifest.Status))
            .Where(p => Matches(choices.CategoryFilter, p.Manifest.Category))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase)
    ];

    private static bool Matches(string filter, string value) =>
        filter.Length == 0 || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The text a non-JSON cell carries; null is an unfetched fact and stays empty, never "0".</summary>
    internal static string Text(object? raw) => raw switch
    {
        null => "",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? "",
    };

    // Relaxed escaping keeps the file the UTF-8 text its encoding claims: the default
    // encoder would render a non-ASCII project name as \uXXXX escapes.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToCsv(IEnumerable<ProjectInfo> projects, ExportChoices choices)
    {
        var columns = Selected(choices);
        var sb = new StringBuilder();
        // RFC 4180 line terminator, so a field's own embedded newline stays distinguishable
        // from the end of a record.
        sb.Append(string.Join(',', columns.Select(c => c.Key))).Append("\r\n");
        foreach (var project in Filtered(projects, choices))
            sb.Append(string.Join(',', columns.Select(c => Escape(Text(c.Raw(project)))))).Append("\r\n");
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<ProjectInfo> projects, ExportChoices choices)
    {
        var columns = Selected(choices);
        // Insertion order is the registry order, so the keys match every other format's
        // headings. A null count serializes as "" rather than null: the other formats write
        // an empty cell there, and the three must state the unfetched fact the same way.
        var rows = Filtered(projects, choices)
            .Select(p =>
            {
                var row = new Dictionary<string, object?>();
                foreach (var column in columns)
                    row[column.Key] = column.Raw(p) ?? "";
                return row;
            })
            .ToList();
        return JsonSerializer.Serialize(rows, JsonOptions) + "\n";
    }

    /// <summary>
    /// A standalone page: the stylesheet is inline and no element references a second file,
    /// so the export opens the same from a mail attachment as from the folder it was written
    /// to. Colours are inherited rather than stated, so the table stays legible under either
    /// browser theme.
    /// </summary>
    public static string ToHtml(IEnumerable<ProjectInfo> projects, ExportChoices choices)
    {
        var columns = Selected(choices);
        var rows = Filtered(projects, choices);
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

        foreach (var column in columns)
            sb.Append("<th>").Append(HtmlEscape(column.Key)).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var project in rows)
        {
            sb.Append("<tr>");
            foreach (var column in columns)
                sb.Append("<td>").Append(HtmlEscape(Text(column.Raw(project)))).Append("</td>");
            sb.Append("</tr>\n");
        }

        return sb.Append("</tbody>\n</table>\n</body>\n</html>\n").ToString();
    }

    public static string Render(IEnumerable<ProjectInfo> projects, PortfolioFormat format, ExportChoices choices) =>
        format switch
        {
            PortfolioFormat.Json => ToJson(projects, choices),
            PortfolioFormat.Html => ToHtml(projects, choices),
            _ => ToCsv(projects, choices),
        };

    public static Task WriteAsync(
        string path, PortfolioFormat format, IEnumerable<ProjectInfo> projects, ExportChoices choices,
        CancellationToken ct = default)
        => AtomicFile.WriteAllTextAsync(path, Render(projects, format, choices), ct);

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

    /// <summary>
    /// The remote URL with any credential stripped before it can reach a file. Git accepts
    /// https://user:token@host/… as a remote, and a token in the config is a token in every
    /// export of the config. Userinfo is dropped whole from http(s) URLs — a bare username is
    /// still an account name in a file built to be shared — and from ssh/git URLs only when it
    /// carries a password, since their conventional "git@" is addressing, not a secret.
    /// </summary>
    internal static string ScrubbedRemoteUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url; // scp-like syntax carries no password position
        var scheme = url[..schemeEnd].ToLowerInvariant();
        var rest = url[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var at = authority.LastIndexOf('@');
        if (at < 0) return url;
        if (scheme is "ssh" or "git" && !authority[..at].Contains(':')) return url;
        return url[..(schemeEnd + 3)] + authority[(at + 1)..] + (slash < 0 ? "" : rest[slash..]);
    }

    /// <summary>owner/repo on any host, not only GitHub; empty when there is no remote.</summary>
    private static object SlugOf(ProjectInfo p)
    {
        if (p.IsRemoteOnly) return p.RemoteSlug;
        var remote = GitRemote.Parse(p.GitStatus.RemoteUrl);
        return remote is null ? "" : $"{remote.Owner}/{remote.Repo}";
    }

    /// <summary>Note lines that carry text; a blank line is not a note.</summary>
    private static int NoteCount(string notes) =>
        string.IsNullOrWhiteSpace(notes) ? 0 : notes.Split('\n').Count(l => l.Trim().Length > 0);

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
