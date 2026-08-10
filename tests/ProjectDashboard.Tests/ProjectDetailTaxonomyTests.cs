using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The manifest editor's four pickers now that their contents are the reader's own lists. What is
/// asserted is that a rename made in Settings reaches a page already on screen, and that a stored
/// value no list holds is offered and named rather than silently replaced by the first entry —
/// which is how a hand-edited or imported value would be lost on the next save.
/// </summary>
[Collection("app-data-sandbox")]
public class ProjectDetailTaxonomyTests
{
    public ProjectDetailTaxonomyTests() => TestSandbox.ResetDataDir();

    private const string RepoPath = @"C:\projects\detail-taxonomy";

    private static SettingsService SavedSettings()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { SettingsSchemaVersion = 1, Taxonomy = Taxonomy.Seed() });
        return settings;
    }

    private static async Task<ProjectDetailViewModel> PageOnAsync(SettingsService settings, ProjectManifest manifest)
    {
        var page = new ProjectDetailViewModel(
            new ProjectDiscoveryService(null!, null!, null!, new ManifestStore()),
            new GitService(), null!, settingsService: settings);

        await page.SetProjectAsync(new ProjectInfo
        {
            DirectoryName = "detail-taxonomy",
            DisplayName = "detail-taxonomy",
            FullPath = RepoPath,
            HasManifest = true,
            Manifest = manifest,
        });
        return page;
    }

    [Fact]
    public async Task ThePickers_OfferTheSavedLists()
    {
        var settings = SavedSettings();
        var page = await PageOnAsync(settings, new ProjectManifest());

        Assert.Equal(Taxonomy.Seed().Categories.Select(e => e.Name), page.Categories);
        Assert.Equal(Taxonomy.Seed().Statuses.Select(e => e.Name), page.Statuses);
        Assert.Equal("", page.OffListNotice);
    }

    [Fact]
    public async Task AStoredValueNoListHolds_IsOfferedAndNamed()
    {
        var settings = SavedSettings();
        var page = await PageOnAsync(settings, new ProjectManifest { Category = "Imported" });

        Assert.Contains("Imported", page.Categories);
        Assert.Equal("Imported", page.SelectedCategory);
        Assert.Contains("category \"Imported\" is not in your metadata lists", page.OffListNotice);
    }

    [Fact]
    public async Task SeveralUnrecognisedValues_AreAllNamed()
    {
        var settings = SavedSettings();
        var page = await PageOnAsync(settings, new ProjectManifest { Category = "Imported", Status = "retired" });

        Assert.Contains("category \"Imported\"", page.OffListNotice);
        Assert.Contains("status \"retired\"", page.OffListNotice);
    }

    /// <summary>
    /// The page outlives every settings write. Read once at construction, the pickers would show
    /// the previous lists until relaunch, and a rename would leave the selection unmatched.
    /// </summary>
    [Fact]
    public async Task ARenameInSettings_ReachesAPageAlreadyOpen()
    {
        var settings = SavedSettings();
        var page = await PageOnAsync(settings, new ProjectManifest { Category = "MECM" });
        Assert.Contains("MECM", page.Categories);

        var stored = settings.Load();
        stored.Taxonomy!.Categories.Single(e => e.Name == "MECM").Name = "SCCM";
        settings.Save(stored);

        Assert.Contains("SCCM", page.Categories);
        // The selection is still MECM until the cascade rewrites the record, so the picker keeps
        // offering it rather than showing nothing selected.
        Assert.Contains("MECM", page.Categories);
    }

    [Fact]
    public async Task AValueAddedInSettings_ReachesAPageAlreadyOpen()
    {
        var settings = SavedSettings();
        var page = await PageOnAsync(settings, new ProjectManifest());

        var stored = settings.Load();
        stored.Taxonomy!.Categories.Add(new TaxonomyEntry { Name = "Clients" });
        settings.Save(stored);

        Assert.Contains("Clients", page.Categories);
    }

    /// <summary>
    /// The collections are replaced in place. A fresh instance per refresh would drop the combo
    /// box's binding and clear a selection the reader never touched.
    /// </summary>
    [Fact]
    public async Task RefreshingTheLists_KeepsTheSameCollectionInstance()
    {
        var settings = SavedSettings();
        var page = await PageOnAsync(settings, new ProjectManifest());
        var before = page.Categories;

        var stored = settings.Load();
        stored.Taxonomy!.Categories.Add(new TaxonomyEntry { Name = "Clients" });
        settings.Save(stored);

        Assert.Same(before, page.Categories);
    }
}
