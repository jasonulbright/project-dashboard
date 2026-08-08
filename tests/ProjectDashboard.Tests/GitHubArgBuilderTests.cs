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
    public void CreatePullRequest_HeadPinsTheSourceBranch()
    {
        // Without --head gh reads whatever is checked out when it spawns, which need
        // not be the branch the compose form named.
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b", "--base", "main", "--head", "feature/x", "--draft"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", "main", draft: true, headBranch: "feature/x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreatePullRequest_HeadOmittedWhenBlank(string? head)
    {
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", null, draft: false, headBranch: head));
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

    [Fact]
    public void DefaultBranch_CarriesTheBranchName()
    {
        Assert.Equal(
            ["repo", "edit", "o/r", "--default-branch", "main"],
            GitHubService.BuildDefaultBranchArgs("o/r", "main"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultBranch_BlankValue_Throws(string branch)
    {
        // A blank flag argument makes gh consume the next token instead of failing.
        Assert.Throws<ArgumentException>(() => GitHubService.BuildDefaultBranchArgs("o/r", branch));
    }

    [Fact]
    public void RepoFeatures_ValueRidesOnTheFlagToken()
    {
        // A bare --enable-issues means true; false can only be expressed as =false.
        Assert.Equal(
            ["repo", "edit", "o/r", "--enable-issues=false", "--enable-wiki=true"],
            GitHubService.BuildRepoFeatureArgs("o/r", issues: false, wiki: true, projects: null));
    }

    [Fact]
    public void RepoFeatures_AllUnset_BuildsBareArgs()
    {
        Assert.Equal(
            ["repo", "edit", "o/r"],
            GitHubService.BuildRepoFeatureArgs("o/r", null, null, null));
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void RepoFeatures_ProjectsFlagCarriesItsOwnValue(bool projects, string value)
    {
        Assert.Equal(
            ["repo", "edit", "o/r", $"--enable-projects={value}"],
            GitHubService.BuildRepoFeatureArgs("o/r", null, null, projects));
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void Notifications_UnreadOnlyUnlessAllRequested(bool includeRead, string all)
    {
        Assert.Equal(
            ["api", $"repos/o/r/notifications?all={all}&per_page=50"],
            GitHubService.BuildNotificationsArgs("o/r", includeRead));
    }

    [Fact]
    public void MarkNotificationRead_PatchesTheThread()
    {
        Assert.Equal(
            ["api", "--method", "PATCH", "notifications/threads/14231733865"],
            GitHubService.BuildMarkNotificationReadArgs("14231733865"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../repos/o/r")]
    [InlineData("123abc")]
    [InlineData(" 123")]
    public void MarkNotificationRead_NonDigitThreadId_Throws(string threadId)
    {
        // The id lands inside a REST path; anything else could address another endpoint.
        Assert.Throws<ArgumentException>(() => GitHubService.BuildMarkNotificationReadArgs(threadId));
    }

    [Fact]
    public void MarkRepoNotificationsRead_PutsTheRepoCollection()
    {
        Assert.Equal(
            ["api", "--method", "PUT", "repos/o/r/notifications"],
            GitHubService.BuildMarkRepoNotificationsReadArgs("o/r"));
    }

    [Fact]
    public void AssetDownload_WritesTheChosenPathAndReplaces()
    {
        Assert.Equal(
            ["release", "download", "v2.0.0", "--repo", "o/r", "--pattern", "setup.exe",
             "--output", @"C:\downloads\setup.exe", "--clobber"],
            GitHubService.BuildAssetDownloadArgs("o/r", "v2.0.0", "setup.exe", @"C:\downloads\setup.exe"));
    }

    [Fact]
    public void ReleaseDirDownload_TargetsTheScratchDirectory()
    {
        Assert.Equal(
            ["release", "download", "v2.0.0", "--repo", "o/r", "--dir", @"C:\scratch", "--clobber"],
            GitHubService.BuildReleaseDirDownloadArgs("o/r", "v2.0.0", @"C:\scratch"));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("Project Dashboard 2.0.0.msi")]
    [InlineData("sha256sums.txt")]
    public void PlainAssetNames_DownloadByPattern(string name)
        => Assert.False(GitHubService.NeedsFullReleaseFetch(name));

    [Theory]
    [InlineData("build[1].zip")]
    [InlineData("report*.txt")]
    [InlineData("what?.bin")]
    public void GlobbedAssetNames_FallBackToTheWholeRelease(string name)
    {
        // gh selects assets with filepath.Match, which has no escape on Windows: the
        // pattern "build[1].zip" matches "build1.zip" and never the literal name.
        Assert.True(GitHubService.NeedsFullReleaseFetch(name));
    }

    [Theory]
    [InlineData("HTTP 403: Must have admin rights to Repository.")]
    [InlineData("")]
    [InlineData("failed to delete repository: network unreachable")]
    public void DeleteFailure_WithoutTheScopeName_IsNotAScopeProblem(string error)
        => Assert.False(GitHubService.NeedsDeleteRepoScope(error));

    [Theory]
    [InlineData("needs the \"delete_repo\" scope")]
    [InlineData("gh auth refresh -h github.com -s DELETE_REPO")]
    public void DeleteFailure_NamingTheScope_IsAScopeProblem(string error)
        => Assert.True(GitHubService.NeedsDeleteRepoScope(error));
}

/// <summary>
/// A notification's only link is its REST url, which answers JSON and names the
/// pull-request collection differently from the site. Anything the mapping cannot
/// vouch for reads as "" so the UI falls back to the repository page rather than
/// launching a guessed address.
/// </summary>
public class GitHubNotificationUrlTests
{
    [Fact]
    public void IssueUrl_MapsToTheIssuePage()
        => Assert.Equal("https://github.com/o/r/issues/41",
            GitHubService.NotificationWebUrl("https://api.github.com/repos/o/r/issues/41"));

    [Fact]
    public void PullsUrl_MapsToTheSingularPullPath()
        => Assert.Equal("https://github.com/o/r/pull/12",
            GitHubService.NotificationWebUrl("https://api.github.com/repos/o/r/pulls/12"));

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases/99")]        // no web equivalent by id
    [InlineData("https://api.github.com/repos/o/r/issues/comments/5")]  // too many segments
    [InlineData("https://api.github.com/repos/o/r/issues/notanumber")]
    [InlineData("https://api.github.com/repos/o/r/issues")]
    [InlineData("https://evil.example.com/repos/o/r/issues/1")]
    [InlineData("http://api.github.com/repos/o/r/issues/1")]
    [InlineData("https://api.github.com/repos/../r/issues/1")]  // would resolve elsewhere on the site
    [InlineData("https://api.github.com/repos/o/../issues/1")]
    [InlineData("https://api.github.com/repos//r/issues/1")]
    [InlineData("https://api.github.com/repos/o/r/issues/41?x=1")]
    [InlineData("")]
    public void AnythingElse_MapsToNothing(string apiUrl)
        => Assert.Equal("", GitHubService.NotificationWebUrl(apiUrl));
}
