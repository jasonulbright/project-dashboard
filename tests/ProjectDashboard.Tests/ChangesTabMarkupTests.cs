using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Changes tab's wiring, asserted at the source. The view model side of each of these is
/// covered by its own tests; what markup alone decides is whether the reader can reach it —
/// a batch command with no multi-select, or an offer with no button, is a feature nothing on
/// screen can invoke.
/// </summary>
public class ChangesTabMarkupTests
{
    private static string Markup => File.ReadAllText(SourceFile("ProjectDetailPage.xaml"));

    private static string ListMarkup(string name)
    {
        var match = Regex.Match(Markup, @"<ListBox\b[^>]*x:Name=""" + name + @"""[^>]*>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"the {name} list was not found");
        return match.Value;
    }

    [Theory]
    [InlineData("UnstagedList")]
    [InlineData("StagedList")]
    public void TheFileLists_TakeAnExtendedSelection(string name)
    {
        Assert.Contains(@"SelectionMode=""Extended""", ListMarkup(name));
    }

    /// <summary>
    /// The selection travels through the code-behind, not a SelectedItem binding: WPF's setter
    /// for that property collapses a multi-selection to one row, so a view model restoring a
    /// selection after a refresh would undo the reader's batch.
    /// </summary>
    [Theory]
    [InlineData("UnstagedList", "OnUnstagedSelectionChanged")]
    [InlineData("StagedList", "OnStagedSelectionChanged")]
    public void TheFileLists_ReportTheirSelectionToTheViewModel(string name, string handler)
    {
        var markup = ListMarkup(name);

        Assert.Contains($@"SelectionChanged=""{handler}""", markup);
        Assert.DoesNotContain("SelectedItem=", markup);
    }

    [Theory]
    [InlineData("UnstagedList")]
    [InlineData("StagedList")]
    [InlineData("HistoryList")]
    public void EveryWorkListTheRefreshRebuilds_TracksWhenItHadFocus(string name)
    {
        Assert.Contains(@"GotKeyboardFocus=""OnWorkListGotKeyboardFocus""", ListMarkup(name));
    }

    [Theory]
    [InlineData("StageSelectedCommand")]
    [InlineData("UnstageSelectedCommand")]
    [InlineData("DiscardSelectedCommand")]
    public void EveryBatchCommand_IsReachableFromTheChangesTab(string command)
    {
        Assert.Contains($"{{Binding {command}}}", Markup);
    }

    [Fact]
    public void TheUndoOffer_HasAButtonBesideTheStatusLine()
    {
        Assert.Contains("{Binding RunUndoOfferCommand}", Markup);
        Assert.Contains("{Binding UndoOfferLabel}", Markup);
        Assert.Contains("{Binding UndoOfferVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            Markup);
    }

    [Fact]
    public void TheDiffPane_OffersBothLayoutsAndShowsOneOfThem()
    {
        Assert.Contains("{Binding ToggleDiffLayoutCommand}", Markup);
        Assert.Contains(@"ItemsSource=""{Binding DiffRows}""", Markup);
        Assert.Contains(
            @"Visibility=""{Binding DiffUnified, Converter={StaticResource BooleanToVisibilityConverter}}""",
            Markup);
        Assert.Contains(
            @"Visibility=""{Binding DiffSideBySide, Converter={StaticResource BooleanToVisibilityConverter}}""",
            Markup);
    }

    /// <summary>
    /// Hunk actions are the same commands in either layout: the two panes render one diff, and
    /// a reader who switches must not lose the actions that were there a moment ago.
    /// </summary>
    [Fact]
    public void BothDiffPanes_CarryTheHunkActions()
    {
        var panes = Regex.Matches(Markup, @"<ListBox\b[^>]*x:Name=""WorkingDiff\w+""[^>]*>.*?</ListBox>",
            RegexOptions.Singleline);

        Assert.Equal(2, panes.Count);
        foreach (Match pane in panes)
        {
            Assert.Contains("{Binding StageHunkCommand}", pane.Value);
            Assert.Contains("{Binding UnstageHunkCommand}", pane.Value);
            Assert.Contains("{Binding DiscardHunkCommand}", pane.Value);
        }
    }

    [Fact]
    public void TheCommitBox_ShowsItsCountersWarningAndSubjectPicker()
    {
        Assert.Contains("{Binding CommitGuide.CounterText}", Markup);
        Assert.Contains("{Binding CommitGuide.Warning}", Markup);
        Assert.Contains(@"ItemsSource=""{Binding RecentSubjects}""", Markup);
    }

    private static string SourceFile(string name, [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..",
            "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
