using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Interactive Issues/PR surface: the VM state transitions and guards that hold
/// without a live gh. A project with no GitHub remote has an empty slug, so every
/// mutating/detail path short-circuits before the (null) GitHubService is touched —
/// the same guard that keeps these safe in production keeps them network-free here.
/// </summary>
public class ProjectDetailViewModelGitHubTests
{
    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo LocalProject()
    {
        var dir = TestEnv.NewDir("gh-vm");
        return new ProjectInfo { DirectoryName = "gh-vm", DisplayName = "gh-vm", FullPath = dir };
    }

    /// <summary>A project with a slug but no repo: guards past the slug check, no gh spawn.</summary>
    private static ProjectInfo RemoteProject()
    {
        var project = LocalProject();
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    [Fact]
    public void SplitLabels_TrimsAndDropsEmpty()
    {
        Assert.Equal(["bug", "p1"], ProjectDetailViewModel.SplitLabels(" bug , p1 ,, "));
        Assert.Empty(ProjectDetailViewModel.SplitLabels("   "));
    }

    [Fact]
    public async Task ShowNewIssue_ShowsComposeAndClearsDrafts()
    {
        var vm = NewVm();
        vm.NewIssueTitle = "stale";
        vm.NewIssueBody = "stale";
        await vm.ShowNewIssueCommand.ExecuteAsync(null);

        Assert.True(vm.IssueComposeVisible);
        Assert.Equal("", vm.NewIssueTitle);
        Assert.Equal("", vm.NewIssueBody);

        vm.CancelNewIssueCommand.Execute(null);
        Assert.False(vm.IssueComposeVisible);
    }

    [Fact]
    public async Task SubmitNewIssue_EmptyTitle_SetsStatusAndDoesNotThrow()
    {
        var vm = NewVm();
        vm.NewIssueTitle = "   ";
        await vm.SubmitNewIssueCommand.ExecuteAsync(null);
        Assert.Equal("Enter an issue title first.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task ShowAndCancelNewPr_TogglesCompose()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(RemoteProject());
        await vm.ShowNewPrCommand.ExecuteAsync(null);
        Assert.True(vm.PullRequestComposeVisible);
        vm.CancelNewPrCommand.Execute(null);
        Assert.False(vm.PullRequestComposeVisible);
    }

    [Fact]
    public async Task ShowNewPr_WithoutRemote_SaysSoAndKeepsComposeClosed()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(LocalProject());
        await vm.ShowNewPrCommand.ExecuteAsync(null);

        Assert.False(vm.PullRequestComposeVisible);
        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SubmitNewPr_WithoutRemote_SaysSoInsteadOfReturningSilently()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(LocalProject());
        vm.NewPrTitle = "Add the thing";
        await vm.SubmitNewPrCommand.ExecuteAsync(null);

        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SubmitNewIssue_WithoutRemote_SaysSoInsteadOfReturningSilently()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(LocalProject());
        vm.NewIssueTitle = "Crash on start";
        await vm.SubmitNewIssueCommand.ExecuteAsync(null);

        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SubmitNewPr_WithoutACheckedOutBranch_RefusesBeforeSpawningGh()
    {
        // A slugged project on a directory that is not a repo: no working state, so no
        // branch to pin. The refusal lands before the (null) service is reached.
        var vm = NewVm();
        await vm.SetProjectAsync(RemoteProject());
        vm.NewPrTitle = "Add the thing";

        await vm.SubmitNewPrCommand.ExecuteAsync(null);

        Assert.Equal("Check out a branch before opening a pull request.", vm.GitHubStatusText);
    }

    [Theory]
    [InlineData(ReviewAction.Approve, "Approve pull request #12?")]
    [InlineData(ReviewAction.RequestChanges, "Request changes on pull request #12?")]
    [InlineData(ReviewAction.Comment, "Comment on pull request #12?")]
    public void ReviewConfirm_NamesTheVerdictAndThePrNumber(ReviewAction action, string expected)
        => Assert.StartsWith(expected, ProjectDetailViewModel.ReviewConfirmMessage(action, 12, "looks good"));

    [Fact]
    public void ReviewConfirm_QuotesTheBodyThatWillBeSubmitted()
    {
        // The body box is shared across verdicts; the confirm must show which text
        // is about to be attached so a comment draft can't land as an approval.
        var message = ProjectDetailViewModel.ReviewConfirmMessage(ReviewAction.Approve, 3, "Not sure about the lock.");
        Assert.Contains("Not sure about the lock.", message);
    }

    [Fact]
    public void ReviewConfirm_EmptyBody_SaysSo()
    {
        var message = ProjectDetailViewModel.ReviewConfirmMessage(ReviewAction.Approve, 3, "");
        Assert.Contains("empty", message);
    }

    [Fact]
    public void ReviewConfirm_LongBody_IsTruncatedAndMarked()
    {
        var message = ProjectDetailViewModel.ReviewConfirmMessage(ReviewAction.Approve, 3, new string('x', 400));
        Assert.Contains("…", message);
        Assert.DoesNotContain(new string('x', 200), message);
    }

    [Fact]
    public void ReviewConfirm_MultilineBody_ShowsTheFirstLineOnly()
    {
        var message = ProjectDetailViewModel.ReviewConfirmMessage(
            ReviewAction.Approve, 3, "Ship it.\nBut see the note about the migration.");
        Assert.Contains("Ship it. …", message);
        Assert.DoesNotContain("migration", message);
    }

    [Theory]
    [InlineData(ReviewAction.Approve, "Approve")]
    [InlineData(ReviewAction.RequestChanges, "Request changes")]
    [InlineData(ReviewAction.Comment, "Comment")]
    public void ReviewVerdictLabel_MatchesTheVerdict(ReviewAction action, string label)
        => Assert.Equal(label, ProjectDetailViewModel.ReviewVerdictLabel(action));

    [Fact]
    public async Task SelectingIssue_WithoutRemote_LeavesDetailNullAndDoesNotThrow()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(LocalProject()); // empty GitHubSlug
        vm.SelectedIssue = new GitHubIssue { Number = 7, Title = "x" };

        Assert.Null(vm.IssueDetail);
        Assert.Equal("", vm.IssueDetailError);
    }

    [Fact]
    public async Task ProjectSwitch_ResetsInteractiveGitHubState()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(LocalProject());

        vm.IssueComposeVisible = true;
        vm.NewIssueTitle = "draft title";
        vm.IssueCommentDraft = "draft comment";
        vm.GitHubStatusText = "Comment done.";
        vm.SelectedMergeStrategy = MergeStrategy.Rebase;
        vm.MergeDeleteBranch = true;
        vm.PullRequestComposeVisible = true;
        vm.ReviewBody = "please fix";

        await vm.SetProjectAsync(LocalProject());

        Assert.False(vm.IssueComposeVisible);
        Assert.Equal("", vm.NewIssueTitle);
        Assert.Equal("", vm.IssueCommentDraft);
        Assert.Equal("", vm.GitHubStatusText);
        Assert.Equal(MergeStrategy.Squash, vm.SelectedMergeStrategy);
        Assert.False(vm.MergeDeleteBranch);
        Assert.False(vm.PullRequestComposeVisible);
        Assert.Equal("", vm.ReviewBody);
        Assert.Null(vm.SelectedIssue);
        Assert.Null(vm.SelectedPullRequest);
    }

    /// <summary>
    /// The detail pane's "Label to add" picker binds AvailableLabelNames, which a
    /// project switch clears. Loading an issue detail must therefore fetch the repo's
    /// labels — otherwise the picker is empty until the New Issue form is opened and
    /// cancelled, and Add label has nothing to send.
    /// </summary>
    [Fact]
    public async Task LoadingAnIssueDetail_PopulatesTheLabelPicker()
    {
        var vm = StubVm();
        await vm.SetProjectAsync(RemoteProject());

        vm.SelectedIssue = new GitHubIssue { Number = 7, Title = "x" };

        Assert.NotNull(vm.IssueDetail);
        Assert.Equal(1, vm.LabelFetches);
        Assert.Equal(["bug", "p1"], vm.AvailableLabelNames);
    }

    [Fact]
    public async Task LabelPicker_IsFetchedOncePerProjectAndRefetchedAfterASwitch()
    {
        var vm = StubVm();
        await vm.SetProjectAsync(RemoteProject());

        vm.SelectedIssue = new GitHubIssue { Number = 7, Title = "x" };
        vm.SelectedIssue = new GitHubIssue { Number = 8, Title = "y" };
        Assert.Equal(1, vm.LabelFetches);

        await vm.SetProjectAsync(RemoteProject());
        Assert.Empty(vm.AvailableLabelNames);

        vm.SelectedIssue = new GitHubIssue { Number = 9, Title = "z" };
        Assert.Equal(2, vm.LabelFetches);
        Assert.Equal(["bug", "p1"], vm.AvailableLabelNames);
    }

    [Fact]
    public async Task AddLabel_WithNothingPicked_SaysSoInsteadOfReturningSilently()
    {
        var vm = StubVm();
        await vm.SetProjectAsync(RemoteProject());
        vm.SelectedIssue = new GitHubIssue { Number = 7, Title = "x" };
        vm.SelectedLabelToAdd = null;

        await vm.AddIssueLabelCommand.ExecuteAsync(null);

        Assert.Equal("Pick a label to add first.", vm.GitHubStatusText);
    }

    private static LabelCountingViewModel StubVm() => new()
    {
        Detail = new IssueDetail { Number = 7, Title = "x", LabelNames = ["bug"] },
        RepoLabels = [new Label { Name = "bug" }, new Label { Name = "p1" }]
    };

    /// <summary>
    /// Serves canned issue details and repo labels so the pane's state transitions run
    /// without a gh process. Both fetches complete synchronously, so the fire-and-forget
    /// detail load has finished by the time the selection setter returns.
    /// </summary>
    private sealed class LabelCountingViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        public IssueDetail? Detail { get; init; }
        public List<Label>? RepoLabels { get; init; }
        public int LabelFetches { get; private set; }

        internal override Task<IssueDetail?> FetchIssueDetailAsync(string slug, int number)
            => Task.FromResult(Detail);

        internal override Task<List<Label>?> FetchLabelsAsync(string slug)
        {
            LabelFetches++;
            return Task.FromResult(RepoLabels);
        }
    }

    [Fact]
    public async Task GitHubMutationGuards_NoOpWithoutRemoteOrSelection()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(LocalProject());

        // No selection / empty slug: these must all be inert (no null-service call).
        await vm.CommentIssueCommand.ExecuteAsync(null);
        await vm.AssignIssueCommand.ExecuteAsync(null);
        await vm.AddIssueLabelCommand.ExecuteAsync(null);
        await vm.CommentPrCommand.ExecuteAsync(null);
        await vm.MergePrCommand.ExecuteAsync(null);
        await vm.CheckoutPrCommand.ExecuteAsync(null);
        await vm.ReviewPrCommand.ExecuteAsync(ReviewAction.Approve);

        Assert.False(vm.IsBusy);
    }
}
