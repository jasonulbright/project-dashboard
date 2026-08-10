using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The metadata-lists editor on the Settings page. What is asserted is that nothing is written
/// when anything is refused, that a rename reaches the stored records rather than leaving them
/// holding a name no list offers, and that a value a project still uses cannot be dropped at all.
/// </summary>
[Collection("app-data-sandbox")]
public class SettingsTaxonomySurfaceTests
{
    public SettingsTaxonomySurfaceTests() => TestSandbox.ResetDataDir();

    private const string AlphaPath = @"C:\projects\alpha";
    private const string BetaPath = @"C:\projects\beta";

    private static (SettingsViewModel Page, SettingsService Settings, ManifestStore Store) Open()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { SettingsSchemaVersion = 1, Taxonomy = Taxonomy.Seed() });
        var store = new ManifestStore();
        return (new SettingsViewModel(settings, null!, null!, manifests: store), settings, store);
    }

    private static TaxonomyListEditor List(SettingsViewModel page, TaxonomyField field) =>
        page.TaxonomyLists.Single(l => l.Field == field);

    private static TaxonomyRow Row(SettingsViewModel page, TaxonomyField field, string name) =>
        List(page, field).Rows.Single(r => r.Name == name);

    [Fact]
    public void ThePage_OpensOnTheSavedLists()
    {
        var (page, _, _) = Open();

        Assert.Equal(4, page.TaxonomyLists.Count);
        Assert.Equal(
            Taxonomy.Seed().Categories.Select(e => e.Name),
            List(page, TaxonomyField.Category).Rows.Select(r => r.Name));
    }

    [Fact]
    public void AddingAValue_ReachesTheSavedListAndTheManifestPickers()
    {
        var (page, settings, _) = Open();

        page.AddTaxonomyValueCommand.Execute(List(page, TaxonomyField.Category));
        List(page, TaxonomyField.Category).Rows[^1].Name = "Clients";
        List(page, TaxonomyField.Category).Rows[^1].Color = TaxonomyPalette.Info;
        page.SaveTaxonomyCommand.Execute(null);

        var saved = settings.Load().Taxonomy!;
        Assert.Equal("Clients", saved.Categories[^1].Name);
        Assert.Equal(TaxonomyPalette.Info, saved.Categories[^1].Color);
        Assert.Contains("Clients", Taxonomy.Choices(saved, TaxonomyField.Category, ""));
    }

    [Fact]
    public void ABlankName_IsRefusedAndNothingIsWritten()
    {
        var (page, settings, _) = Open();

        page.AddTaxonomyValueCommand.Execute(List(page, TaxonomyField.Category));
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("no name", page.TaxonomyStatus);
        Assert.StartsWith("Nothing was saved.", page.TaxonomyStatus);
        Assert.Equal(Taxonomy.Seed().Categories.Count, settings.Load().Taxonomy!.Categories.Count);
    }

    [Fact]
    public void ADuplicateName_IsRefusedWhateverItsCasing()
    {
        var (page, settings, _) = Open();

        page.AddTaxonomyValueCommand.Execute(List(page, TaxonomyField.Category));
        List(page, TaxonomyField.Category).Rows[^1].Name = "web";
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("appears twice", page.TaxonomyStatus);
        Assert.Equal(Taxonomy.Seed().Categories.Count, settings.Load().Taxonomy!.Categories.Count);
    }

    [Fact]
    public void AnEmptiedList_IsRefused()
    {
        var (page, settings, _) = Open();

        foreach (var row in List(page, TaxonomyField.Status).Rows.ToList())
            page.RemoveTaxonomyValueCommand.Execute(row);
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("is empty", page.TaxonomyStatus);
        Assert.Equal(Taxonomy.Seed().Statuses.Count, settings.Load().Taxonomy!.Statuses.Count);
    }

    /// <summary>
    /// The refusal has to name the count. "In use" alone leaves a reader with no idea how much
    /// work reassigning is, and no way to find the projects.
    /// </summary>
    [Fact]
    public void RemovingAValueProjectsStillUse_IsRefusedWithTheCount()
    {
        var (page, settings, store) = Open();
        store.Save(AlphaPath, new ProjectManifest { Category = "MECM" });
        store.Save(BetaPath, new ProjectManifest { Category = "MECM" });

        page.RemoveTaxonomyValueCommand.Execute(Row(page, TaxonomyField.Category, "MECM"));
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("\"MECM\" is still the category of 2 projects", page.TaxonomyStatus);
        Assert.Contains("MECM", settings.Load().Taxonomy!.Categories.Select(e => e.Name));
    }

    [Fact]
    public void RemovingAValueNothingUses_IsApplied()
    {
        var (page, settings, store) = Open();
        store.Save(AlphaPath, new ProjectManifest { Category = "Web" });

        page.RemoveTaxonomyValueCommand.Execute(Row(page, TaxonomyField.Category, "MECM"));
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Equal("Saved the metadata lists.", page.TaxonomyStatus);
        Assert.DoesNotContain("MECM", settings.Load().Taxonomy!.Categories.Select(e => e.Name));
    }

    /// <summary>
    /// A rename is not a delete and an add: every record holding the old value follows it, or the
    /// list and the records disagree from the moment the button is pressed.
    /// </summary>
    [Fact]
    public void ARename_CascadesOntoEveryProjectHoldingTheOldValue()
    {
        var (page, settings, store) = Open();
        store.Save(AlphaPath, new ProjectManifest { Category = "MECM" });
        store.Save(BetaPath, new ProjectManifest { Category = "MECM" });

        Row(page, TaxonomyField.Category, "MECM").Name = "SCCM";
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("renamed the value on 2 projects", page.TaxonomyStatus);
        Assert.Contains("SCCM", settings.Load().Taxonomy!.Categories.Select(e => e.Name));

        var reopened = new ManifestStore();
        Assert.True(reopened.TryGet(AlphaPath, out var alpha));
        Assert.Equal("SCCM", alpha!.Category);
    }

    [Fact]
    public void ARenameMatchingNoProject_SaysSoRatherThanClaimingAnUpdate()
    {
        var (page, _, _) = Open();

        Row(page, TaxonomyField.Category, "MECM").Name = "SCCM";
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("matched no stored project", page.TaxonomyStatus);
    }

    [Fact]
    public void Reordering_IsWhatTheSavedListHolds()
    {
        var (page, settings, _) = Open();

        page.MoveTaxonomyValueUpCommand.Execute(Row(page, TaxonomyField.Category, "Web"));
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Equal("Web", settings.Load().Taxonomy!.Categories[0].Name);
    }

    [Fact]
    public void TheFirstAndLastRows_OfferNoMoveThatWouldDoNothing()
    {
        var (page, _, _) = Open();
        var rows = List(page, TaxonomyField.Category).Rows;

        Assert.False(rows[0].CanMoveUp);
        Assert.True(rows[0].CanMoveDown);
        Assert.True(rows[^1].CanMoveUp);
        Assert.False(rows[^1].CanMoveDown);
    }

    [Fact]
    public void DiscardingChanges_ReloadsTheSavedListsAndWritesNothing()
    {
        var (page, settings, _) = Open();

        Row(page, TaxonomyField.Category, "MECM").Name = "SCCM";
        page.ResetTaxonomyCommand.Execute(null);

        Assert.Contains("MECM", List(page, TaxonomyField.Category).Rows.Select(r => r.Name));
        Assert.Contains("MECM", settings.Load().Taxonomy!.Categories.Select(e => e.Name));
    }

    /// <summary>
    /// A reader fixing four names should not have to press Apply four times to find the fourth.
    /// </summary>
    [Fact]
    public void EveryRefusal_IsReportedTogether()
    {
        var (page, _, store) = Open();
        store.Save(AlphaPath, new ProjectManifest { Category = "MECM" });

        page.RemoveTaxonomyValueCommand.Execute(Row(page, TaxonomyField.Category, "MECM"));
        page.AddTaxonomyValueCommand.Execute(List(page, TaxonomyField.Status));
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Contains("still the category of 1 project", page.TaxonomyStatus);
        Assert.Contains("no name", page.TaxonomyStatus);
    }

    [Fact]
    public void TurningAValueOffTheCards_IsWhatTheSavedListHolds()
    {
        var (page, settings, _) = Open();

        Row(page, TaxonomyField.Status, "archived").ShowOnCard = false;
        page.SaveTaxonomyCommand.Execute(null);

        var saved = settings.Load().Taxonomy!;
        Assert.False(Taxonomy.Badge(saved, TaxonomyField.Status, "archived").Visible);
    }

    /// <summary>The preview is what the card would draw, so a colour choice can be seen before it lands.</summary>
    [Fact]
    public void TheRowPreview_TracksTheNameAndColourBeingEdited()
    {
        var (page, _, _) = Open();
        var row = Row(page, TaxonomyField.Status, "active");

        row.Name = "live";
        row.Color = TaxonomyPalette.Info;

        Assert.Equal("live", row.Preview.Text);
        Assert.Equal(TaxonomyPalette.Info, row.Preview.Color);
    }
}
