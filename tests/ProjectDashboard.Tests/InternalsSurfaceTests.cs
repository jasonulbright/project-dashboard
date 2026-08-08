using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Internals tab: worktrees, submodules, and the root ignore rules. What is asserted is that
/// the worktree listing names the container repository rather than the checkout it was read from,
/// that removing the main worktree is refused before a confirmation is spent on it, that the
/// submodule actions carry the service's discard gates rather than route around them, and that the
/// ignore editor writes a file and claims nothing more.
/// </summary>
public class InternalsSurfaceTests
{
    private static ProjectInfo ProjectFor(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = path };
    }

    /// <summary>Answers the confirmation and the folder picker without a window.</summary>
    private sealed class InternalsViewModel(bool confirm = true, SubmoduleService? submodules = null,
        RepoBusyRegistry? busy = null, GitService? git = null)
        : ProjectDetailViewModel(null!, git ?? new GitService(), null!, null, busy ?? new RepoBusyRegistry(),
            submodules: submodules)
    {
        public int Confirmations { get; private set; }
        public string LastConfirmMessage { get; private set; } = "";

        /// <summary>Null stands for a cancelled picker.</summary>
        public string? PickedDirectory { get; set; }

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastConfirmMessage = message;
            return Task.FromResult(confirm);
        }

        internal override string? PromptForDirectory(string title) => PickedDirectory;
    }

    private static async Task<InternalsViewModel> OpenedOn(string repoPath, bool confirm = true,
        SubmoduleService? submodules = null)
    {
        var vm = new InternalsViewModel(confirm, submodules ?? new SubmoduleService(new GitService()));
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repoPath));
        await vm.LoadBranchesCommand.ExecuteAsync(null);
        await vm.LoadInternalsCommand.ExecuteAsync(null);
        return vm;
    }

    // ── Worktrees ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TheWorktreeList_MarksTheMainWorktreeAndTheOneOnScreen()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-vm");
        var linked = Path.Combine(TestEnv.NewDir("wt-vm-parent"), "linked");
        Assert.True((await new GitService().AddWorktreeAsync(repo.Path, linked, "side")).Success);

        // Read from the LINKED worktree, which is how the app runs in development.
        var vm = await OpenedOn(linked);

        Assert.True(vm.InternalsLoaded);
        Assert.Equal(2, vm.Worktrees.Count);

        var main = vm.Worktrees[0];
        Assert.True(main.Entry.IsMain);
        Assert.False(main.IsCurrent);
        Assert.Contains("main worktree", main.StateLabel);

        var here = vm.Worktrees[1];
        Assert.False(here.Entry.IsMain);
        Assert.True(here.IsCurrent);
        Assert.Contains("this checkout", here.StateLabel);
        Assert.Equal("side", here.BranchLabel);
        // The selection opens on the checkout being described.
        Assert.Same(here, vm.SelectedWorktree);

        await new GitService().RemoveWorktreeAsync(repo.Path, linked);
    }

    [Fact]
    public async Task AProblemFlaggedByGit_AppearsOnTheRowRatherThanOnlyInAnAction()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-prunable");
        var linked = Path.Combine(TestEnv.NewDir("wt-prunable-parent"), "linked");
        await new GitService().AddWorktreeAsync(repo.Path, linked, "side");
        TestEnv.TryDeleteTree(linked);

        var vm = await OpenedOn(repo.Path);

        var stale = vm.Worktrees.Single(w => !w.Entry.IsMain);
        Assert.True(stale.Entry.IsPrunable);
        Assert.Contains("prunable", stale.StateLabel);
        Assert.Contains(stale.Entry.PrunableReason, stale.StateLabel);
    }

    [Theory]
    [InlineData(@"C:\repos\a", "C:/repos/a", true)]
    [InlineData(@"C:\repos\a\", "C:/repos/a", true)]
    [InlineData(@"C:\repos\A", "C:/repos/a", true)]
    [InlineData(@"C:\repos\a", "C:/repos/b", false)]
    [InlineData("", "C:/repos/a", false)]
    public void APathIsMatchedAsADirectoryRatherThanAsBytes(string a, string b, bool same)
        => Assert.Equal(same, ProjectDetailViewModel.SamePath(a, b));

    [Fact]
    public async Task RemovingTheMainWorktree_IsRefusedBeforeAConfirmationIsSpentOnIt()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-main-rm");
        var vm = await OpenedOn(repo.Path);

        vm.SelectedWorktree = vm.Worktrees.Single(w => w.Entry.IsMain);
        await vm.RemoveWorktreeCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Confirmations);
        Assert.Equal(ProjectDetailViewModel.MainWorktreeRefusal, vm.WorktreesErrorText);
        Assert.True(Directory.Exists(repo.Path));
    }

    [Fact]
    public async Task AddingAWorktree_CreatesItOnANewBranchAndRefreshesBothLists()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-add");
        var target = Path.Combine(TestEnv.NewDir("wt-add-parent"), "linked");
        var vm = await OpenedOn(repo.Path);

        vm.NewWorktreePath = target;
        vm.NewWorktreeBranch = "side";
        await vm.AddWorktreeCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(target));
        Assert.Equal(2, vm.Worktrees.Count);
        Assert.Contains(vm.Branches, b => b.Name == "side");
        Assert.Contains("on a new branch side", vm.WorktreesStatusText);
        Assert.Equal("", vm.NewWorktreePath);

        await new GitService().RemoveWorktreeAsync(repo.Path, target);
    }

    [Fact]
    public async Task AddingAWorktree_RefusesABranchNameAlreadyInUse()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-add-dupe");
        var vm = await OpenedOn(repo.Path);

        vm.NewWorktreePath = Path.Combine(TestEnv.NewDir("wt-add-dupe-parent"), "linked");
        vm.NewWorktreeBranch = "main";
        await vm.AddWorktreeCommand.ExecuteAsync(null);

        Assert.Contains("already exists here", vm.WorktreesErrorText);
        Assert.Single(vm.Worktrees);
    }

    [Fact]
    public async Task AddingAWorktree_WithNoPath_SaysSoRatherThanHandingGitNothing()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-add-nopath");
        var vm = await OpenedOn(repo.Path);

        await vm.AddWorktreeCommand.ExecuteAsync(null);

        Assert.Contains("Choose a directory", vm.WorktreesErrorText);
        Assert.Single(vm.Worktrees);
    }

    /// <summary>
    /// Git refuses a worktree path that already exists, and a folder picker can only return one
    /// that does — so the pick is the parent and the branch name is the leaf.
    /// </summary>
    [Fact]
    public async Task ThePathPicker_ChoosesAParentAndAppendsTheBranchName()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-pick");
        var vm = await OpenedOn(repo.Path);
        vm.PickedDirectory = @"C:\worktrees";

        vm.NewWorktreeBranch = "feature/one";
        vm.ChooseWorktreePathCommand.Execute(null);

        Assert.Equal(Path.Combine(@"C:\worktrees", "feature-one"), vm.NewWorktreePath);
    }

    /// <summary>
    /// With no branch name there is no leaf to append, and the bare parent directory already
    /// exists — a path git rejects. The pick is refused rather than spent on it.
    /// </summary>
    [Fact]
    public async Task ThePathPicker_WithNoBranchName_RefusesRatherThanOfferingTheBareParent()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-pick-nobranch");
        var vm = await OpenedOn(repo.Path);
        vm.PickedDirectory = @"C:\worktrees";

        vm.ChooseWorktreePathCommand.Execute(null);

        Assert.Equal("", vm.NewWorktreePath);
        Assert.Equal(ProjectDetailViewModel.BranchNameRequired, vm.WorktreesErrorText);
    }

    /// <summary>
    /// A worktree added here always creates its branch. Git's own default names that branch after
    /// the leaf directory, which creates a branch nobody asked for and skips the collision check —
    /// so the name is required rather than inferred.
    /// </summary>
    [Fact]
    public async Task AddingAWorktree_WithNoBranchName_IsRefusedRatherThanNamingOneAfterTheDirectory()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-add-nobranch");
        var target = Path.Combine(TestEnv.NewDir("wt-add-nobranch-parent"), "linked");
        var vm = await OpenedOn(repo.Path);

        vm.NewWorktreePath = target;
        await vm.AddWorktreeCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.BranchNameRequired, vm.WorktreesErrorText);
        Assert.False(Directory.Exists(target));
        Assert.Single(vm.Worktrees);
        Assert.DoesNotContain(vm.Branches, b => b.Name == "linked");
    }

    [Fact]
    public async Task PruningWorktrees_IsRefusedWithoutTheConfirmation()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-prune-no");
        var linked = Path.Combine(TestEnv.NewDir("wt-prune-no-parent"), "linked");
        await new GitService().AddWorktreeAsync(repo.Path, linked, "side");
        TestEnv.TryDeleteTree(linked);

        var vm = await OpenedOn(repo.Path, confirm: false);
        await vm.PruneWorktreesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Worktrees.Count);
        Assert.Equal("", vm.WorktreesStatusText);
    }

    [Fact]
    public async Task PruningWorktrees_ClearsTheStaleEntriesAndReportsHowMany()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-prune-yes");
        var linked = Path.Combine(TestEnv.NewDir("wt-prune-yes-parent"), "linked");
        await new GitService().AddWorktreeAsync(repo.Path, linked, "side");
        TestEnv.TryDeleteTree(linked);

        var vm = await OpenedOn(repo.Path);
        await vm.PruneWorktreesCommand.ExecuteAsync(null);

        Assert.Single(vm.Worktrees);
        Assert.Contains("Cleared 1 stale worktree entry", vm.WorktreesStatusText);
    }

    [Fact]
    public async Task PruningWorktrees_WithNothingStale_SaysNothingWasStale()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("wt-prune-clean");
        var vm = await OpenedOn(repo.Path);

        await vm.PruneWorktreesCommand.ExecuteAsync(null);

        Assert.Contains("most likely clear nothing", vm.LastConfirmMessage);
        Assert.Contains("Nothing was stale", vm.WorktreesStatusText);
    }

    // ── Submodules ──────────────────────────────────────────────────────────

    private static async Task AddSubmoduleAsync(TempRepo super, TempRepo child, string path)
    {
        await super.GitAsync("submodule", "add", "--", child.FileUrl, path);
        await super.CommitAllAsync($"add submodule {path}");
    }

    [Fact]
    public async Task TheSubmoduleList_ReportsWhatTheSuperprojectKnows()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-super");
        await AddSubmoduleAsync(super, child, "lib");

        var vm = await OpenedOn(super.Path);

        var entry = Assert.Single(vm.Submodules);
        Assert.Equal("lib", entry.Path);
        Assert.True(entry.IsInitialized);
        Assert.Same(entry, vm.SelectedSubmodule);
        Assert.Equal("", vm.SubmodulesErrorText);
    }

    [Fact]
    public async Task ARepositoryWithNoSubmodules_SaysSoRatherThanShowingAnEmptyList()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("sub-vm-none");
        var vm = await OpenedOn(repo.Path);

        Assert.Empty(vm.Submodules);
        Assert.True(vm.SubmodulesEmpty);
        var empty = EmptyStateMarkup(await File.ReadAllTextAsync(PageSource()), "SubmodulesEmptyState");
        Assert.Contains("declares no submodules and records no gitlinks", empty);
        // The claim is made from a read that answered, never from a count an error also produces.
        Assert.Contains("Binding SubmodulesEmpty,", empty);
        Assert.DoesNotContain("Submodules.Count", empty);
    }

    [Fact]
    public async Task WithNoSubmoduleService_TheSurfaceRefusesRatherThanReportingNone()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-off-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-off-super");
        await AddSubmoduleAsync(super, child, "lib");

        var vm = new InternalsViewModel(submodules: null);
        await vm.SetProjectAsync(ProjectFor(super.Path));
        await vm.LoadInternalsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Submodules);
        // The refusal must not read as "this repository declares no submodules" — it declares one.
        Assert.False(vm.SubmodulesEmpty);
        Assert.Equal(ProjectDetailViewModel.SubmodulesUnavailableNotice, vm.SubmodulesErrorText);
        Assert.False(vm.InitSubmoduleCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdatingASubmodule_BringsAnUninitializedOneBack()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-up-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-up-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("submodule", "deinit", "--force", "--", "lib");

        var vm = await OpenedOn(super.Path);
        Assert.False(vm.Submodules[0].IsInitialized);

        await vm.UpdateSubmoduleCommand.ExecuteAsync(null);

        Assert.True(vm.Submodules[0].IsInitialized);
        Assert.Contains("at the commit this repository records", vm.SubmodulesStatusText);
    }

    /// <summary>
    /// The service refuses --force without the acknowledgement. Saying so before the run means the
    /// refusal is not discovered as an opaque failure after a clone has already started.
    /// </summary>
    [Fact]
    public async Task AForcedUpdate_IsRefusedUntilTheDiscardIsAcknowledged()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-force-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-force-super");
        await AddSubmoduleAsync(super, child, "lib");
        File.WriteAllText(Path.Combine(super.Path, "lib", "file.txt"), "local edit\n");

        var vm = await OpenedOn(super.Path);
        vm.SubmoduleForce = true;
        vm.SubmoduleConfirmDiscard = false;
        await vm.UpdateSubmoduleCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Confirmations);
        Assert.Equal(ProjectDetailViewModel.ForceNeedsAcknowledgement, vm.SubmodulesErrorText);
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(super.Path, "lib", "file.txt")));
    }

    [Fact]
    public async Task AForcedUpdate_RunsOnceAcknowledgedAndConfirmed()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-forced-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-forced-super");
        await AddSubmoduleAsync(super, child, "lib");
        File.WriteAllText(Path.Combine(super.Path, "lib", "file.txt"), "local edit\n");

        var vm = await OpenedOn(super.Path);
        vm.SubmoduleForce = true;
        vm.SubmoduleConfirmDiscard = true;
        await vm.UpdateSubmoduleCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("discarded", vm.LastConfirmMessage);
        Assert.Equal("line one\n", File.ReadAllText(Path.Combine(super.Path, "lib", "file.txt")));
    }

    [Fact]
    public async Task DeinitializingASubmodule_IsRefusedWithoutTheConfirmation()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-deinit-no-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-deinit-no-super");
        await AddSubmoduleAsync(super, child, "lib");

        var vm = await OpenedOn(super.Path, confirm: false);
        await vm.DeinitSubmoduleCommand.ExecuteAsync(null);

        Assert.True(vm.Submodules[0].IsInitialized);
        Assert.Equal("", vm.SubmodulesStatusText);
    }

    [Fact]
    public async Task DeinitializingASubmodule_EmptiesItAndLeavesTheRecordedCommit()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-deinit-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-deinit-super");
        await AddSubmoduleAsync(super, child, "lib");
        var recorded = (await super.GitAsync("ls-files", "--stage", "lib")).Split(' ')[1];

        var vm = await OpenedOn(super.Path);
        await vm.DeinitSubmoduleCommand.ExecuteAsync(null);

        Assert.False(vm.Submodules[0].IsInitialized);
        Assert.Equal(recorded, vm.Submodules[0].RecordedSha);
        Assert.Contains("still records its commit", vm.SubmodulesStatusText);
    }

    [Fact]
    public async Task SyncingASubmodule_RewritesItsUrlFromGitmodules()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-vm-sync-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-vm-sync-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("config", "-f", ".gitmodules", "submodule.lib.url", "https://example.test/moved.git");

        var vm = await OpenedOn(super.Path);
        await vm.SyncSubmoduleCommand.ExecuteAsync(null);

        Assert.Contains("https://example.test/moved.git",
            await super.GitAsync("config", "--get", "submodule.lib.url"));
        Assert.Contains("Nothing was fetched", vm.SubmodulesStatusText);
    }

    // ── Ignore rules ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheIgnoreEditor_DistinguishesAnAbsentFileFromAnEmptyOne()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-absent");
        var vm = await OpenedOn(repo.Path);

        Assert.False(vm.GitignoreExists);
        Assert.Equal("", vm.GitignoreText);
        Assert.False(vm.GitignoreDirty);
        var markup = await File.ReadAllTextAsync(PageSource());
        Assert.Contains("has no .gitignore at its root", markup);
    }

    [Fact]
    public async Task SavingTheIgnoreRules_WritesTheFileAndClaimsNothingMore()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-save");
        var vm = await OpenedOn(repo.Path);

        Assert.True(vm.GitignoreLoaded);
        vm.GitignoreText = "bin/\nobj/\n";
        Assert.True(vm.GitignoreDirty);
        Assert.True(vm.SaveGitignoreCommand.CanExecute(null));

        await vm.SaveGitignoreCommand.ExecuteAsync(null);

        Assert.Equal("bin/\nobj/\n", repo.ReadFile(".gitignore"));
        Assert.True(vm.GitignoreExists);
        Assert.False(vm.GitignoreDirty);
        Assert.Contains("stays tracked", vm.GitignoreStatusText);
    }

    /// <summary>
    /// No git command runs, but a rewrite holds the working tree while it replaces it — a file
    /// landing mid-swap belongs to neither history, so the write takes the same lease.
    /// </summary>
    [Fact]
    public async Task SavingTheIgnoreRules_IsRefusedWhileAnotherOperationHoldsTheRepository()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-busy");
        var registry = new RepoBusyRegistry();
        var vm = new InternalsViewModel(busy: registry, submodules: new SubmoduleService(new GitService()));
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repo.Path));
        await vm.LoadInternalsCommand.ExecuteAsync(null);

        vm.GitignoreText = "bin/\n";
        Assert.True(registry.TryAcquire(repo.Path, out var lease));
        using (lease)
            await vm.SaveGitignoreCommand.ExecuteAsync(null);

        Assert.False(repo.FileExists(".gitignore"));
        Assert.True(vm.GitignoreDirty);
        Assert.Contains("another operation is running", vm.GitignoreErrorText);
    }

    /// <summary>
    /// A read that failed leaves the editor empty, which is indistinguishable from a repository
    /// whose rules are empty. Saving that emptiness would replace a file nobody managed to read,
    /// so the write is refused until a read succeeds.
    /// </summary>
    [Fact]
    public async Task SavingTheIgnoreRules_IsRefusedWhenTheReadNeverSucceeded()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-unread");
        repo.WriteFile(".gitignore", "bin/\nobj/\n");

        InternalsViewModel vm;
        // An exclusive handle is what a transient read failure looks like from here. It is
        // released before the save, so nothing but the refusal stands between the editor and
        // the file on disk.
        using (File.Open(Path.Combine(repo.Path, ".gitignore"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm = await OpenedOn(repo.Path);
            Assert.False(vm.GitignoreLoaded);
            Assert.Contains("Could not read .gitignore", vm.GitignoreErrorText);
        }

        vm.GitignoreText = "everything";
        Assert.False(vm.SaveGitignoreCommand.CanExecute(null));
        await vm.SaveGitignoreCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.GitignoreNotLoadedRefusal, vm.GitignoreErrorText);
        Assert.Equal("bin/\nobj/\n", repo.ReadFile(".gitignore"));
    }

    [Fact]
    public async Task ReloadingTheIgnoreRules_DropsTheEdits()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-revert");
        repo.WriteFile(".gitignore", "bin/\n");
        var vm = await OpenedOn(repo.Path);

        vm.GitignoreText = "everything\n";
        await vm.RevertGitignoreCommand.ExecuteAsync(null);

        Assert.Equal("bin/\n", vm.GitignoreText);
        Assert.False(vm.GitignoreDirty);
        Assert.Equal("bin/\n", repo.ReadFile(".gitignore"));
    }

    [Fact]
    public async Task TheIgnoreProbe_AsksGitRatherThanMatchingTheEditorText()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-probe");
        repo.WriteFile(".gitignore", "*.log\n!keep.log\n");
        var vm = await OpenedOn(repo.Path);

        vm.IgnoreProbePath = "debug.log";
        await vm.ProbeIgnorePathCommand.ExecuteAsync(null);
        Assert.Equal("debug.log is ignored.", vm.IgnoreProbeResult);

        // A negation later in the file is exactly what a text match would get wrong.
        vm.IgnoreProbePath = "keep.log";
        await vm.ProbeIgnorePathCommand.ExecuteAsync(null);
        Assert.Contains("is not ignored", vm.IgnoreProbeResult);
    }

    /// <summary>
    /// check-ignore consults the index, so a tracked path exits 1 — the same exit as a path no
    /// rule matches — even while a rule does match it. The two cannot be reported as one answer.
    /// </summary>
    [Fact]
    public async Task TheIgnoreProbe_SaysWhenTheIndexIsWhatOutranksTheRules()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-tracked");
        repo.WriteFile("kept.log", "x\n");
        await repo.GitAsync("add", "--force", "--", "kept.log");
        await repo.CommitAllAsync("track a log");
        repo.WriteFile(".gitignore", "*.log\n");
        var vm = await OpenedOn(repo.Path);

        vm.IgnoreProbePath = "kept.log";
        await vm.ProbeIgnorePathCommand.ExecuteAsync(null);

        Assert.Contains("git already tracks it", vm.IgnoreProbeResult);
        Assert.Contains("once the path is untracked", vm.IgnoreProbeResult);
    }

    /// <summary>
    /// A path outside the repository makes check-ignore exit 128. Reporting that as "not ignored"
    /// would answer a question git refused to answer.
    /// </summary>
    [Fact]
    public async Task TheIgnoreProbe_SaysWhenGitCouldNotAnswerRatherThanAnsweringNo()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-unanswerable");
        repo.WriteFile(".gitignore", "*.log\n");
        var vm = await OpenedOn(repo.Path);

        vm.IgnoreProbePath = "../outside.log";
        await vm.ProbeIgnorePathCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not tell whether", vm.IgnoreProbeResult);
        Assert.DoesNotContain("is not ignored", vm.IgnoreProbeResult);
    }

    [Fact]
    public async Task TheIgnoreProbe_RefusesWhileTheEditorHasUnsavedRules()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-probe-dirty");
        var vm = await OpenedOn(repo.Path);

        vm.GitignoreText = "*.log\n";
        vm.IgnoreProbePath = "debug.log";
        await vm.ProbeIgnorePathCommand.ExecuteAsync(null);

        Assert.Contains("Save the ignore rules first", vm.IgnoreProbeResult);
    }

    [Fact]
    public async Task TheIgnoreProbe_WithNoPath_AsksForOne()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-probe-empty");
        var vm = await OpenedOn(repo.Path);

        await vm.ProbeIgnorePathCommand.ExecuteAsync(null);

        Assert.Contains("Type a repository-relative path", vm.IgnoreProbeResult);
    }

    [Fact]
    public async Task SwitchingProjects_DropsEverythingTheTabHeld()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("internals-switch-a");
        using var other = await TempRepo.CreateWithCommitAsync("internals-switch-b");
        repo.WriteFile(".gitignore", "bin/\n");
        var vm = await OpenedOn(repo.Path);
        Assert.NotEmpty(vm.Worktrees);

        await vm.SetProjectAsync(ProjectFor(other.Path));

        Assert.False(vm.InternalsLoaded);
        Assert.Empty(vm.Worktrees);
        Assert.Empty(vm.Submodules);
        Assert.Equal("", vm.GitignoreText);
        Assert.False(vm.GitignoreExists);
    }

    /// <summary>
    /// A read still in flight when the reader moves on carries the previous repository's answer.
    /// Marking the tab loaded from that continuation asserts the NEW repository's empty lists as
    /// fact — nothing has been read about it yet.
    /// </summary>
    [Fact]
    public async Task SwitchingProjectsMidRead_LeavesTheIncomingProjectsTabUnloaded()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("internals-race-a");
        using var other = await TempRepo.CreateWithCommitAsync("internals-race-b");
        var git = new SwitchMidReadGitService();
        var vm = new InternalsViewModel(submodules: new SubmoduleService(new GitService()), git: git);
        await vm.SetProjectAsync(ProjectFor(repo.Path));

        git.OnNextCall = () => vm.SetProjectAsync(ProjectFor(other.Path));
        await vm.LoadInternalsCommand.ExecuteAsync(null);

        Assert.False(vm.InternalsLoaded);
        Assert.Empty(vm.Worktrees);
    }

    /// <summary>The markup from an element's automation id onward, for asserting what gates it.</summary>
    private static string EmptyStateMarkup(string markup, string automationId)
    {
        var at = markup.IndexOf($"AutomationId=\"{automationId}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{automationId} is not in the markup");
        return markup[at..(at + Math.Min(600, markup.Length - at))];
    }

    private static string PageSource([System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages",
            "ProjectDetailPage.xaml"));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
