using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Tag listing/create/delete/push against disposable local + file:// fixtures.</summary>
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

        var tags = (await _git.GetTagsAsync(repo.Path)).Tags;
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

    /// <summary>
    /// `tag -a` cleans a message handed to it with strip, which deletes every line starting with
    /// the comment character. A subject typed as an issue reference vanishes and the tag is
    /// recorded with no message at all, while git exits 0 and the surface reports the tag created.
    /// Nothing in a repository's config can make this safe, so the message cleanup pin carries
    /// every annotated tag.
    /// </summary>
    [Fact]
    public async Task CreateTag_Annotated_RecordsAHashLeadingMessageVerbatim()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-hash-message");

        Assert.True((await _git.CreateTagAsync(repo.Path, "v1", "#42 ship it")).Success);
        Assert.True((await _git.CreateTagAsync(repo.Path, "v2", "#42 ship it\n\n#43 and this line too\n")).Success);

        var tag = (await _git.GetTagsAsync(repo.Path)).Tags.Single(t => t.Name == "v1");
        Assert.True(tag.IsAnnotated);
        Assert.Equal("#42 ship it", tag.Subject);

        Assert.Equal("#42 ship it\n\n#43 and this line too",
            (await repo.GitAsync("tag", "-l", "--format=%(contents)", "v2")).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public async Task CreateTag_AtExplicitCommit_TargetsThatCommit()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-at");
        var firstCommit = await repo.HeadShaAsync();
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("second");

        Assert.True((await _git.CreateTagAsync(repo.Path, "at-first", message: null, targetCommit: firstCommit)).Success);

        var tag = Assert.Single((await _git.GetTagsAsync(repo.Path)).Tags);
        Assert.Equal(firstCommit, tag.TargetSha);
    }

    [Fact]
    public async Task DeleteTag_RemovesIt()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-del");
        await _git.CreateTagAsync(repo.Path, "temp");
        Assert.Single((await _git.GetTagsAsync(repo.Path)).Tags);

        Assert.True((await _git.DeleteTagAsync(repo.Path, "temp")).Success);
        Assert.Empty((await _git.GetTagsAsync(repo.Path)).Tags);
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

    [Fact]
    public async Task GetTags_ReportsTheTargetCommitsSubjectAndDate_ForBothKinds()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tag-target");
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("the commit\tbehind the tag");

        await _git.CreateTagAsync(repo.Path, "light");
        await _git.CreateTagAsync(repo.Path, "annot", "the tag's own message");

        var tags = (await _git.GetTagsAsync(repo.Path)).Tags;

        var light = tags.Single(t => t.Name == "light");
        // A tab inside the subject must not split a field — the format is unit-separated.
        Assert.Equal("the commit\tbehind the tag", light.TargetSubject);
        Assert.NotNull(light.TargetDate);
        Assert.Equal(light.TargetDate, light.DisplayDate);
        Assert.Equal("lightweight", light.KindLabel);

        var annot = tags.Single(t => t.Name == "annot");
        Assert.Equal("the commit\tbehind the tag", annot.TargetSubject);
        Assert.Equal("the tag's own message", annot.Subject);
        Assert.NotNull(annot.TargetDate);
        Assert.Equal(annot.TaggerDate, annot.DisplayDate);
        Assert.Equal("annotated", annot.KindLabel);
    }

    /// <summary>
    /// The Tags tab shows "no tags yet" off an empty list, which is a claim about the
    /// repository. A ref read that could not run produces the same empty list and supports no
    /// such claim, so the two are separated at the read.
    /// </summary>
    [Fact]
    public async Task GetTags_SeparatesAnUntaggedRepositoryFromAReadThatFailed()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-none");

        var untagged = await _git.GetTagsAsync(repo.Path);
        Assert.Empty(untagged.Tags);
        Assert.False(untagged.HasError);
        Assert.Equal("", untagged.ErrorText);

        var refused = await _git.GetTagsAsync(TestEnv.NewDir("tags-not-a-repo"));
        Assert.Empty(refused.Tags);
        Assert.True(refused.HasError);
        Assert.NotEqual("", refused.ErrorText);
    }
}
