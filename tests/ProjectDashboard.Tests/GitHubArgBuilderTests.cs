using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

public class GitHubArgBuilderTests
{
    [Fact]
    public void CreateIssue_LabelsRepeatPerFlag()
    {
        Assert.Equal(
            ["issue", "create", "--repo", "o/r", "--title", "Crash", "--body", "It broke.",
             "--label", "bug", "--label", "p1"],
            GitHubService.BuildCreateIssueArgs("o/r", "Crash", "It broke.", ["bug", "p1"]));
    }

    [Fact]
    public void CreateIssue_EmptyBodyStillPassesBodyFlag()
    {
        Assert.Equal(
            ["issue", "create", "--repo", "o/r", "--title", "t", "--body", ""],
            GitHubService.BuildCreateIssueArgs("o/r", "t", "", null));
    }

    [Fact]
    public void LabelEdit_AddThenRemove_ExactOrder()
    {
        Assert.Equal(
            ["issue", "edit", "41", "--repo", "o/r",
             "--add-label", "confirmed", "--add-label", "ui", "--remove-label", "triage"],
            GitHubService.BuildIssueLabelEditArgs("o/r", 41, ["confirmed", "ui"], ["triage"]));
    }

    [Fact]
    public void Merge_SquashWithDeleteBranch()
    {
        Assert.Equal(
            ["pr", "merge", "55", "--repo", "o/r", "--squash", "--delete-branch"],
            GitHubService.BuildMergeArgs("o/r", 55, "squash", deleteBranch: true));
    }

    [Theory]
    [InlineData("merge", "--merge")]
    [InlineData("squash", "--squash")]
    [InlineData("rebase", "--rebase")]
    public void Merge_StrategyMapsToFlag(string strategy, string flag)
    {
        var args = GitHubService.BuildMergeArgs("o/r", 1, strategy, deleteBranch: false);
        Assert.Equal(["pr", "merge", "1", "--repo", "o/r", flag], args);
    }

    [Fact]
    public void Merge_UnknownStrategy_Throws()
        => Assert.Throws<ArgumentException>(() => GitHubService.BuildMergeArgs("o/r", 1, "octopus", false));

    [Theory]
    [InlineData("approve", "--approve")]
    [InlineData("requestChanges", "--request-changes")]
    [InlineData("request-changes", "--request-changes")]
    [InlineData("comment", "--comment")]
    public void Review_ActionMapsToFlag(string action, string flag)
    {
        var args = GitHubService.BuildReviewArgs("o/r", 9, action, "");
        Assert.Equal(["pr", "review", "9", "--repo", "o/r", flag], args);
    }

    [Fact]
    public void Review_BodyAppendedWhenPresent()
    {
        Assert.Equal(
            ["pr", "review", "9", "--repo", "o/r", "--comment", "--body", "Nice."],
            GitHubService.BuildReviewArgs("o/r", 9, "comment", "Nice."));
    }

    [Fact]
    public void Review_UnknownAction_Throws()
        => Assert.Throws<ArgumentException>(() => GitHubService.BuildReviewArgs("o/r", 9, "dismiss", ""));

    [Fact]
    public void CreatePullRequest_BaseOmittedWhenNull()
    {
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b", "--draft"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", null, draft: true));
    }

    [Fact]
    public void CreatePullRequest_BaseIncludedWhenSet()
    {
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b", "--base", "main"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", "main", draft: false));
    }

    [Fact]
    public void ReleaseCreate_NotesFileAndFlags()
    {
        Assert.Equal(
            ["release", "create", "v2.0.0", "--title", "Project Dashboard 2.0",
             "--notes-file", @"C:\temp\notes.md", "--draft", "--prerelease"],
            GitHubService.BuildReleaseCreateArgs("v2.0.0", "Project Dashboard 2.0", @"C:\temp\notes.md",
                draft: true, prerelease: true));
    }

    [Fact]
    public void ReleaseCreate_NoOptionalFlagsWhenFalse()
    {
        Assert.Equal(
            ["release", "create", "v1.0.0", "--title", "One", "--notes-file", "n.md"],
            GitHubService.BuildReleaseCreateArgs("v1.0.0", "One", "n.md", draft: false, prerelease: false));
    }

    [Fact]
    public void Visibility_CarriesConsequencesFlag()
    {
        Assert.Equal(
            ["repo", "edit", "o/r", "--visibility", "public", "--accept-visibility-change-consequences"],
            GitHubService.BuildVisibilityArgs("o/r", "public"));
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("secret")]
    [InlineData("")]
    public void Visibility_UnknownValue_Throws(string visibility)
        => Assert.Throws<ArgumentException>(() => GitHubService.BuildVisibilityArgs("o/r", visibility));

    [Fact]
    public void RepoEdit_NullMeansOmit_EmptyMeansClear()
    {
        // description null -> flag absent (unchanged); homepage "" -> flag present (clears).
        Assert.Equal(
            ["repo", "edit", "o/r", "--homepage", "", "--add-topic", "wpf", "--remove-topic", "old"],
            GitHubService.BuildRepoEditArgs("o/r", null, "", ["wpf"], ["old"]));
    }

    [Fact]
    public void RepoEdit_AllUnset_BuildsBareArgs()
    {
        Assert.Equal(
            ["repo", "edit", "o/r"],
            GitHubService.BuildRepoEditArgs("o/r", null, null, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rerun_FailedFlagOnlyWhenRequested(bool failedOnly)
    {
        var expected = failedOnly
            ? new[] { "run", "rerun", "16752341890", "--repo", "o/r", "--failed" }
            : ["run", "rerun", "16752341890", "--repo", "o/r"];
        Assert.Equal(expected, GitHubService.BuildRerunArgs("o/r", 16752341890L, failedOnly));
    }
}
