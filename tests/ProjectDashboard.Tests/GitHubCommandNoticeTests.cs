using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A GitHub command that cannot run says why. The two states a reader can actually be in
/// — a project with no GitHub remote, and a command whose row is not selected — used to
/// return silently on several commands while their siblings named the same conditions,
/// so the same click produced a message or nothing depending on which button it was. The
/// busy gate stays silent by design: the operation holding it already names itself on
/// this status line.
/// </summary>
public class GitHubCommandNoticeTests
{
    private const string NoRemote = "This project has no GitHub remote.";

    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo LocalProject()
    {
        var dir = TestEnv.NewDir("gh-notice");
        return new ProjectInfo { DirectoryName = "gh-notice", DisplayName = "gh-notice", FullPath = dir };
    }

    /// <summary>A project whose slug is o/r, with nothing selected on any surface.</summary>
    private static ProjectInfo RemoteProject()
    {
        var project = LocalProject();
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    private static IEnumerable<(string Name, Func<ProjectDetailViewModel, Task> Run)> Commands =>
    [
        ("CommentIssue", vm => vm.CommentIssueCommand.ExecuteAsync(null)),
        ("CloseIssue", vm => vm.CloseIssueCommand.ExecuteAsync(null)),
        ("ReopenIssue", vm => vm.ReopenIssueCommand.ExecuteAsync(null)),
        ("AddIssueLabel", vm => vm.AddIssueLabelCommand.ExecuteAsync(null)),
        ("RemoveIssueLabel", vm => vm.RemoveIssueLabelCommand.ExecuteAsync(null)),
        ("AssignIssue", vm => vm.AssignIssueCommand.ExecuteAsync(null)),
        ("CommentPr", vm => vm.CommentPrCommand.ExecuteAsync(null)),
        ("ClosePr", vm => vm.ClosePrCommand.ExecuteAsync(null)),
        ("MergePr", vm => vm.MergePrCommand.ExecuteAsync(null)),
        ("CheckoutPr", vm => vm.CheckoutPrCommand.ExecuteAsync(null)),
        ("MarkPrReady", vm => vm.MarkPrReadyCommand.ExecuteAsync(null)),
        ("ReviewPr", vm => vm.ReviewPrCommand.ExecuteAsync(ReviewAction.Comment)),
        ("RerunWorkflowRun", vm => vm.RerunWorkflowRunCommand.ExecuteAsync(null)),
        ("CancelWorkflowRun", vm => vm.CancelWorkflowRunCommand.ExecuteAsync(null)),
        ("DeleteRelease", vm => vm.DeleteReleaseCommand.ExecuteAsync(null)),
        ("MarkAllNotificationsRead", vm => vm.MarkAllNotificationsReadCommand.ExecuteAsync(null)),
        ("SaveRepoDetails", vm => vm.SaveRepoDetailsCommand.ExecuteAsync(null)),
        ("SaveRepoFeatures", vm => vm.SaveRepoFeaturesCommand.ExecuteAsync(null)),
        ("ChangeDefaultBranch", vm => vm.ChangeDefaultBranchCommand.ExecuteAsync(null)),
        ("ChangeRepoVisibility", vm => vm.ChangeRepoVisibilityCommand.ExecuteAsync(null)),
        ("DeleteRepo", vm => vm.DeleteRepoCommand.ExecuteAsync(null)),
    ];

    [Fact]
    public async Task WithoutARemote_EveryMutatingCommandSaysSo()
    {
        foreach (var (name, run) in Commands)
        {
            var vm = NewVm();
            await vm.SetProjectAsync(LocalProject());

            await run(vm);

            Assert.True(NoRemote == vm.GitHubStatusText,
                $"{name} left the status line at \"{vm.GitHubStatusText}\"");
        }
    }

    /// <summary>
    /// With a remote but nothing picked, the command names the surface to pick from
    /// rather than doing nothing. The repository-wide commands are excluded: they act on
    /// the repository itself and have no row to select.
    /// </summary>
    [Theory]
    [InlineData("CommentIssue", "an issue")]
    [InlineData("CloseIssue", "an issue")]
    [InlineData("ReopenIssue", "an issue")]
    [InlineData("AddIssueLabel", "an issue")]
    [InlineData("RemoveIssueLabel", "an issue")]
    [InlineData("AssignIssue", "an issue")]
    [InlineData("CommentPr", "a pull request")]
    [InlineData("ClosePr", "a pull request")]
    [InlineData("MergePr", "a pull request")]
    [InlineData("CheckoutPr", "a pull request")]
    [InlineData("MarkPrReady", "a pull request")]
    [InlineData("ReviewPr", "a pull request")]
    [InlineData("RerunWorkflowRun", "a workflow run")]
    [InlineData("CancelWorkflowRun", "a workflow run")]
    [InlineData("DeleteRelease", "a release")]
    public async Task WithNothingSelected_TheCommandNamesWhatToSelect(string command, string noun)
    {
        var vm = NewVm();
        await vm.SetProjectAsync(RemoteProject());

        await Commands.Single(c => c.Name == command).Run(vm);

        Assert.Equal($"Select {noun} first.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task WithAnIssueSelectedButNothingTyped_TheCommandNamesTheEmptyField()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(RemoteProject());
        vm.SelectedIssue = new GitHubIssue { Number = 4, Title = "t" };

        await vm.CommentIssueCommand.ExecuteAsync(null);
        Assert.Equal("Enter a comment first.", vm.GitHubStatusText);

        await vm.AssignIssueCommand.ExecuteAsync(null);
        Assert.Equal("Enter a username to assign first.", vm.GitHubStatusText);

        await vm.RemoveIssueLabelCommand.ExecuteAsync(null);
        Assert.Equal("Pick a label to remove first.", vm.GitHubStatusText);

        await vm.AddIssueLabelCommand.ExecuteAsync(null);
        Assert.Equal("Pick a label to add first.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task WithAPullRequestSelectedButNoComment_TheCommandNamesTheEmptyField()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(RemoteProject());
        vm.SelectedPullRequest = new GitHubPullRequest { Number = 9, Title = "t" };

        await vm.CommentPrCommand.ExecuteAsync(null);

        Assert.Equal("Enter a comment first.", vm.GitHubStatusText);
    }

    /// <summary>
    /// The Repo tab's saves compare against settings the tab fetched. Reached before that
    /// fetch lands — the surface is keyboard-reachable the moment the tab opens — they
    /// have nothing to compare and must say so.
    /// </summary>
    [Fact]
    public async Task BeforeTheRepoTabLoads_ItsSavesSayTheSettingsAreNotThereYet()
    {
        var vm = NewVm();
        await vm.SetProjectAsync(RemoteProject());

        foreach (var run in new Func<Task>[]
                 {
                     () => vm.SaveRepoDetailsCommand.ExecuteAsync(null),
                     () => vm.SaveRepoFeaturesCommand.ExecuteAsync(null),
                     () => vm.ChangeDefaultBranchCommand.ExecuteAsync(null),
                     () => vm.ChangeRepoVisibilityCommand.ExecuteAsync(null),
                 })
        {
            vm.GitHubStatusText = "";
            await run();
            Assert.Equal("Repository settings haven't loaded yet.", vm.GitHubStatusText);
        }
    }
}
