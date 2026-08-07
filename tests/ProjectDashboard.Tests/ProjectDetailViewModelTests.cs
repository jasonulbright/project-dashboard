using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Busy-gate and generation semantics of the detail page against real fixture
/// repos. Long-running git ops are made deterministic with a pre-commit hook
/// that blocks until a sentinel file appears, so a project switch can be
/// interleaved at an exact point of an in-flight operation.
/// </summary>
public class ProjectDetailViewModelTests
{
    /// <summary>
    /// The discovery service is only reached through SaveManifestCommand and the
    /// GitHub service only behind a non-empty GitHubSlug (fixture repos have no
    /// remote), so both nulls keep the tests to local git operations.
    /// </summary>
    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    /// <summary>Blocks `git commit` in the pre-commit hook until the sentinel file exists (self-caps at ~20 s).</summary>
    private static void InstallBlockingPreCommitHook(TempRepo repo, string sentinelPath)
    {
        var hookDir = System.IO.Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hookDir);
        var sentinel = sentinelPath.Replace('\\', '/');
        File.WriteAllText(System.IO.Path.Combine(hookDir, "pre-commit"),
            $"#!/bin/sh\nn=0\nwhile [ ! -f '{sentinel}' ]; do\n  n=$((n+1))\n  [ $n -gt 200 ] && exit 1\n  sleep 0.1\ndone\nexit 0\n");
    }

    private static async Task<TempRepo> RepoWithStagedChangeAsync(string prefix, string file, string content)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile(file, content);
        await repo.GitAsync("add", "-A");
        return repo;
    }

    [Fact]
    public async Task SwitchMidCommit_StaleFinallyDoesNotReleaseTheNewProjectsGate()
    {
        using var repoA = await RepoWithStagedChangeAsync("gate-a", "a.txt", "a\n");
        using var repoB = await RepoWithStagedChangeAsync("gate-b", "b.txt", "b\n");
        var sentinelDir = TestEnv.NewDir("sentinels");
        var sentinelA = System.IO.Path.Combine(sentinelDir, "release-a");
        var sentinelB = System.IO.Path.Combine(sentinelDir, "release-b");
        InstallBlockingPreCommitHook(repoA, sentinelA);
        InstallBlockingPreCommitHook(repoB, sentinelB);

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.StagedFiles = [new WorkingFile { Path = "a.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "slow commit on A";

        var commitA = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); // A holds the gate; git is blocked in the hook

        // Switch to B mid-commit: the reset opens the gate for B's own work.
        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(vm.IsBusy);
        vm.StagedFiles = [new WorkingFile { Path = "b.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "draft typed on B";

        var commitB = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); // B holds the gate now

        // A finishes while B is still mid-flight. Its finally must not release
        // B's gate, its status must not surface, and its success continuation
        // must not clear B's message.
        File.WriteAllText(sentinelA, "go");
        await commitA;
        Assert.True(vm.IsBusy);
        Assert.Equal("Commit…", vm.SyncStatusText);
        Assert.Equal("draft typed on B", vm.CommitMessage);

        // B completes normally: only its own finally releases the gate.
        File.WriteAllText(sentinelB, "go");
        await commitB;
        Assert.False(vm.IsBusy);
        Assert.Equal("Commit done.", vm.SyncStatusText);
        Assert.Equal("", vm.CommitMessage);

        // The stale guard suppresses UI writes only — both commits really landed.
        Assert.Equal(2, await repoA.CommitCountAsync());
        Assert.Equal(2, await repoB.CommitCountAsync());
        Assert.Equal("draft typed on B", await repoB.HeadSubjectAsync());
    }

    [Fact]
    public async Task SwitchMidCommit_StaleCompletionTouchesNothingOnAnIdleNewProject()
    {
        using var repoA = await RepoWithStagedChangeAsync("idle-a", "a.txt", "a\n");
        using var repoB = await TempRepo.CreateWithCommitAsync("idle-b");
        var sentinelA = System.IO.Path.Combine(TestEnv.NewDir("sentinels"), "release-a");
        InstallBlockingPreCommitHook(repoA, sentinelA);

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.StagedFiles = [new WorkingFile { Path = "a.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "slow commit on A";

        var commitA = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        await vm.SetProjectAsync(ProjectFor(repoB));
        vm.CommitMessage = "draft typed on B";

        File.WriteAllText(sentinelA, "go");
        await commitA;

        // No op runs on B, so the gate stays open, the status stays reset, and
        // the draft survives A's completion.
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.SyncStatusText);
        Assert.Equal("draft typed on B", vm.CommitMessage);
        Assert.Equal(2, await repoA.CommitCountAsync());
    }

    [Fact]
    public async Task StaleLockRetry_RemovesTheLockAndRerunsTheFailedOp()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("lock-retry");
        repo.WriteFile("new.txt", "x\n");

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repo));

        var lockPath = System.IO.Path.Combine(repo.Path, ".git", "index.lock");
        File.WriteAllText(lockPath, "");

        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.True(vm.StaleLockRetryVisible);
        Assert.StartsWith("Stage all failed:", vm.SyncStatusText);
        Assert.False(vm.IsBusy);

        // Age the lock past the staleness threshold, then retry via the offer.
        var old = DateTime.UtcNow.AddMinutes(-10);
        File.SetCreationTimeUtc(lockPath, old);
        File.SetLastWriteTimeUtc(lockPath, old);

        await vm.RemoveStaleLockAndRetryCommand.ExecuteAsync(null);

        Assert.False(File.Exists(lockPath));
        Assert.False(vm.StaleLockRetryVisible);
        Assert.Equal("Stage all done.", vm.SyncStatusText);
        var state = await new GitService().GetWorkingStateAsync(repo.Path);
        Assert.Contains(state!.Staged, f => f.Path == "new.txt");
    }

    /// <summary>Bounded wait for an async UI write to land; fails loudly instead of hanging.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), "condition not reached within the wait cap");
    }

    [Fact]
    public async Task SwitchAwayAndBack_StaleCompletionRefreshesTheRepoItMutated()
    {
        using var repoA = await RepoWithStagedChangeAsync("back-a", "a.txt", "a\n");
        using var repoB = await TempRepo.CreateWithCommitAsync("back-b");
        var sentinelA = System.IO.Path.Combine(TestEnv.NewDir("sentinels"), "release-a");
        InstallBlockingPreCommitHook(repoA, sentinelA);

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.StagedFiles = [new WorkingFile { Path = "a.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "slow commit on A";

        var commitA = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        // Away and back while the commit is blocked in the hook. The switch-back
        // refresh reads the mid-commit state; wait for it to land so the stale
        // completion's refresh is provably the last writer.
        await vm.SetProjectAsync(ProjectFor(repoB));
        await vm.SetProjectAsync(ProjectFor(repoA));
        await WaitForAsync(() => vm.WorkingState is not null);
        Assert.Contains(vm.StagedFiles, f => f.Path == "a.txt");

        File.WriteAllText(sentinelA, "go");
        await commitA;

        // The commit landed in the repo now on screen, so the Changes tab must
        // describe the post-commit state, not the mid-commit snapshot — while
        // the stale suppression of gate, status, and draft still holds.
        Assert.Equal(2, await repoA.CommitCountAsync());
        Assert.Empty(vm.StagedFiles);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.SyncStatusText);
        Assert.Equal("", vm.CommitMessage);
    }

    [Fact]
    public async Task StaleLockRetry_SwitchDuringCleanup_AbandonsTheRetryAndTouchesNeitherRepo()
    {
        using var repoA = await TempRepo.CreateWithCommitAsync("abandon-a");
        using var repoB = await TempRepo.CreateWithCommitAsync("abandon-b");
        repoA.WriteFile("a-new.txt", "a\n");
        repoB.WriteFile("b-new.txt", "b\n");

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repoA));

        var lockPath = System.IO.Path.Combine(repoA.Path, ".git", "index.lock");
        File.WriteAllText(lockPath, "");
        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.True(vm.StaleLockRetryVisible);

        var old = DateTime.UtcNow.AddMinutes(-10);
        File.SetCreationTimeUtc(lockPath, old);
        File.SetLastWriteTimeUtc(lockPath, old);

        // The retry parks at the cleanup's 500 ms re-check delay on its first
        // yielding await, so the synchronous switch below lands mid-cleanup,
        // before the wrapper's generation check can run.
        var retry = vm.RemoveStaleLockAndRetryCommand.ExecuteAsync(null);
        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(retry.IsCompleted); // interleave landed inside the cleanup window

        await retry;

        // The cleanup finishes against A (the lock it was pointed at goes away),
        // but the moved generation abandons the replay: the stage-all reruns in
        // NEITHER repo — A keeps its file unstaged, B is untouched — and the
        // stale wrapper leaves B's freshly reset UI state alone.
        Assert.False(File.Exists(lockPath));
        var git = new GitService();
        var stateA = await git.GetWorkingStateAsync(repoA.Path);
        Assert.DoesNotContain(stateA!.Staged, f => f.Path == "a-new.txt");
        Assert.Contains(stateA.Unstaged, f => f.Path == "a-new.txt");
        var stateB = await git.GetWorkingStateAsync(repoB.Path);
        Assert.Empty(stateB!.Staged);
        Assert.Contains(stateB.Unstaged, f => f.Path == "b-new.txt");
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.SyncStatusText);
        Assert.False(vm.StaleLockRetryVisible);
    }

    // ── Mid-confirm project switch (two-repo probe) ──────────────────────────
    //
    // Every confirmed op names a repository in its dialog text. Reading RepoPath
    // after the dialog closes binds the op to whatever project a switch made
    // current instead — `git checkout --`, `branch -d` and `stash drop` against a
    // repository the reader never sanctioned. Both fixture repos carry the same
    // file, branch and stash names, so a wrong-repo run destroys real state rather
    // than failing on a missing ref.

    /// <summary>
    /// Answers the confirmation without WPF, running a hook first: the interleave
    /// point is the open dialog, which no headless test can otherwise reach.
    /// </summary>
    private sealed class ConfirmProbeViewModel(GitService git)
        : ProjectDetailViewModel(null!, git, null!)
    {
        public Func<Task>? WhileDialogOpen { get; set; }

        internal override async Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            if (WhileDialogOpen is not null) await WhileDialogOpen();
            return true;
        }
    }

    /// <summary>Two repos with identical names inside, so only the bound path tells them apart.</summary>
    private static async Task<TempRepo> TwinRepoAsync(string prefix, string content)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("shared.txt", content);
        await repo.CommitAllAsync("add shared.txt");
        await repo.GitAsync("branch", "feature");
        repo.WriteFile("shared.txt", content + "edited\n");
        await repo.GitAsync("stash", "push", "-m", "twin stash");
        repo.WriteFile("shared.txt", content + "edited\n");
        return repo;
    }

    private static ConfirmProbeViewModel ProbeSwitchingTo(TempRepo target)
    {
        var vm = new ConfirmProbeViewModel(new GitService());
        vm.WhileDialogOpen = () => vm.SetProjectAsync(ProjectFor(target));
        return vm;
    }

    /// <summary>The switched-to project is left idle: no busy gate, no status, no retry offer.</summary>
    private static void AssertNothingAttributed(ProjectDetailViewModel vm)
    {
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.SyncStatusText);
        Assert.False(vm.StaleLockRetryVisible);
    }

    [Fact]
    public async Task DiscardMidConfirm_SwitchingProjects_DiscardsInNeitherRepo()
    {
        using var repoA = await TwinRepoAsync("discard-a", "a\n");
        using var repoB = await TwinRepoAsync("discard-b", "b\n");

        var vm = ProbeSwitchingTo(repoB);
        await vm.SetProjectAsync(ProjectFor(repoA));

        await vm.DiscardFileCommand.ExecuteAsync(
            new WorkingFile { Path = "shared.txt", WorktreeStatus = 'M' });

        // The confirmation was given for A and the generation moved, so the op is
        // suppressed: B keeps the edit it never offered up, and A keeps its own.
        Assert.Equal("b\nedited\n", repoB.ReadFile("shared.txt"));
        Assert.Equal("a\nedited\n", repoA.ReadFile("shared.txt"));
        AssertNothingAttributed(vm);
    }

    [Fact]
    public async Task DeleteBranchMidConfirm_SwitchingProjects_DeletesInNeitherRepo()
    {
        using var repoA = await TwinRepoAsync("branch-a", "a\n");
        using var repoB = await TwinRepoAsync("branch-b", "b\n");

        var vm = ProbeSwitchingTo(repoB);
        await vm.SetProjectAsync(ProjectFor(repoA));

        await vm.DeleteBranchCommand.ExecuteAsync(new BranchInfo { Name = "feature" });

        Assert.Contains("feature", await repoB.GitAsync("branch", "--list", "feature"));
        Assert.Contains("feature", await repoA.GitAsync("branch", "--list", "feature"));
        AssertNothingAttributed(vm);
    }

    [Fact]
    public async Task StashDropMidConfirm_SwitchingProjects_DropsInNeitherRepo()
    {
        using var repoA = await TwinRepoAsync("stash-a", "a\n");
        using var repoB = await TwinRepoAsync("stash-b", "b\n");

        var vm = ProbeSwitchingTo(repoB);
        await vm.SetProjectAsync(ProjectFor(repoA));

        await vm.StashDropCommand.ExecuteAsync(
            new StashEntry { Ref = "stash@{0}", Subject = "twin stash" });

        var git = new GitService();
        Assert.Single(await git.GetStashesAsync(repoB.Path));
        Assert.Single(await git.GetStashesAsync(repoA.Path));
        AssertNothingAttributed(vm);
    }

    [Fact]
    public async Task DiscardConfirmed_WithoutSwitch_StillDiscards()
    {
        using var repo = await TwinRepoAsync("discard-plain", "a\n");

        var vm = new ConfirmProbeViewModel(new GitService());
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DiscardFileCommand.ExecuteAsync(
            new WorkingFile { Path = "shared.txt", WorktreeStatus = 'M' });

        Assert.Equal("a\n", repo.ReadFile("shared.txt"));
        Assert.Equal("Discard done.", vm.SyncStatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Commit_WithoutSwitch_ClearsMessageAndReloadsCommits()
    {
        using var repo = await RepoWithStagedChangeAsync("plain", "work.txt", "work\n");

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.StagedFiles = [new WorkingFile { Path = "work.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "second commit";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
        Assert.Equal("Commit done.", vm.SyncStatusText);
        Assert.Equal("", vm.CommitMessage);
        Assert.Equal(2, vm.Commits.Count);
        Assert.Equal("second commit", await repo.HeadSubjectAsync());
    }
}
