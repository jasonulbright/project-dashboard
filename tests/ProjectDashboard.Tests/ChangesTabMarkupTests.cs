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

    /// <summary>
    /// The two-column rows split one arranged width, and nothing about the split is left to a
    /// shared size scope: recycling virtualization reuses a row's containers for other rows, so a
    /// scope measured from whatever is realized moves the boundary as the reader scrolls.
    /// </summary>
    [Fact]
    public void TheSideBySideRows_SplitTheirOwnWidthRatherThanASharedScope()
    {
        var template = Regex.Match(Markup,
            @"<DataTemplate x:Key=""SideBySideRowTemplate"".*?</DataTemplate>", RegexOptions.Singleline);

        Assert.True(template.Success, "the side-by-side row template was not found");
        Assert.Contains("<helpers:DiffRowPanel", template.Value);
        Assert.DoesNotContain("SharedSizeGroup", Markup);
        Assert.DoesNotContain(@"Grid.IsSharedSizeScope", Markup);
        foreach (var cell in new[] { "Span", "OldNumber", "OldText", "NewNumber", "NewText" })
            Assert.Contains($@"helpers:DiffRowPanel.Cell=""{cell}""", template.Value);
    }

    /// <summary>A line longer than the pane is reached by scrolling, in either layout.</summary>
    [Theory]
    [InlineData("DiffListStyle")]
    [InlineData("SideBySideListStyle")]
    public void BothDiffPanes_ScrollHorizontallyRatherThanWrap(string style)
    {
        var declared = Regex.Match(Markup,
            @"<Style x:Key=""" + style + @""".*?</Style>", RegexOptions.Singleline);

        Assert.True(declared.Success, $"the {style} style was not found");
        Assert.Contains(@"<Setter Property=""ScrollViewer.HorizontalScrollBarVisibility"" Value=""Auto"" />",
            declared.Value);
    }

    [Fact]
    public void TheCommitBox_ShowsItsCountersWarningAndSubjectPicker()
    {
        Assert.Contains("{Binding CommitGuide.CounterText}", Markup);
        Assert.Contains("{Binding CommitGuide.Warning}", Markup);
        Assert.Contains(@"ItemsSource=""{Binding RecentSubjects}""", Markup);
    }

    /// <summary>
    /// A signing choice with no button is a commit the reader can never complete: the gate
    /// refuses every attempt and the only two answers live on these two commands.
    /// </summary>
    [Fact]
    public void TheCommitSigningOffer_CarriesBothAnswersAndItsExplanation()
    {
        var offer = Regex.Match(Markup,
            @"<StackPanel x:Name=""CommitSigningOffer"".*?</StackPanel>\s*</StackPanel>", RegexOptions.Singleline);

        Assert.True(offer.Success, "the commit signing offer was not found");
        Assert.Contains("{Binding CommitSigningOfferText}", offer.Value);
        Assert.Contains("{Binding CommitSignedCommand}", offer.Value);
        Assert.Contains("{Binding CommitUnsignedCommand}", offer.Value);
        Assert.Contains(@"Visibility=""{Binding CommitSigningOfferVisible", offer.Value);
    }

    /// <summary>
    /// The chip is the only place the session's unsigned answer is visible, so its text has to
    /// reach a reader who cannot see the tint it sits on.
    /// </summary>
    [Fact]
    public void TheCommitSigningChip_IsAnnouncedAndSaysWhatItReports()
    {
        var chip = Regex.Match(Markup,
            @"<Border x:Name=""CommitSigningChip"".*?</Border>", RegexOptions.Singleline);

        Assert.True(chip.Success, "the commit signing chip was not found");
        Assert.Contains("{Binding CommitSigningChipText}", chip.Value);
        Assert.Contains("AutomationProperties.Name=\"{Binding CommitSigningChipTooltip}\"", chip.Value);
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", chip.Value);
        Assert.Contains(@"Visibility=""{Binding CommitSigningChipVisible", chip.Value);
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
