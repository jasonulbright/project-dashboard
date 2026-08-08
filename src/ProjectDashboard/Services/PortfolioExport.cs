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
}

/// <summary>
/// One project as the export describes it. Declaration order is the column order, and both
/// formats are built from these same rows, so a CSV and a JSON export of one dashboard
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

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

    public static string Render(IEnumerable<ProjectInfo> projects, PortfolioFormat format) =>
        format == PortfolioFormat.Json ? ToJson(projects) : ToCsv(projects);

    /// <summary>
    /// Writes the inventory as UTF-8 without a byte-order mark, matching every other file
    /// this app produces.
    /// </summary>
    public static Task WriteAsync(
        string path, PortfolioFormat format, IEnumerable<ProjectInfo> projects, CancellationToken ct = default) =>
        File.WriteAllTextAsync(path, Render(projects, format), Utf8NoBom, ct);

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
            ".csv" => PortfolioFormat.Csv,
            _ => filterIndex == 2 ? PortfolioFormat.Json : PortfolioFormat.Csv,
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
    /// RFC 4180 field: quoted when it holds a comma, a quote, or a line break, with embedded
    /// quotes doubled. A project path may hold every one of those.
    /// </summary>
    private static string Escape(string value) =>
        value.IndexOfAny(['"', ',', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
