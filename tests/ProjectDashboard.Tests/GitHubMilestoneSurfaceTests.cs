using System.Text.RegularExpressions;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The milestone facet on the Issues surface, driven through the page's read seams so every
/// outcome is reachable without gh. What is asserted throughout is that the facet is applied by
/// gh rather than to what came back, that every line describing a page names the milestone the
/// page was read under rather than the one the picker currently shows, and that a milestone read
/// that failed leaves a picker that says so instead of one that reads as a repository defining
/// no milestones.
/// </summary>
public class GitHubMilestoneSurfaceTests
{
    private static ProjectInfo RemoteProject(string prefix = "gh-milestone")
    {
        var dir = TestEnv.NewDir(prefix);
        var project = new ProjectInfo { DirectoryName = prefix, DisplayName = prefix, FullPath = dir };
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    private static Milestone Open(int number, string title, int? open = 2, int? closed = 3,
        DateTimeOffset? due = null) =>
        new()
        {
            Number = number, Title = title, State = "open", DueOn = due,
            OpenIssues = open, ClosedIssues = closed
        };

    private static Milestone Closed(int number, string title) =>
        new() { Number = number, Title = title, State = "closed", OpenIssues = 0, ClosedIssues = 4 };

    private static List<GitHubIssue> IssueRows(int count) =>
        [.. Enumerable.Range(1, count).Select(n => new GitHubIssue { Number = n, Title = $"issue {n}", State = "open" })];

    /// <summary>Answers the issue-list and milestone reads without gh, recording every query.</summary>
    private class MilestoneViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        public List<GitHubService.GitHubListQuery> IssueReads { get; } = [];
        public int MilestoneReads { get; private set; }

        public Func<GitHubService.GitHubListQuery, GitHubService.IssuePage?> IssueAnswer { get; set; } =
            query => new GitHubService.IssuePage(IssueRows(0), false, query.Limit);

        /// <summary>Null stands for a milestone read that failed.</summary>
        public List<Milestone>? Milestones { get; set; } = [];

        public List<string> CreateArgs { get; } = [];

        internal override Task<GitHubService.ListRead<GitHubService.IssuePage>> FetchIssuePageAsync(
            string slug, GitHubService.GitHubListQuery query)
        {
            IssueReads.Add(query);
            return Task.FromResult(new GitHubService.ListRead<GitHubService.IssuePage>(IssueAnswer(query), ""));
        }

        internal override Task<GitHubService.ListRead<GitHubService.PullRequestPage>> FetchPullRequestPageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.PullRequestPage>(
                new GitHubService.PullRequestPage([], false, query.Limit), ""));

        internal override Task<List<Label>?> FetchLabelsAsync(string slug) => Task.FromResult<List<Label>?>([]);

        internal override Task<List<Milestone>?> FetchMilestonesAsync(string slug)
        {
            MilestoneReads++;
            return Task.FromResult(Milestones);
        }
    }

    private static async Task<MilestoneViewModel> OpenedOn(List<Milestone>? milestones,
        Func<GitHubService.GitHubListQuery, GitHubService.IssuePage?>? answer = null)
    {
        var vm = new MilestoneViewModel { Milestones = milestones };
        if (answer is not null) vm.IssueAnswer = answer;
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        await vm.IssueMilestonesLoad;
        return vm;
    }

    // ── The picker ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePicker_OffersEveryMilestoneBehindTheRowThatFiltersToNone()
    {
        var vm = await OpenedOn([Closed(2, "v1.0"), Open(3, "v2.0")]);

        Assert.Equal(["Any milestone", "v2.0", "v1.0 (closed)"],
            vm.IssueMilestoneChoices.Select(c => c.Label));
        Assert.Same(MilestoneChoice.Any, vm.SelectedIssueMilestone);
        Assert.Equal("", vm.IssueMilestonesError);
    }

    /// <summary>Soonest work first: open before closed, then by due date, then by title.</summary>
    [Fact]
    public async Task ThePicker_PutsTheSoonestOpenMilestoneFirst()
    {
        var vm = await OpenedOn([
            Open(1, "later", due: DateTimeOffset.Parse("2030-01-01T00:00:00Z")),
            Closed(2, "done"),
            Open(3, "sooner", due: DateTimeOffset.Parse("2026-01-01T00:00:00Z"))
        ]);

        Assert.Equal(["Any milestone", "sooner", "later", "done (closed)"],
            vm.IssueMilestoneChoices.Select(c => c.Label));
    }

    /// <summary>
    /// A repository defining no milestones and a milestone read that failed both leave one row on
    /// screen; only the line beside it tells them apart.
    /// </summary>
    [Fact]
    public async Task AFailedMilestoneRead_SaysSoRatherThanReadingAsARepositoryWithNoMilestones()
    {
        var vm = await OpenedOn(null);

        Assert.Equal(["Any milestone"], vm.IssueMilestoneChoices.Select(c => c.Label));
        Assert.Equal(ProjectDetailViewModel.MilestonesUnavailable, vm.IssueMilestonesError);
    }

    [Fact]
    public async Task ARepositoryWithNoMilestones_LeavesThePickerUnfilteredAndSaysNothingWentWrong()
    {
        var vm = await OpenedOn([]);

        Assert.Equal(["Any milestone"], vm.IssueMilestoneChoices.Select(c => c.Label));
        Assert.Equal("", vm.IssueMilestonesError);
    }

    /// <summary>A cached failure would leave the picker empty for the life of the project.</summary>
    [Fact]
    public async Task AFailedMilestoneRead_IsRetriedRatherThanCached()
    {
        var vm = await OpenedOn(null);
        Assert.Equal(1, vm.MilestoneReads);

        vm.Milestones = [Open(3, "v2.0")];
        await vm.RefreshIssuesCommand.ExecuteAsync(null);
        await vm.IssueMilestonesLoad;

        Assert.Equal(2, vm.MilestoneReads);
        Assert.Equal(["Any milestone", "v2.0"], vm.IssueMilestoneChoices.Select(c => c.Label));
        Assert.Equal("", vm.IssueMilestonesError);
    }

    [Fact]
    public async Task ASucceededMilestoneRead_IsNotRepeatedForTheSameProject()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);

        await vm.RefreshIssuesCommand.ExecuteAsync(null);
        await vm.IssueMilestonesLoad;

        Assert.Equal(1, vm.MilestoneReads);
    }

    // ── The facet reaching gh ───────────────────────────────────────────────────

    [Fact]
    public async Task SelectingAMilestone_RereadsTheListWithTheFacetOnTheQuery()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);
        var before = vm.IssueReads.Count;

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Equal(before + 1, vm.IssueReads.Count);
        Assert.Equal(new MilestoneFacet(3, "v2.0"), vm.IssueReads[^1].Milestone);
    }

    /// <summary>A changed facet is a different question; the depth paged into is not carried over.</summary>
    [Fact]
    public async Task SelectingAMilestone_ReadsFromTheFirstWindow()
    {
        var vm = await OpenedOn([Open(3, "v2.0")],
            query => new GitHubService.IssuePage(IssueRows(query.Limit), true, query.Limit));
        await vm.LoadMoreIssuesCommand.ExecuteAsync(null);
        await vm.IssuesPageLoad;
        Assert.Equal(200, vm.IssueReads[^1].Limit);

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Equal(ProjectDetailViewModel.GitHubListWindow, vm.IssueReads[^1].Limit);
    }

    [Fact]
    public async Task ReturningToAnyMilestone_DropsTheFacetFromTheQuery()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);
        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        vm.SelectedIssueMilestone = MilestoneChoice.Any;
        await vm.IssuesPageLoad;

        Assert.Null(vm.IssueReads[^1].Milestone);
    }

    // ── What the surface may claim ──────────────────────────────────────────────

    [Fact]
    public async Task TheFooter_NamesTheMilestoneTheListWasReadUnder()
    {
        var vm = await OpenedOn([Open(3, "v2.0")],
            query => new GitHubService.IssuePage(IssueRows(3), false, query.Limit));

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Equal("All 3 open issues in milestone “v2.0” shown.", vm.IssuesFooterText);
    }

    [Fact]
    public async Task TheEmptyState_SeparatesAnEmptyMilestoneFromAnEmptyRepository()
    {
        var vm = await OpenedOn([Open(3, "v2.0")],
            query => new GitHubService.IssuePage(IssueRows(0), false, query.Limit));
        Assert.Equal("No open issues.", vm.IssuesEmptyText);

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Equal("No open issues in milestone “v2.0”.", vm.IssuesEmptyText);
    }

    [Fact]
    public async Task TheProgressLine_ReportsTheMilestonesOwnCounts()
    {
        var vm = await OpenedOn([Open(3, "v2.0", open: 2, closed: 8)]);

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Equal("Milestone “v2.0”: 8 of 10 closed.", vm.IssueMilestoneProgressText);
    }

    /// <summary>
    /// A count the read never carried is unavailable, not zero: "0 of 0 closed" describes an empty
    /// milestone, which is the opposite of what a missing count established.
    /// </summary>
    [Fact]
    public async Task AMilestoneWithNoCounts_SaysTheCountsAreUnavailableRatherThanShowingZero()
    {
        var vm = await OpenedOn([Open(3, "v2.0", open: null, closed: null)]);

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Equal("Milestone “v2.0”: issue counts unavailable.", vm.IssueMilestoneProgressText);
        Assert.DoesNotContain("0 of 0", vm.IssueMilestoneProgressText);
    }

    [Fact]
    public async Task NoMilestoneInForce_LeavesTheProgressLineToSayNothing()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);

        Assert.Equal("", vm.IssueMilestoneProgressText);
    }

    // ── A search that overrules the picker ──────────────────────────────────────

    [Fact]
    public async Task ASearchNamingAMilestone_SaysThePickerIsNotApplied()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);
        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        vm.IssuesSearchText = "milestone:\"v1.0\"";

        Assert.Equal(ProjectDetailViewModel.SearchSetsMilestoneNotice, vm.IssuesFacetNotice);
    }

    /// <summary>Both facets overruled at once is one notice per facet, not one that drops the other.</summary>
    [Fact]
    public async Task ASearchNamingBothFacets_SaysNeitherPickerIsApplied()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);
        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        vm.IssuesSearchText = "is:closed milestone:v1.0";

        Assert.Contains(ProjectDetailViewModel.SearchSetsStateNotice, vm.IssuesFacetNotice);
        Assert.Contains(ProjectDetailViewModel.SearchSetsMilestoneNotice, vm.IssuesFacetNotice);
    }

    /// <summary>A milestone qualifier overrules nothing while the picker is on its unfiltered row.</summary>
    [Fact]
    public async Task ASearchNamingAMilestone_SaysNothingWhileNoMilestoneIsSelected()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);

        vm.IssuesSearchText = "milestone:\"v1.0\"";

        Assert.Equal("", vm.IssuesFacetNotice);
    }

    /// <summary>
    /// The milestone the search overruled reached no gh call, so the lines describing the page
    /// must not name it either.
    /// </summary>
    [Fact]
    public async Task AnOverruledMilestone_IsNamedByNoLineDescribingThePage()
    {
        var vm = await OpenedOn([Open(3, "v2.0")],
            query => new GitHubService.IssuePage(IssueRows(2), false, query.Limit));
        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;
        Assert.Contains("v2.0", vm.IssuesFooterText);

        vm.IssuesSearchText = "milestone:v1.0";
        await vm.ApplyIssueFiltersCommand.ExecuteAsync(null);
        await vm.IssuesPageLoad;

        Assert.DoesNotContain("v2.0", vm.IssuesFooterText);
        Assert.Equal("", vm.IssueMilestoneProgressText);
    }

    // ── The list the next visit opens on ────────────────────────────────────────

    /// <summary>
    /// The seeded list opens under the default facets, so a page read under a milestone would seed
    /// it with rows the picker then names wrongly.
    /// </summary>
    [Fact]
    public async Task APageReadUnderAMilestone_DoesNotSeedTheProjectsIssueList()
    {
        var vm = await OpenedOn([Open(3, "v2.0")],
            query => new GitHubService.IssuePage(IssueRows(2), false, query.Limit));
        var project = vm.Project!;
        Assert.Equal(2, project.Issues.Count);
        project.Issues = [];

        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        Assert.Empty(project.Issues);
    }

    // ── The compose picker ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheComposePicker_OffersOpenMilestonesOnlyBehindTheRowThatJoinsNone()
    {
        var vm = await OpenedOn([Open(3, "v2.0"), Closed(2, "v1.0")]);

        await vm.ShowNewIssueCommand.ExecuteAsync(null);

        Assert.Equal(["None", "v2.0"], vm.NewIssueMilestoneChoices.Select(c => c.Label));
        Assert.Same(MilestoneChoice.None, vm.NewIssueMilestone);
    }

    /// <summary>The facet is one of two milestone shapes; creating an issue addresses one by name.</summary>
    [Fact]
    public async Task TheComposePicker_SelectsAMilestoneByItsTitle()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);
        await vm.ShowNewIssueCommand.ExecuteAsync(null);

        vm.NewIssueMilestone = vm.NewIssueMilestoneChoices.Single(c => c.Label == "v2.0");

        Assert.Equal("v2.0", vm.NewIssueMilestone.Milestone!.Title);
    }

    // ── The facets a project switch must not carry ──────────────────────────────

    [Fact]
    public async Task AProjectSwitch_ReturnsBothPickersToTheirNoMilestoneRows()
    {
        var vm = await OpenedOn([Open(3, "v2.0")]);
        vm.SelectedIssueMilestone = vm.IssueMilestoneChoices.Single(c => c.Label == "v2.0");
        await vm.IssuesPageLoad;

        vm.Milestones = [Open(9, "other")];
        await vm.SetProjectAsync(RemoteProject("gh-milestone-next"));
        await vm.IssuesPageLoad;
        await vm.IssueMilestonesLoad;

        Assert.Equal(["Any milestone", "other"], vm.IssueMilestoneChoices.Select(c => c.Label));
        Assert.Same(MilestoneChoice.Any, vm.SelectedIssueMilestone);
        Assert.Equal("", vm.IssueMilestoneProgressText);
    }

    // ── Reachability in the shipped markup ──────────────────────────────────────

    private static string Markup => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

    [Fact]
    public void TheFilterPicker_IsBoundToTheChoicesAndCarriesAName()
    {
        var picker = Regex.Match(Markup,
            @"<ComboBox[^>]*?ItemsSource=""\{Binding IssueMilestoneChoices\}""[^>]*?/>", RegexOptions.Singleline);

        Assert.True(picker.Success, "no milestone filter bound to IssueMilestoneChoices");
        Assert.Contains("SelectedItem=\"{Binding SelectedIssueMilestone}\"", picker.Value);
        Assert.Contains("AutomationProperties.Name=\"Issue milestone filter\"", picker.Value);
    }

    [Fact]
    public void TheComposePicker_IsBoundToTheOpenChoicesAndCarriesAName()
    {
        var picker = Regex.Match(Markup,
            @"<ComboBox[^>]*?ItemsSource=""\{Binding NewIssueMilestoneChoices\}""[^>]*?/>", RegexOptions.Singleline);

        Assert.True(picker.Success, "no milestone picker bound to NewIssueMilestoneChoices");
        Assert.Contains("SelectedItem=\"{Binding NewIssueMilestone}\"", picker.Value);
        Assert.Contains("AutomationProperties.Name=\"New issue milestone\"", picker.Value);
    }

    /// <summary>A line that changes without being announced is a disclosure a reader never hears.</summary>
    [Theory]
    [InlineData("IssueMilestoneProgressText")]
    [InlineData("IssueMilestonesError")]
    public void EachMilestoneLine_IsRenderedAndAnnounced(string binding)
    {
        var block = Regex.Match(Markup, @"<TextBlock Text=""\{Binding " + binding + @"\}"".*?</TextBlock>",
            RegexOptions.Singleline);

        Assert.True(block.Success, $"no block bound to {binding}");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
        Assert.Contains($@"<DataTrigger Binding=""{{Binding {binding}}}"" Value="""">", block.Value);
    }
}
