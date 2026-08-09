using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Stash push with message/untracked and stash diff; base apply/pop/drop live in GitServiceTests.</summary>
public class GitServiceStashDepthTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task StashPush_WithMessageAndUntracked_ClearsWorkingTree()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-push");
        repo.WriteFile("file.txt", "modified\n");
        repo.WriteFile("scratch.txt", "untracked\n");

        Assert.True((await _git.StashPushAsync(repo.Path, "wip snapshot", includeUntracked: true)).Success);

        // Tracked change reverted and the untracked file swept into the stash.
        Assert.Equal("line one\n", repo.ReadFile("file.txt"));
        Assert.False(repo.FileExists("scratch.txt"));

        var entry = Assert.Single(await _git.GetStashesAsync(repo.Path));
        Assert.Contains("wip snapshot", entry.Subject);
    }

    [Fact]
    public async Task StashPush_WithoutUntracked_LeavesUntrackedInPlace()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-tracked");
        repo.WriteFile("file.txt", "modified\n");
        repo.WriteFile("scratch.txt", "untracked\n");

        Assert.True((await _git.StashPushAsync(repo.Path, "tracked only")).Success);

        Assert.Equal("line one\n", repo.ReadFile("file.txt"));
        // Untracked file untouched when -u is not requested.
        Assert.True(repo.FileExists("scratch.txt"));
    }

    [Fact]
    public async Task GetStashDiff_ParsesTheStashedChange()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-diff");
        repo.WriteFile("file.txt", "line one\nline two\n");
        await _git.StashPushAsync(repo.Path, "wip");

        var diffs = await _git.GetStashDiffAsync(repo.Path, "stash@{0}");
        Assert.NotNull(diffs);
        var diff = Assert.Single(diffs);
        Assert.Equal("file.txt", diff.Path);
        Assert.Contains(diff.Lines, l => l is { Kind: ProjectDashboard.Models.DiffLineKind.Added, Text: "line two" });
    }

    /// <summary>An empty list means a stash that changed nothing; a failed read must not say that.</summary>
    [Fact]
    public async Task GetStashDiff_OnARefThatDoesNotResolve_ReturnsNullRatherThanNoChanges()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-diff-missing");

        Assert.Null(await _git.GetStashDiffAsync(repo.Path, "stash@{9}"));
    }
}
