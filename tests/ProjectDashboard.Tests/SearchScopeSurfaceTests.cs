using System.Xml;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// The scope switches themselves: what they select, what they say, and the one rule that keeps the
/// widest of them from costing anything nobody asked for — it is never carried from one open of a
/// search surface to the next.
/// </summary>
public class SearchScopeSurfaceTests
{
    [Fact]
    public void ASearchOpensOnTrackedContent()
    {
        Assert.Equal(SearchContentScope.Tracked, new SearchScopeSelection().Current);
        Assert.Equal(SearchContentScope.Tracked, SearchScope.Default.Content);
        Assert.Equal(SearchBreadth.Portfolio, SearchScope.Default.Breadth);
        Assert.Equal(SearchBreadth.CurrentRepo, SearchScope.OneRepo.Breadth);
    }

    /// <summary>
    /// The rule the widest scope's cost rests on. A scope that survived a close would read every
    /// repository's ignored tree on the first keystroke of the next search, which nobody widened.
    /// </summary>
    [Fact]
    public void AWidenedScope_DoesNotSurviveTheNextOpen()
    {
        var selection = new SearchScopeSelection();
        selection.Select(SearchContentScope.Everything);
        Assert.Equal(SearchContentScope.Everything, selection.Current);

        selection.Reset();

        Assert.Equal(SearchContentScope.Tracked, selection.Current);
    }

    /// <summary>Re-selecting the scope already in force reports no move, so nothing re-runs git for it.</summary>
    [Fact]
    public void SelectingTheScopeAlreadyInForce_ChangesNothing()
    {
        var selection = new SearchScopeSelection();

        Assert.False(selection.Select(SearchContentScope.Tracked));
        Assert.True(selection.Select(SearchContentScope.WithUntracked));
        Assert.False(selection.Select(SearchContentScope.WithUntracked));
    }

    [Fact]
    public void CyclingStepsThroughTheThreeScopesAndWraps()
    {
        var selection = new SearchScopeSelection();

        selection.Cycle();
        Assert.Equal(SearchContentScope.WithUntracked, selection.Current);
        selection.Cycle();
        Assert.Equal(SearchContentScope.Everything, selection.Current);
        selection.Cycle();
        Assert.Equal(SearchContentScope.Tracked, selection.Current);
    }

    [Theory]
    [InlineData(1, SearchContentScope.Tracked)]
    [InlineData(2, SearchContentScope.WithUntracked)]
    [InlineData(3, SearchContentScope.Everything)]
    public void EachScopeHasItsOwnDirectGesture(int digit, SearchContentScope expected)
        => Assert.Equal(expected, SearchScopeSelection.ForDigit(digit));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void ADigitNoScopeAnswersTo_SelectsNothing(int digit)
        => Assert.Null(SearchScopeSelection.ForDigit(digit));

    /// <summary>
    /// The results header names the scope in force. A flood produced by widening the scope rather
    /// than by the term reads as the term's doing when only the term is on screen.
    /// </summary>
    [Fact]
    public void TheResultsHeaderNamesTheScopeInForce()
    {
        Assert.Contains("tracked files", SearchScopeCopy.Header(SearchContentScope.Tracked));
        Assert.Contains("untracked", SearchScopeCopy.Header(SearchContentScope.WithUntracked));
        Assert.Contains("ignored", SearchScopeCopy.Header(SearchContentScope.Everything));

        // Three distinct headers, so no two scopes present their results under one heading.
        var headers = Enum.GetValues<SearchContentScope>().Select(SearchScopeCopy.Header).ToList();
        Assert.Equal(headers.Count, headers.Distinct().Count());
    }

    /// <summary>The widest switch states its cost; the other two cost what a reader expects.</summary>
    [Fact]
    public void OnlyTheWidestScopeCarriesACostNotice()
    {
        Assert.Contains("build output", SearchScopeCopy.EverythingNotice);
        Assert.Equal("", SearchScopeCopy.RowLabel(SearchFileScope.Tracked));
        Assert.Equal("untracked", SearchScopeCopy.RowLabel(SearchFileScope.Untracked));
        Assert.Equal("ignored", SearchScopeCopy.RowLabel(SearchFileScope.Ignored));
    }

    /// <summary>
    /// Every way a fan-out can come back short is named in the summary and named separately. One
    /// number covering all of them would let a search that reached nothing read like a search that
    /// found nothing.
    /// </summary>
    [Fact]
    public void TheSummaryNamesEachWayTheAnswerCameBackShort()
    {
        var whole = new RepoSearchResult([], 0, 4, 0);
        var summary = SearchScopeCopy.Summary(whole, SearchScope.Default);
        Assert.Contains("4 repositories", summary);
        Assert.Contains("tracked files", summary);
        Assert.DoesNotContain("partial", summary);
        Assert.DoesNotContain("error", summary);

        var short_ = new RepoSearchResult([], More: 7, ReposSearched: 2, ReposSkipped: 1,
            ReposTruncated: 3, ReposFailed: 1);
        var told = SearchScopeCopy.Summary(short_, new SearchScope(SearchContentScope.Everything, SearchBreadth.Portfolio));

        Assert.Contains("7 more matches", told);
        Assert.Contains("3 repositories ran out of time", told);
        Assert.Contains("1 repository reported an error", told);
        Assert.Contains("1 repository could not be read", told);
        Assert.Contains("ignored", told);
    }

    /// <summary>A one-repository search does not describe itself as a count of repositories.</summary>
    [Fact]
    public void AOneRepositorySearch_SaysSoRatherThanCountingRepositories()
    {
        var summary = SearchScopeCopy.Summary(new RepoSearchResult([], 0, 1, 0), SearchScope.OneRepo);

        Assert.StartsWith("This repository searched", summary);
        Assert.DoesNotContain("1 repositories", summary);
    }

    // ── Shipped surfaces ────────────────────────────────────────────────────────

    private const string PaletteXaml = "src/ProjectDashboard/Views/Windows/MainWindow.xaml";
    private const string FindXaml = "src/ProjectDashboard/Views/Pages/FindInRepoView.xaml";

    /// <summary>
    /// The palette is keyboard-first, so its scope switches are named for a reader and the tracked
    /// one is the switch the markup ships checked — the reset in code and the markup have to agree
    /// about which scope a fresh palette is on.
    /// </summary>
    [Fact]
    public void ThePalettesScopeSwitchesAreNamed_AndOpenOnTracked()
    {
        var markup = MarkupName.Markup(PaletteXaml);
        var switches = Radios(markup, "PaletteScope");

        AssertScopeSwitches(switches);

        var checkedOnes = switches.Where(s => s.GetAttribute("IsChecked") == "True").ToList();
        var onlyChecked = Assert.Single(checkedOnes);
        Assert.Equal("Tracked", onlyChecked.GetAttribute("Tag"));
    }

    /// <summary>The find pane offers the same three scopes, worded the same way.</summary>
    [Fact]
    public void TheFindPanesScopeSwitchesAreNamed_AndCoverTheSameThreeScopes()
        => AssertScopeSwitches(Radios(MarkupName.Markup(FindXaml), "FindScope"));

    /// <summary>
    /// One switch per scope, in the scopes' own order, labelled and named from the one copy source.
    /// The palette and the find pane declare these separately, and two wordings for one scope read
    /// as two scopes.
    /// </summary>
    private static void AssertScopeSwitches(List<XmlElement> switches)
    {
        var scopes = Enum.GetValues<SearchContentScope>();
        Assert.Equal(scopes.Length, switches.Count);
        Assert.Equal(
            scopes.Select(s => s.ToString()).ToList(),
            switches.Select(s => s.GetAttribute("Tag")).ToList());
        Assert.Equal(
            scopes.Select(SearchScopeCopy.Chip).ToList(),
            switches.Select(s => s.GetAttribute("Content")).ToList());
        Assert.Equal(
            scopes.Select(SearchScopeCopy.ChipHint).ToList(),
            switches.Select(s => s.GetAttribute("AutomationProperties.Name")).ToList());
    }

    /// <summary>
    /// Both surfaces move their scope on the switch being checked, not on it being clicked. A
    /// reader reaches these through UI Automation, which selects a switch without raising a click,
    /// and a scope that only moved for a mouse would leave that reader on the tracked one.
    /// </summary>
    [Fact]
    public void TheScopeSwitchesRespondToBeingChecked_NotToBeingClicked()
    {
        foreach (var (xaml, group) in new[] { (PaletteXaml, "PaletteScope"), (FindXaml, "FindScope") })
            Assert.All(Radios(MarkupName.Markup(xaml), group), s =>
            {
                Assert.NotEqual("", s.GetAttribute("Checked"));
                Assert.Equal("", s.GetAttribute("Command"));
            });
    }

    /// <summary>
    /// A status line nothing announces is a status line a screen-reader user never hears, and the
    /// scope notice and the summary are the two places the cost and the shortfall are stated.
    /// </summary>
    [Fact]
    public void TheScopeNoticeAndTheResultSummaryAreAnnounced()
    {
        foreach (var id in new[] { "PaletteScopeNotice", "PaletteSearchSummary" })
            Assert.Equal("Polite", Announced(PaletteXaml, id));

        foreach (var id in new[] { "FindScopeNoticeText", "FindStatusText" })
            Assert.Equal("Polite", Announced(FindXaml, id));
    }

    /// <summary>Every gesture the two surfaces register has a row on the cheat sheet, or it is invisible.</summary>
    [Fact]
    public void TheScopeGesturesAndFindAreOnTheCheatSheet()
    {
        var palette = ShortcutTable.All.Where(e => e.Group == ShortcutTable.PaletteGroup).ToList();
        Assert.Contains(palette, e => e.Gesture.Contains("Alt+1"));
        Assert.Contains(palette, e => e.Gesture == "Ctrl+Shift+S");

        Assert.Contains(ShortcutTable.All,
            e => e.Group == ShortcutTable.DetailGroup && e.Gesture == "Ctrl+F");
    }

    private static List<XmlElement> Radios(XmlDocument markup, string group) =>
        [.. markup.SelectNodes($"//*[local-name()='RadioButton'][@GroupName='{group}']")!
            .OfType<XmlElement>()];

    private static string Announced(string viewXaml, string automationId) =>
        MarkupName.Element(
                MarkupName.Markup(viewXaml),
                $"//*[@AutomationProperties.AutomationId='{automationId}']",
                viewXaml)
            .GetAttribute("AutomationProperties.LiveSetting");
}
