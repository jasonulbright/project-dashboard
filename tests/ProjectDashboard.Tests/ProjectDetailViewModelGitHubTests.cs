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
    public void ShowAndCancelNewPr_TogglesCompose()
    {
        var vm = NewVm();
        vm.ShowNewPrCommand.Execute(null);
        Assert.True(vm.PullRequestComposeVisible);
        vm.CancelNewPrCommand.Execute(null);
        Assert.False(vm.PullRequestComposeVisible);
    }

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
