using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The card chips, read from the shipped markup. They used to be triggers keyed on the literal
/// metadata values — "maintenance", "archived", "experimental", "none", "daily" — which a reader
/// renaming a value would silently stop matching, leaving the chip plain with nothing reporting
/// it. This is the regression guard for that: the markup must key on the palette key the model
/// resolved, never on a value the reader can edit.
/// </summary>
public class TaxonomyMarkupTests
{
    private static string Dashboard => RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml");

    private static string App => RepoSource.Read("src/ProjectDashboard/App.xaml");

    private static string Detail => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

    [Theory]
    [InlineData("Manifest.Status")]
    [InlineData("Manifest.Category")]
    [InlineData("Manifest.ProjectType")]
    [InlineData("Manifest.ValidationSchedule")]
    public void NoCardChip_ReadsAMetadataValueOutOfTheModelToMatchItInMarkup(string path)
        => Assert.DoesNotContain(path, Dashboard, StringComparison.Ordinal);

    [Fact]
    public void EachOfTheFourFields_DrawsTheChipTheModelResolved()
    {
        foreach (var badge in new[] { "StatusBadge", "CategoryBadge", "TypeBadge", "ScheduleBadge" })
        {
            Assert.Contains($"Content=\"{{Binding {badge}}}\"", Dashboard, StringComparison.Ordinal);
            Assert.Contains($"{badge}.Visible", Dashboard, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One chip definition, in the application's own dictionary, so the card grid and the
    /// Settings preview cannot drift apart into two renderings of the same value.
    /// </summary>
    [Fact]
    public void TheChip_IsDefinedOnceForEverySurface()
    {
        Assert.Contains("DataType=\"{x:Type models:TaxonomyBadge}\"", App, StringComparison.Ordinal);
        Assert.DoesNotContain("models:TaxonomyBadge", Dashboard, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chip that said "unrecognised" only by being untinted would say it to nobody who cannot
    /// tell the tints apart. The glyph is the second signal, and the name carries it in words.
    /// </summary>
    [Fact]
    public void AnOffListChip_SaysSoWithoutRelyingOnItsColour()
    {
        Assert.Contains("Visibility=\"{Binding OffList", App, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccessibleName}\"", App, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pickers were static lists compiled into the view model. Bound to a static, a rename in
    /// Settings would reach a page already on screen only at the next relaunch.
    /// </summary>
    [Theory]
    [InlineData("ProjectTypes")]
    [InlineData("Statuses")]
    [InlineData("Categories")]
    [InlineData("Schedules")]
    public void TheManifestPickers_ReadTheReadersOwnLists(string property)
    {
        Assert.Contains($"ItemsSource=\"{{Binding {property}}}\"", Detail, StringComparison.Ordinal);
        Assert.DoesNotContain($"ProjectDetailViewModel.{property}", Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManifestEditor_HasSomewhereToSayAValueIsUnrecognised()
        => Assert.Contains("ManifestOffListNotice", Detail, StringComparison.Ordinal);
}
