using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// GitService against real disposable repos under %TEMP%\pd-fixtures. Local
/// operations only — no network, no gh; remote flows use file:// fixtures in
/// <see cref="GitServiceRemoteTests"/>.
/// </summary>
public class GitServiceTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task InitWithFirstCommit_CreatesSingleCleanCommit()
    {
        using var repo = TempRepo.CreateEmptyDir("init");
        repo.WriteFile("readme.md", "hello\n");

        var error = await _git.InitWithFirstCommitAsync(repo.Path, "initial commit");

        Assert.Null(error);
        Assert.True(GitService.IsGitRepo(repo.Path));
        Assert.Equal(1, await repo.CommitCountAsync());
        Assert.Equal("initial commit", await repo.HeadSubjectAsync());

        var state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.NotNull(state);
        Assert.False(state.IsDirty);
        Assert.False(state.NoCommitsYet);
    }

    [Fact]
    public async Task InitWithFirstCommit_ReportsErrorWhenNothingToCommit()
    {
        using var repo = TempRepo.CreateEmptyDir("init-empty");

        var error = await _git.InitWithFirstCommitAsync(repo.Path, "initial commit");

        Assert.NotNull(error);
        Assert.Contains("commit", error);
    }

    [Fact]
    public async Task StageUnstageDiscard_TrackedFile_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tracked");
        repo.WriteFile("file.txt", "line one\nline two\n");

        var state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Equal(["file.txt"], state!.Unstaged.Select(f => f.Path));
        Assert.Empty(state.Staged);

        Assert.True((await _git.StageAsync(repo.Path, "file.txt")).Success);
        state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Equal(["file.txt"], state!.Staged.Select(f => f.Path));
        Assert.Empty(state.Unstaged);

        Assert.True((await _git.UnstageAsync(repo.Path, "file.txt")).Success);
        state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Empty(state!.Staged);
        Assert.Equal(["file.txt"], state.Unstaged.Select(f => f.Path));

        var tracked = state.Files.Single();
        Assert.True((await _git.DiscardAsync(repo.Path, tracked)).Success);
        Assert.Equal("line one\n", repo.ReadFile("file.txt"));
        state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.False(state!.IsDirty);
    }

    [Fact]
    public async Task StageUnstageDiscard_UntrackedFile_EndsWithDeletion()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("untracked");
        repo.WriteFile("extra.txt", "scratch\n");

        var state = await _git.GetWorkingStateAsync(repo.Path);
        var untracked = state!.Files.Single();
        Assert.True(untracked.IsUntracked);

        Assert.True((await _git.StageAsync(repo.Path, "extra.txt")).Success);
        state = await _git.GetWorkingStateAsync(repo.Path);
        var staged = state!.Staged.Single();
        Assert.Equal('A', staged.IndexStatus);
        Assert.False(staged.IsUntracked);

        Assert.True((await _git.UnstageAsync(repo.Path, "extra.txt")).Success);
        state = await _git.GetWorkingStateAsync(repo.Path);
        untracked = state!.Files.Single();
        Assert.True(untracked.IsUntracked);

        // Discard of an untracked file deletes it from disk (git clean -f).
        Assert.True((await _git.DiscardAsync(repo.Path, untracked)).Success);
        Assert.False(repo.FileExists("extra.txt"));
        state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.False(state!.IsDirty);
    }

    [Fact]
    public async Task CommitThenAmend_KeepsCountRewritesSubject()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("amend");
        repo.WriteFile("file.txt", "changed\n");
        Assert.True((await _git.StageAllAsync(repo.Path)).Success);

        Assert.True((await _git.CommitAsync(repo.Path, "second commit", amend: false)).Success);
        Assert.Equal(2, await repo.CommitCountAsync());
        Assert.Equal("second commit", await repo.HeadSubjectAsync());

        Assert.True((await _git.CommitAsync(repo.Path, "second commit, reworded", amend: true)).Success);
        Assert.Equal(2, await repo.CommitCountAsync());
        Assert.Equal("second commit, reworded", await repo.HeadSubjectAsync());
        Assert.Equal("second commit, reworded", await _git.GetLastCommitMessageAsync(repo.Path));
    }

    [Fact]
    public async Task BranchCreateSwitchDelete_SafeDeleteRefusesUnmerged()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("branch");

        Assert.True((await _git.CreateBranchAsync(repo.Path, "feature")).Success);
        var state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Equal("feature", state!.Branch);

        repo.WriteFile("feature.txt", "only on feature\n");
        await repo.CommitAllAsync("feature work");

        Assert.True((await _git.SwitchBranchAsync(repo.Path, "main")).Success);
        state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.Equal("main", state!.Branch);

        // Safe delete (-d) refuses an unmerged branch; the branch must survive.
        var refusal = await _git.DeleteBranchAsync(repo.Path, "feature");
        Assert.False(refusal.Success);
        Assert.Contains("not fully merged", refusal.FirstError);
        var branches = await _git.GetBranchesAsync(repo.Path);
        Assert.Contains(branches, b => b.Name == "feature");
        Assert.Contains(branches, b => b.Name == "main" && b.IsCurrent);

        await repo.GitAsync("merge", "feature");
        Assert.True((await _git.DeleteBranchAsync(repo.Path, "feature")).Success);
        branches = await _git.GetBranchesAsync(repo.Path);
        Assert.DoesNotContain(branches, b => b.Name == "feature");
    }

    [Fact]
    public async Task Stash_ListApplyPopDrop_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash");

        repo.WriteFile("file.txt", "stash me\n");
        Assert.True((await _git.RunAsync(repo.Path, ["stash", "push", "-m", "wip one"])).Success);
        Assert.Equal("line one\n", repo.ReadFile("file.txt"));

        var stashes = await _git.GetStashesAsync(repo.Path);
        var entry = Assert.Single(stashes);
        Assert.Equal("stash@{0}", entry.Ref);
        Assert.Contains("wip one", entry.Subject);
        Assert.NotNull(entry.Date);

        // Apply restores the change and keeps the stash entry.
        Assert.True((await _git.StashApplyAsync(repo.Path, entry.Ref)).Success);
        Assert.Equal("stash me\n", repo.ReadFile("file.txt"));
        Assert.Single(await _git.GetStashesAsync(repo.Path));

        await repo.GitAsync("restore", ".");

        // Pop restores the change and removes the entry.
        Assert.True((await _git.StashPopAsync(repo.Path, entry.Ref)).Success);
        Assert.Equal("stash me\n", repo.ReadFile("file.txt"));
        Assert.Empty(await _git.GetStashesAsync(repo.Path));

        await repo.GitAsync("restore", ".");
        repo.WriteFile("file.txt", "second stash\n");
        Assert.True((await _git.RunAsync(repo.Path, ["stash", "push", "-m", "wip two"])).Success);

        // Drop removes the entry without touching the working tree.
        Assert.True((await _git.StashDropAsync(repo.Path, "stash@{0}")).Success);
        Assert.Empty(await _git.GetStashesAsync(repo.Path));
        Assert.Equal("line one\n", repo.ReadFile("file.txt"));
    }

    [Fact]
    public async Task GetWorkingState_OnConflictedMerge_ReportsConflictAndMergeActivity()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("conflict");

        await repo.GitAsync("switch", "-c", "feature");
        repo.WriteFile("file.txt", "feature version\n");
        await repo.CommitAllAsync("feature edit");

        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "main version\n");
        await repo.CommitAllAsync("main edit");

        var merge = await Git.TryRunAsync(repo.Path, "merge", "feature");
        Assert.False(merge.Success);

        var state = await _git.GetWorkingStateAsync(repo.Path);
        Assert.NotNull(state);
        Assert.True(state.HasConflicts);
        Assert.Equal(RepoActivity.Merging, state.Activity);

        var conflict = Assert.Single(state.Conflicted);
        Assert.Equal("file.txt", conflict.Path);
        Assert.DoesNotContain(state.Staged, f => f.Path == "file.txt");
        Assert.DoesNotContain(state.Unstaged, f => f.Path == "file.txt");
    }
}
