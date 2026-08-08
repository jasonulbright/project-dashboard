using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Every dashboard mutation reports what it did. The view preferences and the hidden set
/// persist through the settings file, whose write can fail — a full disk, a locked file,
/// a profile that went away — and a discarded write result showed a pinned glyph, a
/// tightened card, or a vanished project that all reverted on the next launch with nothing
/// having said so. The in-memory effect therefore lands only after the write does, and the
/// refusal reaches the status line. The shell hand-offs are the other half: an absent
/// terminal or a folder removed since the scan threw out of Process.Start, which on the UI
/// thread is a crash.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardOutcomeReportingTests
{
    public DashboardOutcomeReportingTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task ADensityToggleWhoseWriteFails_LeavesTheCardsAloneAndSaysSo()
    {
        var (dashboard, _) = await NewDashboardAsync("density-fail");
        Assert.False(dashboard.IsCompactDensity);

        using (new BlockedSettingsWrites())
            dashboard.ToggleDensityCommand.Execute(null);

        Assert.False(dashboard.IsCompactDensity);
        Assert.Equal($"Card density unchanged — {DashboardViewModel.SettingsWriteFailure}", dashboard.OpStatusText);
        Assert.Equal("comfortable", new SettingsService().Load().CardDensity);
    }

    [Fact]
    public async Task ADensityToggleThatIsSaved_NamesTheDensityNowInEffect()
    {
        var (dashboard, settings) = await NewDashboardAsync("density-ok");

        dashboard.ToggleDensityCommand.Execute(null);

        Assert.True(dashboard.IsCompactDensity);
        Assert.Equal("Cards are now compact.", dashboard.OpStatusText);
        Assert.Equal("compact", settings.Load().CardDensity);

        dashboard.ToggleDensityCommand.Execute(null);

        Assert.False(dashboard.IsCompactDensity);
        Assert.Equal("Cards are now comfortable.", dashboard.OpStatusText);
        Assert.Equal("comfortable", settings.Load().CardDensity);
    }

    [Fact]
    public async Task APinWhoseWriteFails_LeavesTheProjectUnpinnedAndSaysSo()
    {
        var (dashboard, settings) = await NewDashboardAsync("pin-fail");
        var project = NewProject("alpha");
        dashboard.Projects.Add(project);

        using (new BlockedSettingsWrites())
            dashboard.TogglePinCommand.Execute(project);

        Assert.False(project.IsPinned);
        Assert.Empty(settings.Load().PinnedProjectPaths);
        Assert.Equal($"Pin alpha: {DashboardViewModel.SettingsWriteFailure}", dashboard.OpStatusText);

        // The refused pin left no in-memory key behind either: the next attempt is still
        // a pin, not the unpin a half-applied toggle would have made it.
        dashboard.TogglePinCommand.Execute(project);
        Assert.True(project.IsPinned);
        Assert.Equal("Pinned alpha.", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AnUnpinWhoseWriteFails_LeavesTheProjectPinnedAndSaysSo()
    {
        var (dashboard, settings) = await NewDashboardAsync("unpin-fail");
        var project = NewProject("bravo");
        dashboard.Projects.Add(project);

        dashboard.TogglePinCommand.Execute(project);
        Assert.True(project.IsPinned);

        using (new BlockedSettingsWrites())
            dashboard.TogglePinCommand.Execute(project);

        Assert.True(project.IsPinned);
        Assert.Equal([project.FullPath], settings.Load().PinnedProjectPaths);
        Assert.Equal($"Unpin bravo: {DashboardViewModel.SettingsWriteFailure}", dashboard.OpStatusText);

        dashboard.TogglePinCommand.Execute(project);
        Assert.False(project.IsPinned);
        Assert.Equal("Unpinned bravo.", dashboard.OpStatusText);
        Assert.Empty(settings.Load().PinnedProjectPaths);
    }

    [Fact]
    public async Task AHideWhoseWriteFails_KeepsTheProjectVisibleAndSaysSo()
    {
        var (dashboard, settings) = await NewDashboardAsync("hide-fail");
        var project = NewProject("charlie");

        using (new BlockedSettingsWrites())
            await dashboard.HideProjectCommand.ExecuteAsync(project);

        Assert.DoesNotContain("charlie", settings.Load().ExcludedDirectories);
        Assert.Equal($"Hide charlie: {DashboardViewModel.SettingsWriteFailure}", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AHideThatIsSaved_ReportsThatTheProjectIsHidden()
    {
        var (dashboard, settings) = await NewDashboardAsync("hide-ok");
        var project = NewProject("delta");

        await dashboard.HideProjectCommand.ExecuteAsync(project);

        Assert.Contains("delta", settings.Load().ExcludedDirectories);
        Assert.StartsWith("delta is now hidden", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AnUnhideWhoseWriteFails_KeepsTheProjectHiddenAndSaysSo()
    {
        var (dashboard, settings) = await NewDashboardAsync("unhide-fail");
        var project = NewProject("echo");

        await dashboard.HideProjectCommand.ExecuteAsync(project);
        Assert.Contains("echo", settings.Load().ExcludedDirectories);

        using (new BlockedSettingsWrites())
            await dashboard.UnhideProjectCommand.ExecuteAsync(project);

        Assert.Contains("echo", settings.Load().ExcludedDirectories);
        Assert.Equal($"Unhide echo: {DashboardViewModel.SettingsWriteFailure}", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AnUnhideThatIsSaved_ReportsThatTheProjectIsBack()
    {
        var (dashboard, settings) = await NewDashboardAsync("unhide-ok");
        var project = NewProject("foxtrot");

        await dashboard.HideProjectCommand.ExecuteAsync(project);
        await dashboard.UnhideProjectCommand.ExecuteAsync(project);

        Assert.DoesNotContain("foxtrot", settings.Load().ExcludedDirectories);
        Assert.StartsWith("foxtrot is no longer hidden", dashboard.OpStatusText);
    }

    [Fact]
    public async Task AShellHandOffThatCannotStart_ReportsInsteadOfThrowing()
    {
        var (dashboard, _) = await NewDashboardAsync("launch-fail");
        var project = NewProject("golf");
        // Removed since the scan: the shell has nothing to open and Process.Start throws.
        project.FullPath = Path.Combine(TestEnv.Root, "gone-" + Guid.NewGuid().ToString("N")[..8]);

        dashboard.OpenFolderCommand.Execute(project);

        Assert.StartsWith("Open folder for golf failed — ", dashboard.OpStatusText);
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        DirectoryName = name,
        DisplayName = name,
        FullPath = Path.Combine(TestEnv.Root, "outcome-projects", name),
    };

    private static async Task<(DashboardViewModel Dashboard, SettingsService Settings)> NewDashboardAsync(string prefix)
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
        return (dashboard, settings);
    }

    /// <summary>
    /// Fails every settings write while held. The durable writer stages new content in a
    /// sibling .tmp and swaps it in; a directory occupying that name fails the staging write
    /// and leaves the live file exactly as it was — the shape a real failed write has.
    /// </summary>
    private sealed class BlockedSettingsWrites : IDisposable
    {
        private readonly string _staging = AppPaths.SettingsFile + ".tmp";

        public BlockedSettingsWrites() => Directory.CreateDirectory(_staging);

        public void Dispose() => Directory.Delete(_staging, recursive: true);
    }
}
