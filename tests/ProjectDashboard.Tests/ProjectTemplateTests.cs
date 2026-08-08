using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The template catalogue itself: identity, and the promise the picker makes. The list of
/// paths a template shows before it runs is the only thing the reader agrees to, so it has
/// to be the list the scaffold actually writes — the round trip is asserted in
/// <see cref="ProjectScaffoldTemplateTests"/> against real folders.
/// </summary>
public class ProjectTemplateCatalogueTests
{
    [Fact]
    public void EveryTemplate_HasAUniqueIdAndNamesSomethingItCreates()
    {
        Assert.NotEmpty(ProjectTemplates.All);
        Assert.Equal(
            ProjectTemplates.All.Count,
            ProjectTemplates.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var template in ProjectTemplates.All)
        {
            Assert.NotEmpty(template.Name);
            Assert.NotEmpty(template.Summary);
            Assert.NotEmpty(template.Creates);
            Assert.NotEmpty(template.ProjectType);
        }
    }

    [Fact]
    public void EveryTemplate_CreatesAReadmeAChangelogAndAnIgnoreFile()
    {
        foreach (var template in ProjectTemplates.All)
        {
            Assert.Contains("README.md", template.Creates);
            Assert.Contains("CHANGELOG.md", template.Creates);
            Assert.Contains(".gitignore", template.Creates);
        }
    }

    [Fact]
    public void OnlyTheSdkBackedTemplates_ClaimToNeedTheSdk()
    {
        Assert.True(ProjectTemplates.ById("dotnet-console")!.NeedsDotnetSdk);
        Assert.True(ProjectTemplates.ById("dotnet-classlib")!.NeedsDotnetSdk);
        Assert.False(ProjectTemplates.ById("empty")!.NeedsDotnetSdk);
        Assert.False(ProjectTemplates.ById("docs")!.NeedsDotnetSdk);
        Assert.False(ProjectTemplates.ById("powershell")!.NeedsDotnetSdk);
    }

    [Fact]
    public void TheDefaultTemplate_IsTheEmptyOne()
    {
        Assert.Equal("empty", ProjectTemplates.Default.Id);
    }

    [Fact]
    public void AnUnknownId_ResolvesToNothingRatherThanASubstitute()
    {
        Assert.Null(ProjectTemplates.ById("no-such-template"));
    }

    [Fact]
    public void ThePreviewedPaths_CarryTheProjectsOwnName()
    {
        var script = ProjectTemplates.ById("powershell")!;

        Assert.Contains("trackr.ps1", script.CreatesFor("trackr"));
        Assert.Equal(
            "Creates: README.md, CHANGELOG.md, .gitignore, trackr.ps1",
            script.CreatesLine("trackr"));
    }

    [Theory]
    [InlineData("  My New Project  ", "my-new-project")]
    [InlineData("Trackr", "trackr")]
    [InlineData("a/b\\c:d", "abcd")]
    [InlineData("...", "")]
    [InlineData("", "")]
    public void ATypedName_IsReducedToAFolderSafeOne(string typed, string expected)
    {
        Assert.Equal(expected, DashboardViewModel.SanitizeProjectName(typed));
    }
}

/// <summary>
/// Scaffolding against real folders under %TEMP% and real git. Every listed template is
/// created here in full: a layout the app cannot finish is not one the picker may offer,
/// and the SDK-backed pair is asserted both ways — the success path where the machine has
/// the SDK, and the refusal-with-nothing-left-behind where it does not. Which of the two
/// applies is decided by probing this machine, never by skipping.
/// </summary>
[Collection("app-data-sandbox")]
public class ProjectScaffoldTemplateTests
{
    public ProjectScaffoldTemplateTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task TheEmptyTemplate_WritesTheThreeFilesItPromisedAndCommitsThem()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-empty");
        var template = ProjectTemplates.ById("empty")!;
        var path = Path.Combine(root, "alpha");

        var outcome = await dashboard.ScaffoldProjectAsync(path, "alpha", template);

        Assert.True(outcome.Created);
        Assert.Null(outcome.Error);
        AssertPromisedPathsExist(template, path, "alpha");
        await AssertCommittedAsync(path, template.CreatesFor("alpha"));
        Assert.Equal("Created alpha from the Empty project template.", dashboard.OpStatusText);
    }

    [Fact]
    public async Task TheDocumentationTemplate_WritesItsDocsIndex()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-docs");
        var template = ProjectTemplates.ById("docs")!;
        var path = Path.Combine(root, "bravo");

        var outcome = await dashboard.ScaffoldProjectAsync(path, "bravo", template);

        Assert.True(outcome.Created);
        AssertPromisedPathsExist(template, path, "bravo");
        Assert.Contains("# bravo", await File.ReadAllTextAsync(Path.Combine(path, "docs", "index.md")));
        await AssertCommittedAsync(path, template.CreatesFor("bravo"));
    }

    [Fact]
    public async Task ThePowerShellTemplate_WritesAnEntryScriptUnderStrictMode()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-ps");
        var template = ProjectTemplates.ById("powershell")!;
        var path = Path.Combine(root, "charlie");

        var outcome = await dashboard.ScaffoldProjectAsync(path, "charlie", template);

        Assert.True(outcome.Created);
        AssertPromisedPathsExist(template, path, "charlie");

        var script = await File.ReadAllTextAsync(Path.Combine(path, "charlie.ps1"));
        Assert.Contains("Set-StrictMode -Version Latest", script);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        await AssertCommittedAsync(path, template.CreatesFor("charlie"));
    }

    [Fact]
    public async Task EveryTemplatesFiles_AreWrittenAsUtf8WithoutAByteOrderMark()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-bom");
        var template = ProjectTemplates.ById("powershell")!;
        var path = Path.Combine(root, "delta");

        Assert.True((await dashboard.ScaffoldProjectAsync(path, "delta", template)).Created);

        foreach (var relative in template.CreatesFor("delta"))
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(path, relative));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{relative} was written with a byte-order mark");
        }
    }

    [Fact]
    public async Task ADotnetTemplateWithNoSdkToRun_IsWithheldAndRefusedWithNothingLeftBehind()
    {
        var (dashboard, root) = await NewDashboardAsync(
            "tmpl-no-sdk",
            new ProjectTemplateService(Path.Combine(TestEnv.Root, "no-such-dotnet.exe")));
        var template = ProjectTemplates.ById("dotnet-console")!;
        var path = Path.Combine(root, "echo");

        var outcome = await dashboard.ScaffoldProjectAsync(path, "echo", template);

        Assert.False(outcome.Created);
        Assert.Equal(ProjectTemplateService.MissingSdkReason(template), outcome.Error);
        Assert.Contains(".NET SDK", outcome.Error);
        Assert.False(Directory.Exists(path));
        Assert.Contains("dotnet new console", dashboard.OpStatusText);
    }

    [Fact]
    public async Task ThePickerWithoutAnSdk_OffersOnlyTheLayoutsTheAppWritesItself()
    {
        var service = new ProjectTemplateService(Path.Combine(TestEnv.Root, "no-such-dotnet.exe"));

        var offered = await service.AvailableAsync(ProjectTemplates.All);

        Assert.DoesNotContain(offered, t => t.NeedsDotnetSdk);
        Assert.Equal(
            ProjectTemplates.All.Where(t => !t.NeedsDotnetSdk).Select(t => t.Id),
            offered.Select(t => t.Id));
    }

    [Fact]
    public async Task TheDotnetConsoleTemplate_CreatesTheProjectWhereTheSdkOffersItAndRefusesWhereItDoesNot()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-console");
        var template = ProjectTemplates.ById("dotnet-console")!;
        var path = Path.Combine(root, "foxtrot");

        // Which half of this test applies is a fact about the machine, so it is probed
        // rather than assumed — and whichever half applies is asserted in full.
        var unavailable = await new ProjectTemplateService().UnavailableReasonAsync(template);
        var outcome = await dashboard.ScaffoldProjectAsync(path, "foxtrot", template);

        if (unavailable is null)
        {
            Assert.True(outcome.Created);
            Assert.Null(outcome.Error);
            AssertPromisedPathsExist(template, path, "foxtrot");
            await AssertCommittedAsync(path, template.CreatesFor("foxtrot"));
            Assert.Equal("Created foxtrot from the .NET console app template.", dashboard.OpStatusText);
        }
        else
        {
            Assert.False(outcome.Created);
            Assert.Equal(ProjectTemplateService.MissingSdkReason(template), outcome.Error);
            Assert.False(Directory.Exists(path));
            Assert.Contains("New project:", dashboard.OpStatusText);
        }
    }

    [Fact]
    public async Task TheDotnetClassLibraryTemplate_CreatesTheProjectWhereTheSdkOffersItAndRefusesWhereItDoesNot()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-classlib");
        var template = ProjectTemplates.ById("dotnet-classlib")!;
        var path = Path.Combine(root, "golf");

        var unavailable = await new ProjectTemplateService().UnavailableReasonAsync(template);
        var outcome = await dashboard.ScaffoldProjectAsync(path, "golf", template);

        if (unavailable is null)
        {
            Assert.True(outcome.Created);
            Assert.Null(outcome.Error);
            AssertPromisedPathsExist(template, path, "golf");
            await AssertCommittedAsync(path, template.CreatesFor("golf"));
        }
        else
        {
            Assert.False(outcome.Created);
            Assert.Equal(ProjectTemplateService.MissingSdkReason(template), outcome.Error);
            Assert.False(Directory.Exists(path));
        }
    }

    [Fact]
    public async Task AScaffoldedProject_RecordsItsTemplatesProjectTypeInTheManifest()
    {
        var (dashboard, root) = await NewDashboardAsync("tmpl-manifest");
        var template = ProjectTemplates.ById("docs")!;
        var path = Path.Combine(root, "hotel");

        Assert.True((await dashboard.ScaffoldProjectAsync(path, "hotel", template)).Created);

        Assert.True(new ManifestStore().TryGet(path, out var manifest));
        Assert.Equal("docs", manifest!.ProjectType);
        Assert.Equal("experimental", manifest.Status);
    }

    /// <summary>Every path the template named is on disk, and no promised path is missing.</summary>
    private static void AssertPromisedPathsExist(ProjectTemplate template, string projectPath, string projectName)
    {
        foreach (var relative in template.CreatesFor(projectName))
        {
            var full = Path.Combine(projectPath, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"{template.Id} promised {relative} and did not write it");
        }
    }

    /// <summary>The scaffold's first commit tracks every promised path.</summary>
    private static async Task AssertCommittedAsync(string projectPath, IReadOnlyList<string> promised)
    {
        var git = new GitService();
        var tracked = await git.RunAsync(projectPath, ["ls-tree", "-r", "--name-only", "HEAD"]);
        Assert.True(tracked.Success, tracked.FirstError);

        var names = tracked.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
        foreach (var relative in promised)
            Assert.Contains(relative.Replace('\\', '/'), names);
    }

    private static async Task<(DashboardViewModel Dashboard, string Root)> NewDashboardAsync(
        string prefix, ProjectTemplateService? templates = null)
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
            uiPost: callback => callback(),
            recovery: null,
            templateService: templates);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        return (dashboard, root);
    }
}
