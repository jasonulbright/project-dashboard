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
    [InlineData("WorkflowRunsFooterText")]
    public void EachListDisclosesItsOwnDepth(string footer)
        => Assert.Contains($"{{Binding {footer}}}", Markup);

    /// <summary>A footer that changes without being announced is a disclosure a reader never hears.</summary>
    [Theory]
    [InlineData("IssuesFooterText")]
    [InlineData("PullRequestsFooterText")]
    [InlineData("WorkflowRunsFooterText")]
    public void EachFooter_IsAnnouncedWhenItChanges(string footer)
    {
        var block = Regex.Match(Markup, @"<TextBlock Text=""\{Binding " + footer + @"\}""[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(block.Success, $"no footer text block bound to {footer}");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
    }

    [Theory]
    [InlineData("LoadMoreIssuesCommand", "IssuesHasMore", "Load more issues", "GitHubListLoadMoreLabel")]
    [InlineData("LoadMorePullRequestsCommand", "PullRequestsHasMore", "Load more pull requests",
        "GitHubListLoadMoreLabel")]
    [InlineData("LoadMoreWorkflowRunsCommand", "WorkflowRunsHasMore", "Load more workflow runs",
        "WorkflowRunLoadMoreLabel")]
    public void EachLoadMore_HasANamedButtonOfferedOnlyWhileMoreMayExist(
        string command, string hasMore, string name, string label)
    {
        var button = Regex.Match(Markup, @"<ui:Button[^>]*?\{Binding " + command + @"\}[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(button.Success, $"no button bound to {command}");
        Assert.Contains($"{{Binding {hasMore}, Converter={{StaticResource BooleanToVisibilityConverter}}}}",
            button.Value);
        Assert.Contains($@"AutomationProperties.Name=""{name}""", button.Value);
        // The count in the label and the window the click asks for come from one place.
        Assert.Contains($"{{x:Static local:ProjectDetailViewModel.{label}}}", button.Value);
    }

    [Theory]
    [InlineData("IssuesState", "GitHubListStates", "Issue state filter")]
    [InlineData("PullRequestsState", "GitHubListStates", "Pull request state filter")]
    [InlineData("SelectedWorkflowRunStatus", "WorkflowRunStatuses", "Workflow run status filter")]
    public void EachEnumFilter_IsAnEnumPickerWithAName(string binding, string source, string name)
    {
        var picker = Regex.Match(Markup, @"<ComboBox[^>]*?SelectedItem=""\{Binding " + binding + @"\}""[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(picker.Success, $"no picker bound to {binding}");
        // Enum-bound, so the token that reaches gh is one the builder maps and never typed text.
        Assert.Contains($"{{x:Static local:ProjectDetailViewModel.{source}}}", picker.Value);
        Assert.Contains($@"AutomationProperties.Name=""{name}""", picker.Value);
    }

    /// <summary>
    /// The workflow picker's rows are the loaded runs' workflows, so it binds a choice row rather
    /// than an enum — and the row that filters to none is one of them, since a combo box with
    /// nothing selected reads as a picker that failed to load.
    /// </summary>
    [Fact]
    public void TheWorkflowFilter_IsAChoicePickerWithAName()
    {
        var picker = Regex.Match(Markup,
            @"<ComboBox[^>]*?SelectedItem=""\{Binding SelectedWorkflow\}""[^>]*?/>", RegexOptions.Singleline);

        Assert.True(picker.Success, "no picker bound to SelectedWorkflow");
        Assert.Contains("{Binding WorkflowChoices}", picker.Value);
        Assert.Contains(@"DisplayMemberPath=""Label""", picker.Value);
        Assert.Contains(@"AutomationProperties.Name=""Workflow run workflow filter""", picker.Value);
    }

    /// <summary>
    /// Each apply is a gh spawn, so the search runs on Enter and on the button — never on a
    /// keystroke.
    /// </summary>
    [Theory]
    [InlineData("IssuesSearchText", "ApplyIssueFiltersCommand")]
    [InlineData("PullRequestsSearchText", "ApplyPullRequestFiltersCommand")]
    [InlineData("WorkflowRunsBranchText", "ApplyWorkflowRunFiltersCommand")]
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
    [InlineData("WorkflowRunsEmptyText")]
    public void EachEmptyState_ComesFromTheViewModelThatKnowsTheFacets(string binding)
        => Assert.Contains($"{{Binding {binding}}}", Markup);

    [Fact]
    public void TheHardcodedUnfilteredEmptyStates_AreGone()
    {
        Assert.DoesNotContain("\"No open issues.\"", Markup);
        Assert.DoesNotContain("\"No open pull requests.\"", Markup);
        Assert.DoesNotContain("Text=\"No workflow runs.\"", Markup);
    }

    [Theory]
    [InlineData("IssuesFacetNotice")]
    [InlineData("PullRequestsFacetNotice")]
    [InlineData("WorkflowFilterNotice")]
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
