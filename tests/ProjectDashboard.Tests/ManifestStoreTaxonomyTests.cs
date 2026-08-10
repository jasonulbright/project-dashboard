using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// Renaming a metadata value across every record that holds it, and counting the records that
/// hold one before it can be dropped. The cascade is one write over the whole index — the same
/// blast radius as any other save here — so a failure has to leave the live index describing the
/// file exactly as the rest of <see cref="ManifestStoreTests"/> pins down.
/// </summary>
[Collection("app-data-sandbox")]
public class ManifestStoreTaxonomyTests : IDisposable
{
    private static readonly string BlockedTmpPath = AppPaths.ManifestIndexFile + ".tmp";

    public ManifestStoreTaxonomyTests()
    {
        UnblockTheWritePath();
        TestSandbox.ResetDataDir();
    }

    public void Dispose() => UnblockTheWritePath();

    private static void UnblockTheWritePath()
    {
        if (Directory.Exists(BlockedTmpPath)) Directory.Delete(BlockedTmpPath, recursive: true);
    }

    private static string PathFor(int i) => $@"C:\projects\repo{i}";

    private static ManifestStore Seeded(int count, string category)
    {
        var store = new ManifestStore();
        for (var i = 0; i < count; i++)
            store.Save(PathFor(i), new ProjectManifest { Category = category, Description = $"repo {i}" });
        return store;
    }

    [Fact]
    public void TheCount_IsEveryRecordHoldingTheValue()
    {
        var store = Seeded(3, "MECM");
        store.Save(@"C:\projects\other", new ProjectManifest { Category = "Web" });

        Assert.Equal(3, store.CountUsing(TaxonomyField.Category, "MECM"));
        Assert.Equal(3, store.CountUsing(TaxonomyField.Category, "mecm"));
        Assert.Equal(0, store.CountUsing(TaxonomyField.Category, "Games"));
        Assert.Equal(0, store.CountUsing(TaxonomyField.Category, ""));
    }

    [Fact]
    public void ARename_ReachesEveryRecordAndSurvivesAReload()
    {
        var store = Seeded(4, "MECM");

        Assert.Equal(4, store.RenameValues([new TaxonomyRename(TaxonomyField.Category, "MECM", "SCCM")]));

        var reopened = new ManifestStore();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(reopened.TryGet(PathFor(i), out var manifest));
            Assert.Equal("SCCM", manifest!.Category);
            // The rest of the record is untouched; a cascade is not a rewrite of the manifest.
            Assert.Equal($"repo {i}", manifest.Description);
        }
    }

    [Fact]
    public void ARename_LeavesOtherFieldsAndOtherValuesAlone()
    {
        var store = new ManifestStore();
        store.Save(PathFor(0), new ProjectManifest { Category = "MECM", Status = "MECM", ProjectType = "library" });

        Assert.Equal(1, store.RenameValues([new TaxonomyRename(TaxonomyField.Category, "MECM", "SCCM")]));

        Assert.True(store.TryGet(PathFor(0), out var manifest));
        Assert.Equal("SCCM", manifest!.Category);
        Assert.Equal("MECM", manifest.Status);
        Assert.Equal("library", manifest.ProjectType);
    }

    /// <summary>
    /// Two values trading names is the case a sequential rename gets wrong: the first rename
    /// moves the records the second then matches, and both end up as the second name.
    /// </summary>
    [Fact]
    public void TwoValuesTradingNames_EndUpSwappedRatherThanMerged()
    {
        var store = new ManifestStore();
        store.Save(PathFor(0), new ProjectManifest { Category = "Web" });
        store.Save(PathFor(1), new ProjectManifest { Category = "Games" });

        Assert.Equal(2, store.RenameValues(
        [
            new TaxonomyRename(TaxonomyField.Category, "Web", "Games"),
            new TaxonomyRename(TaxonomyField.Category, "Games", "Web"),
        ]));

        Assert.True(store.TryGet(PathFor(0), out var first));
        Assert.True(store.TryGet(PathFor(1), out var second));
        Assert.Equal("Games", first!.Category);
        Assert.Equal("Web", second!.Category);
    }

    [Fact]
    public void ARenameMatchingNothing_WritesNothingAndSaysSo()
    {
        var store = Seeded(2, "MECM");

        Assert.Equal(0, store.RenameValues([new TaxonomyRename(TaxonomyField.Category, "Absent", "Present")]));
        Assert.Equal(0, store.RenameValues([]));
        Assert.Equal(0, store.RenameValues([new TaxonomyRename(TaxonomyField.Category, "MECM", "MECM")]));
    }

    /// <summary>
    /// A cascade that could not be written leaves nothing renamed anywhere — in memory or on
    /// disk. Half a rename would leave records holding a name no list still offers.
    /// </summary>
    [Fact]
    public void AFailedWrite_RenamesNothingAndSaysSo()
    {
        var store = Seeded(3, "MECM");
        Directory.CreateDirectory(BlockedTmpPath);

        Assert.Null(store.RenameValues([new TaxonomyRename(TaxonomyField.Category, "MECM", "SCCM")]));

        Assert.True(store.TryGet(PathFor(0), out var live));
        Assert.Equal("MECM", live!.Category);

        UnblockTheWritePath();
        Assert.True(new ManifestStore().TryGet(PathFor(0), out var stored));
        Assert.Equal("MECM", stored!.Category);
    }
}
