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
    public void NewSettings_HaveExpectedDefaults()
    {
        var defaults = new AppSettings();
        Assert.Equal(10, defaults.BackupRetentionCount);
        Assert.False(defaults.DangerZoneEnabled);
    }

    [Fact]
    public void BackupRetentionAndDangerZone_RoundTrip()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { BackupRetentionCount = 25, DangerZoneEnabled = true });

        var loaded = service.Load();
        Assert.Equal(25, loaded.BackupRetentionCount);
        Assert.True(loaded.DangerZoneEnabled);
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
    public void Save_UnwritableTarget_IsLoggedInsteadOfThrown()
    {
        // The window's Closing handler saves; a throw there cancels the close.
        var logOffset = File.Exists(AppPaths.LogFile) ? new FileInfo(AppPaths.LogFile).Length : 0;
        Directory.CreateDirectory(SettingsPath);
        try
        {
            new SettingsService().Save(new AppSettings { ProjectsRootPath = @"C:\root-one" });

            Assert.Contains($"Failed to save settings to {SettingsPath}", ReadLogFrom(logOffset));
        }
        finally
        {
            Directory.Delete(SettingsPath, recursive: true);
        }
    }

    [Fact]
    public void Save_UnwritableTarget_ReturnsFalseWithoutThrowing()
    {
        Directory.CreateDirectory(SettingsPath);
        try
        {
            Assert.False(new SettingsService().Save(new AppSettings { ProjectsRootPath = @"C:\root-one" }));
        }
        finally
        {
            Directory.Delete(SettingsPath, recursive: true);
        }
    }

    [Fact]
    public void Save_WritableTarget_ReturnsTrue()
    {
        Assert.True(new SettingsService().Save(new AppSettings { ProjectsRootPath = @"C:\root-one" }));
    }

    [Fact]
    public void ClosePathSave_Failure_IsIgnorable_SoTheCloseIsNeverCancelled()
    {
        // A directory occupying the settings file path makes the write fail. A throw
        // from the window's Closing handler cancels the close and leaves the app unclosable.
        var service = new SettingsService();
        Directory.CreateDirectory(SettingsPath);
        try
        {
            var s = service.Load();
            s.PaneOpen = false;
            s.WindowMaximized = true;
            Assert.False(service.Save(s));
        }
        finally
        {
            Directory.Delete(SettingsPath, recursive: true);
        }
    }

    [Fact]
    public void Save_UnwritableTarget_LeavesTheServiceUsable()
    {
        Directory.CreateDirectory(SettingsPath);
        try
        {
            new SettingsService().Save(new AppSettings { ProjectsRootPath = @"C:\root-one" });
            Assert.Equal(new AppSettings().ProjectsRootPath, new SettingsService().Load().ProjectsRootPath);
        }
        finally
        {
            Directory.Delete(SettingsPath, recursive: true);
        }

        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\root-two" });
        Assert.Equal(@"C:\root-two", service.Load().ProjectsRootPath);
    }

    private static string ReadLogFrom(long offset)
    {
        using var stream = new FileStream(
            AppPaths.LogFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
