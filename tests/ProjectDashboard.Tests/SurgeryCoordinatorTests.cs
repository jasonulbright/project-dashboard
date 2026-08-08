using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Surgery;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The gated surgery entry point plus commit injection, reset, revert, and cherry-pick. Backups and the journal
/// live under AppPaths, so these join the serialized app-data sandbox collection.
/// </summary>
[Collection("app-data-sandbox")]
public class SurgeryCoordinatorTests
{
    private readonly ITestOutputHelper _output;

    public SurgeryCoordinatorTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static SurgeryCoordinator NewCoordinator(RepoBusyRegistry? busy = null)
    {
        var git = new GitService();
        var driver = new RebaseDriver(git, GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"));
        return new SurgeryCoordinator(
            new BackupService(git, new SettingsService()),
            busy ?? new RepoBusyRegistry(),
            git,
            driver);
    }

    // ── the gates ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reorder_DirtyWorkingTree_RefusesBeforeAnyBackupIsTaken()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var before = await repo.RefStateAsync();
        repo.Write("alpha.txt", "uncommitted edit\n");

        var shas = await repo.RangeShasAsync(2);
        var result = await NewCoordinator().ReorderAsync(repo.Path, 2, [shas[1], shas[0]]);

        Assert.False(result.Success);
        Assert.Contains("uncommitted", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alpha.txt", result.FailureReason);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Empty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(repo.Path));
        Assert.Equal("uncommitted edit\n", repo.Read("alpha.txt"));
    }

    [Fact]
    public async Task Reorder_RepoBusy_RefusesWithoutTouchingTheRepository()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var before = await repo.RefStateAsync();
        var busy = new RepoBusyRegistry();
        var coordinator = NewCoordinator(busy);
        var shas = await repo.RangeShasAsync(2);

        using (busy.Acquire(repo.Path))
        {
            var result = await coordinator.ReorderAsync(repo.Path, 2, [shas[1], shas[0]]);
            Assert.False(result.Success);
            Assert.Contains("busy", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, await repo.RefStateAsync());
        // The lease is released in a finally, so the next operation is admitted.
        Assert.False(busy.IsBusy(repo.Path));
    }

    [Fact]
    public async Task Reorder_RepoAlreadyMidRebase_IsRefused()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "a\nSHARED-ONE\n");
        await repo.CommitAllAsync("one");
        repo.Write("shared.txt", "a\nSHARED-TWO\n");
        await repo.CommitAllAsync("two");

        var driver = new RebaseDriver(new GitService(), GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"));
        var scope = await driver.LoadScopeAsync(repo.Path, 2);
        await driver.ReorderAsync(scope, [scope.Commits[1].Sha, scope.Commits[0].Sha], RebaseConflictPolicy.LeaveStopped);
        Assert.True(repo.RebaseInProgress);

        var result = await NewCoordinator().ReorderAsync(repo.Path, 2, [scope.Commits[1].Sha, scope.Commits[0].Sha]);

        Assert.False(result.Success);
        Assert.Contains("rebase", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        await repo.GitAsync("rebase", "--abort");
    }

    // ── rebase through the rails, with undo ───────────────────────────────

    [Fact]
    public async Task Reorder_ThroughTheRails_SucceedsThenUndoRestoresRefsExactly()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var before = await repo.RefStateAsync();
        var shas = await repo.RangeShasAsync(2);

        var result = await NewCoordinator().ReorderAsync(repo.Path, 2, [shas[1], shas[0]]);

        Assert.True(result.Success, result.FailureReason);
        Assert.NotNull(result.Undo);
        Assert.Equal(["seed", "beta", "alpha"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        Assert.NotEqual(before, await repo.RefStateAsync());
        // Success clears the journal.
        Assert.Null(await new RewriteJournal().ReadPendingAsync());

        var restore = await result.Undo!.RestoreAsync();
        Assert.True(restore.Success, restore.Message);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Equal(["seed", "alpha", "beta"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        _output.WriteLine($"reorder round-trip: refs byte-identical after undo\n{before}");
    }

    [Fact]
    public async Task Reorder_ThatConflicts_AbortsAndKeepsTheBackupButClearsTheJournal()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "a\nSHARED-ONE\n");
        await repo.CommitAllAsync("one");
        repo.Write("shared.txt", "a\nSHARED-TWO\n");
        await repo.CommitAllAsync("two");

        var stateBefore = await repo.FullStateAsync();
        var shas = await repo.RangeShasAsync(2);

        var result = await NewCoordinator().ReorderAsync(repo.Path, 2, [shas[1], shas[0]]);

        Assert.False(result.Success);
        Assert.True(result.Rebase!.Aborted);
        Assert.Equal(shas[1], result.Rebase.ConflictCommit);
        Assert.Equal(stateBefore, await repo.FullStateAsync());

        // The backup survives the refusal, so undo still works. The recovery marker does NOT:
        // the abort proved nothing moved, and a marker for an operation that never landed would
        // offer a restore at the next launch for history that was never rewritten.
        Assert.NotNull(result.Undo);
        Assert.NotEmpty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(repo.Path));
        Assert.Null(await new RewriteJournal().ReadPendingAsync());
    }

    [Fact]
    public async Task Drop_ByTheDisplayShaOfARepoWithAShorterCoreAbbrev_StillResolves()
    {
        // core.abbrev bottoms out at four characters and every short sha the UI hands back is a
        // %h read, so a range index that only knows seven-character prefixes refuses commits that
        // are in it — after a backup and a journal write.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        await repo.GitAsync("config", "core.abbrev", "5");
        var shortSha = (await repo.GitAsync("log", "-1", "--format=%h", "HEAD~1")).Trim();
        Assert.True(shortSha.Length < 7, $"expected an abbreviation shorter than seven, got '{shortSha}'");

        var result = await NewCoordinator().DropAsync(repo.Path, 2, [shortSha]);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["beta", "seed"], await repo.SubjectsAsync());
        _output.WriteLine($"core.abbrev=5 yielded '{shortSha}' ({shortSha.Length} chars); the drop resolved it");
    }

    [Fact]
    public async Task Squash_RefusedByValidation_LeavesNoRecoveryMarker()
    {
        // A driver refusal never reaches git, so a pending marker would train the user to
        // dismiss the prompt that matters.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta", "gamma");
        var before = await repo.FullStateAsync();
        var shas = await repo.RangeShasAsync(4);

        var result = await NewCoordinator().SquashAsync(repo.Path, 3, [shas[1], shas[3]]);

        Assert.False(result.Success);
        Assert.Contains("contiguous", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await repo.FullStateAsync());
        Assert.Null(await new RewriteJournal().ReadPendingAsync());
    }

    [Fact]
    public async Task Reorder_ThatConflicts_UnderLeaveStopped_KeepsTheRecoveryMarker()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "a\nSHARED-ONE\n");
        await repo.CommitAllAsync("one");
        repo.Write("shared.txt", "a\nSHARED-TWO\n");
        await repo.CommitAllAsync("two");
        var shas = await repo.RangeShasAsync(2);

        var result = await NewCoordinator().ReorderAsync(
            repo.Path, 2, [shas[1], shas[0]], RebaseConflictPolicy.LeaveStopped);

        Assert.False(result.Success);
        Assert.True(result.Rebase!.LeftStopped);
        // The repository really is mid-rebase, so the marker is what makes it recoverable.
        var pending = await new RewriteJournal().ReadPendingAsync();
        Assert.NotNull(pending);
        Assert.Equal(repo.Path, pending!.RepoPath);
        Assert.Equal("rebase", pending.Phase);

        await repo.GitAsync("rebase", "--abort");
        await new RewriteJournal().ClearAllAsync();
    }

    // ── amend a fix into an older commit ─────────────────────────────────

    [Fact]
    public async Task InjectStaged_IntoAnOlderCommit_LandsInThatCommitAndLeavesLaterOnesIntact()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta", "gamma");
        var shas = await repo.RangeShasAsync(4); // oldest first: seed, alpha, beta, gamma
        var target = shas[1];                    // "alpha"
        var targetMessage = await repo.MessageAsync(target);
        var headTreeBefore = await repo.TreeAsync();

        // The fix belongs in alpha's commit, not on the tip.
        repo.Write("alpha.txt", "alpha content\nthe fix\n");
        await repo.GitAsync("add", "-A");

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, target);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["seed", "alpha", "beta", "gamma"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());

        var after = await repo.RangeShasAsync(4);
        Assert.Equal(shas[0], after[0]);      // seed untouched
        Assert.NotEqual(shas[1], after[1]);   // alpha rewritten

        // The fix is IN alpha's commit, its message is unchanged, and every later commit still
        // carries exactly its own content.
        Assert.Equal("alpha content\nthe fix\n", await repo.ShowAsync(after[1], "alpha.txt"));
        Assert.Equal(targetMessage, await repo.MessageAsync(after[1]));
        Assert.Equal("beta content\n", await repo.ShowAsync(after[2], "beta.txt"));
        Assert.Equal("gamma content\n", await repo.ShowAsync(after[3], "gamma.txt"));
        Assert.Empty(await repo.StatusAsync());
        // The end state gained exactly the fix.
        Assert.NotEqual(headTreeBefore, await repo.TreeAsync());
        Assert.Equal("alpha content\nthe fix\n", repo.Read("alpha.txt"));
        _output.WriteLine($"injection: fix landed in {after[1][..8]} (\"{targetMessage}\"); beta and gamma content unchanged");
    }

    [Fact]
    public async Task InjectStaged_IntoTheRootCommit_UsesTheRootRebase()
    {
        using var repo = await SurgeryRepo.CreateAsync("root", "alpha");
        var shas = await repo.RangeShasAsync(2);
        var root = shas[0];

        repo.Write("root.txt", "root content\nroot fix\n");
        await repo.GitAsync("add", "-A");

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, root);

        Assert.True(result.Success, result.FailureReason);
        var after = await repo.RangeShasAsync(2);
        Assert.Equal("root content\nroot fix\n", await repo.ShowAsync(after[0], "root.txt"));
        Assert.Equal("root", await repo.MessageAsync(after[0]));
        Assert.Equal(["root", "alpha"], (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        // Still the only parentless commit: the --root replay did not graft a parent onto it.
        Assert.Equal(after[0], (await repo.GitAsync("rev-list", "--max-parents=0", "HEAD")).Trim());
    }

    [Fact]
    public async Task InjectStaged_WithNothingStaged_IsRefusedByTheTreeGate()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var shas = await repo.RangeShasAsync(2);
        var before = await repo.RefStateAsync();

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, shas[0]);

        Assert.False(result.Success);
        Assert.Contains("staged", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Empty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(repo.Path));
    }

    [Fact]
    public async Task InjectStaged_WithUnstagedChangesAlongside_IsRefused()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var shas = await repo.RangeShasAsync(2);
        repo.Write("alpha.txt", "alpha content\nstaged fix\n");
        await repo.GitAsync("add", "-A");
        repo.Write("seed.txt", "unstaged noise\n");
        var before = await repo.RefStateAsync();

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, shas[0]);

        Assert.False(result.Success);
        Assert.Contains("unstaged", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seed.txt", result.FailureReason);
        Assert.Equal(before, await repo.RefStateAsync());
    }

    [Fact]
    public async Task InjectStaged_WithTheUsersOwnFixupCommitInRange_FoldsOnlyItsOwn()
    {
        // A `fixup!` the user made and has not folded yet is theirs to fold when they choose.
        // Only the commit this operation creates may be folded; the rest replay as ordinary
        // commits, whatever their subjects say.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        repo.Write("extra.txt", "extra content\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "--no-verify", "-m", "fixup! alpha");
        repo.Write("gamma.txt", "gamma content\n");
        await repo.CommitAllAsync("gamma");

        var shas = await repo.RangeShasAsync(5); // oldest first: seed, alpha, beta, fixup! alpha, gamma
        var target = shas[1];
        var countBefore = (await repo.ShasAsync()).Count;
        Assert.Equal(5, countBefore);

        repo.Write("alpha.txt", "alpha content\nthe fix\n");
        await repo.GitAsync("add", "-A");

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, target);

        Assert.True(result.Success, result.FailureReason);
        // The user's fixup! is still its own commit, in its own place.
        Assert.Equal(["seed", "alpha", "beta", "fixup! alpha", "gamma"],
            (await repo.SubjectsAsync()).AsEnumerable().Reverse());
        Assert.Equal(countBefore, (await repo.ShasAsync()).Count);

        var after = await repo.RangeShasAsync(5);
        // The injection landed in alpha, whose message is untouched.
        Assert.Equal("alpha content\nthe fix\n", await repo.ShowAsync(after[1], "alpha.txt"));
        Assert.Equal("alpha", await repo.MessageAsync(after[1]));
        // The user's fixup! still carries its own content, unfolded into anything.
        Assert.Equal("extra content\n", await repo.ShowAsync(after[3], "extra.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ShowAsync(after[1], "extra.txt"));
        _output.WriteLine($"injection with a pre-existing fixup! in range: {countBefore} commits before, " +
                          $"{(await repo.ShasAsync()).Count} after; subjects {string.Join(" -> ", (await repo.SubjectsAsync()).AsEnumerable().Reverse())}");
    }

    [Fact]
    public async Task InjectStaged_WithTheUsersOwnSquashCommitInRange_DoesNotRewriteTheTargetsMessage()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        repo.Write("extra.txt", "extra content\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "--no-verify", "-m", "squash! alpha\n\nbody the squash would graft on");

        var shas = await repo.RangeShasAsync(4);
        var target = shas[1];
        var targetMessage = await repo.MessageAsync(target);

        repo.Write("alpha.txt", "alpha content\nthe fix\n");
        await repo.GitAsync("add", "-A");

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, target);

        Assert.True(result.Success, result.FailureReason);
        var after = await repo.RangeShasAsync(4);
        Assert.Equal(targetMessage, await repo.MessageAsync(after[1]));
        Assert.DoesNotContain("body the squash would graft on", await repo.MessageAsync(after[1]));
        Assert.Equal(["seed", "alpha", "beta", "squash! alpha"],
            (await repo.SubjectsAsync()).AsEnumerable().Reverse());
    }

    [Fact]
    public async Task InjectStaged_CalledDirectly_RefusesUnstagedChangesWithoutRecordingAFixup()
    {
        // The rule is documented on the method, so it is enforced by the method: a direct call
        // must not record a fixup holding half the intended change.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var shas = await repo.RangeShasAsync(2);
        repo.Write("alpha.txt", "alpha content\nstaged fix\n");
        await repo.GitAsync("add", "-A");
        repo.Write("seed.txt", "seed content\nunstaged noise\n");
        var before = await repo.FullStateAsync();

        var git = new GitService();
        var driver = new RebaseDriver(git, GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"));
        var result = await new CommitSurgery(git, driver).InjectStagedIntoAsync(repo.Path, shas[0]);

        Assert.False(result.Success);
        Assert.Contains("unstaged", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.RepositoryUntouched);
        Assert.Equal(before, await repo.FullStateAsync());
        Assert.Equal(2, (await repo.ShasAsync()).Count);
    }

    [Fact]
    public async Task InjectStaged_WithAMergeInTheReplayRange_RefusesAndUnwindsTheFixup()
    {
        // Replaying a merge without --rebase-merges would flatten it. The refusal comes after the
        // fixup commit exists, so the fix has to end up staged again exactly as it was.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var target = await repo.HeadAsync();
        await repo.GitAsync("switch", "-q", "-c", "side");
        repo.Write("side.txt", "side content\n");
        await repo.CommitAllAsync("side-one");
        await repo.GitAsync("switch", "-q", "main");
        repo.Write("main.txt", "main content\n");
        await repo.CommitAllAsync("main-two");
        await repo.GitAsync("merge", "-q", "--no-ff", "side", "-m", "merge side");

        var countBefore = (await repo.ShasAsync()).Count;
        repo.Write("alpha.txt", "alpha content\nthe fix\n");
        await repo.GitAsync("add", "-A");
        var statusBefore = await repo.StatusAsync();

        var result = await NewCoordinator().InjectStagedIntoAsync(repo.Path, target);

        Assert.False(result.Success);
        Assert.Contains("merge", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        // No fixup commit survives, and the fix is staged again as the caller had it.
        Assert.Equal(countBefore, (await repo.ShasAsync()).Count);
        Assert.Equal(statusBefore, await repo.StatusAsync());
        Assert.Equal("alpha content\nthe fix\n", repo.Read("alpha.txt"));
    }

    // ── reset ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_Soft_MovesHeadButKeepsIndexAndWorktree()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var before = await repo.RefStateAsync();
        var shas = await repo.RangeShasAsync(3);

        var result = await NewCoordinator().ResetAsync(repo.Path, shas[1], ResetMode.Soft);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(shas[1], await repo.HeadAsync());
        // beta.txt is still on disk and still staged.
        Assert.Equal("beta content\n", repo.Read("beta.txt"));
        Assert.Contains("A  beta.txt", await repo.StatusAsync());
        // The commit left the branch, so the rails applied: a backup and a working undo.
        Assert.NotEmpty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(repo.Path));
        Assert.NotNull(result.Undo);
        var restore = await result.Undo!.RestoreAsync();
        Assert.True(restore.Success, restore.Message);
        Assert.Equal(before, await repo.RefStateAsync());
    }

    [Fact]
    public async Task Reset_Mixed_ToAnEarlierCommit_BacksUpAndUndoRestoresTheDroppedCommits()
    {
        // A mixed reset moves the branch off commits exactly as a hard one does; only the index
        // and worktree differ. Without a backup those commits would be reachable through the
        // reflog alone, which nothing in the app surfaces.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var before = await repo.RefStateAsync();
        var subjectsBefore = await repo.SubjectsAsync();
        var shas = await repo.RangeShasAsync(3);

        var result = await NewCoordinator().ResetAsync(repo.Path, shas[0], ResetMode.Mixed);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(shas[0], await repo.HeadAsync());
        Assert.Equal(["seed"], await repo.SubjectsAsync());
        Assert.Equal("beta content\n", repo.Read("beta.txt"));
        Assert.Contains("?? beta.txt", await repo.StatusAsync());

        Assert.NotEmpty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(repo.Path));
        Assert.NotNull(result.Undo);
        Assert.Null(await new RewriteJournal().ReadPendingAsync());

        var restore = await result.Undo!.RestoreAsync();
        Assert.True(restore.Success, restore.Message);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Equal(subjectsBefore, await repo.SubjectsAsync());
        _output.WriteLine($"mixed reset round-trip: {subjectsBefore.Count} commit(s) restored, refs byte-identical after undo\n{before}");
    }

    [Fact]
    public async Task Reset_Mixed_ToASiblingBranchTip_IsAlsoBackedUp()
    {
        // The target is an arbitrary revision, so a reset can replace history wholesale rather
        // than only rewind it.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        await repo.GitAsync("switch", "-q", "-c", "sideline", "HEAD~1");
        repo.Write("side.txt", "side content\n");
        await repo.CommitAllAsync("side-work");
        var sideline = await repo.HeadAsync();
        await repo.GitAsync("switch", "-q", "main");
        var before = await repo.RefStateAsync();

        var result = await NewCoordinator().ResetAsync(repo.Path, sideline, ResetMode.Mixed);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["side-work", "seed"], await repo.SubjectsAsync());
        Assert.NotNull(result.Undo);

        var restore = await result.Undo!.RestoreAsync();
        Assert.True(restore.Success, restore.Message);
        Assert.Equal(before, await repo.RefStateAsync());
    }

    [Fact]
    public async Task Reset_Hard_DiscardsTheCommitThroughTheRails_AndUndoRestoresIt()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var before = await repo.RefStateAsync();
        var shas = await repo.RangeShasAsync(3);

        var result = await NewCoordinator().ResetAsync(repo.Path, shas[1], ResetMode.Hard);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(shas[1], await repo.HeadAsync());
        Assert.False(repo.Exists("beta.txt"));
        Assert.Empty(await repo.StatusAsync());
        // A hard reset destroys work, so it takes the full rails: a verified backup and a
        // journal entry cleared on success.
        Assert.NotNull(result.Undo);
        Assert.NotEmpty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(repo.Path));
        Assert.Null(await new RewriteJournal().ReadPendingAsync());

        var restore = await result.Undo!.RestoreAsync();
        Assert.True(restore.Success, restore.Message);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Equal("beta content\n", repo.Read("beta.txt"));
        _output.WriteLine($"hard reset round-trip: refs byte-identical after undo\n{before}");
    }

    [Fact]
    public async Task Reset_Hard_OnADirtyTree_IsRefused()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var shas = await repo.RangeShasAsync(2);
        repo.Write("alpha.txt", "uncommitted\n");

        var result = await NewCoordinator().ResetAsync(repo.Path, shas[0], ResetMode.Hard);

        Assert.False(result.Success);
        Assert.Contains("uncommitted", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("uncommitted\n", repo.Read("alpha.txt"));
    }

    // ── revert and cherry-pick ────────────────────────────────────────────

    [Fact]
    public async Task Revert_CleanRevert_AddsTheInverseCommit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var shas = await repo.RangeShasAsync(3);

        var result = await NewCoordinator().RevertAsync(repo.Path, shas[2]);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(repo.Exists("beta.txt"));
        Assert.Equal(4, (await repo.ShasAsync()).Count);
        Assert.Contains("Revert", await repo.MessageAsync("HEAD"));
        Assert.NotNull(result.Undo);
        Assert.Null(await new RewriteJournal().ReadPendingAsync());
    }

    [Fact]
    public async Task Revert_WithoutAutoCommit_ReportsThatTheRepositoryIsLeftMidRevert()
    {
        // `revert --no-commit` succeeds but leaves REVERT_HEAD, which every later gated
        // operation refuses. A bare success plus a cleared marker would hide that.
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var shas = await repo.RangeShasAsync(3);

        var result = await NewCoordinator().RevertAsync(repo.Path, shas[2], autoCommit: false);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(result.Edit!.LeftMidOperation);
        Assert.Contains("mid-revert", result.Advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Edit.Advisory, result.Advisory);

        // The repository really does read as mid-revert, so the next operation is refused.
        var state = await new GitService().GetWorkingStateAsync(repo.Path);
        Assert.Equal(ProjectDashboard.Models.RepoActivity.Reverting, state!.Activity);
        var next = await NewCoordinator().ResetAsync(repo.Path, shas[1], ResetMode.Soft);
        Assert.False(next.Success);
        Assert.Contains("revert", next.FailureReason, StringComparison.OrdinalIgnoreCase);

        // The marker survives the "success", because the operation is not concluded.
        var pending = await new RewriteJournal().ReadPendingAsync();
        Assert.NotNull(pending);
        Assert.Equal("revert", pending!.Phase);

        await repo.GitAsync("revert", "--quit");
        await new RewriteJournal().ClearAllAsync();
    }

    [Fact]
    public async Task Revert_ThatConflicts_LeavesTheRepositoryMidRevertWithTheJournalPending()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "a\nSHARED-ONE\n");
        await repo.CommitAllAsync("one");
        repo.Write("shared.txt", "a\nSHARED-TWO\n");
        await repo.CommitAllAsync("two");
        var shas = await repo.RangeShasAsync(3);

        // Reverting "one" means restoring a line "two" already rewrote.
        var result = await NewCoordinator().RevertAsync(repo.Path, shas[1]);

        Assert.False(result.Success);
        Assert.True(result.Edit!.Conflicted);
        Assert.Contains("shared.txt", result.Edit.ConflictPaths);
        Assert.Contains("terminal", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Deliberately NOT auto-aborted: the repo stays mid-revert and the existing state
        // detection reports it, which drives the banner and Open in Terminal.
        var state = await new GitService().GetWorkingStateAsync(repo.Path);
        Assert.Equal(ProjectDashboard.Models.RepoActivity.Reverting, state!.Activity);
        Assert.True(state.HasConflicts);

        // Backup and journal remain, so undo is still available.
        Assert.NotNull(result.Undo);
        var pending = await new RewriteJournal().ReadPendingAsync();
        Assert.NotNull(pending);
        Assert.Equal("revert", pending!.Phase);

        await repo.GitAsync("revert", "--abort");
        await new RewriteJournal().ClearAllAsync();
        _output.WriteLine($"revert conflict left mid-revert on {string.Join(", ", result.Edit.ConflictPaths)}; journal pending for undo");
    }

    [Fact]
    public async Task CherryPick_OntoAnotherBranch_AppliesTheCommit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        repo.Write("feature.txt", "feature content\n");
        await repo.CommitAllAsync("feature-work");
        var pick = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.GitAsync("switch", "-q", "main");
        // main has to move on, or the replayed commit would be byte-identical to the original
        // and prove nothing about the cherry-pick.
        repo.Write("main-extra.txt", "main extra\n");
        await repo.CommitAllAsync("main-extra");

        var result = await NewCoordinator().CherryPickAsync(repo.Path, [pick]);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("feature content\n", repo.Read("feature.txt"));
        Assert.Equal("feature-work", (await repo.SubjectsAsync())[0]);
        Assert.NotEqual(pick, await repo.HeadAsync());
        // The pick landed on top of main's own work, not on the feature branch.
        Assert.Equal("main extra\n", repo.Read("main-extra.txt"));
        Assert.NotNull(result.Undo);
    }

    [Fact]
    public async Task CherryPick_ThatConflicts_LeavesTheRepositoryMidCherryPick()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "base\n");
        await repo.CommitAllAsync("base-line");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        repo.Write("shared.txt", "feature version\n");
        await repo.CommitAllAsync("feature-edit");
        var pick = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.GitAsync("switch", "-q", "main");
        repo.Write("shared.txt", "main version\n");
        await repo.CommitAllAsync("main-edit");

        var result = await NewCoordinator().CherryPickAsync(repo.Path, [pick]);

        Assert.False(result.Success);
        Assert.True(result.Edit!.Conflicted);
        Assert.Contains("shared.txt", result.Edit.ConflictPaths);

        var state = await new GitService().GetWorkingStateAsync(repo.Path);
        Assert.Equal(ProjectDashboard.Models.RepoActivity.CherryPicking, state!.Activity);
        Assert.NotNull(result.Undo);

        await repo.GitAsync("cherry-pick", "--abort");
        await new RewriteJournal().ClearAllAsync();
    }
}
