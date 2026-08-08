using System.Text.Json;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The inventory file. A row's cells have to stay under the headings they were written for,
/// so the column list and the cell list are asserted against each other rather than trusted;
/// a project path is the field most likely to carry a comma, a quote, or a line break, and
/// an unescaped one silently shifts every following column of that row.
/// </summary>
public class PortfolioExportTests
{
    [Fact]
    public void TheHeaderRow_IsTheDeclaredColumnsInOrder()
    {
        var csv = PortfolioExport.ToCsv([NewProject("alpha")]);

        Assert.Equal(string.Join(',', PortfolioExport.Columns), csv.Split("\r\n")[0]);
    }

    [Fact]
    public void EveryRow_HasExactlyOneCellPerColumn()
    {
        var csv = PortfolioExport.ToCsv([NewProject("alpha"), NewProject("bravo")]);

        foreach (var line in csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            Assert.Equal(PortfolioExport.Columns.Count, SplitCsvLine(line).Count);
    }

    [Fact]
    public void APathHoldingACommaAQuoteOrANewline_IsQuotedAndReadsBackWhole()
    {
        var awkward = "C:\\projects\\a,b \"quoted\"\nsecond line";
        var project = NewProject("awkward");
        project.FullPath = awkward;

        var csv = PortfolioExport.ToCsv([project]);
        var cells = SplitCsvLine(csv.Split("\r\n")[1]);

        Assert.Equal(awkward, cells[PortfolioExport.Columns.ToList().IndexOf("Path")]);
    }

    [Fact]
    public void AValueWithoutASeparator_IsLeftUnquoted()
    {
        var csv = PortfolioExport.ToCsv([NewProject("alpha")]);

        Assert.StartsWith("alpha,", csv.Split("\r\n")[1]);
    }

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

        var row = PortfolioExport.Rows([project]).Single();

        Assert.Equal("trackr", row.Name);
        Assert.Equal("dotnet", row.Type);
        Assert.Equal("active", row.Status);
        Assert.Equal("Tools", row.Category);
        Assert.Equal("1.4.2", row.Version);
        Assert.Equal("2026-03-04T05:06:07-05:00", row.LastCommitDate);
        Assert.Equal(new string('a', 40), row.LastCommitSha);
        Assert.Equal("main", row.Branch);
        Assert.True(row.Dirty);
        Assert.Equal(2, row.Ahead);
        Assert.Equal(3, row.Behind);
        Assert.Equal("acme/trackr", row.RemoteSlug);
        Assert.Equal(2, row.NoteCount);
    }

    [Fact]
    public void ARepositoryWithNoCommitsOrRemote_ExportsBlanksRatherThanInventedValues()
    {
        var row = PortfolioExport.Rows([NewProject("bare")]).Single();

        Assert.Equal("", row.LastCommitDate);
        Assert.Equal("", row.LastCommitSha);
        Assert.Equal("", row.RemoteSlug);
        Assert.Equal(0, row.NoteCount);
    }

    [Fact]
    public void ARemoteOnASelfHostedHost_StillExportsItsOwnerAndRepo()
    {
        var project = NewProject("internal-tool");
        project.GitStatus = new GitStatus { RemoteUrl = "git@gitlab.example.com:platform/team/internal-tool.git" };

        Assert.Equal("platform/team/internal-tool", PortfolioExport.Rows([project]).Single().RemoteSlug);
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

        var row = PortfolioExport.Rows([cloud]).Single();

        Assert.Equal("", row.Path);
        Assert.Equal("acme/sketchpad", row.RemoteSlug);
    }

    [Fact]
    public void TheRowOrder_IsByNameRatherThanDiscoveryOrder()
    {
        var rows = PortfolioExport.Rows([NewProject("charlie"), NewProject("alpha"), NewProject("bravo")]);

        Assert.Equal(["alpha", "bravo", "charlie"], rows.Select(r => r.Name));
    }

    [Fact]
    public void TheJsonAndCsvExports_DescribeTheSameRowsInTheSameOrder()
    {
        var projects = new[] { NewProject("charlie"), NewProject("alpha") };

        var fromJson = JsonSerializer.Deserialize<List<PortfolioRow>>(PortfolioExport.ToJson(projects))!;

        Assert.Equal(PortfolioExport.Rows(projects), fromJson);
    }

    [Fact]
    public void TheJson_KeepsNonAsciiTextAsTextRatherThanEscapes()
    {
        var project = NewProject("caf\u00e9");

        Assert.Contains("caf\u00e9", PortfolioExport.ToJson([project]));
    }

    [Theory]
    [InlineData(@"C:\out\projects.csv", 1, PortfolioFormat.Csv)]
    [InlineData(@"C:\out\projects.json", 2, PortfolioFormat.Json)]
    // A typed extension outranks the filter that was left selected.
    [InlineData(@"C:\out\projects.json", 1, PortfolioFormat.Json)]
    [InlineData(@"C:\out\projects.csv", 2, PortfolioFormat.Csv)]
    // No extension to go on: the filter decides.
    [InlineData(@"C:\out\projects", 2, PortfolioFormat.Json)]
    [InlineData(@"C:\out\projects", 1, PortfolioFormat.Csv)]
    public void TheChosenFormat_FollowsTheExtensionAndFallsBackToTheFilter(
        string path, int filterIndex, PortfolioFormat expected)
    {
        Assert.Equal(expected, PortfolioExport.FormatFor(path, filterIndex));
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        DirectoryName = name,
        DisplayName = name,
        FullPath = $@"C:\projects\{name}",
    };

    /// <summary>A minimal RFC 4180 reader, so the escaping is asserted by reading it back.</summary>
    private static List<string> SplitCsvLine(string line)
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
/// The export as the dashboard runs it: the file that lands on disk, its encoding, and what
/// the reader is told is in it before a destination is chosen.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardPortfolioExportTests
{
    public DashboardPortfolioExportTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task AnExportedCsv_IsUtf8WithoutAByteOrderMarkAndCoversEveryProject()
    {
        var (dashboard, root) = await NewDashboardAsync("export-csv");
        dashboard.Projects.Add(NewProject("alpha"));
        dashboard.Projects.Add(NewProject("bravo"));
        var target = Path.Combine(root, "projects.csv");

        await dashboard.WritePortfolioAsync(target, PortfolioFormat.Csv);

        var bytes = await File.ReadAllBytesAsync(target);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the export was written with a byte-order mark");

        var text = await File.ReadAllTextAsync(target);
        Assert.Equal(PortfolioExport.ToCsv(dashboard.Projects), text);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal($"Exported 2 projects to {target}", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AnExportedJsonFile_ParsesBackIntoTheSameRows()
    {
        var (dashboard, root) = await NewDashboardAsync("export-json");
        dashboard.Projects.Add(NewProject("alpha"));
        var target = Path.Combine(root, "projects.json");

        await dashboard.WritePortfolioAsync(target, PortfolioFormat.Json);

        var rows = JsonSerializer.Deserialize<List<PortfolioRow>>(await File.ReadAllTextAsync(target))!;
        Assert.Equal(PortfolioExport.Rows(dashboard.Projects), rows);
    }

    [Fact]
    public async Task AnExportThatCannotBeWritten_ReportsInsteadOfThrowing()
    {
        var (dashboard, root) = await NewDashboardAsync("export-fail");
        dashboard.Projects.Add(NewProject("alpha"));
        // A directory at the destination: the write cannot replace it.
        var target = Path.Combine(root, "taken");
        Directory.CreateDirectory(target);

        await dashboard.WritePortfolioAsync(target, PortfolioFormat.Csv);

        Assert.StartsWith("Export failed — ", dashboard.OpStatusText);
    }

    [Fact]
    public async Task ExportingWithNothingDiscovered_SaysSoAndOpensNoDialog()
    {
        var (dashboard, _) = await NewDashboardAsync("export-empty");

        await dashboard.ExportPortfolioCommand.ExecuteAsync(null);

        Assert.Equal("Export: no projects have been discovered to export.", dashboard.OpStatusText);
    }

    [Fact]
    public void TheNoticeShownBeforeTheFileDialog_NamesEveryColumnAndWhereTheValuesCameFrom()
    {
        var notice = DashboardViewModel.ExportNotice(7);

        Assert.Contains("Exports 7 projects", notice);
        foreach (var column in PortfolioExport.Columns)
            Assert.Contains(column, notice);
        Assert.Contains("Nothing is re-read from git", notice);
    }

    [Fact]
    public void TheToolbar_BindsTheExportCommand()
    {
        var xaml = RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml");

        Assert.Contains("Binding ExportPortfolioCommand", xaml);
        Assert.Contains("Export project inventory", xaml);
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        DirectoryName = name,
        DisplayName = name,
        FullPath = $@"C:\projects\{name}",
    };

    private static async Task<(DashboardViewModel Dashboard, string Root)> NewDashboardAsync(string prefix)
    {
        var root = TestEnv.NewDir(prefix);
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
            RefreshIntervalSeconds = 7200,
        });

        var gitHub = new GitHubService(settings);
        var watcher = new ProjectWatcherService();
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
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
        return (dashboard, root);
    }
}
