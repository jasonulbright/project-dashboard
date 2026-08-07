using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The interactive-rebase mechanism itself: the sequence editor genuinely applies a prepared
/// todo, no editor ever blocks, each todo transformation lands the history it promises, and a
/// stopped rebase aborts back to a byte-identical repository.
///
/// These drive the driver directly (no coordinator), so they touch no shared app-data state
/// and can run in parallel with the rest of the suite.
/// </summary>
public class RebaseDriverTests
{
    private readonly ITestOutputHelper _output;

    public RebaseDriverTests(ITestOutputHelper output) => _output = output;

    private static RebaseDriver NewDriver() =>
        new(new GitService(), GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"));

    /// <summary>seed, then two commits that rewrite the SAME line — replaying them out of order must conflict.</summary>
    private static async Task<SurgeryRepo> ConflictingRepoAsync()
    {
        var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "a\nSHARED-ONE\n");
        await repo.CommitAllAsync("one");
        repo.Write("shared.txt", "a\nSHARED-TWO\n");
        await repo.CommitAllAsync("two");
        return repo;
    }

    // ── the mechanism ─────────────────────────────────────────────────────

    [Fact]
    public async Task PreparedTodo_ReplacesTheGeneratedOne_AndNoEditorEverBlocks()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta", "gamma");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 3);

        // A todo git would never generate on its own: reversed, with the middle commit removed.
        // A `reword` line rides along — with GIT_EDITOR a no-op it must complete without
        // blocking and leave the message untouched, which is the proof no editor can hang us.
        var todo = new List<string>
        {
            $"pick {scope.Commits[2].Sha} {scope.Commits[2].Subject}",
            $"reword {scope.Commits[0].Sha} {scope.Commits[0].Subject}"
        };

        var result = await driver.RunTodoAsync(scope, todo, new Dictionary<string, string>());

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "gamma", "alpha"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        Assert.False(repo.Exists("beta.txt"));
        // The no-op editor left the reworded message exactly as it was.
        Assert.Equal("alpha", await repo.MessageAsync("HEAD"));
        _output.WriteLine("sequence editor applied a prepared todo git never generated; reword completed with a no-op GIT_EDITOR");
    }

    [Fact]
    public async Task LoadScope_RangeContainsAMerge_RefusesRatherThanFlatteningIt()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "main-one");
        await repo.GitAsync("switch", "-q", "-c", "side");
        repo.Write("side.txt", "side\n");
        await repo.CommitAllAsync("side-one");
        await repo.GitAsync("switch", "-q", "main");
        repo.Write("main.txt", "main\n");
        await repo.CommitAllAsync("main-two");
        await repo.GitAsync("merge", "-q", "--no-ff", "side", "-m", "merge side");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewDriver().LoadScopeAsync(repo.Path, 3));
        Assert.Contains("merge", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── the operations ────────────────────────────────────────────────────

    [Fact]
    public async Task Reorder_SwapsTwoCommits_KeepingTheResultingTreeIdentical()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var treeBefore = await repo.TreeAsync();

        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);
        Assert.Equal(["alpha", "beta"], scope.Commits.Select(c => c.Subject));

        var result = await driver.ReorderAsync(scope, [scope.Commits[1].Sha, scope.Commits[0].Sha]);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "beta", "alpha"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        // Independent commits: the order changed but the end state did not.
        Assert.Equal(treeBefore, await repo.TreeAsync());
        Assert.Equal("alpha content\n", repo.Read("alpha.txt"));
        Assert.Equal("beta content\n", repo.Read("beta.txt"));
        // Each intermediate commit carries only its own file, proving the replay really reordered.
        Assert.Equal("beta content\n", await repo.ShowAsync("HEAD~1", "beta.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ShowAsync("HEAD~1", "alpha.txt"));
        _output.WriteLine($"reorder: subjects now {string.Join(" -> ", (await repo.SubjectsAsync()).AsEnumerable().Reverse())}, tree unchanged at {treeBefore[..8]}");
    }

    [Fact]
    public async Task Squash_KeepFirstMessage_FoldsContentIntoOneCommit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);

        var result = await driver.SquashAsync(scope, [scope.Commits[0].Sha, scope.Commits[1].Sha]);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "alpha"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        Assert.Equal("alpha", await repo.MessageAsync("HEAD"));
        // Union of both trees in the single surviving commit.
        Assert.Equal("alpha content\n", await repo.ShowAsync("HEAD", "alpha.txt"));
        Assert.Equal("beta content\n", await repo.ShowAsync("HEAD", "beta.txt"));
    }

    [Fact]
    public async Task Squash_WithNewMessageFile_InstallsThatMessageOnTheFoldedCommit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);
        const string message = "combined alpha and beta\n\nA body line the amend must preserve.";

        var result = await driver.SquashAsync(scope, [scope.Commits[0].Sha, scope.Commits[1].Sha], message);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(2, (await repo.ShasAsync()).Count);
        Assert.Equal(message, await repo.MessageAsync("HEAD"));
        Assert.Equal("alpha content\n", await repo.ShowAsync("HEAD", "alpha.txt"));
        Assert.Equal("beta content\n", await repo.ShowAsync("HEAD", "beta.txt"));
    }

    [Fact]
    public async Task Squash_NonContiguousRun_IsRefusedBeforeGitRuns()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta", "gamma");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 3);
        var before = await repo.RefStateAsync();

        var result = await driver.SquashAsync(scope, [scope.Commits[0].Sha, scope.Commits[2].Sha]);

        Assert.False(result.Success);
        Assert.Contains("contiguous", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await repo.RefStateAsync());
    }

    [Fact]
    public async Task Drop_MiddleCommit_RemovesItAndPreservesTheOthers()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta", "gamma");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 3);

        var result = await driver.DropAsync(scope, [scope.Commits[1].Sha]);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "alpha", "gamma"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        Assert.False(repo.Exists("beta.txt"));
        Assert.Equal("alpha content\n", repo.Read("alpha.txt"));
        Assert.Equal("gamma content\n", repo.Read("gamma.txt"));
    }

    [Fact]
    public async Task Drop_EveryCommitInRange_IsRefused()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);

        var result = await driver.DropAsync(scope, scope.Commits.Select(c => c.Sha).ToList());

        Assert.False(result.Success);
        Assert.Contains("reset", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, (await repo.ShasAsync()).Count);
    }

    [Fact]
    public async Task Reword_ThirdOfFive_ChangesOnlyThatMessage_LeavingTheTreeIdentical()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "c1", "c2", "c3", "c4", "c5");
        var treeBefore = await repo.TreeAsync();
        var shasBefore = await repo.RangeShasAsync(6); // oldest first: seed, c1..c5

        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 5);
        var target = scope.Commits[2];
        Assert.Equal("c3", target.Subject);

        var result = await driver.RewordAsync(scope, target.Sha, "c3 reworded");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "c1", "c2", "c3 reworded", "c4", "c5"],
            (await repo.SubjectsAsync()).AsEnumerable().Reverse());

        var shasAfter = await repo.RangeShasAsync(6);
        // Everything up to and including c2 is untouched; c3 onward is rewritten.
        Assert.Equal(shasBefore.Take(3), shasAfter.Take(3));
        for (var i = 3; i < 6; i++) Assert.NotEqual(shasBefore[i], shasAfter[i]);
        // Content is byte-identical — only the message moved.
        Assert.Equal(treeBefore, await repo.TreeAsync());
        _output.WriteLine($"reword: shas 0-2 unchanged, 3-5 rewritten, HEAD tree still {treeBefore[..8]}");
    }

    // ── conflict policy ───────────────────────────────────────────────────

    [Fact]
    public async Task Reorder_ThatConflicts_AbortsAndLeavesTheRepositoryByteIdentical()
    {
        using var repo = await ConflictingRepoAsync();
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);
        var conflicting = scope.Commits[1]; // "two" replayed before "one" cannot apply

        var stateBefore = await repo.FullStateAsync();
        var contentBefore = repo.Read("shared.txt");

        var result = await driver.ReorderAsync(scope, [conflicting.Sha, scope.Commits[0].Sha]);

        Assert.False(result.Success);
        Assert.True(result.Aborted);
        Assert.False(result.LeftStopped);
        Assert.Equal(conflicting.Sha, result.ConflictCommit);
        Assert.Equal("two", result.ConflictSubject);
        Assert.Contains("aborted", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Refs, HEAD, working-tree status and the absence of a rebase state dir all match.
        Assert.Equal(stateBefore, await repo.FullStateAsync());
        Assert.False(repo.RebaseInProgress);
        Assert.Equal(contentBefore, repo.Read("shared.txt"));
        Assert.Equal(["seed", "one", "two"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        _output.WriteLine($"conflict abort: named {result.ConflictSubject} ({result.ConflictCommit![..8]}); post-abort state identical to:\n{stateBefore}");
    }

    [Fact]
    public async Task Reorder_ThatConflicts_UnderLeaveStopped_KeepsTheRebaseForTheTerminal()
    {
        using var repo = await ConflictingRepoAsync();
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);

        var result = await driver.ReorderAsync(
            scope, [scope.Commits[1].Sha, scope.Commits[0].Sha], RebaseConflictPolicy.LeaveStopped);

        Assert.False(result.Success);
        Assert.True(result.LeftStopped);
        Assert.False(result.Aborted);
        Assert.True(repo.RebaseInProgress);
        Assert.Contains("terminal", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // The existing working-state detection reports the stopped rebase, which is what drives
        // the state banner and Open in Terminal.
        var state = await new GitService().GetWorkingStateAsync(repo.Path);
        Assert.NotNull(state);
        Assert.Equal(ProjectDashboard.Models.RepoActivity.Rebasing, state!.Activity);

        await repo.GitAsync("rebase", "--abort");
    }

    [Fact]
    public async Task LeaveStopped_KeepsTheMessageFilesTheStoppedTodoStillPointsAt()
    {
        using var repo = await ConflictingRepoAsync();
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);

        // The conflicting commit replays first, so the trailing amend has not run yet: its
        // message file must survive for a `git rebase --continue` from the terminal.
        const string token = "@@MESSAGE@@";
        var todo = new List<string>
        {
            $"pick {scope.Commits[1].Sha} {scope.Commits[1].Subject}",
            $"pick {scope.Commits[0].Sha} {scope.Commits[0].Subject}",
            $"exec git commit --amend --no-verify -F {token}"
        };

        var result = await driver.RunTodoAsync(
            scope, todo, new Dictionary<string, string> { [token] = "a message the amend has yet to apply" },
            RebaseConflictPolicy.LeaveStopped);

        Assert.True(result.LeftStopped);
        var remaining = await File.ReadAllTextAsync(Path.Combine(repo.Path, ".git", "rebase-merge", "git-rebase-todo"));
        var execLine = remaining.Split('\n').Single(l => l.Contains("--amend"));
        var quoted = execLine[(execLine.IndexOf('"') + 1)..execLine.LastIndexOf('"')];
        Assert.True(File.Exists(quoted), $"the stopped todo points at a message file that no longer exists: {quoted}");

        await repo.GitAsync("rebase", "--abort");
    }
}
