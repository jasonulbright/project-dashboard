using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The notice a scan raises when saved metadata followed a repository, or could not be placed.
/// It is an announcement about one pass, not a standing condition: the periodic reconcile is
/// answered from the discovery cache and hands back the same conclusions every time, so a notice
/// that re-renders on every refresh stands for the rest of the session and stops being read.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardMetadataNoticeTests : IDisposable
{
    private readonly string _fixtures = TestEnv.NewDir("notice");
    private readonly string _root;

    public DashboardMetadataNoticeTests()
    {
        TestSandbox.ResetDataDir();
        _root = Path.Combine(_fixtures, "projects");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TestEnv.TryDeleteTree(_fixtures);

    private async Task<(DashboardViewModel Dashboard, ManifestStore Store, string Repo)> NewDashboardAsync()
    {
        var repo = Path.Combine(_root, "tabkit");
        Directory.CreateDirectory(repo);
        await Git.RunAsync(repo, "init", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "file.txt"), "one\n");
        await Git.RunAsync(repo, "add", "-A");
        await Git.RunAsync(repo, "commit", "-m", "initial commit");

        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectRoots = [new ProjectRoot { Path = _root }],
            GhPath = Path.Combine(_fixtures, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            // The subject is what successive refreshes do with one pass's conclusions, so the
            // refreshes have to be the ones this test performs. Left on, the file watcher fires on
            // the move itself and lands a refresh of its own between the scan and the assertion.
            EnableAutoRefresh = false,
            RefreshIntervalSeconds = 7200,
        });

        var store = new ManifestStore();
        store.Save(repo, new ProjectManifest { Description = "a tab manager" });

        var gitHub = new GitHubService(settings);
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, store),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            new ProjectWatcherService(),
            new RepoBusyRegistry(),
            // No Application in the test host, so the default post target has no dispatcher.
            uiPost: callback => callback());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        return (dashboard, store, repo);
    }

    /// <summary>
    /// One adoption, two refreshes. The scan that made it says so; the next refresh — the shape
    /// every periodic reconcile takes, answered from the cache — takes it down with nobody having
    /// touched anything.
    /// </summary>
    [Fact]
    public async Task ANoticeIsSaidOnce_AndTheNextRefreshTakesItDown()
    {
        var (dashboard, store, repo) = await NewDashboardAsync();
        Assert.False(dashboard.MetadataNoticeVisible);

        Directory.Move(repo, Path.Combine(_root, "tab-kit"));

        await dashboard.ForceRefreshCommand.ExecuteAsync(null);

        Assert.True(dashboard.MetadataNoticeVisible);
        Assert.Contains("tabkit", dashboard.MetadataNoticeText);
        Assert.True(store.TryGet(Path.Combine(_root, "tab-kit"), out _));

        // The next tick. Nothing was pressed, and no scan concluded anything new.
        await dashboard.LoadProjectsCommand.ExecuteAsync(null);

        Assert.False(dashboard.MetadataNoticeVisible);
        Assert.Equal("", dashboard.MetadataNoticeText);
    }

    /// <summary>A reader who has read it can take it down early; what it said is still in Settings.</summary>
    [Fact]
    public async Task ANoticeCanBeDismissedBeforeTheNextRefresh()
    {
        var (dashboard, _, repo) = await NewDashboardAsync();
        Directory.Move(repo, Path.Combine(_root, "tab-kit"));
        await dashboard.ForceRefreshCommand.ExecuteAsync(null);
        Assert.True(dashboard.MetadataNoticeVisible);

        dashboard.DismissMetadataNoticeCommand.Execute(null);

        Assert.False(dashboard.MetadataNoticeVisible);
        Assert.Equal("", dashboard.MetadataNoticeText);
    }

    /// <summary>
    /// A scan that concluded nothing about anyone's metadata says nothing. The report exists on
    /// every pass; only one carrying news is worth a line on the dashboard.
    /// </summary>
    [Fact]
    public async Task AScanThatPlacedNothing_RaisesNoNotice()
    {
        var (dashboard, _, _) = await NewDashboardAsync();

        await dashboard.ForceRefreshCommand.ExecuteAsync(null);

        Assert.False(dashboard.MetadataNoticeVisible);
        Assert.Equal("", dashboard.MetadataNoticeText);
    }

    /// <summary>The notice offers a way out of itself, named for a reader.</summary>
    [Fact]
    public void TheNoticeCarriesADismissNamedForAReader()
    {
        var markup = RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml");

        Assert.Contains("Dismiss the saved project metadata notice", markup);
        Assert.Contains("DismissMetadataNoticeCommand", markup);
    }
}
