using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// The UI derives the gh token from an enum, never free text, so the token reaching
/// GitHubService.BuildMergeArgs / BuildReviewArgs is always mapped —
/// those methods' unmapped-token throw is unreachable from the UI. These assertions
/// pin the exact strings the service's switch expects.
/// </summary>
public class GitHubActionTokensTests
{
    [Theory]
    [InlineData(MergeStrategy.Merge, "merge")]
    [InlineData(MergeStrategy.Squash, "squash")]
    [InlineData(MergeStrategy.Rebase, "rebase")]
    public void MergeStrategy_MapsToServiceToken(MergeStrategy strategy, string token)
    {
        Assert.Equal(token, strategy.Token());
        // The mapped token must round-trip through the service arg builder without throwing.
        var args = GitHubService.BuildMergeArgs("o/r", 1, strategy.Token(), deleteBranch: false);
        Assert.Contains($"--{token}", args);
    }

    [Theory]
    [InlineData(ReviewAction.Approve, "approve")]
    [InlineData(ReviewAction.RequestChanges, "requestChanges")]
    [InlineData(ReviewAction.Comment, "comment")]
    public void ReviewAction_MapsToServiceToken(ReviewAction action, string token)
    {
        Assert.Equal(token, action.Token());
        // No throw for any enum value the UI can produce.
        _ = GitHubService.BuildReviewArgs("o/r", 1, action.Token(), "");
    }

    [Fact]
    public void EveryMergeStrategy_IsAccepted()
    {
        foreach (var s in Enum.GetValues<MergeStrategy>())
            _ = GitHubService.BuildMergeArgs("o/r", 1, s.Token(), false);
    }

    [Fact]
    public void EveryReviewAction_IsAccepted()
    {
        foreach (var a in Enum.GetValues<ReviewAction>())
            _ = GitHubService.BuildReviewArgs("o/r", 1, a.Token(), "");
    }

    [Theory]
    [InlineData(RepoVisibility.Public, "public")]
    [InlineData(RepoVisibility.Private, "private")]
    [InlineData(RepoVisibility.Internal, "internal")]
    public void RepoVisibility_MapsToServiceToken(RepoVisibility visibility, string token)
    {
        Assert.Equal(token, visibility.Token());
        Assert.Contains(token, GitHubService.BuildVisibilityArgs("o/r", visibility.Token()));
    }

    [Fact]
    public void EveryRepoVisibility_IsAccepted()
    {
        foreach (var v in Enum.GetValues<RepoVisibility>())
            _ = GitHubService.BuildVisibilityArgs("o/r", v.Token());
    }

    [Fact]
    public void RepoVisibility_RoundTripsThroughTheParsedForm()
    {
        // The Repo tab seeds its picker from gh's lowercase reading; a value that did
        // not round-trip would show the wrong current visibility.
        foreach (var v in Enum.GetValues<RepoVisibility>())
            Assert.Equal(v, GitHubActionTokens.ParseVisibility(v.Token()));
    }

    [Theory]
    [InlineData("PUBLIC")]
    [InlineData("secret")]
    [InlineData("")]
    public void UnknownVisibilityString_ParsesToNull(string visibility)
        => Assert.Null(GitHubActionTokens.ParseVisibility(visibility));
}
