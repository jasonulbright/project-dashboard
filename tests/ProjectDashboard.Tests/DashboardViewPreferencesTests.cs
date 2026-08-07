using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Pinned paths and card density share settings.json with the window geometry, so the
/// round-trip test also pins that saving a view preference cannot wipe the other keys —
/// a wholesale write of a fresh AppSettings would reset the window on every pin.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardViewPreferencesTests
{
    public DashboardViewPreferencesTests() => TestSandbox.ResetDataDir();

    [Fact]
    public void NewSettings_HaveNoPinsAndComfortableDensity()
    {
        var defaults = new AppSettings();
        Assert.Empty(defaults.PinnedProjectPaths);
        Assert.Equal("comfortable", defaults.CardDensity);
    }

    [Fact]
    public void PinnedPathsAndDensity_RoundTrip()
    {
        var service = new SettingsService();
        service.Save(new AppSettings
        {
            PinnedProjectPaths = [@"C:\projects\alpha", @"C:\projects\bravo"],
            CardDensity = "compact",
        });

        var loaded = service.Load();

        Assert.Equal([@"C:\projects\alpha", @"C:\projects\bravo"], loaded.PinnedProjectPaths);
        Assert.Equal("compact", loaded.CardDensity);
    }

    [Fact]
    public void PinnedOrdering_SurvivesASettingsRoundTrip()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { PinnedProjectPaths = [@"C:\projects\charlie"] });

        var projects = new[]
        {
            NewProject("alpha"),
            NewProject("bravo"),
            NewProject("charlie"),
        };

        var pins = DashboardOrdering.KeySet(service.Load().PinnedProjectPaths);
        var ordered = DashboardOrdering.Apply(projects, "Name", pins).ToList();

        Assert.Equal("charlie", ordered[0].DisplayName);
        Assert.True(DashboardOrdering.IsPinned(projects[2], pins));
    }

    [Fact]
    public void SavingAPin_PreservesWindowStateAndEveryOtherKey()
    {
        var service = new SettingsService();
        service.Save(new AppSettings
        {
            WindowLeft = 120,
            WindowTop = 64,
            WindowWidth = 1400,
            WindowHeight = 900,
            WindowMaximized = true,
            PaneOpen = false,
            Theme = "Light",
            GhPath = @"C:\tools\gh.exe",
            BackupRetentionCount = 25,
            ExcludedDirectories = ["skip-me"],
        });

        // The load-mutate-save shape the pin and density commands use.
        var settings = service.Load();
        settings.PinnedProjectPaths = [@"C:\projects\alpha"];
        settings.CardDensity = "compact";
        service.Save(settings);

        var reloaded = service.Load();

        Assert.Equal(120, reloaded.WindowLeft);
        Assert.Equal(64, reloaded.WindowTop);
        Assert.Equal(1400, reloaded.WindowWidth);
        Assert.Equal(900, reloaded.WindowHeight);
        Assert.True(reloaded.WindowMaximized);
        Assert.False(reloaded.PaneOpen);
        Assert.Equal("Light", reloaded.Theme);
        Assert.Equal(@"C:\tools\gh.exe", reloaded.GhPath);
        Assert.Equal(25, reloaded.BackupRetentionCount);
        Assert.Equal(["skip-me"], reloaded.ExcludedDirectories);
        Assert.Equal([@"C:\projects\alpha"], reloaded.PinnedProjectPaths);
        Assert.Equal("compact", reloaded.CardDensity);
    }

    [Fact]
    public void SettingsWrittenBeforeThePinKeyExisted_LoadWithDefaults()
    {
        File.WriteAllText(AppPaths.SettingsFile, """{"ProjectsRootPath": "C:\\legacy"}""");

        var loaded = new SettingsService().Load();

        Assert.Equal(@"C:\legacy", loaded.ProjectsRootPath);
        Assert.Empty(loaded.PinnedProjectPaths);
        Assert.Equal("comfortable", loaded.CardDensity);
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        DisplayName = name,
        DirectoryName = name,
        FullPath = $@"C:\projects\{name}",
    };
}
