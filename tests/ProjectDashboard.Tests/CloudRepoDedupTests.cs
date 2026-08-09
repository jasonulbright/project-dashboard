using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Which of the account's repositories get a Cloud card. Suppressing one is a claim that the
/// reader already has it on disk, and the only evidence for that claim is a local remote
/// pointing at the same repository.
/// </summary>
public class CloudRepoDedupTests
{
    private static ProjectInfo LocalRepo(string folder, string? remoteUrl = null) => new()
    {
        DirectoryName = folder,
        DisplayName = folder,
        FullPath = $@"C:\projects\{folder}",
        GitStatus = new GitStatus { RemoteUrl = remoteUrl ?? "" }
    };

    private static RemoteRepo Cloud(string nameWithOwner) => new() { NameWithOwner = nameWithOwner };

    private static List<string> Surfaced(IReadOnlyList<ProjectInfo> local, params string[] remotes) =>
        ProjectDiscoveryService
            .RemotesWithoutALocalClone(local, [.. remotes.Select(Cloud)])
            .Select(r => r.NameWithOwner)
            .ToList();

    /// <summary>
    /// The folder name matched, so the card was suppressed — and the reader was never told the
    /// repository existed. Nothing about a local folder called "api" says it is bob/api.
    /// </summary>
    [Fact]
    public void ALocalFolderWithNoRemote_DoesNotHideACloudRepoOfTheSameName()
    {
        Assert.Equal(["bob/api"], Surfaced([LocalRepo("api")], "bob/api"));
    }

    [Fact]
    public void ALocalRepoOnAnotherHost_DoesNotHideACloudRepoOfTheSameName()
    {
        var local = LocalRepo("api", "https://gitlab.com/someone/api.git");

        Assert.Equal(["bob/api"], Surfaced([local], "bob/api"));
    }

    /// <summary>A different account's repo of the same name is a different repository.</summary>
    [Fact]
    public void ALocalCloneOfAnotherOwnersRepo_DoesNotHideTheAccountsOwn()
    {
        var local = LocalRepo("api", "https://github.com/carol/api.git");

        Assert.Equal(["bob/api"], Surfaced([local], "bob/api"));
    }

    [Fact]
    public void ACloneOfTheRepo_SuppressesItsCloudCard()
    {
        var local = LocalRepo("api", "https://github.com/bob/api.git");

        Assert.Empty(Surfaced([local], "bob/api"));
    }

    /// <summary>The slug travels with the remote, so the folder it was cloned into is irrelevant.</summary>
    [Theory]
    [InlineData("https://github.com/bob/api.git")]
    [InlineData("git@github.com:bob/api.git")]
    [InlineData("ssh://git@github.com/bob/api")]
    public void ACloneUnderARenamedFolder_StillSuppressesItsCloudCard(string remoteUrl)
    {
        var local = LocalRepo("api-work", remoteUrl);

        Assert.Empty(Surfaced([local], "bob/api"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive_AsGitHubSlugsAre()
    {
        var local = LocalRepo("api", "https://github.com/Bob/API.git");

        Assert.Empty(Surfaced([local], "bob/api"));
    }

    [Fact]
    public void OnlyTheClonedRepoIsSuppressed()
    {
        var local = LocalRepo("api", "https://github.com/bob/api.git");

        Assert.Equal(["bob/dashboard"], Surfaced([local], "bob/api", "bob/dashboard"));
    }
}
