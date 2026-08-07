using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The History tab's commit-surgery surface: which commands a precondition disables and why,
/// what a confirmation says before anything runs, and what a failure and an undo report.
/// Backups and the journal live under AppPaths, so these join the serialized app-data sandbox.
/// The service layer has its own deep coverage; only two of these drive a real rebase.
/// </summary>
[Collection("app-data-sandbox")]
public class ProjectDetailViewModelSurgeryTests
{
    public ProjectDetailViewModelSurgeryTests() => TestSandbox.ResetDataDir();

    private static SurgeryCoordinator NewCoordinator()
    {
        var git = new GitService();
        var driver = new RebaseDriver(git, GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"));
        return new SurgeryCoordinator(
            new BackupService(git, new SettingsService()), new RepoBusyRegistry(), git, driver);
    }

    /// <summary>
    /// The discovery and GitHub services are unreachable from these paths (no manifest save,
    /// no remote on a fixture repo), so both nulls keep the test to local git.
    /// </summary>
    private static async Task<ProjectDetailViewModel> VmForAsync(SurgeryRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        var commits = await new GitService().GetRecentCommitsAsync(repo.Path, 50);
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!) { Surgery = NewCoordinator() };
        await vm.SetProjectAsync(new ProjectInfo
        {
            DirectoryName = name,
            DisplayName = name,
            FullPath = repo.Path,
            RecentCommits = commits
        });
        await vm.RefreshWorkingStateAsync();
        return vm;
    }

    private static List<SurgeryConfirmation> CaptureConfirmations(ProjectDetailViewModel vm, bool answer)
    {
        var seen = new List<SurgeryConfirmation>();
        vm.ConfirmSurgeryAsync = confirmation =>
        {
            seen.Add(confirmation);
            return Task.FromResult(answer);
        };
        return seen;
    }

    // ── preconditions are disabled state, not failure ──────────────────────

    [Fact]
    public async Task DirtyTree_DisablesTheRewritingCommandsAndNamesTheOffendingFile()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        repo.Write("alpha.txt", "uncommitted edit\n");
        var vm = await VmForAsync(repo);
        vm.SelectedCommit = vm.Commits[1];

        Assert.False(vm.DropSelectedCommitCommand.CanExecute(null));
        Assert.False(vm.RewordSelectedCommitCommand.CanExecute(null));
        Assert.False(vm.PlanHistoryEditCommand.CanExecute(null));
        Assert.False(vm.ResetHardToSelectedCommitCommand.CanExecute(null));
        Assert.Contains("alpha.txt", vm.SurgeryBlockedReason!);
        Assert.Contains("Stash or commit them first", vm.SurgeryBlockedReason!);
        Assert.Null(vm.ResetBlockedReason);

        // A soft reset is defined on a dirty tree, so it stays available.
        Assert.True(vm.ResetSoftToSelectedCommitCommand.CanExecute(null));
        Assert.True(vm.ResetMixedToSelectedCommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingStaged_DisablesAmendIntoCommitWithItsOwnReason()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);
        vm.SelectedCommit = vm.Commits[0];

        Assert.False(vm.AmendStagedIntoSelectedCommitCommand.CanExecute(null));
        Assert.Equal("Nothing is staged — stage the fix first.", vm.AmendIntoCommitBlockedReason);

        repo.Write("extra.txt", "fix\n");
        await repo.GitAsync("add", "-A");
        await vm.RefreshWorkingStateAsync();

        Assert.True(vm.AmendStagedIntoSelectedCommitCommand.CanExecute(null));
        Assert.Null(vm.AmendIntoCommitBlockedReason);
    }

    [Fact]
    public async Task UnstagedChangesAlongsideStagedOnes_DisableAmendIntoCommit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        repo.Write("extra.txt", "fix\n");
        await repo.GitAsync("add", "-A");
        repo.Write("alpha.txt", "unstaged edit\n");
        var vm = await VmForAsync(repo);
        vm.SelectedCommit = vm.Commits[0];

        Assert.False(vm.AmendStagedIntoSelectedCommitCommand.CanExecute(null));
        Assert.Contains("unstaged change(s) would make git refuse the rebase", vm.AmendIntoCommitBlockedReason!);
    }

    [Fact]
    public async Task BusyRepository_DisablesEverySurgeryCommand()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);
        vm.SelectedCommit = vm.Commits[0];
        Assert.True(vm.DropSelectedCommitCommand.CanExecute(null));

        vm.IsBusy = true;

        Assert.False(vm.DropSelectedCommitCommand.CanExecute(null));
        Assert.False(vm.RevertSelectedCommitCommand.CanExecute(null));
        Assert.False(vm.CherryPickSelectedCommitCommand.CanExecute(null));
        Assert.False(vm.ResetSoftToSelectedCommitCommand.CanExecute(null));
        Assert.False(vm.PlanHistoryEditCommand.CanExecute(null));
        Assert.Equal("Another git operation is running.", vm.SurgeryBlockedReason);
    }

    [Fact]
    public async Task NoSelection_DisablesEverySurgeryCommandWithASelectionReason()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);

        Assert.Null(vm.SelectedCommit);
        Assert.False(vm.DropSelectedCommitCommand.CanExecute(null));
        Assert.Equal("Select a commit in the list first.", vm.SurgeryBlockedReason);
    }

    [Fact]
    public async Task WithoutACoordinator_TheCommandsAreDisabledRatherThanThrowing()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);
        vm.Surgery = null;
        vm.SelectedCommit = vm.Commits[0];

        Assert.False(vm.DropSelectedCommitCommand.CanExecute(null));
        Assert.Contains("unavailable", vm.SurgeryBlockedReason!);
        await vm.DropSelectedCommitCommand.ExecuteAsync(null);
        Assert.Equal(["alpha", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task SquashIntoPrevious_IsDisabledOnTheOldestLoadedCommit()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);

        vm.SelectedCommit = vm.Commits[0];
        Assert.True(vm.SquashSelectedIntoPreviousCommand.CanExecute(null));

        vm.SelectedCommit = vm.Commits[^1];
        Assert.False(vm.SquashSelectedIntoPreviousCommand.CanExecute(null));
    }

    // ── confirmations name the target ──────────────────────────────────────

    [Fact]
    public async Task DropConfirmation_NamesTheCommitAndDecliningRunsNothing()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: false);
        vm.SelectedCommit = vm.Commits[1];
        var target = vm.Commits[1];

        await vm.DropSelectedCommitCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(seen);
        Assert.Equal("Drop this commit?", confirmation.Title);
        Assert.Equal("Drop", confirmation.ConfirmLabel);
        Assert.Contains(target.ShortHash, confirmation.Message);
        Assert.Contains("alpha", confirmation.Message);
        Assert.Contains("Undo restores it", confirmation.Message);
        Assert.Equal(["beta", "alpha", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task HardResetConfirmation_SaysTheWorkingTreeChangesAreDeleted()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: false);
        vm.SelectedCommit = vm.Commits[2];
        var target = vm.Commits[2];

        await vm.ResetHardToSelectedCommitCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(seen);
        Assert.Contains(target.ShortHash, confirmation.Message);
        Assert.Contains("seed", confirmation.Message);
        Assert.Contains("The 2 commit(s) after it leave the branch", confirmation.Message);
        Assert.Contains("DELETED from the working tree", confirmation.Message);
        Assert.Equal("Hard reset", confirmation.ConfirmLabel);
    }

    [Fact]
    public async Task SoftResetConfirmation_SaysTheChangesStayStaged()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: false);
        vm.SelectedCommit = vm.Commits[1];

        await vm.ResetSoftToSelectedCommitCommand.ExecuteAsync(null);

        Assert.Contains("stay staged", Assert.Single(seen).Message);
    }

    [Fact]
    public async Task RewordConfirmation_NamesBothTheOldAndTheNewSubject()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: false);
        vm.PromptForCommitMessageAsync = (_, _, _) => Task.FromResult<string?>("a much better subject");
        vm.SelectedCommit = vm.Commits[0];

        await vm.RewordSelectedCommitCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(seen);
        Assert.Contains("alpha", confirmation.Message);
        Assert.Contains("a much better subject", confirmation.Message);
    }

    [Fact]
    public async Task RewordWithoutAMessage_NeverReachesTheConfirmation()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: true);
        vm.PromptForCommitMessageAsync = (_, _, _) => Task.FromResult<string?>(null);
        vm.SelectedCommit = vm.Commits[0];

        await vm.RewordSelectedCommitCommand.ExecuteAsync(null);

        Assert.Empty(seen);
        Assert.Equal("alpha", (await repo.SubjectsAsync())[0]);
    }

    [Fact]
    public async Task AmendIntoCommitConfirmation_CountsTheStagedChanges()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        repo.Write("extra.txt", "fix\n");
        await repo.GitAsync("add", "-A");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: false);
        vm.SelectedCommit = vm.Commits[1];

        await vm.AmendStagedIntoSelectedCommitCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(seen);
        Assert.Contains("the 1 staged change(s)", confirmation.Message);
        Assert.Contains("seed", confirmation.Message);
    }

    // ── the service's own words reach the surface ──────────────────────────

    [Fact]
    public async Task PlanningARangeContainingAMerge_SurfacesTheServicesOwnRefusal()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        await repo.GitAsync("checkout", "-q", "-b", "side");
        repo.Write("side.txt", "s\n");
        await repo.CommitAllAsync("side work");
        await repo.GitAsync("checkout", "-q", "main");
        repo.Write("main.txt", "m\n");
        await repo.CommitAllAsync("main work");
        await repo.GitAsync("merge", "--no-ff", "-m", "merge side", "side");

        var vm = await VmForAsync(repo);
        vm.SelectedCommit = vm.Commits[0];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Equal("The range is not editable.", vm.SurgeryStatusText);
        Assert.Contains("is a merge", vm.SurgeryFailureText);
        Assert.Contains("narrow the range", vm.SurgeryFailureText);
    }

    [Fact]
    public async Task NonContiguousSquashPlan_SurfacesTheDriversContiguityRefusal()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b", "c", "d");
        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);
        // Marks on the 2nd and 5th commits fold two disjoint runs, skipping the 3rd.
        vm.ShowHistoryPlanAsync = planned =>
        {
            var list = planned.ToList();
            list[1].SquashIntoPrevious = true;
            list[4].SquashIntoPrevious = true;
            return Task.FromResult<IReadOnlyList<PlannedCommit>?>(list);
        };
        vm.SelectedCommit = vm.Commits[^1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Contains("contiguous", vm.SurgeryFailureText);
        Assert.Equal(["d", "c", "b", "a", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task MixedPlan_IsRefusedBeforeAnythingRuns()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: true);
        vm.ShowHistoryPlanAsync = planned =>
        {
            var list = planned.ToList();
            list[2].Drop = true;
            HistoryPlan.MoveUp(list, 1);
            return Task.FromResult<IReadOnlyList<PlannedCommit>?>(list);
        };
        vm.SelectedCommit = vm.Commits[^1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Equal("Nothing applied.", vm.SurgeryStatusText);
        Assert.Contains("mixes a reorder and a drop", vm.SurgeryFailureText);
        Assert.Empty(seen);
        Assert.Equal(["b", "a", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task CancelledPlanDialog_RunsNothing()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: true);
        vm.ShowHistoryPlanAsync = _ => Task.FromResult<IReadOnlyList<PlannedCommit>?>(null);
        vm.SelectedCommit = vm.Commits[^1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Empty(seen);
        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal(["b", "a", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task ARefusalOnADirtyTree_NamesTheFilesAndOffersTheStash()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];

        // Dirtied after the working state the command was enabled on was read: the gate, not
        // the disabled state, is what refuses here.
        repo.Write("alpha.txt", "uncommitted edit\n");
        await vm.DropSelectedCommitCommand.ExecuteAsync(null);

        Assert.Contains("alpha.txt", vm.SurgeryFailureText);
        Assert.Contains("uncommitted change(s)", vm.SurgeryFailureText);
        Assert.True(vm.SurgeryStashOfferVisible);
        Assert.Equal(["beta", "alpha", "seed"], await repo.SubjectsAsync());

        await vm.StashBeforeSurgeryCommand.ExecuteAsync(null);

        Assert.False(vm.SurgeryStashOfferVisible);
        Assert.Equal("", await repo.StatusAsync());

        // The reload after an op replaces every commit instance, so the list selection is
        // remade before asserting the command is available again.
        vm.SelectedCommit = vm.Commits[1];
        Assert.True(vm.DropSelectedCommitCommand.CanExecute(null));
        Assert.Null(vm.SurgeryBlockedReason);
    }

    // ── the two cases that drive a real rebase ─────────────────────────────

    [Fact]
    public async Task Drop_ThenUndo_RestoresTheCommitAndReportsWhatTheHardResetDiscarded()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];
        var target = vm.Commits[1];

        await vm.DropSelectedCommitCommand.ExecuteAsync(null);

        Assert.Equal($"Drop {target.ShortHash} done.", vm.SurgeryStatusText);
        Assert.Equal("", vm.SurgeryFailureText);
        Assert.True(vm.SurgeryUndoVisible);
        Assert.Equal($"Undo “Drop {target.ShortHash}”", vm.SurgeryUndoLabel);
        Assert.Equal(["beta", "seed"], await repo.SubjectsAsync());
        Assert.False(repo.Exists("alpha.txt"));

        // A tracked edit made after the drop is what the restoring hard reset throws away.
        repo.Write("seed.txt", "edited after the drop\n");
        await vm.RefreshWorkingStateAsync();
        seen.Clear();

        await vm.UndoLastSurgeryCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(seen);
        Assert.Contains("holds 1 uncommitted change(s) right now", confirmation.Message);
        Assert.Contains("hard reset", confirmation.Message);
        // The service counts against the restored refs, so the edit and the file the restored
        // commit re-adds are both in its total — its number is the one reported, not a recount.
        Assert.Equal("Restored — 2 uncommitted change(s) were discarded.", vm.SurgeryStatusText);
        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal(["beta", "alpha", "seed"], await repo.SubjectsAsync());
        Assert.Equal("seed content\n", repo.Read("seed.txt"));
    }

    [Fact]
    public async Task AConflictingReorder_AbortsAndStillOffersUndoAndTheLeaveStoppedRetry()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed");
        repo.Write("shared.txt", "1\n");
        await repo.CommitAllAsync("second");
        repo.Write("shared.txt", "1\n2\n");
        await repo.CommitAllAsync("third");
        var before = await repo.FullStateAsync();

        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);
        vm.ShowHistoryPlanAsync = planned =>
        {
            var list = planned.ToList();
            HistoryPlan.MoveUp(list, 1);
            return Task.FromResult<IReadOnlyList<PlannedCommit>?>(list);
        };
        vm.SelectedCommit = vm.Commits[1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Contains("the rebase was aborted", vm.SurgeryFailureText);
        Assert.Contains("refs, index and tracked content are unchanged", vm.SurgeryFailureText);
        Assert.True(vm.SurgeryLeaveStoppedOfferVisible);
        // The undo the service hands back on failure is offered too — the backup outlives it.
        Assert.True(vm.SurgeryUndoVisible);
        Assert.Equal(before, await repo.FullStateAsync());
    }

    // ── offers belong to the project they were made on ─────────────────────

    [Fact]
    public async Task SwitchingProjects_ClearsTheUndoAndFailureOffers()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        using var other = await SurgeryRepo.CreateAsync("other-seed");
        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];

        await vm.DropSelectedCommitCommand.ExecuteAsync(null);
        Assert.True(vm.SurgeryUndoVisible);

        var name = Path.GetFileName(other.Path);
        await vm.SetProjectAsync(new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = other.Path });

        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal("", vm.SurgeryStatusText);
        Assert.Equal("", vm.SurgeryFailureText);
        Assert.False(vm.UndoLastSurgeryCommand.CanExecute(null));
        Assert.Equal(["beta", "seed"], await repo.SubjectsAsync());
    }
}
