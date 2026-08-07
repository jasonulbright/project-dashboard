using ProjectDashboard.Models;

namespace ProjectDashboard.Tests;

public class GitRemoteParseTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("http://github.com/owner/repo.git")]
    [InlineData("git://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("git@github.com:owner/repo")]
    [InlineData("  https://github.com/owner/repo.git  ")]
    [InlineData("https://github.com/owner/repo/")]
    public void CommonGitHubShapes_ParseToSameSlug(string url)
    {
        var remote = GitRemote.Parse(url);

        Assert.NotNull(remote);
        Assert.Equal("github.com", remote.Host);
        Assert.Equal("owner", remote.Owner);
        Assert.Equal("repo", remote.Repo);
        Assert.True(remote.IsGitHub);
    }

    [Fact]
    public void SshWithPort_StripsPortFromHost()
    {
        var remote = GitRemote.Parse("ssh://git@github.com:2222/owner/repo.git");

        Assert.NotNull(remote);
        Assert.Equal("github.com", remote.Host);
        Assert.Equal("owner", remote.Owner);
        Assert.Equal("repo", remote.Repo);
    }

    [Fact]
    public void UserInfoInHttpsHost_IsStripped()
    {
        var remote = GitRemote.Parse("https://user@github.com/owner/repo.git");

        Assert.NotNull(remote);
        Assert.Equal("github.com", remote.Host);
    }

    [Theory]
    [InlineData("https://github.com/owner/user.github.io.git", "user.github.io")]
    [InlineData("https://github.com/owner/user.github.io", "user.github.io")]
    [InlineData("git@github.com:owner/user.github.io.git", "user.github.io")]
    public void DotGitInsideRepoName_SurvivesSuffixStrip(string url, string expectedRepo)
    {
        var remote = GitRemote.Parse(url);

        Assert.NotNull(remote);
        Assert.Equal(expectedRepo, remote.Repo);
    }

    [Fact]
    public void GitLabHost_ParsesButIsNotGitHub()
    {
        var remote = GitRemote.Parse("https://gitlab.com/owner/repo.git");

        Assert.NotNull(remote);
        Assert.Equal("gitlab.com", remote.Host);
        Assert.False(remote.IsGitHub);
    }

    [Fact]
    public void NestedGitLabGroups_FoldIntoOwner()
    {
        var remote = GitRemote.Parse("https://gitlab.com/group/subgroup/repo.git");

        Assert.NotNull(remote);
        Assert.Equal("group/subgroup", remote.Owner);
        Assert.Equal("repo", remote.Repo);
        Assert.False(remote.IsGitHub);
    }

    [Fact]
    public void ScpLikeGitLab_ParsesHost()
    {
        var remote = GitRemote.Parse("git@gitlab.com:owner/repo.git");

        Assert.NotNull(remote);
        Assert.Equal("gitlab.com", remote.Host);
        Assert.False(remote.IsGitHub);
    }

    [Fact]
    public void WwwGitHubHost_CountsAsGitHub()
    {
        var remote = GitRemote.Parse("https://www.github.com/owner/repo");

        Assert.NotNull(remote);
        Assert.True(remote.IsGitHub);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///C:/code/repo.git")]
    [InlineData(@"C:\code\repo")]
    [InlineData("C:/code/repo")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("origin:repo")]
    [InlineData("https://github.com/owner")]
    [InlineData("ftp://github.com/owner/repo")]
    public void NonRemoteOrUnparseable_ReturnsNull(string? url)
    {
        Assert.Null(GitRemote.Parse(url));
    }
}

public class RepoNameFromUrlTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "repo")]
    [InlineData("https://github.com/owner/repo", "repo")]
    [InlineData("https://github.com/owner/repo/", "repo")]
    [InlineData("git@github.com:owner/repo.git", "repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "repo")]
    [InlineData("https://gitlab.com/group/subgroup/repo.git", "repo")]
    public void RemoteUrls_YieldLastSegmentWithoutSuffix(string url, string expected)
    {
        Assert.Equal(expected, GitRemote.RepoNameFromUrl(url));
    }

    [Theory]
    [InlineData("file:///C:/fixtures/bare-repo.git", "bare-repo")]
    [InlineData(@"C:\fixtures\myrepo", "myrepo")]
    [InlineData(@"C:\fixtures\myrepo\", "myrepo")]
    [InlineData("C:/fixtures/myrepo.git", "myrepo")]
    [InlineData(@"C:\fixtures\my repo", "my repo")]
    public void LocalPathsAndFileUrls_YieldFolderName(string url, string expected)
    {
        Assert.Equal(expected, GitRemote.RepoNameFromUrl(url));
    }

    [Theory]
    [InlineData("https://github.com/owner/user.github.io", "user.github.io")]
    [InlineData("https://github.com/owner/user.github.io.git", "user.github.io")]
    public void DotGitInsideName_OnlySuffixIsStripped(string url, string expected)
    {
        Assert.Equal(expected, GitRemote.RepoNameFromUrl(url));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("repo", "repo")]
    public void DegenerateInputs_FallBackSafely(string? url, string expected)
    {
        Assert.Equal(expected, GitRemote.RepoNameFromUrl(url));
    }
}
