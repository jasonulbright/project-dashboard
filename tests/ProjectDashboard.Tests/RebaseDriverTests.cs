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

    [Theory]
    // `--empty=stop` is only understood from Git 2.45. `ask` is the same behaviour under the
    // older name: accepted everywhere, but deprecated loudly enough on a new git that the
    // warning would become the first line of every rebase failure message.
    [InlineData("git version 2.53.0.windows.1", "stop")]
    [InlineData("git version 2.45.0", "stop")]
    [InlineData("git version 3.0.1", "stop")]
    [InlineData("git version 2.44.0.windows.1", "ask")]
    [InlineData("git version 2.39.2", "ask")]
    [InlineData("", "ask")]
    [InlineData("not a version line", "ask")]
    // Only the token after the literal `version` decides it: a dotted-numeric token anywhere
    // else on the line belongs to something that is not git's version.
    [InlineData("C:\\tools\\git-2.99.0\\bin\\git.exe: git version 2.39.2", "ask")]
    [InlineData("warning: 9.9.9 something\ngit version 2.44.0", "ask")]
    [InlineData("git version notanumber", "ask")]
    [InlineData("2.53.0", "ask")]
    public void EmptyMode_FollowsTheGitVersion(string versionOutput, string expected) =>
        Assert.Equal(expected, RebaseDriver.EmptyModeFor(versionOutput));

    [Fact]
    public void RebaseArgs_CarryTheEmptyStopAndNeverAutosquash()
    {
        var args = RebaseDriver.BuildRebaseArgs("deadbeef", RebaseDriver.EmptyModeFor("git version 2.53.0.windows.1"));

        Assert.Equal(["rebase", "-i", "--empty=stop", "--onto", "deadbeef", "deadbeef"], args.Skip(args.Count - 6));
        // `fixup!` subjects are never reinterpreted: the todo says what happens, nothing else.
        Assert.DoesNotContain("--autosquash", args);
        Assert.Contains("-c", args);
        Assert.Contains("rebase.autoSquash=false", args);
        // An older git gets the spelling it understands, and a range reaching the root replays
        // with --root instead of an --onto pair.
        Assert.Contains("--empty=ask", RebaseDriver.BuildRebaseArgs("deadbeef", RebaseDriver.EmptyModeFor("git version 2.39.2")));
        Assert.Contains("--root", RebaseDriver.BuildRebaseArgs(null, "stop"));
        _output.WriteLine("rebase args: " + string.Join(" ", args));
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
    public async Task AShaPrefixTwoCommitsShare_IsRefusedAsAmbiguous_NotAsOutOfRange()
    {
        // Hand-built shas: git's own %h auto-extends past a collision, so a shared four-character
        // prefix only reaches the driver from a caller that abbreviates on its own.
        var scope = new RebaseScope
        {
            RepoPath = TestEnv.NewDir("ambiguous-prefix"),
            BaseSha = new string('0', 40),
            Commits =
            [
                new RebaseCommit("abcd" + new string('1', 36), "one"),
                new RebaseCommit("abcd" + new string('2', 36), "two"),
                new RebaseCommit("beef" + new string('3', 36), "three")
            ]
        };
        var driver = NewDriver();

        var ambiguous = await driver.DropAsync(scope, ["abcd"]);

        Assert.False(ambiguous.Success);
        Assert.Contains("matches more than one commit in the range", ambiguous.FailureReason);
        Assert.DoesNotContain("not in the editable range", ambiguous.FailureReason);

        // A prefix no commit carries keeps the range wording.
        var absent = await driver.DropAsync(scope, ["dead"]);
        Assert.False(absent.Success);
        Assert.Contains("not in the editable range", absent.FailureReason);

        // Longer prefixes each name one commit: the refusal is the one that only a scope with
        // every commit resolved can produce.
        var resolved = await driver.DropAsync(scope, ["abcd1111", "abcd2222", "beef3333"]);
        Assert.False(resolved.Success);
        Assert.Contains("empty the branch", resolved.FailureReason);

        var reworded = await driver.RewordAsync(scope, "abcd", "new message");
        Assert.False(reworded.Success);
        Assert.Contains("matches more than one commit in the range", reworded.FailureReason);
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

    // ── combined plans ────────────────────────────────────────────────────

    [Fact]
    public async Task RunPlan_ReordersDropsFoldsAndRewords_InOneReplay()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b", "c", "d", "e");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 5);
        var (a, b, c, d, e) = (scope.Commits[0], scope.Commits[1], scope.Commits[2], scope.Commits[3], scope.Commits[4]);

        var plan = new RebaseTodo
        {
            Steps =
            [
                new RebaseStep(a.Sha, RebaseStepAction.Pick, "a, reworded\n\nwith a body"),
                new RebaseStep(b.Sha, RebaseStepAction.Fixup),
                new RebaseStep(c.Sha, RebaseStepAction.Drop),
                new RebaseStep(e.Sha, RebaseStepAction.Pick),
                new RebaseStep(d.Sha, RebaseStepAction.Pick)
            ]
        };

        var result = await driver.RunPlanAsync(scope, plan);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "a, reworded", "e", "d"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        // The fold's content landed in the reworded commit, the drop's did not land at all.
        Assert.Equal("b content\n", await repo.ShowAsync("HEAD~2", "b.txt"));
        Assert.Equal("a, reworded\n\nwith a body", await repo.MessageAsync("HEAD~2"));
        Assert.False(repo.Exists("c.txt"));
        _output.WriteLine("combined plan: " + string.Join(" | ", result.Todo));
    }

    [Fact]
    public async Task RunPlan_InARepoWhoseCleanupStrips_StoresTheHashSubjectTheCallerAsked()
    {
        // commit.cleanup=strip treats a `#` line as commentary: unpinned, the amend that installs
        // a reword silently stores the body line as the subject instead, on an operation whose
        // whole promise is that the history matches the preview.
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b");
        await repo.GitAsync("config", "commit.cleanup", "strip");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);
        const string message = "#123 fix the thing\n\nthe body that must stay the body";

        var plan = new RebaseTodo
        {
            Steps =
            [
                new RebaseStep(scope.Commits[0].Sha, RebaseStepAction.Pick, message),
                new RebaseStep(scope.Commits[1].Sha, RebaseStepAction.Pick)
            ]
        };

        var result = await driver.RunPlanAsync(scope, plan);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("#123 fix the thing", (await repo.GitAsync("log", "-1", "--format=%s", "HEAD~1")).Trim());
        Assert.Equal(message, await repo.MessageAsync("HEAD~1"));
    }

    [Fact]
    public async Task Reword_InARepoWhoseCleanupStrips_StoresTheHashSubjectTheCallerAsked()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b");
        await repo.GitAsync("config", "commit.cleanup", "strip");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);

        var result = await driver.RewordAsync(scope, scope.Commits[0].Sha, "#123 fix the thing");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("#123 fix the thing", (await repo.GitAsync("log", "-1", "--format=%s", "HEAD~1")).Trim());
    }

    [Fact]
    public async Task Squash_InARepoWhoseCleanupStrips_StoresTheHashSubjectTheCallerAsked()
    {
        // An all-`#` message is emptied by strip, which fails the amend exec and surfaces as a
        // rebase stop the caller would read as a conflict.
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b");
        await repo.GitAsync("config", "commit.cleanup", "strip");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 2);

        var result = await driver.SquashAsync(
            scope, [scope.Commits[0].Sha, scope.Commits[1].Sha], "#123 fix the thing");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("#123 fix the thing", (await repo.GitAsync("log", "-1", "--format=%s", "HEAD")).Trim());
    }

    [Fact]
    public async Task RunPlan_UnderRoot_ReplaysWithoutGraftingAParentOntoTheRoot()
    {
        using var repo = await SurgeryRepo.CreateAsync("root", "a", "b");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 3);
        Assert.True(scope.IncludesRoot);

        var plan = new RebaseTodo
        {
            Steps =
            [
                new RebaseStep(scope.Commits[1].Sha, RebaseStepAction.Pick, "a, first now"),
                new RebaseStep(scope.Commits[0].Sha, RebaseStepAction.Fixup),
                new RebaseStep(scope.Commits[2].Sha, RebaseStepAction.Pick)
            ]
        };

        var result = await driver.RunPlanAsync(scope, plan);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["a, first now", "b"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        var parentless = (await repo.GitAsync("rev-list", "--max-parents=0", "HEAD")).Trim();
        Assert.Equal(parentless, (await repo.GitAsync("rev-parse", "HEAD~1")).Trim());
        Assert.Equal("root content\n", repo.Read("root.txt"));
    }

    [Fact]
    public async Task RunPlan_AContradiction_IsRefusedWithoutStartingGit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b");
        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 3);
        var before = await repo.FullStateAsync();

        var plan = new RebaseTodo
        {
            Steps =
            [
                new RebaseStep(scope.Commits[0].Sha, RebaseStepAction.Pick),
                new RebaseStep(scope.Commits[1].Sha, RebaseStepAction.Drop),
                new RebaseStep(scope.Commits[2].Sha, RebaseStepAction.Fixup)
            ]
        };

        var result = await driver.RunPlanAsync(scope, plan);

        Assert.False(result.Success);
        Assert.Contains("a dropped commit cannot be a squash anchor", result.FailureReason);
        Assert.True(result.RepositoryUntouched);
        // Nothing was handed to git: no todo to audit, and the repository is byte-identical.
        Assert.Empty(result.Todo);
        Assert.Equal(before, await repo.FullStateAsync());
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
    public async Task Abort_ReportsTheUntrackedFilesTheReplayLeftBehind()
    {
        // `git rebase --abort` restores refs, index and tracked content, but not untracked files
        // written during the replay. Unreported, they become a dirty tree the next operation
        // refuses for changes the user never made.
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "a\nSHARED-ONE\n");
        await repo.CommitAllAsync("one");
        repo.Write("shared.txt", "a\nSHARED-TWO\n");
        await repo.CommitAllAsync("two");
        repo.Write("three.txt", "three content\n");
        await repo.CommitAllAsync("three");

        var driver = NewDriver();
        var scope = await driver.LoadScopeAsync(repo.Path, 3); // one, two, three
        var refsBefore = await repo.RefStateAsync();

        // "three" replays cleanly, the exec writes a file the way a hook would, then "two"
        // replayed without "one" underneath it conflicts.
        var todo = new List<string>
        {
            $"pick {scope.Commits[2].Sha} {scope.Commits[2].Subject}",
            "exec echo hook-output > written-by-a-hook.txt",
            $"pick {scope.Commits[1].Sha} {scope.Commits[1].Subject}",
            $"pick {scope.Commits[0].Sha} {scope.Commits[0].Subject}"
        };

        var result = await driver.RunTodoAsync(scope, todo, new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.True(result.Aborted);
        Assert.False(repo.RebaseInProgress);
        // Refs and tracked content are exactly as before — the scoped guarantee holds.
        Assert.Equal(refsBefore, await repo.RefStateAsync());
        Assert.Equal("a\nSHARED-TWO\n", repo.Read("shared.txt"));

        // The untracked leftover is named rather than left to be discovered by the next gate.
        Assert.True(repo.Exists("written-by-a-hook.txt"));
        Assert.Contains("written-by-a-hook.txt", result.UntrackedAdded);
        Assert.Contains("written-by-a-hook.txt", result.FailureReason);
        _output.WriteLine($"abort with a hook leftover: {result.FailureReason}");
    }

    [Fact]
    public async Task Run_SweepsScratchTreesLeftByACrash_ButKeepsTheOneAStoppedRebaseNeeds()
    {
        using var stopped = await ConflictingRepoAsync();
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");

        // A repository genuinely left mid-rebase: its scratch holds the message files the
        // stopped todo points at, so reclaiming it would break `git rebase --continue`.
        var stoppedDriver = new RebaseDriver(new GitService(), GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"));
        var stoppedScope = await stoppedDriver.LoadScopeAsync(stopped.Path, 2);
        await stoppedDriver.ReorderAsync(
            stoppedScope, [stoppedScope.Commits[1].Sha, stoppedScope.Commits[0].Sha], RebaseConflictPolicy.LeaveStopped);
        Assert.True(stopped.RebaseInProgress);

        var workRoot = Path.Combine(TestEnv.NewDir("surgery-work"), "work");
        var leaked = NewScratch(workRoot, "rebase-leaked", repo.Path, DateTime.UtcNow.AddDays(-3));
        var inUse = NewScratch(workRoot, "rebase-in-use", stopped.Path, DateTime.UtcNow.AddDays(-3));
        var recent = NewScratch(workRoot, "rebase-recent", repo.Path, DateTime.UtcNow);

        var driver = new RebaseDriver(new GitService(), GitGuard.GitExe, workRoot);
        var scope = await driver.LoadScopeAsync(repo.Path, 2);
        var result = await driver.ReorderAsync(scope, [scope.Commits[1].Sha, scope.Commits[0].Sha]);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(Directory.Exists(leaked), "a scratch whose repository is not mid-rebase was not reclaimed");
        Assert.True(Directory.Exists(inUse), "the scratch a stopped rebase still points at was reclaimed");
        Assert.True(Directory.Exists(recent), "a scratch younger than the grace period was reclaimed");

        await stopped.GitAsync("rebase", "--abort");
    }

    private static string NewScratch(string workRoot, string name, string ownerRepo, DateTime lastWriteUtc)
    {
        var dir = Path.Combine(workRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "repo-path.txt"), ownerRepo);
        Directory.SetLastWriteTimeUtc(dir, lastWriteUtc);
        return dir;
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
