using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The lists behind the four manifest fields. The seed has to equal the values that were
/// compiled into the manifest editor before the lists became editable — a seed that differed by
/// one string would re-tag every project already carrying it as a value the reader never chose.
/// </summary>
public class TaxonomyTests
{
    /// <summary>What <c>ProjectDetailViewModel</c> offered before the lists moved into settings.</summary>
    private static readonly string[] CompiledTypes =
        ["mecm-tool", "powershell-script", "web-app", "game", "framework", "library", "dashboard", "unknown"];

    private static readonly string[] CompiledStatuses = ["active", "maintenance", "archived", "experimental"];

    private static readonly string[] CompiledCategories =
        ["MECM", "Web", "Games", "Infrastructure", "Utilities", "Uncategorized"];

    private static readonly string[] CompiledSchedules = ["none", "daily", "weekly", "monthly"];

    [Fact]
    public void TheSeed_IsTheListsThatWereCompiledIn()
    {
        var seed = Taxonomy.Seed();

        Assert.Equal(CompiledTypes, seed.Types.Select(e => e.Name));
        Assert.Equal(CompiledStatuses, seed.Statuses.Select(e => e.Name));
        Assert.Equal(CompiledCategories, seed.Categories.Select(e => e.Name));
        Assert.Equal(CompiledSchedules, seed.Schedules.Select(e => e.Name));
    }

    /// <summary>
    /// The card drew these four colours from markup keyed on the literal status strings. The
    /// seed carries them so the rework reads as a refactor rather than as every card changing.
    /// </summary>
    [Theory]
    [InlineData("active", TaxonomyPalette.Good)]
    [InlineData("maintenance", TaxonomyPalette.Warn)]
    [InlineData("archived", TaxonomyPalette.Neutral)]
    [InlineData("experimental", TaxonomyPalette.Accent)]
    public void TheSeededStatusColours_AreTheOnesTheCardsAlreadyDrew(string status, string expected)
    {
        var badge = Taxonomy.Badge(Taxonomy.Seed(), TaxonomyField.Status, status);

        Assert.Equal(expected, badge.Color);
        Assert.True(badge.Visible);
        Assert.False(badge.OffList);
    }

    /// <summary>
    /// The schedule chip was collapsed for "none" by a trigger keyed on that literal. The rule
    /// moved onto the value, so renaming it keeps the behaviour instead of silently showing a
    /// chip on every card.
    /// </summary>
    [Fact]
    public void TheSeededScheduleNone_DrawsNoChip()
    {
        Assert.False(Taxonomy.Badge(Taxonomy.Seed(), TaxonomyField.Schedule, "none").Visible);
        Assert.True(Taxonomy.Badge(Taxonomy.Seed(), TaxonomyField.Schedule, "daily").Visible);
    }

    [Fact]
    public void AValueTheListDoesNotHold_KeepsItsOwnTextAndIsMarked()
    {
        var badge = Taxonomy.Badge(Taxonomy.Seed(), TaxonomyField.Category, "Imported");

        Assert.Equal("Imported", badge.Text);
        Assert.True(badge.OffList);
        Assert.True(badge.Visible);
        Assert.Equal(TaxonomyPalette.None, badge.Color);
        Assert.Contains("not in your category list", badge.AccessibleName);
    }

    [Fact]
    public void AnEmptyValue_DrawsNothing()
    {
        Assert.False(Taxonomy.Badge(Taxonomy.Seed(), TaxonomyField.Category, "").Visible);
        Assert.False(Taxonomy.Badge(Taxonomy.Seed(), TaxonomyField.Category, "   ").Visible);
    }

    /// <summary>
    /// A picker whose list does not hold the stored value shows nothing selected, and the next
    /// save writes whatever is picked over a value the reader never saw.
    /// </summary>
    [Fact]
    public void ThePicker_OffersAStoredValueTheListDoesNotHold()
    {
        var choices = Taxonomy.Choices(Taxonomy.Seed(), TaxonomyField.Status, "retired");

        Assert.Contains("retired", choices);
        Assert.Equal("retired", choices[^1]);
    }

    [Fact]
    public void ThePicker_DoesNotRepeatAValueTheListAlreadyHolds()
    {
        var choices = Taxonomy.Choices(Taxonomy.Seed(), TaxonomyField.Status, "ACTIVE");

        Assert.Equal(4, choices.Count);
    }

    /// <summary>A hand-edited settings file can carry a colour key nothing draws.</summary>
    [Fact]
    public void AnUnknownColourKey_DrawsUntintedRatherThanThrowing()
    {
        var config = new TaxonomyConfig { Statuses = [new TaxonomyEntry { Name = "live", Color = "chartreuse" }] };

        Assert.Equal(TaxonomyPalette.None, Taxonomy.Badge(config, TaxonomyField.Status, "live").Color);
    }

    /// <summary>An empty list is a file written before the lists existed, not a reader's choice.</summary>
    [Fact]
    public void SeedingFillsOnlyTheListsThatAreEmpty()
    {
        var settings = new AppSettings
        {
            Taxonomy = new TaxonomyConfig { Statuses = [new TaxonomyEntry { Name = "live" }] },
        };

        Taxonomy.EnsureSeeded(settings);

        Assert.Equal(["live"], settings.Taxonomy!.Statuses.Select(e => e.Name));
        Assert.Equal(CompiledCategories, settings.Taxonomy.Categories.Select(e => e.Name));
    }

    /// <summary>
    /// Every card chip and every picker is re-derived from this write; a delta that missed a
    /// colour or an order change would leave them showing the previous lists until relaunch.
    /// </summary>
    [Fact]
    public void TheDelta_SeesEveryKindOfListEdit()
    {
        var before = new AppSettings { Taxonomy = Taxonomy.Seed() };

        Assert.False(SettingsDelta.TaxonomyChanged(new SettingsChange(before, Renamed(s => s.Statuses[0].Name = "active"))));
        Assert.True(SettingsDelta.TaxonomyChanged(new SettingsChange(before, Renamed(s => s.Statuses[0].Name = "live"))));
        Assert.True(SettingsDelta.TaxonomyChanged(new SettingsChange(before, Renamed(s => s.Statuses[0].Color = TaxonomyPalette.Bad))));
        Assert.True(SettingsDelta.TaxonomyChanged(new SettingsChange(before, Renamed(s => s.Schedules[0].ShowOnCard = true))));
        Assert.True(SettingsDelta.TaxonomyChanged(new SettingsChange(before, Renamed(s => s.Categories.Reverse()))));
        Assert.True(SettingsDelta.TaxonomyChanged(new SettingsChange(before, Renamed(s => s.Types.RemoveAt(0)))));
    }

    private static AppSettings Renamed(Action<TaxonomyConfig> edit)
    {
        var config = Taxonomy.Seed();
        edit(config);
        return new AppSettings { Taxonomy = config };
    }
}
