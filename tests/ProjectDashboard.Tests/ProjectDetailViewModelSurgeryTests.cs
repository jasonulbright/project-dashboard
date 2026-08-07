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
    public async Task SquashIntoPrevious_IsDisabledOnTheOldestLoadedCommitWithItsOwnReason()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha");
        var vm = await VmForAsync(repo);

        vm.SelectedCommit = vm.Commits[0];
        Assert.True(vm.SquashSelectedIntoPreviousCommand.CanExecute(null));
        Assert.Null(vm.SquashIntoPreviousBlockedReason);

        vm.SelectedCommit = vm.Commits[^1];

        // Nothing else about the repository blocks a rebase here, so the shared reason is null:
        // without a dedicated one the bound tooltip would be absent on a disabled menu item.
        Assert.False(vm.SquashSelectedIntoPreviousCommand.CanExecute(null));
        Assert.Null(vm.SurgeryBlockedReason);
        Assert.Equal("Only the loaded history can be squashed — this is the oldest commit shown.",
            vm.SquashIntoPreviousBlockedReason);
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

    [Theory]
    // Two runs separated by a gap, and two runs that sit next to each other. The adjacent pair
    // is the dangerous one: its shas form a contiguous list a driver folds into ONE commit,
    // while the preview the confirm showed named two.
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    public async Task APlanFoldingTwoGroups_IsRefusedBeforeTheConfirm(int first, int second)
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b", "c", "d");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: true);
        vm.ShowHistoryPlanAsync = planned =>
        {
            var list = planned.ToList();
            list[first].SquashIntoPrevious = true;
            list[second].SquashIntoPrevious = true;
            return Task.FromResult<IReadOnlyList<PlannedCommit>?>(list);
        };
        vm.SelectedCommit = vm.Commits[^1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Equal("Nothing applied.", vm.SurgeryStatusText);
        Assert.Contains("folds 2 separate groups", vm.SurgeryFailureText);
        Assert.Empty(seen);
        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal(["d", "c", "b", "a", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task APlanDroppingEveryCommit_IsRefusedBeforeABackupIsTaken()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a");
        var vm = await VmForAsync(repo);
        var seen = CaptureConfirmations(vm, answer: true);
        vm.ShowHistoryPlanAsync = planned =>
        {
            var list = planned.ToList();
            foreach (var commit in list) commit.Drop = true;
            return Task.FromResult<IReadOnlyList<PlannedCommit>?>(list);
        };
        vm.SelectedCommit = vm.Commits[^1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        // The driver refuses this too, but only after a verified backup and a journal entry,
        // which would leave an Undo standing for an operation that never touched anything.
        Assert.Contains("empty the branch", vm.SurgeryFailureText);
        Assert.Empty(seen);
        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal(["a", "seed"], await repo.SubjectsAsync());
    }

    /// <summary>
    /// The plan cases whose preview must survive a real rebase unchanged: one fold run, one
    /// longer fold run, drops, and a pure reorder.
    /// </summary>
    [Theory]
    [InlineData("one-fold")]
    [InlineData("long-fold")]
    [InlineData("drops")]
    [InlineData("reorder")]
    public async Task AnAppliedPlan_ProducesExactlyTheCommitsItsPreviewShowed(string plan)
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "a", "b", "c", "d");
        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);

        // The preview the confirm renders, captured from the accepted plan itself.
        List<string> preview = [];
        vm.ShowHistoryPlanAsync = planned =>
        {
            var list = planned.ToList();
            switch (plan)
            {
                case "one-fold": list[3].SquashIntoPrevious = true; break;
                case "long-fold":
                    list[3].SquashIntoPrevious = true;
                    list[4].SquashIntoPrevious = true;
                    break;
                case "drops":
                    list[1].Drop = true;
                    list[3].Drop = true;
                    break;
                default: HistoryPlan.MoveUp(list, 4); break;
            }
            preview = HistoryPlan.Preview(list).ToList();
            return Task.FromResult<IReadOnlyList<PlannedCommit>?>(list);
        };
        vm.SelectedCommit = vm.Commits[^1];

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Equal("", vm.SurgeryFailureText);
        var produced = await repo.SubjectsAsync();
        produced.Reverse();
        Assert.Equal(preview.Select(PreviewedSubject), produced);
    }

    /// <summary>
    /// The subject the commit on a preview line ends up carrying: the line is "sha  subject",
    /// and a fold keeps the anchor's message, so everything from " + " on is absorbed.
    /// </summary>
    private static string PreviewedSubject(string line)
    {
        var subject = line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..];
        var fold = subject.IndexOf(" + ", StringComparison.Ordinal);
        return fold < 0 ? subject : subject[..fold];
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
    public async Task AConflictingReorder_AbortsAndOffersTheRetryRatherThanAnUndo()
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
        // The backup outlives the abort, but restoring it ends in a hard reset over a repository
        // the abort already put back — the retry is the offer that has work left to do.
        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal(before, await repo.FullStateAsync());

        await vm.RetrySurgeryLeavingItStoppedCommand.ExecuteAsync(null);

        // Stopped mid-rebase: now the repository has moved and the undo is the way back.
        Assert.True(repo.RebaseInProgress);
        Assert.True(vm.SurgeryUndoVisible);
        Assert.True(vm.UndoLastSurgeryCommand.CanExecute(null));
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

    /// <summary>Switches the view-model to <paramref name="repo"/> the way the project list does.</summary>
    private static Task SwitchToAsync(ProjectDetailViewModel vm, SurgeryRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return vm.SetProjectAsync(new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path });
    }

    [Fact]
    public async Task AProjectSwitchWhileTheConfirmIsOpen_LeavesNothingAttributedToEitherProject()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        using var other = await SurgeryRepo.CreateAsync("other-seed");
        other.Write("dirt-one.txt", "1\n");
        other.Write("dirt-two.txt", "2\n");
        other.Write("dirt-three.txt", "3\n");

        var vm = await VmForAsync(repo);
        vm.SelectedCommit = vm.Commits[1];
        // A confirmation does not block input: the switch lands between the click and the answer.
        vm.ConfirmSurgeryAsync = async _ =>
        {
            await SwitchToAsync(vm, other);
            return true;
        };

        await vm.DropSelectedCommitCommand.ExecuteAsync(null);

        // Nothing describes the drop on the project now on screen, and its dirty tree — which an
        // undo's hard reset would discard — is not what any offer counts.
        Assert.Equal("", vm.SurgeryStatusText);
        Assert.Equal("", vm.SurgeryFailureText);
        Assert.False(vm.SurgeryUndoVisible);
        Assert.False(vm.SurgeryLeaveStoppedOfferVisible);
        Assert.False(vm.UndoLastSurgeryCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
        Assert.Equal(3, (await other.StatusAsync()).Split('\n').Length);
        // The surface that asked for the drop is gone, so the drop it would have to report is
        // not run behind the reader's back either.
        Assert.Equal(["beta", "alpha", "seed"], await repo.SubjectsAsync());
    }

    [Fact]
    public async Task AProjectSwitchWhileTheUndoConfirmIsOpen_RestoresNothing()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        using var other = await SurgeryRepo.CreateAsync("other-seed");
        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];

        await vm.DropSelectedCommitCommand.ExecuteAsync(null);
        Assert.True(vm.SurgeryUndoVisible);

        vm.ConfirmSurgeryAsync = async _ =>
        {
            await SwitchToAsync(vm, other);
            return true;
        };
        await vm.UndoLastSurgeryCommand.ExecuteAsync(null);

        // The dirty count the confirm quoted was the dropped-on repository's; a restore running
        // after the switch would report against a repository the reader never saw it named.
        Assert.Equal("", vm.SurgeryStatusText);
        Assert.False(vm.IsBusy);
        Assert.Equal(["beta", "seed"], await repo.SubjectsAsync());
    }

    // ── an offer is only made where it is the answer ────────────────────────

    [Fact]
    public async Task ARefusalFromTheBusyRegistry_DoesNotOfferAStash()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var busy = new RepoBusyRegistry();
        var git = new GitService();
        var vm = await VmForAsync(repo);
        vm.Surgery = new SurgeryCoordinator(
            new BackupService(git, new SettingsService()), busy, git,
            new RebaseDriver(git, GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work")));
        CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];

        // Held elsewhere, and dirty: a stash would clear the tree and change nothing.
        Assert.True(busy.TryAcquire(repo.Path, out var lease));
        using (lease)
        {
            repo.Write("alpha.txt", "uncommitted edit\n");
            await vm.DropSelectedCommitCommand.ExecuteAsync(null);
        }

        Assert.Contains("busy", vm.SurgeryFailureText);
        Assert.False(vm.SurgeryStashOfferVisible);
        Assert.False(vm.SurgeryUndoVisible);
        Assert.Equal(["beta", "alpha", "seed"], await repo.SubjectsAsync());
    }

    /// <summary>
    /// A driver whose replay throws after the gates have passed. The throw is what leaves the
    /// outcome unknown: no <see cref="RebaseRunResult"/> is ever produced to classify it.
    /// </summary>
    private sealed class ThrowingRebaseDriver : RebaseDriver
    {
        public ThrowingRebaseDriver(GitService git)
            : base(git, GitGuard.GitExe, Path.Combine(TestEnv.NewDir("surgery-work"), "work"))
        {
        }

        public override Task<RebaseRunResult> RunTodoAsync(
            RebaseScope scope, IReadOnlyList<string> todoLines, IReadOnlyDictionary<string, string> messageFiles,
            RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default) =>
            throw new IOException("the prepared todo could not be written");
    }

    [Fact]
    public async Task AFailureTheServiceCouldNotClassify_KeepsTheUndoOffer()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var git = new GitService();
        var vm = await VmForAsync(repo);
        vm.Surgery = new SurgeryCoordinator(
            new BackupService(git, new SettingsService()), new RepoBusyRegistry(), git,
            new ThrowingRebaseDriver(git));
        CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];

        await vm.DropSelectedCommitCommand.ExecuteAsync(null);

        // Neither git-level result exists, which is the one outcome the service refuses to call
        // untouched — it leaves the journal pending for exactly this case, so the offer that
        // answers it has to stand rather than be read as "nothing ran".
        Assert.Contains("the rebase failed", vm.SurgeryFailureText);
        Assert.True(vm.SurgeryUndoVisible);
        Assert.True(vm.UndoLastSurgeryCommand.CanExecute(null));
        Assert.False(vm.SurgeryStashOfferVisible);
    }

    [Fact]
    public async Task OpeningThePlanDialog_ClearsTheFailureTextAndTheOffersItExplained()
    {
        using var repo = await SurgeryRepo.CreateAsync("seed", "alpha", "beta");
        var vm = await VmForAsync(repo);
        CaptureConfirmations(vm, answer: true);
        vm.SelectedCommit = vm.Commits[1];

        repo.Write("alpha.txt", "uncommitted edit\n");
        await vm.DropSelectedCommitCommand.ExecuteAsync(null);
        Assert.True(vm.SurgeryStashOfferVisible);

        await repo.GitAsync("checkout", "--", "alpha.txt");
        await vm.RefreshWorkingStateAsync();
        vm.SelectedCommit = vm.Commits[1];
        vm.ShowHistoryPlanAsync = _ => Task.FromResult<IReadOnlyList<PlannedCommit>?>(null);

        await vm.PlanHistoryEditCommand.ExecuteAsync(null);

        Assert.Equal("", vm.SurgeryFailureText);
        Assert.False(vm.SurgeryStashOfferVisible);
        Assert.False(vm.SurgeryLeaveStoppedOfferVisible);
    }
}
