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

    /// <summary>
    /// Blocks `git commit` in the pre-commit hook until the sentinel file exists.
    /// The iteration cap only breaks a hang in an already-broken test — it is not a
    /// budget the passing path spends, so it sits far above any load-induced delay.
    /// </summary>
    private static void InstallBlockingPreCommitHook(TempRepo repo, string sentinelPath)
    {
        const int hangBreakerTenths = 3000; // 5 minutes
        var hookDir = System.IO.Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hookDir);
        var sentinel = sentinelPath.Replace('\\', '/');
        File.WriteAllText(System.IO.Path.Combine(hookDir, "pre-commit"),
            $"#!/bin/sh\nn=0\nwhile [ ! -f '{sentinel}' ]; do\n  n=$((n+1))\n  [ $n -gt {hangBreakerTenths} ] && exit 1\n  sleep 0.1\ndone\nexit 0\n");
    }

    private static async Task<TempRepo> RepoWithStagedChangeAsync(string prefix, string file, string content)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile(file, content);
        await repo.GitAsync("add", "-A");
        return repo;
    }

    /// <summary>The page seeds its History list from the project's cached commits, so a selection test has to supply them.</summary>
    private static async Task<ProjectInfo> ProjectWithHistoryAsync(TempRepo repo)
    {
        var project = ProjectFor(repo);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path, 50);
        return project;
    }

    /// <summary>
    /// Surgery runs FROM the History selection, and every reload replaces every GitCommit object,
    /// so a selection kept by reference is lost by the reload that follows any mutating operation.
    /// </summary>
    [Fact]
    public async Task ReloadAfterAMutatingOp_KeepsTheHistorySelectionBySha()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("sel-keep");
        repo.WriteFile("second.txt", "second\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "second");

        var vm = NewVm();
        await vm.SetProjectAsync(await ProjectWithHistoryAsync(repo));
        var older = vm.Commits.Last();
        var olderSha = older.Ref;
        vm.SelectedCommit = older;

        repo.WriteFile("third.txt", "third\n");
        await vm.StageAllCommand.ExecuteAsync(null);
        vm.CommitMessage = "third";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Commits.Count);
        Assert.NotNull(vm.SelectedCommit);
        Assert.Equal(olderSha, vm.SelectedCommit.Ref);
        // The reload really did rebuild the list, so this is a re-selection, not a survivor.
        Assert.False(ReferenceEquals(older, vm.SelectedCommit));
    }

    [Fact]
    public async Task ReloadDropsTheSelectionWhenTheSelectedCommitNoLongerExists()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("sel-drop");
        repo.WriteFile("second.txt", "second\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "second");

        var vm = NewVm();
        await vm.SetProjectAsync(await ProjectWithHistoryAsync(repo));
        vm.SelectedCommit = vm.Commits.First();

        // Amending replaces the tip, so the selected sha is not in the reloaded history.
        repo.WriteFile("second.txt", "second edited\n");
        await repo.GitAsync("add", "-A");
        vm.AmendMode = true;
        vm.CommitMessage = "second amended";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedCommit);
    }

    /// <summary>
    /// A click that lands while the history read is in flight is the user's current intent, so
    /// the sha captured before the read must not be restored over it.
    /// </summary>
    [Fact]
    public async Task AClickDuringTheReload_SurvivesTheReSelection()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("sel-race");
        repo.WriteFile("second.txt", "second\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "second");

        var git = new SelectsDuringHistoryRead();
        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(await ProjectWithHistoryAsync(repo));
        var oldest = vm.Commits.Last();
        var tip = vm.Commits.First();
        vm.SelectedCommit = oldest;

        // The click lands inside the reload's own `git log`, after the pre-await capture.
        git.OnHistoryRead = () => vm.SelectedCommit = tip;

        repo.WriteFile("third.txt", "third\n");
        await vm.StageAllCommand.ExecuteAsync(null);
        vm.CommitMessage = "third";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedCommit);
        Assert.Equal(tip.Ref, vm.SelectedCommit.Ref);
        Assert.False(ReferenceEquals(tip, vm.SelectedCommit));
    }

    /// <summary>Fires once, during the `git log -n 50` the History reload issues and nothing else does.</summary>
    private sealed class SelectsDuringHistoryRead : GitService
    {
        private int _fired;

        public Action? OnHistoryRead { get; set; }

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            if (argv is ["log", ..] && argv.Contains("50") && Interlocked.Exchange(ref _fired, 1) == 0)
                OnHistoryRead?.Invoke();
            return base.RunAsync(repoPath, argv, environment, ct, timeout);
        }
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
        // refresh reads the mid-commit state; awaiting that refresh itself — not a
        // timed poll for what it writes — is what makes the stale completion's own
        // refresh provably the last writer.
        await vm.SetProjectAsync(ProjectFor(repoB));
        await vm.SetProjectAsync(ProjectFor(repoA));
        await vm.WorkingStateRefresh;
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

    /// <summary>
    /// The switched-to project is left idle — no busy gate, no retry offer — and the
    /// dropped op says so rather than vanishing: the reader sanctioned a destructive
    /// op and is owed the news that it did not run.
    /// </summary>
    private static void AssertSuppressedWithNotice(ProjectDetailViewModel vm, string notice)
    {
        Assert.False(vm.IsBusy);
        Assert.Equal(notice, vm.SyncStatusText);
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
        AssertSuppressedWithNotice(vm,
            "Discard cancelled — the project changed while the dialog was open.");
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
        AssertSuppressedWithNotice(vm,
            "Branch delete cancelled — the project changed while the dialog was open.");
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
        AssertSuppressedWithNotice(vm,
            "Stash drop cancelled — the project changed while the dialog was open.");
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
