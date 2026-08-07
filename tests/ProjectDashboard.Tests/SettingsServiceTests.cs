using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

[Collection("app-data-sandbox")]
public class SettingsServiceTests
{
    private static readonly string SettingsPath = AppPaths.SettingsFile;

    public SettingsServiceTests() => TestSandbox.ResetDataDir();

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = new SettingsService().Load();
        Assert.Equal(new AppSettings().ProjectsRootPath, settings.ProjectsRootPath);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\custom-root", Theme = "Light" });

        var loaded = service.Load();
        Assert.Equal(@"C:\custom-root", loaded.ProjectsRootPath);
        Assert.Equal("Light", loaded.Theme);
    }

    [Fact]
    public void CorruptSettings_RecoveredFromBackup_NotSilentlyDefaults()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\root-one", Theme = "Light" });
        service.Save(new AppSettings { ProjectsRootPath = @"C:\root-two", Theme = "Light" });

        File.WriteAllText(SettingsPath, "{\"ProjectsRootPath\": \"C:\\\\ro");

        var loaded = service.Load();
        Assert.Equal(@"C:\root-one", loaded.ProjectsRootPath);
        Assert.Equal("Light", loaded.Theme);

        Assert.Single(Directory.GetFiles(AppPaths.LocalDir, "settings.json.corrupt-*"));

        // Recovery restores the live file; a second load must not regress to defaults.
        Assert.Equal(@"C:\root-one", service.Load().ProjectsRootPath);
    }

    [Fact]
    public void CorruptSettings_NoBackup_QuarantinesAndReturnsDefaults()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\root-one" });

        File.WriteAllText(SettingsPath, "garbage");

        var loaded = service.Load();
        Assert.Equal(new AppSettings().ProjectsRootPath, loaded.ProjectsRootPath);

        var quarantined = Directory.GetFiles(AppPaths.LocalDir, "settings.json.corrupt-*");
        Assert.Single(quarantined);
        Assert.Equal("garbage", File.ReadAllText(quarantined[0]));
    }
}
