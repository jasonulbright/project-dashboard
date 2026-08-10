using ProjectDashboard.Models;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// What a repository is recorded as. One remote written four ways is one remote: two spellings of
/// it recorded as different values leave a moved repository unmatched and its metadata stranded.
/// </summary>
public class RepoFingerprintTests
{
    [Theory]
    [InlineData("https://github.com/Owner/TabKit.git", "github.com/owner/tabkit")]
    [InlineData("https://github.com/owner/tabkit", "github.com/owner/tabkit")]
    [InlineData("git@github.com:owner/tabkit.git", "github.com/owner/tabkit")]
    [InlineData("ssh://git@github.com:22/owner/tabkit", "github.com/owner/tabkit")]
    [InlineData("git://github.com/owner/tabkit.git", "github.com/owner/tabkit")]
    [InlineData("https://gitlab.com/group/sub/tabkit.git", "gitlab.com/group/sub/tabkit")]
    public void EveryShapeGitProducesForOneRemote_NormalizesToOneValue(string url, string expected) =>
        Assert.Equal(expected, RepoFingerprint.NormalizeRemote(url));

    /// <summary>A local or file:// origin names a path; the fixtures in this suite use exactly that.</summary>
    [Theory]
    [InlineData(@"C:\origins\remote.git", @"c:\origins\remote")]
    [InlineData("file:///C:/origins/remote.git", @"c:\origins\remote")]
    [InlineData("file:///C:/origins/remote", @"c:\origins\remote")]
    [InlineData(@"C:\origins\remote\", @"c:\origins\remote")]
    public void ALocalOrigin_NormalizesToItsPath(string url, string expected) =>
        Assert.Equal(expected, RepoFingerprint.NormalizeRemote(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoRemote_NormalizesToNothing(string? url) =>
        Assert.Equal("", RepoFingerprint.NormalizeRemote(url));

    [Fact]
    public void ARepositoryWithNoCommitsAndNoRemote_IsNotStrongEnoughToMatchOn()
    {
        Assert.False(RepoFingerprint.For("fresh", [], "").IsStrong);
        Assert.False(RepoFingerprint.Matches(
            RepoFingerprint.For("fresh", [], ""), RepoFingerprint.For("fresh", [], "")));
    }

    [Fact]
    public void RootCommits_AreRecordedSortedAndDeduplicated()
    {
        var print = RepoFingerprint.For("repo", ["bbb", "aaa", "AAA", "  ", "ccc"], "");

        Assert.Equal(["aaa", "bbb", "ccc"], print.RootCommitOids);
    }

    [Fact]
    public void AFingerprintComparesEqualToACopyOfItself()
    {
        var print = RepoFingerprint.For("repo", ["aaa"], "https://github.com/owner/repo");

        Assert.True(print.SameAs(print.Copy()));
        Assert.False(print.SameAs(RepoFingerprint.For("repo", ["bbb"], "https://github.com/owner/repo")));
        Assert.False(print.SameAs(null));
    }
}
