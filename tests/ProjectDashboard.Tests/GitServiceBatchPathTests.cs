using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// The multi-path working-tree operations behind the Changes tab's batch actions (X-04).
/// A selection reaches git as pathspecs, batched under a command-line budget: sent as one run,
/// a few hundred paths overrun the Windows limit and nothing at all is staged.
/// </summary>
public class GitServiceBatchPathTests
{
    private static async Task<TempRepo> ThreeEditedFilesAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) repo.WriteFile(name, "one\n");
        await repo.CommitAllAsync("three files");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) repo.WriteFile(name, "two\n");
        return repo;
    }

    [Fact]
    public async Task StagingSeveralPaths_StagesExactlyThose()
    {
        using var repo = await ThreeEditedFilesAsync("git-batch-stage");
        var git = new GitService();

        var result = await git.StageAsync(repo.Path, ["a.txt", "c.txt"]);

        Assert.True(result.Success);
        var state = await git.GetWorkingStateAsync(repo.Path);
        Assert.Equal(["a.txt", "c.txt"], state!.Staged.Select(f => f.Path).Order());
        Assert.Equal(["b.txt"], state.Unstaged.Select(f => f.Path));
    }

    [Fact]
    public async Task UnstagingSeveralPaths_UnstagesExactlyThose()
    {
        using var repo = await ThreeEditedFilesAsync("git-batch-unstage");
        await repo.GitAsync("add", "-A");
        var git = new GitService();

        Assert.True((await git.UnstageAsync(repo.Path, ["a.txt", "b.txt"])).Success);

        var state = await git.GetWorkingStateAsync(repo.Path);
        Assert.Equal(["c.txt"], state!.Staged.Select(f => f.Path));
    }

    /// <summary>
    /// A mixed selection needs two commands. Tracked paths are restored first, so a failure
    /// there stops the run before anything is deleted from disk.
    /// </summary>
    [Fact]
    public async Task DiscardingAMixedSelection_RestoresTrackedFilesAndDeletesUntrackedOnes()
    {
        using var repo = await ThreeEditedFilesAsync("git-batch-discard");
        repo.WriteFile("new.txt", "fresh\n");
        var git = new GitService();
        var state = await git.GetWorkingStateAsync(repo.Path);

        var result = await git.DiscardAsync(repo.Path,
            state!.Unstaged.Where(f => f.Path != "c.txt").ToList());

        Assert.True(result.Success);
        Assert.Equal("one\n", repo.ReadFile("a.txt"));
        Assert.Equal("one\n", repo.ReadFile("b.txt"));
        Assert.Equal("two\n", repo.ReadFile("c.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Path, "new.txt")));
    }

    /// <summary>An operation given nothing to do runs nothing and reports no failure.</summary>
    [Fact]
    public async Task AnEmptySelection_RunsNothing()
    {
        using var repo = await ThreeEditedFilesAsync("git-batch-empty");
        var git = new GitService();

        Assert.True((await git.StageAsync(repo.Path, [])).Success);
        Assert.True((await git.DiscardAsync(repo.Path, [])).Success);
        Assert.Equal("two\n", repo.ReadFile("a.txt"));
    }

    [Fact]
    public void PathspecsWithinTheBudget_TravelAsOneRun()
    {
        var batches = GitService.PathspecBatches(["a.txt", "b.txt", "c.txt"]);

        Assert.Single(batches);
        Assert.Equal(3, batches[0].Count);
        Assert.All(batches[0], spec => Assert.StartsWith(":(literal)", spec));
    }

    [Fact]
    public void PathspecsPastTheBudget_AreSplitAcrossRunsWithNoneDropped()
    {
        var paths = Enumerable.Range(0, 40).Select(i => new string('p', 100) + i).ToList();

        var batches = GitService.PathspecBatches(paths, budget: 1000);

        Assert.True(batches.Count > 1);
        Assert.All(batches, b => Assert.True(b.Sum(s => s.Length + 1) <= 1000 || b.Count == 1));
        Assert.Equal(paths.Count, batches.Sum(b => b.Count));
    }

    /// <summary>
    /// A single pathspec longer than the whole budget still gets a run of its own: git's own
    /// limit is what refuses it, rather than the batching silently dropping the path.
    /// </summary>
    [Fact]
    public void APathspecLongerThanTheBudget_IsStillSent()
    {
        var batches = GitService.PathspecBatches([new string('x', 500)], budget: 100);

        Assert.Single(batches);
        Assert.Single(batches[0]);
    }

    [Fact]
    public async Task ABatchedStage_StagesEveryPathAcrossRuns()
    {
        var repo = await TempRepo.CreateWithCommitAsync("git-batch-many");
        using var _ = repo;
        var names = Enumerable.Range(0, 60).Select(i => $"file{i:00}.txt").ToList();
        foreach (var name in names) repo.WriteFile(name, "content\n");
        var git = new GitService();

        Assert.True((await git.StageAsync(repo.Path, names)).Success);

        var state = await git.GetWorkingStateAsync(repo.Path);
        Assert.Equal(names.Count, state!.Staged.Count());
    }
}
