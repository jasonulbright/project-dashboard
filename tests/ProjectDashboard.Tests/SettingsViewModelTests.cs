using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

[Collection("app-data-sandbox")]
public class SettingsViewModelTests
{
    public SettingsViewModelTests() => TestSandbox.ResetDataDir();

    /// <summary>
    /// The GitHubService argument is only reached through the constructor's auth
    /// probe, whose catch-all absorbs the null dereference; DashboardViewModel is
    /// only reached through the ForceSync command, never invoked here. Both nulls
    /// keep the test free of process spawns and the app host.
    /// </summary>
    private static SettingsViewModel NewVm(SettingsService service) => new(service, null!, null!);

    [Fact]
    public void ExternalExcludedDirectoriesChange_SurvivesSettingsSave()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ExcludedDirectories = ["alpha"] });

        var vm = NewVm(service); // constructor snapshot: alpha only

        // Mid-session external write — the HideProject load-mutate-save shape.
        var external = service.Load();
        external.ExcludedDirectories = [.. external.ExcludedDirectories, "hidden-proj"];
        service.Save(external);

        // Page navigation re-snapshots, then the user hits Save.
        vm.LoadSettings();
        vm.SaveSettingsCommand.Execute(null);

        var persisted = service.Load();
        Assert.Contains("hidden-proj", persisted.ExcludedDirectories);
        Assert.Contains("alpha", persisted.ExcludedDirectories);
    }

    [Fact]
    public void LoadSettings_RefreshesEveryBoundFieldFromDisk()
    {
        var service = new SettingsService();
        service.Save(new AppSettings());

        var vm = NewVm(service);

        service.Save(new AppSettings
        {
            ProjectsRootPath = @"C:\moved-root",
            ExcludedDirectories = ["alpha", "beta"],
            GhPath = @"C:\tools\gh.exe",
            RefreshIntervalSeconds = 900,
            EnableGitHubDiscovery = false,
            EnableAutoRefresh = false
        });

        vm.LoadSettings();

        Assert.Equal(@"C:\moved-root", vm.ProjectsRootPath);
        Assert.Equal("alpha, beta", vm.ExcludedDirectories);
        Assert.Equal(@"C:\tools\gh.exe", vm.GhPath);
        Assert.Equal(900, vm.RefreshIntervalSeconds);
        Assert.False(vm.EnableGitHubDiscovery);
        Assert.False(vm.EnableAutoRefresh);
    }

    [Fact]
    public void Save_PreservesFieldsTheSettingsPageDoesNotEdit()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { WindowLeft = -1500, WindowTop = 40, WindowWidth = 999, PaneOpen = false });

        var vm = NewVm(service);
        vm.LoadSettings();
        vm.SaveSettingsCommand.Execute(null);

        var persisted = service.Load();
        Assert.Equal(-1500, persisted.WindowLeft);
        Assert.Equal(40, persisted.WindowTop);
        Assert.Equal(999, persisted.WindowWidth);
        Assert.False(persisted.PaneOpen);
    }
}
