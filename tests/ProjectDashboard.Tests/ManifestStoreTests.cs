using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

[Collection("app-data-sandbox")]
public class ManifestStoreTests
{
    private static readonly string IndexPath = AppPaths.ManifestIndexFile;

    private static readonly string AlphaPath = @"C:\projects\alpha";
    private static readonly string BetaPath = @"C:\projects\beta";

    public ManifestStoreTests() => TestSandbox.ResetDataDir();

    [Fact]
    public void SaveThenReload_RoundTrips()
    {
        new ManifestStore().Save(AlphaPath, new ProjectManifest { Description = "alpha desc", Category = "Tools" });

        var found = new ManifestStore().TryGet(AlphaPath, out var manifest);

        Assert.True(found);
        Assert.Equal("alpha desc", manifest!.Description);
        Assert.Equal("Tools", manifest.Category);
    }

    [Fact]
    public void StaleTmpFromInterruptedWrite_OriginalIntact_NextSaveSucceeds()
    {
        new ManifestStore().Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });

        // Crash before the swap: a garbage .tmp exists, the live file is untouched.
        File.WriteAllText(IndexPath + ".tmp", "{\"half\": tru");

        var reloaded = new ManifestStore();
        Assert.True(reloaded.TryGet(AlphaPath, out var manifest));
        Assert.Equal("alpha desc", manifest!.Description);

        reloaded.Save(BetaPath, new ProjectManifest { Description = "beta desc" });

        var final = new ManifestStore();
        Assert.True(final.TryGet(AlphaPath, out _));
        Assert.True(final.TryGet(BetaPath, out _));
    }

    [Fact]
    public void CorruptIndex_QuarantinedAndRecoveredFromBackup_NotSilentlyEmpty()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        store.Save(BetaPath, new ProjectManifest { Description = "beta desc" });

        // Truncate in place: the crash-mid-write shape the atomic swap prevents,
        // still reachable through external interference.
        File.WriteAllText(IndexPath, File.ReadAllText(IndexPath)[..40]);

        var reloaded = new ManifestStore();
        Assert.True(reloaded.TryGet(AlphaPath, out var manifest));
        Assert.Equal("alpha desc", manifest!.Description);

        Assert.Single(Directory.GetFiles(AppPaths.RoamingDir, "manifests.json.corrupt-*"));

        // Recovery restores the live file, so a later launch that never saves
        // still sees the recovered data instead of starting empty.
        Assert.True(new ManifestStore().TryGet(AlphaPath, out _));
    }

    [Fact]
    public void CorruptIndex_NoBackup_QuarantinesInsteadOfDiscarding()
    {
        new ManifestStore().Save(AlphaPath, new ProjectManifest());
        File.WriteAllText(IndexPath, "not json at all");

        Assert.False(new ManifestStore().TryGet(AlphaPath, out _));

        var quarantined = Directory.GetFiles(AppPaths.RoamingDir, "manifests.json.corrupt-*");
        Assert.Single(quarantined);
        Assert.Equal("not json at all", File.ReadAllText(quarantined[0]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGet_NullEmptyOrWhitespacePath_ReturnsFalseWithoutThrowing(string? repoPath)
    {
        var found = new ManifestStore().TryGet(repoPath!, out var manifest);

        Assert.False(found);
        Assert.Null(manifest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_NullEmptyOrWhitespacePath_IsIgnored(string? repoPath)
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });

        store.Save(repoPath!, new ProjectManifest { Description = "orphan" });

        var reloaded = new ManifestStore();
        Assert.True(reloaded.TryGet(AlphaPath, out _));
        Assert.DoesNotContain("orphan", File.ReadAllText(IndexPath));
    }

    [Fact]
    public void Save_KeepsPreviousVersionAsBackup()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "v1" });
        store.Save(AlphaPath, new ProjectManifest { Description = "v2" });

        Assert.True(File.Exists(IndexPath + ".bak"));
        Assert.Contains("v1", File.ReadAllText(IndexPath + ".bak"));
        Assert.Contains("v2", File.ReadAllText(IndexPath));
    }
}
