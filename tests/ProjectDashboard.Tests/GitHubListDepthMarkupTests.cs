using System.Text.RegularExpressions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The depth and facet controls on the Issues and Pull Requests tabs, asserted at the source. The
/// view model side is covered by its own tests; what markup alone decides is whether a reader can
/// reach any of it — a truncation the footer never renders, a state filter with no picker, or a
/// load-more command with no button, is a disclosure nothing on screen makes.
/// </summary>
public class GitHubListDepthMarkupTests
{
    private static string Markup => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

    [Theory]
    [InlineData("IssuesFooterText")]
    [InlineData("PullRequestsFooterText")]
    public void EachListDisclosesItsOwnDepth(string footer)
        => Assert.Contains($"{{Binding {footer}}}", Markup);

    /// <summary>A footer that changes without being announced is a disclosure a reader never hears.</summary>
    [Theory]
    [InlineData("IssuesFooterText")]
    [InlineData("PullRequestsFooterText")]
    public void EachFooter_IsAnnouncedWhenItChanges(string footer)
    {
        var block = Regex.Match(Markup, @"<TextBlock Text=""\{Binding " + footer + @"\}""[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(block.Success, $"no footer text block bound to {footer}");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
    }

    [Theory]
    [InlineData("LoadMoreIssuesCommand", "IssuesHasMore", "Load more issues")]
    [InlineData("LoadMorePullRequestsCommand", "PullRequestsHasMore", "Load more pull requests")]
    public void EachLoadMore_HasANamedButtonOfferedOnlyWhileMoreMayExist(
        string command, string hasMore, string name)
    {
        var button = Regex.Match(Markup, @"<ui:Button[^>]*?\{Binding " + command + @"\}[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(button.Success, $"no button bound to {command}");
        Assert.Contains($"{{Binding {hasMore}, Converter={{StaticResource BooleanToVisibilityConverter}}}}",
            button.Value);
        Assert.Contains($@"AutomationProperties.Name=""{name}""", button.Value);
        Assert.Contains("{x:Static local:ProjectDetailViewModel.GitHubListLoadMoreLabel}", button.Value);
    }

    [Theory]
    [InlineData("IssuesState", "Issue state filter")]
    [InlineData("PullRequestsState", "Pull request state filter")]
    public void EachStateFilter_IsAnEnumPickerWithAName(string binding, string name)
    {
        var picker = Regex.Match(Markup, @"<ComboBox[^>]*?SelectedItem=""\{Binding " + binding + @"\}""[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(picker.Success, $"no state picker bound to {binding}");
        // Enum-bound, so the token that reaches gh is one the builder maps and never typed text.
        Assert.Contains("{x:Static local:ProjectDetailViewModel.GitHubListStates}", picker.Value);
        Assert.Contains($@"AutomationProperties.Name=""{name}""", picker.Value);
    }

    /// <summary>
    /// Each apply is a gh spawn, so the search runs on Enter and on the button — never on a
    /// keystroke.
    /// </summary>
    [Theory]
    [InlineData("IssuesSearchText", "ApplyIssueFiltersCommand")]
    [InlineData("PullRequestsSearchText", "ApplyPullRequestFiltersCommand")]
    public void EachSearchBox_AppliesOnEnterAndOnItsButton(string binding, string command)
    {
        var box = Regex.Match(Markup,
            @"<ui:TextBox Text=""\{Binding " + binding + @", UpdateSourceTrigger=PropertyChanged\}"".*?</ui:TextBox>",
            RegexOptions.Singleline);

        Assert.True(box.Success, $"no search box bound to {binding}");
        Assert.Contains($@"<KeyBinding Key=""Enter"" Command=""{{Binding {command}}}"" />", box.Value);
        Assert.Contains("AutomationProperties.Name=", box.Value);
        Assert.Contains("PlaceholderText=", box.Value);

        var button = Regex.Match(Markup, @"<ui:Button[^>]*?\{Binding " + command + @"\}[^>]*?/>",
            RegexOptions.Singleline);
        Assert.True(button.Success, $"no search button bound to {command}");
        Assert.Contains("AutomationProperties.Name=", button.Value);
    }

    /// <summary>
    /// The empty line is the view model's, not the markup's: it names the state and the search that
    /// produced the emptiness, and a hardcoded "No open issues." would describe a filter that is
    /// no longer in force.
    /// </summary>
    [Theory]
    [InlineData("IssuesEmptyText")]
    [InlineData("PullRequestsEmptyText")]
    public void EachEmptyState_ComesFromTheViewModelThatKnowsTheFacets(string binding)
        => Assert.Contains($"{{Binding {binding}}}", Markup);

    [Fact]
    public void TheHardcodedOpenOnlyEmptyStates_AreGone()
    {
        Assert.DoesNotContain("\"No open issues.\"", Markup);
        Assert.DoesNotContain("\"No open pull requests.\"", Markup);
    }

    [Theory]
    [InlineData("IssuesFacetNotice")]
    [InlineData("PullRequestsFacetNotice")]
    public void EachFacetNotice_IsRenderedAndAnnounced(string binding)
    {
        var block = Regex.Match(Markup, @"<TextBlock Text=""\{Binding " + binding + @"\}"".*?</TextBlock>",
            RegexOptions.Singleline);

        Assert.True(block.Success, $"no notice block bound to {binding}");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
        // Collapsed when empty: an always-present blank line reads as a control with no text.
        Assert.Contains($@"<DataTrigger Binding=""{{Binding {binding}}}"" Value="""">", block.Value);
    }
}
