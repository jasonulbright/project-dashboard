using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Tag listing/create/delete/push (L-01) against disposable local + file:// fixtures.</summary>
public class GitServiceTagTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task GetTags_DistinguishesAnnotatedFromLightweight()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags");
        var head = await repo.HeadShaAsync();

        Assert.True((await _git.CreateTagAsync(repo.Path, "v1-light")).Success);
        Assert.True((await _git.CreateTagAsync(repo.Path, "v1-annot", "annotated release")).Success);

        var tags = await _git.GetTagsAsync(repo.Path);
        Assert.Equal(2, tags.Count);

        var light = tags.Single(t => t.Name == "v1-light");
        Assert.False(light.IsAnnotated);
        Assert.Equal(head, light.TargetSha);
        Assert.Null(light.Subject);
        Assert.Null(light.TaggerDate);

        var annot = tags.Single(t => t.Name == "v1-annot");
        Assert.True(annot.IsAnnotated);
        // TargetSha dereferences the tag object to the commit it points at.
        Assert.Equal(head, annot.TargetSha);
        Assert.Equal("annotated release", annot.Subject);
        Assert.NotNull(annot.TaggerDate);
    }

    [Fact]
    public async Task CreateTag_AtExplicitCommit_TargetsThatCommit()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-at");
        var firstCommit = await repo.HeadShaAsync();
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("second");

        Assert.True((await _git.CreateTagAsync(repo.Path, "at-first", message: null, targetCommit: firstCommit)).Success);

        var tag = Assert.Single(await _git.GetTagsAsync(repo.Path));
        Assert.Equal(firstCommit, tag.TargetSha);
    }

    [Fact]
    public async Task DeleteTag_RemovesIt()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-del");
        await _git.CreateTagAsync(repo.Path, "temp");
        Assert.Single(await _git.GetTagsAsync(repo.Path));

        Assert.True((await _git.DeleteTagAsync(repo.Path, "temp")).Success);
        Assert.Empty(await _git.GetTagsAsync(repo.Path));
    }

    [Fact]
    public async Task PushTag_And_PushAllTags_ReachBareOrigin()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tag-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tag-push");

        await _git.CreateTagAsync(clone.Path, "single");
        Assert.True((await _git.PushTagAsync(clone.Path, "origin", "single")).Success);
        var refs = await Git.RunAsync(bare.Path, "tag", "--list");
        Assert.Contains("single", refs);

        await _git.CreateTagAsync(clone.Path, "bulk-a");
        await _git.CreateTagAsync(clone.Path, "bulk-b", "annotated");
        Assert.True((await _git.PushAllTagsAsync(clone.Path, "origin")).Success);
        var allRefs = await Git.RunAsync(bare.Path, "tag", "--list");
        Assert.Contains("bulk-a", allRefs);
        Assert.Contains("bulk-b", allRefs);
    }

    [Fact]
    public async Task IsValidTagName_AcceptsWhatGitWouldCreateAndRefusesTheRest()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-names");

        Assert.True(await _git.IsValidTagNameAsync(repo.Path, "v1.2.3"));
        Assert.True(await _git.IsValidTagNameAsync(repo.Path, "release/2026-08"));

        Assert.False(await _git.IsValidTagNameAsync(repo.Path, ""));
        Assert.False(await _git.IsValidTagNameAsync(repo.Path, "-delete"));
        Assert.False(await _git.IsValidTagNameAsync(repo.Path, "has space"));
        Assert.False(await _git.IsValidTagNameAsync(repo.Path, "two..dots"));
        Assert.False(await _git.IsValidTagNameAsync(repo.Path, "trailing.lock"));
        Assert.False(await _git.IsValidTagNameAsync(repo.Path, "tilde~1"));
    }
}
