using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The hidden-project badge. Deriving its count means reading the settings file and
/// probing every excluded directory twice on disk, and it is bound twice on the summary
/// bar, which re-reads on every notification — and the file watcher notifies on every
/// save in every repository. Done in the property getter that is disk work on the UI
/// thread at typing speed, so the count is computed off-thread and served from a field.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardHiddenCountTests
{
    public DashboardHiddenCountTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task TheCount_IsServedFromTheLastRecount_NotProbedOnEveryRead()
    {
        var root = TestEnv.NewDir("hidden-count");
        await InitRepoAsync(Path.Combine(root, "alpha"));
        await InitRepoAsync(Path.Combine(root, "beta"));
        // An excluded name with nothing behind it is not a hidden project.
        Directory.CreateDirectory(Path.Combine(root, "gamma"));

        var settings = new SettingsService();
        settings.Save(BaseSettings(root, "alpha", "beta", "gamma"));

        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        await WaitUntil(() => dashboard.HiddenCount == 2);

        // Disk moves under it with nothing notifying anyone. A getter that probed per read
        // would report the change immediately — that reporting IS the per-read disk work.
        Directory.Delete(Path.Combine(root, "beta", ".git"), recursive: true);
        Assert.Equal(2, dashboard.HiddenCount);
        Assert.Equal(2, dashboard.HiddenCount);

        // A settings write is one of the two things that recounts it.
        var unhidden = settings.Load();
        unhidden.ProjectRoots[0].ExcludedDirectories = ["alpha"];
        settings.Save(unhidden);

        await WaitUntil(() => dashboard.HiddenCount == 1);
    }

    /// <summary>The count itself, without a view model in the way.</summary>
    [Fact]
    public async Task OnlyExcludedDirectoriesThatAreRepositories_AreCounted()
    {
        var root = TestEnv.NewDir("hidden-count-pure");
        await InitRepoAsync(Path.Combine(root, "alpha"));
        await InitRepoAsync(Path.Combine(root, "beta"));
        Directory.CreateDirectory(Path.Combine(root, "plain"));

        Assert.Equal(2, DashboardViewModel.CountHiddenRepos(
            BaseSettings(root, "alpha", "beta", "plain", "never-created")));
        Assert.Equal(0, DashboardViewModel.CountHiddenRepos(BaseSettings(root)));
    }

    private static AppSettings BaseSettings(string root, params string[] excluded) => new()
    {
        ProjectsRootPath = root,
        ExcludedDirectories = excluded,
        // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
        GhPath = Path.Combine(root, "no-such-gh.exe"),
        EnableGitHubDiscovery = false,
        RefreshIntervalSeconds = 7200,
    };

    private static async Task InitRepoAsync(string path)
    {
        Directory.CreateDirectory(path);
        await Git.RunAsync(path, "init", "-b", "main");
    }

    private static DashboardViewModel NewDashboard(SettingsService settings, ProjectWatcherService watcher)
    {
        var gitHub = new GitHubService(settings);
        return new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            watcher,
            new RepoBusyRegistry(),
            // No Application in the test host, so the default post target has no dispatcher
            // and would drop the recount's publication.
            uiPost: callback => callback());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the awaited condition never became true");
            await Task.Delay(15);
        }
    }
}
