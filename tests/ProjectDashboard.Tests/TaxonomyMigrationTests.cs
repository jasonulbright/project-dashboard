using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The one-time pass over a settings file written before the lists were editable. Nothing ever
/// constrained a manifest field to the compiled-in lists, so a stored record can hold a value no
/// list names; the pass takes those in rather than leaving them permanently unrecognised. It has
/// to run once — a pass that ran on every load would re-adopt a value the reader had removed.
/// </summary>
[Collection("app-data-sandbox")]
public class TaxonomyMigrationTests
{
    public TaxonomyMigrationTests() => TestSandbox.ResetDataDir();

    private const string AlphaPath = @"C:\projects\alpha";
    private const string BetaPath = @"C:\projects\beta";

    [Fact]
    public void AFileWithNoLists_IsSeededWithTheOnesThatWereCompiledIn()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());

        var result = TaxonomyMigration.Run(settings, new ManifestStore());

        Assert.Equal(TaxonomyMigration.Outcome.Recorded, result.Outcome);
        Assert.Equal(0, result.Adopted);
        Assert.Equal(
            Taxonomy.Seed().Statuses.Select(e => e.Name),
            settings.Load().Taxonomy!.Statuses.Select(e => e.Name));
    }

    [Fact]
    public void AStoredValueNoListNames_IsTakenIntoTheList()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());

        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Category = "Imported", Status = "retired" });
        store.Save(BetaPath, new ProjectManifest { Category = "Imported", Status = "active" });

        var result = TaxonomyMigration.Run(settings, store);

        Assert.Equal(TaxonomyMigration.Outcome.Recorded, result.Outcome);
        Assert.Equal(2, result.Adopted);

        var config = settings.Load().Taxonomy!;
        Assert.Contains("Imported", config.Categories.Select(e => e.Name));
        Assert.Contains("retired", config.Statuses.Select(e => e.Name));
        // Appended, so the seeded order a reader already knows is not rearranged under them.
        Assert.Equal("Imported", config.Categories[^1].Name);
    }

    /// <summary>
    /// A second run must change nothing. Re-running would undo every removal the reader has made
    /// since, which is the failure the version guard exists for.
    /// </summary>
    [Fact]
    public void ASecondRun_ReadsNothingAndChangesNothing()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());

        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Category = "Imported" });
        TaxonomyMigration.Run(settings, store);

        var afterFirst = settings.Load();
        var edited = afterFirst.Taxonomy!;
        edited.Categories.RemoveAll(e => e.Name == "Imported");
        afterFirst.Taxonomy = edited;
        settings.Save(afterFirst);

        var second = TaxonomyMigration.Run(settings, store);

        Assert.Equal(TaxonomyMigration.Outcome.AlreadyRun, second.Outcome);
        Assert.DoesNotContain("Imported", settings.Load().Taxonomy!.Categories.Select(e => e.Name));
    }

    /// <summary>
    /// A value already in the seeded list is not a value to adopt, whatever its casing — an
    /// adoption per casing would show the same value twice in every picker.
    /// </summary>
    [Fact]
    public void AStoredValueTheSeedAlreadyHolds_IsNotAdoptedAgain()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());

        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Category = "web", Status = "ACTIVE" });

        var result = TaxonomyMigration.Run(settings, store);

        Assert.Equal(0, result.Adopted);
        Assert.Equal(
            Taxonomy.Seed().Categories.Count,
            settings.Load().Taxonomy!.Categories.Count);
    }

    /// <summary>
    /// A settings file the write could not reach must stay unversioned, so the next launch tries
    /// again rather than leaving an unrecognised value with nothing that would ever adopt it.
    /// </summary>
    [Fact]
    public void AFailedWrite_LeavesTheFileUnversioned()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());

        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Category = "Imported" });

        var blocked = AppPaths.SettingsFile + ".tmp";
        Directory.CreateDirectory(blocked);
        try
        {
            var result = TaxonomyMigration.Run(settings, store);

            Assert.Equal(TaxonomyMigration.Outcome.NotRecorded, result.Outcome);
            Assert.Equal(0, settings.Load().SettingsSchemaVersion);
        }
        finally
        {
            Directory.Delete(blocked, recursive: true);
        }
    }
}
