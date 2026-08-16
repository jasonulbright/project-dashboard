using System.Text.Json;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The inventory file. One column registry feeds every format, so the headings, keys, and
/// header cells are asserted as the same list; a project path is the field most likely to
/// carry a comma, a quote, or a line break, and an unescaped one silently shifts every
/// following column of that row.
/// </summary>
public class PortfolioExportTests
{
    /// <summary>The original fixed export: default columns, full paths.</summary>
    internal static ExportChoices Legacy => ExportChoices.Default with { PathMode = ExportPathMode.Full };

    private static List<string> SelectedKeys(ExportChoices choices) =>
        [.. PortfolioExport.Selected(choices).Select(c => c.Key)];

    [Fact]
    public void TheDefaultSelection_IsTheOriginalFourteenColumnsInTheirOriginalOrder()
    {
        Assert.Equal(
            ["Name", "Path", "Type", "Status", "Category", "Version",
             "LastCommitDate", "LastCommitSha", "Branch", "Dirty", "Ahead", "Behind",
             "RemoteSlug", "NoteCount"],
            SelectedKeys(Legacy));
    }

    /// <summary>
    /// The pinned shape of the pre-registry export: same headings, same cell values, same
    /// escaping. The rewrite must not move a byte of what a default full-path export writes.
    /// </summary>
    [Fact]
    public void TheDefaultFullPathExport_MatchesThePinnedLegacyBytes()
    {
        var project = NewProject("alpha");
        project.LatestVersion = "1.0";
        project.Manifest = new ProjectManifest { ProjectType = "dotnet", Status = "active", Category = "Tools" };
        project.GitStatus = new GitStatus { Branch = "main", IsDirty = true, AheadBy = 2, BehindBy = 0 };

        Assert.Equal(
            "Name,Path,Type,Status,Category,Version,LastCommitDate,LastCommitSha,Branch,Dirty,Ahead,Behind,RemoteSlug,NoteCount\r\n"
            + @"alpha,C:\projects\alpha,dotnet,active,Tools,1.0,,,main,true,2,0,,0" + "\r\n",
            PortfolioExport.ToCsv([project], Legacy));
    }

    [Fact]
    public void EveryRow_HasExactlyOneCellPerColumn()
    {
        var csv = PortfolioExport.ToCsv([NewProject("alpha"), NewProject("bravo")], Legacy);

        foreach (var line in csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            Assert.Equal(SelectedKeys(Legacy).Count, SplitCsvLine(line).Count);
    }

    [Fact]
    public void APathHoldingACommaAQuoteOrANewline_IsQuotedAndReadsBackWhole()
    {
        var awkward = "C:\\projects\\a,b \"quoted\"\nsecond line";
        var project = NewProject("awkward");
        project.FullPath = awkward;

        var csv = PortfolioExport.ToCsv([project], Legacy);
        var cells = SplitCsvLine(csv.Split("\r\n")[1]);

        Assert.Equal(awkward, cells[SelectedKeys(Legacy).IndexOf("Path")]);
    }

    // ── Column selection ────────────────────────────────────────────────────

    /// <summary>
    /// Whatever subset is picked, the CSV headings, JSON keys, and HTML header cells are the
    /// same list in the same order — the registry's order, not the click order.
    /// </summary>
    [Fact]
    public void ASelectedSubset_WritesTheSameColumnsInTheSameOrderInEveryFormat()
    {
        var choices = Legacy with { ColumnKeys = ["Visibility", "Name", "Branch"] };
        var project = NewProject("alpha");

        var csvHeader = SplitCsvLine(PortfolioExport.ToCsv([project], choices).Split("\r\n")[0]);
        var jsonKeys = JsonRows(PortfolioExport.ToJson([project], choices)).Single().Keys.ToList();
        var htmlHeads = System.Text.RegularExpressions.Regex
            .Matches(PortfolioExport.ToHtml([project], choices), "<th>(.*?)</th>")
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(["Name", "Branch", "Visibility"], csvHeader);
        Assert.Equal(csvHeader, jsonKeys);
        Assert.Equal(csvHeader, htmlHeads);
    }

    [Fact]
    public void TheNewColumns_CarryTheFactsTheOldExportLeftOut()
    {
        var project = NewProject("rich");
        project.Manifest = new ProjectManifest { Description = "the flagship" };
        project.GitStatus = new GitStatus
        {
            Visibility = "private",
            RemoteUrl = "https://github.com/acme/rich.git",
            ModifiedCount = 3,
            UntrackedCount = 1,
            HasConflicts = true,
            ActivityLabel = "rebase",
        };
        project.OpenIssueCount = 4;
        project.IsPinned = true;
        var choices = Legacy with
        {
            ColumnKeys = ["Description", "Visibility", "RemoteUrl", "OpenIssueCount",
                          "ModifiedCount", "UntrackedCount", "HasConflicts", "Activity", "IsPinned", "IsHidden"],
        };

        var row = JsonRows(PortfolioExport.ToJson([project], choices)).Single();

        Assert.Equal("the flagship", row["Description"].GetString());
        Assert.Equal("private", row["Visibility"].GetString());
        Assert.Equal("https://github.com/acme/rich.git", row["RemoteUrl"].GetString());
        Assert.Equal(4, row["OpenIssueCount"].GetInt32());
        Assert.Equal(3, row["ModifiedCount"].GetInt32());
        Assert.Equal(1, row["UntrackedCount"].GetInt32());
        Assert.True(row["HasConflicts"].GetBoolean());
        Assert.Equal("rebase", row["Activity"].GetString());
        Assert.True(row["IsPinned"].GetBoolean());
        Assert.False(row["IsHidden"].GetBoolean());
    }

    /// <summary>Null is a count nothing fetched; written as zero it would claim an answer.</summary>
    [Fact]
    public void AnUnfetchedIssueCount_ExportsEmptyInEveryFormatAndNeverZero()
    {
        var project = NewProject("quiet");
        var choices = Legacy with { ColumnKeys = ["Name", "OpenIssueCount", "OpenPrCount"] };

        var csvCells = SplitCsvLine(PortfolioExport.ToCsv([project], choices).Split("\r\n")[1]);
        Assert.Equal(["quiet", "", ""], csvCells);

        var row = JsonRows(PortfolioExport.ToJson([project], choices)).Single();
        Assert.Equal("", row["OpenIssueCount"].GetString());

        Assert.Equal(["quiet", "", ""], HtmlBodyRows(PortfolioExport.ToHtml([project], choices)).Single());
    }

    // ── Path modes ──────────────────────────────────────────────────────────

    [Fact]
    public void TheThreePathModes_WriteTheFullPathTheFolderNameOrNoColumnAtAll()
    {
        var project = NewProject("alpha");

        var full = PortfolioExport.ToCsv([project], Legacy with { PathMode = ExportPathMode.Full });
        Assert.Contains(@"C:\projects\alpha", full);

        var folder = PortfolioExport.ToCsv([project], Legacy with { PathMode = ExportPathMode.FolderName });
        Assert.DoesNotContain(@"C:\projects", folder);
        var cells = SplitCsvLine(folder.Split("\r\n")[1]);
        Assert.Equal("alpha", cells[1]);

        var omitted = PortfolioExport.ToCsv([project], Legacy with { PathMode = ExportPathMode.Omit });
        Assert.DoesNotContain("Path", omitted.Split("\r\n")[0]);
        Assert.Equal(SelectedKeys(Legacy).Count - 1, SplitCsvLine(omitted.Split("\r\n")[0]).Count);
    }

    [Fact]
    public void TheOmittedPathColumn_IsAbsentFromEveryFormatNotBlankUnderItsHeading()
    {
        var project = NewProject("alpha");
        var choices = Legacy with { PathMode = ExportPathMode.Omit };

        Assert.False(JsonRows(PortfolioExport.ToJson([project], choices)).Single().ContainsKey("Path"));
        Assert.DoesNotContain("<th>Path</th>", PortfolioExport.ToHtml([project], choices));
    }

    [Fact]
    public void AFolderNameHoldingACommaOrQuote_IsStillEscapedInCsv()
    {
        var project = NewProject("alpha");
        project.DirectoryName = "a,b \"c\"";

        var csv = PortfolioExport.ToCsv([project], Legacy with { PathMode = ExportPathMode.FolderName });

        Assert.Equal("a,b \"c\"", SplitCsvLine(csv.Split("\r\n")[1])[1]);
    }

    // ── Filters ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheFilters_NarrowTheExportedSetToExactlyTheMatchingRows()
    {
        var visible = NewProject("visible");
        var hidden = NewProject("hidden");
        hidden.IsHidden = true;
        var cloud = new ProjectInfo { DirectoryName = "cloud", DisplayName = "cloud", IsRemoteOnly = true };
        var priv = NewProject("locked");
        priv.GitStatus = new GitStatus { Visibility = "private" };
        var tooled = NewProject("tooled");
        tooled.Manifest = new ProjectManifest { Category = "Tools" };
        var all = new[] { visible, hidden, cloud, priv, tooled };

        Assert.Equal(["cloud", "locked", "tooled", "visible"],
            PortfolioExport.Filtered(all, Legacy with { ExcludeHidden = true }).Select(p => p.DisplayName));
        Assert.Equal(["hidden", "locked", "tooled", "visible"],
            PortfolioExport.Filtered(all, Legacy with { ExcludeRemoteOnly = true }).Select(p => p.DisplayName));
        Assert.Equal(["locked"],
            PortfolioExport.Filtered(all, Legacy with { VisibilityFilter = "private" }).Select(p => p.DisplayName));
        Assert.Equal(["tooled"],
            PortfolioExport.Filtered(all, Legacy with { CategoryFilter = "Tools" }).Select(p => p.DisplayName));
        Assert.Equal(["tooled"],
            PortfolioExport.Filtered(all, Legacy with
            {
                ExcludeHidden = true, ExcludeRemoteOnly = true, CategoryFilter = "Tools",
            }).Select(p => p.DisplayName));
    }

    // ── The facts a row carries (unchanged from the fixed export) ───────────

    [Fact]
    public void TheRows_CarryTheFactsDiscoveryAlreadyHolds()
    {
        var project = NewProject("trackr");
        project.LatestVersion = "1.4.2";
        project.Manifest = new ProjectManifest
        {
            ProjectType = "dotnet",
            Status = "active",
            Category = "Tools",
            Notes = "TASK: ship it\n\nBUG: fix that\n",
        };
        project.GitStatus = new GitStatus
        {
            Branch = "main",
            IsDirty = true,
            AheadBy = 2,
            BehindBy = 3,
            RemoteUrl = "https://github.com/acme/trackr.git",
            LastCommitDate = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5)),
        };
        project.RecentCommits = [new GitCommit { Hash = new string('a', 40), ShortHash = "aaaaaaa" }];

        var row = JsonRows(PortfolioExport.ToJson([project], Legacy)).Single();

        Assert.Equal("trackr", row["Name"].GetString());
        Assert.Equal("dotnet", row["Type"].GetString());
        Assert.Equal("active", row["Status"].GetString());
        Assert.Equal("Tools", row["Category"].GetString());
        Assert.Equal("1.4.2", row["Version"].GetString());
        Assert.Equal("2026-03-04T05:06:07-05:00", row["LastCommitDate"].GetString());
        Assert.Equal(new string('a', 40), row["LastCommitSha"].GetString());
        Assert.Equal("main", row["Branch"].GetString());
        Assert.True(row["Dirty"].GetBoolean());
        Assert.Equal(2, row["Ahead"].GetInt32());
        Assert.Equal(3, row["Behind"].GetInt32());
        Assert.Equal("acme/trackr", row["RemoteSlug"].GetString());
        Assert.Equal(2, row["NoteCount"].GetInt32());
    }

    [Fact]
    public void ARepositoryWithNoCommitsOrRemote_ExportsBlanksRatherThanInventedValues()
    {
        var row = JsonRows(PortfolioExport.ToJson([NewProject("bare")], Legacy)).Single();

        Assert.Equal("", row["LastCommitDate"].GetString());
        Assert.Equal("", row["LastCommitSha"].GetString());
        Assert.Equal("", row["RemoteSlug"].GetString());
        Assert.Equal(0, row["NoteCount"].GetInt32());
    }

    [Fact]
    public void ARemoteOnASelfHostedHost_StillExportsItsOwnerAndRepo()
    {
        var project = NewProject("internal-tool");
        project.GitStatus = new GitStatus { RemoteUrl = "git@gitlab.example.com:platform/team/internal-tool.git" };

        Assert.Equal("platform/team/internal-tool",
            JsonRows(PortfolioExport.ToJson([project], Legacy)).Single()["RemoteSlug"].GetString());
    }

    [Fact]
    public void ACloudCard_ExportsItsSlugAndAnEmptyPath()
    {
        var cloud = new ProjectInfo
        {
            DirectoryName = "sketchpad",
            DisplayName = "sketchpad",
            FullPath = "",
            IsRemoteOnly = true,
            RemoteSlug = "acme/sketchpad",
        };

        var row = JsonRows(PortfolioExport.ToJson([cloud], Legacy)).Single();

        Assert.Equal("", row["Path"].GetString());
        Assert.Equal("acme/sketchpad", row["RemoteSlug"].GetString());
    }

    [Fact]
    public void TheRowOrder_IsByNameRatherThanDiscoveryOrder()
    {
        var rows = JsonRows(PortfolioExport.ToJson(
            [NewProject("charlie"), NewProject("alpha"), NewProject("bravo")], Legacy));

        Assert.Equal(["alpha", "bravo", "charlie"], rows.Select(r => r["Name"].GetString()));
    }

    [Fact]
    public void TheJsonCsvAndHtmlExports_DescribeTheSameRowsInTheSameOrder()
    {
        var projects = new[] { NewProject("charlie"), NewProject("alpha") };

        var fromJson = JsonRows(PortfolioExport.ToJson(projects, Legacy));
        var name = SelectedKeys(Legacy).IndexOf("Name");
        var path = SelectedKeys(Legacy).IndexOf("Path");
        var fromCsv = PortfolioExport.ToCsv(projects, Legacy)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(SplitCsvLine)
            .ToList();
        var fromHtml = HtmlBodyRows(PortfolioExport.ToHtml(projects, Legacy));

        Assert.Equal(2, fromJson.Count);
        Assert.Equal(2, fromCsv.Count);
        Assert.Equal(2, fromHtml.Count);
        for (var i = 0; i < fromJson.Count; i++)
        {
            Assert.Equal(fromJson[i]["Name"].GetString(), fromCsv[i][name]);
            Assert.Equal(fromJson[i]["Name"].GetString(), fromHtml[i][name]);
            Assert.Equal(fromJson[i]["Path"].GetString(), fromCsv[i][path]);
            Assert.Equal(fromJson[i]["Path"].GetString(), fromHtml[i][path]);
        }
    }

    [Fact]
    public void TheHtmlTable_HasOneCellPerColumnForEveryProject()
    {
        var html = PortfolioExport.ToHtml([NewProject("alpha"), NewProject("bravo"), NewProject("charlie")], Legacy);
        var columns = SelectedKeys(Legacy).Count;

        Assert.Equal(columns, CountOccurrences(html, "<th>"));
        Assert.Equal(3 * columns, CountOccurrences(html, "<td>"));
        foreach (var row in HtmlBodyRows(html))
            Assert.Equal(columns, row.Count);
    }

    [Fact]
    public void AProjectNameHoldingMarkup_IsEscapedRatherThanRendered()
    {
        var hostile = "<script>alert(\"x\")</script> & co";
        var project = NewProject(hostile);

        var html = PortfolioExport.ToHtml([project], Legacy);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);

        var cells = HtmlBodyRows(html).Single();
        Assert.Equal(SelectedKeys(Legacy).Count, cells.Count);
        Assert.Equal(hostile, cells[SelectedKeys(Legacy).IndexOf("Name")]);
    }

    [Fact]
    public void TheHtmlPage_CarriesItsOwnStylesAndReferencesNoOtherFile()
    {
        var html = PortfolioExport.ToHtml([NewProject("alpha")], Legacy);

        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        // Only the chrome is scheme-free by construction; row cells carry whatever a project holds.
        var chrome = html[..html.IndexOf("<tbody>", StringComparison.Ordinal)];
        Assert.DoesNotContain("http", chrome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHtmlPage_NamesTheAppAndWhenItWasExported()
    {
        var html = PortfolioExport.ToHtml([NewProject("alpha")], Legacy);

        Assert.Contains("Project Dashboard", html, StringComparison.Ordinal);
        // A literal date would fail on a run that straddles midnight; the shape is the claim.
        Assert.Matches(@"· exported \d{4}-\d{2}-\d{2} \d{2}:\d{2}", html);
        Assert.Contains("1 project ", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\",\"y\")")]
    [InlineData("+1-800-repo")]
    [InlineData("-release")]
    [InlineData("@everyone")]
    [InlineData("\tindented")]
    [InlineData("\rreturned")]
    public void ABranchStartingLikeAFormula_IsQuotedInCsvAndLeftAloneElsewhere(string branch)
    {
        var project = NewProject("alpha");
        project.GitStatus = new GitStatus { Branch = branch };
        var column = SelectedKeys(Legacy).IndexOf("Branch");

        var csvCell = SplitCsvLine(PortfolioExport.ToCsv([project], Legacy).Split("\r\n")[1])[column];
        Assert.Equal("'" + branch, csvCell);

        Assert.Equal(branch, JsonRows(PortfolioExport.ToJson([project], Legacy)).Single()["Branch"].GetString());
        Assert.Equal(branch, HtmlBodyRows(PortfolioExport.ToHtml([project], Legacy)).Single()[column]);
    }

    [Fact]
    public void ABranchWithAnOrdinaryFirstCharacter_GainsNoApostrophe()
    {
        var project = NewProject("alpha");
        project.GitStatus = new GitStatus { Branch = "main" };

        var cells = SplitCsvLine(PortfolioExport.ToCsv([project], Legacy).Split("\r\n")[1]);

        Assert.Equal("main", cells[SelectedKeys(Legacy).IndexOf("Branch")]);
    }

    /// <summary>
    /// Git accepts https://user:token@host/… as a remote; a token in the config must never be a
    /// token in an export. Userinfo is dropped whole from http(s); ssh's conventional "git@" is
    /// addressing, not a secret, and stays.
    /// </summary>
    [Theory]
    [InlineData("https://jason:ghp_secret123@github.com/acme/x.git", "https://github.com/acme/x.git")]
    [InlineData("https://ghp_secret123@github.com/acme/x.git", "https://github.com/acme/x.git")]
    [InlineData("http://user:pw@host.example/repo", "http://host.example/repo")]
    [InlineData("ssh://git@github.com/acme/x.git", "ssh://git@github.com/acme/x.git")]
    [InlineData("ssh://user:pw@host.example/repo", "ssh://host.example/repo")]
    [InlineData("git@github.com:acme/x.git", "git@github.com:acme/x.git")]
    [InlineData("https://github.com/acme/x.git", "https://github.com/acme/x.git")]
    public void TheRemoteUrlColumn_NeverCarriesACredential(string remote, string expected)
    {
        Assert.Equal(expected, PortfolioExport.ScrubbedRemoteUrl(remote));

        var project = NewProject("alpha");
        project.GitStatus = new GitStatus { RemoteUrl = remote };
        var choices = Legacy with { ColumnKeys = ["Name", "RemoteUrl"] };

        Assert.Equal(expected, JsonRows(PortfolioExport.ToJson([project], choices)).Single()["RemoteUrl"].GetString());
        Assert.DoesNotContain("secret123", PortfolioExport.ToCsv([project], choices));
        Assert.DoesNotContain("secret123", PortfolioExport.ToHtml([project], choices));
    }

    [Fact]
    public void TheJson_KeepsNonAsciiTextAsTextRatherThanEscapes()
    {
        var project = NewProject("caf\u00e9");

        Assert.Contains("caf\u00e9", PortfolioExport.ToJson([project], Legacy));
    }

    [Theory]
    [InlineData(@"C:\out\projects.csv", 1, PortfolioFormat.Csv)]
    [InlineData(@"C:\out\projects.json", 2, PortfolioFormat.Json)]
    [InlineData(@"C:\out\projects.html", 3, PortfolioFormat.Html)]
    [InlineData(@"C:\out\projects.htm", 3, PortfolioFormat.Html)]
    // A typed extension outranks the filter that was left selected.
    [InlineData(@"C:\out\projects.json", 1, PortfolioFormat.Json)]
    [InlineData(@"C:\out\projects.csv", 2, PortfolioFormat.Csv)]
    [InlineData(@"C:\out\projects.html", 1, PortfolioFormat.Html)]
    [InlineData(@"C:\out\projects.csv", 3, PortfolioFormat.Csv)]
    // No extension to go on: the filter decides.
    [InlineData(@"C:\out\projects", 2, PortfolioFormat.Json)]
    [InlineData(@"C:\out\projects", 1, PortfolioFormat.Csv)]
    [InlineData(@"C:\out\projects", 3, PortfolioFormat.Html)]
    public void TheChosenFormat_FollowsTheExtensionAndFallsBackToTheFilter(
        string path, int filterIndex, PortfolioFormat expected)
    {
        Assert.Equal(expected, PortfolioExport.FormatFor(path, filterIndex));
    }

    internal static ProjectInfo NewProject(string name) => new()
    {
        DirectoryName = name,
        DisplayName = name,
        FullPath = $@"C:\projects\{name}",
    };

    internal static List<Dictionary<string, JsonElement>> JsonRows(string json) =>
        JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The table body as cells, so the escaping is asserted by reading it back rather than by
    /// matching the markup the writer happened to emit.
    /// </summary>
    internal static List<List<string>> HtmlBodyRows(string html)
    {
        var body = html[(html.IndexOf("<tbody>", StringComparison.Ordinal) + "<tbody>".Length)..
                        html.IndexOf("</tbody>", StringComparison.Ordinal)];

        return [.. System.Text.RegularExpressions.Regex
            .Matches(body, "<tr>(.*?)</tr>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(row => System.Text.RegularExpressions.Regex
                .Matches(row.Groups[1].Value, "<td>(.*?)</td>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(cell => HtmlUnescape(cell.Groups[1].Value))
                .ToList())];
    }

    /// <summary>The ampersand is restored last, mirroring the writer's order.</summary>
    private static string HtmlUnescape(string value) =>
        value.Replace("&lt;", "<", StringComparison.Ordinal)
             .Replace("&gt;", ">", StringComparison.Ordinal)
             .Replace("&quot;", "\"", StringComparison.Ordinal)
             .Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>A minimal RFC 4180 reader, so the escaping is asserted by reading it back.</summary>
    internal static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') { cell.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; continue; }
                quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>
/// The export as the dashboard runs it: the file that lands on disk, its encoding, and the
/// preferences that survive between exports.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardPortfolioExportTests
{
    public DashboardPortfolioExportTests() => TestSandbox.ResetDataDir();

    private static ExportChoices Legacy => PortfolioExportTests.Legacy;

    [Fact]
    public async Task AnExportedCsv_IsUtf8WithoutAByteOrderMarkAndCoversEveryProject()
    {
        var (dashboard, root) = await NewDashboardAsync("export-csv");
        dashboard.Projects.Add(PortfolioExportTests.NewProject("alpha"));
        dashboard.Projects.Add(PortfolioExportTests.NewProject("bravo"));
        var target = Path.Combine(root, "projects.csv");

        await dashboard.WritePortfolioAsync(target, PortfolioFormat.Csv, [.. dashboard.Projects], Legacy);

        var bytes = await File.ReadAllBytesAsync(target);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the export was written with a byte-order mark");

        var text = await File.ReadAllTextAsync(target);
        Assert.Equal(PortfolioExport.ToCsv(dashboard.Projects, Legacy), text);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal($"Exported 2 projects to {target}", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AnExportedHtmlPage_HoldsARowPerProjectAndStandsAlone()
    {
        var (dashboard, root) = await NewDashboardAsync("export-html");
        dashboard.Projects.Add(PortfolioExportTests.NewProject("alpha"));
        dashboard.Projects.Add(PortfolioExportTests.NewProject("bravo"));
        var target = Path.Combine(root, "projects.html");

        await dashboard.WritePortfolioAsync(target, PortfolioFormat.Html, [.. dashboard.Projects], Legacy);

        var html = await File.ReadAllTextAsync(target);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</html>\n", html);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            dashboard.Projects.Select(p => p.DisplayName),
            PortfolioExportTests.HtmlBodyRows(html).Select(cells => cells[0]));
        Assert.Equal($"Exported 2 projects to {target}", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AnExportThatFailsToWrite_LeavesThePreviousFileIntactAndStagesNothing()
    {
        var (dashboard, root) = await NewDashboardAsync("export-atomic");
        dashboard.Projects.Add(PortfolioExportTests.NewProject("alpha"));
        var target = Path.Combine(root, "projects.csv");
        await File.WriteAllTextAsync(target, "an earlier export\r\n");

        // Opened without sharing: the staged file cannot replace it, which is the failure a
        // direct write would have met only after truncating the destination.
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            await dashboard.WritePortfolioAsync(target, PortfolioFormat.Csv, [.. dashboard.Projects], Legacy);

        Assert.StartsWith("Export failed — ", dashboard.OpStatusText);
        Assert.Equal("an earlier export\r\n", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".tmp"), "the staged file was left beside the destination");
    }

    [Fact]
    public async Task ExportingWithNothingDiscovered_SaysSoAndOpensNoDialog()
    {
        var (dashboard, _) = await NewDashboardAsync("export-empty");

        await dashboard.ExportPortfolioCommand.ExecuteAsync(null);

        Assert.Equal("Export: no projects have been discovered to export.", dashboard.OpStatusText);
    }

    /// <summary>Accepts the dialog without a window, keeping the view model it was shown.</summary>
    private sealed class AcceptingDashboard : DashboardViewModel
    {
        private readonly bool _accept;

        public AcceptingDashboard(
            ProjectDiscoveryService discovery, SettingsService settings, GitHubService gitHub,
            ProjectWatcherService watcher, bool accept)
            : base(discovery, null!, settings, gitHub, new GitService(), watcher,
                new RepoBusyRegistry(), uiPost: callback => callback())
            => _accept = accept;

        public ViewModels.Windows.ExportDialogViewModel? Shown { get; private set; }

        internal override Task<bool> ShowExportDialogAsync(ViewModels.Windows.ExportDialogViewModel dialog)
        {
            Shown = dialog;
            return Task.FromResult(_accept);
        }

        internal override (string Path, PortfolioFormat Format)? PromptForExportDestination() => null;
    }

    /// <summary>
    /// The dialog's accepted choices survive to the next export — including through the save
    /// dialog being cancelled, which must not cost the reader the columns they just picked.
    /// </summary>
    [Fact]
    public async Task AcceptedChoices_AreRememberedEvenWhenTheSaveDialogIsCancelled()
    {
        var (settings, discovery, gitHub, watcher) = await ServicesAsync("export-prefs");
        var dashboard = new AcceptingDashboard(discovery, settings, gitHub, watcher, accept: true);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        dashboard.Projects.Add(PortfolioExportTests.NewProject("alpha"));

        await dashboard.ExportPortfolioCommand.ExecuteAsync(null);

        Assert.NotNull(dashboard.Shown);
        var saved = settings.Load().Export;
        Assert.NotNull(saved);
        Assert.Equal("FolderName", saved!.PathMode);
        Assert.Equal(dashboard.Shown!.Choices().ColumnKeys, saved.Columns);
    }

    [Fact]
    public async Task ACancelledDialog_WritesNothingAndRemembersNothing()
    {
        var (settings, discovery, gitHub, watcher) = await ServicesAsync("export-cancel");
        var dashboard = new AcceptingDashboard(discovery, settings, gitHub, watcher, accept: false);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        dashboard.Projects.Add(PortfolioExportTests.NewProject("alpha"));

        await dashboard.ExportPortfolioCommand.ExecuteAsync(null);

        Assert.Null(settings.Load().Export);
    }

    [Fact]
    public void TheSaveDialogFilter_OffersOneFormatPerFilterIndex()
    {
        var entries = DashboardViewModel.ExportFilter.Split('|');

        Assert.Equal(["*.csv", "*.json", "*.html"], entries.Where((_, i) => i % 2 == 1));
        Assert.Equal(PortfolioFormat.Csv, PortfolioExport.FormatFor(@"C:\out\projects", 1));
        Assert.Equal(PortfolioFormat.Json, PortfolioExport.FormatFor(@"C:\out\projects", 2));
        Assert.Equal(PortfolioFormat.Html, PortfolioExport.FormatFor(@"C:\out\projects", 3));
    }

    [Fact]
    public void TheToolbar_BindsTheExportCommand()
    {
        var xaml = RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml");

        Assert.Contains("Binding ExportPortfolioCommand", xaml);
        Assert.Contains("Export project inventory", xaml);
    }

    private static async Task<(SettingsService, ProjectDiscoveryService, GitHubService, ProjectWatcherService)>
        ServicesAsync(string prefix)
    {
        var root = TestEnv.NewDir(prefix);
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
            RefreshIntervalSeconds = 7200,
        });
        var gitHub = new GitHubService(settings);
        return (settings,
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            gitHub,
            new ProjectWatcherService());
    }

    private static async Task<(DashboardViewModel Dashboard, string Root)> NewDashboardAsync(string prefix)
    {
        var (settings, discovery, gitHub, watcher) = await ServicesAsync(prefix);
        var dashboard = new DashboardViewModel(
            discovery,
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            watcher,
            new RepoBusyRegistry(),
            // No Application in the test host, so the default post target has no dispatcher
            // and would drop every callback the drain runs through.
            uiPost: callback => callback());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        var root = ProjectRootSettings.Scannable(settings.Load()).First().Path;
        return (dashboard, root);
    }
}
