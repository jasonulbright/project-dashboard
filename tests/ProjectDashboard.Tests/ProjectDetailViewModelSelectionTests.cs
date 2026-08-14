using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Multi-select staging, the undo offers a reversible operation leaves, and the
/// selection half of focus restore. Every case runs against a real repository: what a
/// batch command does to the index is the whole point of it.
/// </summary>
public class ProjectDetailViewModelSelectionTests
{
    private sealed class ConfirmingViewModel(GitService git, bool answer)
        : ProjectDetailViewModel(null!, git, null!)
    {
        public string LastMessage { get; private set; } = "";
        public int Confirmations { get; private set; }

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastMessage = message;
            return Task.FromResult(answer);
        }
    }

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    /// <summary>Three tracked files, each with an unstaged edit.</summary>
    private static async Task<TempRepo> ThreeEditedFilesAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) repo.WriteFile(name, "one\n");
        await repo.CommitAllAsync("three files");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) repo.WriteFile(name, "two\n");
        return repo;
    }

    private static async Task<T> OpenAsync<T>(T vm, TempRepo repo) where T : ProjectDetailViewModel
    {
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        return vm;
    }

    private static void SelectUnstaged(ProjectDetailViewModel vm, params string[] paths) =>
        vm.SetUnstagedSelection(vm.UnstagedFiles.Where(f => paths.Contains(f.Path)).ToList(), null);

    [Fact]
    public async Task StagingASelection_StagesEveryFileInIt()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-stage");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        SelectUnstaged(vm, "a.txt", "c.txt");
        await vm.StageSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Stage done.", vm.SyncStatusText);
        Assert.Equal(["a.txt", "c.txt"], vm.StagedFiles.Select(f => f.Path).Order());
        Assert.Equal(["b.txt"], vm.UnstagedFiles.Select(f => f.Path));
    }

    [Fact]
    public async Task UnstagingASelection_UnstagesEveryFileInIt()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-unstage");
        await repo.GitAsync("add", "-A");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        vm.SetStagedSelection(vm.StagedFiles.Where(f => f.Path != "b.txt").ToList(), null);
        await vm.UnstageSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["b.txt"], vm.StagedFiles.Select(f => f.Path));
        Assert.Equal(["a.txt", "c.txt"], vm.UnstagedFiles.Select(f => f.Path).Order());
    }

    [Fact]
    public async Task DiscardingASelection_RevertsEveryFileInIt()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-discard");
        var vm = await OpenAsync(new ConfirmingViewModel(new GitService(), answer: true), repo);

        SelectUnstaged(vm, "a.txt", "b.txt");
        await vm.DiscardSelectedCommand.ExecuteAsync(null);

        Assert.Equal("one\n", repo.ReadFile("a.txt"));
        Assert.Equal("one\n", repo.ReadFile("b.txt"));
        Assert.Equal("two\n", repo.ReadFile("c.txt"));
    }

    [Fact]
    public async Task ARefusedDiscard_ChangesNothing()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-discard-no");
        var vm = await OpenAsync(new ConfirmingViewModel(new GitService(), answer: false), repo);

        SelectUnstaged(vm, "a.txt", "b.txt");
        await vm.DiscardSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Equal("two\n", repo.ReadFile("a.txt"));
    }

    /// <summary>
    /// A discard of several files is confirmed once, and the confirmation names what it is
    /// about to destroy: a Shift-selection can hold rows the reader never looked at.
    /// </summary>
    [Fact]
    public async Task TheDiscardConfirmation_NamesTheCountAndTheFiles()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-discard-text");
        var vm = await OpenAsync(new ConfirmingViewModel(new GitService(), answer: false), repo);

        SelectUnstaged(vm, "a.txt", "b.txt", "c.txt");
        await vm.DiscardSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("3 files", vm.LastMessage);
        Assert.Contains("a.txt", vm.LastMessage);
        Assert.Contains("c.txt", vm.LastMessage);
        Assert.Contains("cannot be undone", vm.LastMessage);
    }

    [Fact]
    public void ALongSelection_IsListedUpToACapAndThenCounted()
    {
        var files = Enumerable.Range(1, 30)
            .Select(i => new WorkingFile { Path = $"file{i:00}.txt", WorktreeStatus = 'M' })
            .ToList();

        var message = ProjectDetailViewModel.DiscardMessage(files);

        Assert.Contains("30 files", message);
        Assert.Contains("file01.txt", message);
        Assert.Contains("+22 more", message);
        Assert.DoesNotContain("file30.txt", message);
    }

    [Fact]
    public void UntrackedFilesInASelection_AreCalledOutAsDeletions()
    {
        var files = new List<WorkingFile>
        {
            new() { Path = "kept.txt", WorktreeStatus = 'M' },
            new() { Path = "new.txt", IsUntracked = true }
        };

        Assert.Contains("One of them is untracked and will be deleted.",
            ProjectDetailViewModel.DiscardMessage(files));
        Assert.Contains("All of them are untracked and will be deleted.",
            ProjectDetailViewModel.DiscardMessage([
                new WorkingFile { Path = "one.txt", IsUntracked = true },
                new WorkingFile { Path = "two.txt", IsUntracked = true }
            ]));
    }

    /// <summary>A single file keeps the wording it had before batching existed.</summary>
    [Fact]
    public void ASingleUntrackedFile_IsStillCalledADeletion()
    {
        var message = ProjectDetailViewModel.DiscardMessage(
            [new WorkingFile { Path = "new.txt", IsUntracked = true }]);

        Assert.Equal("Delete untracked file new.txt?\n\nThis cannot be undone.", message);
    }

    /// <summary>
    /// Every refresh builds new WorkingFile instances. A batch the reader assembled by hand
    /// must survive the refresh that the operation on it triggers, or the next action reads a
    /// selection the list no longer shows.
    /// </summary>
    [Fact]
    public async Task AMultiSelection_SurvivesARefresh()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-refresh");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        SelectUnstaged(vm, "a.txt", "c.txt");
        await vm.RefreshWorkingStateAsync();

        Assert.Equal(["a.txt", "c.txt"], vm.SelectedUnstagedFiles.Select(f => f.Path).Order());
        Assert.All(vm.SelectedUnstagedFiles, f => Assert.Contains(f, vm.UnstagedFiles));
    }

    [Fact]
    public async Task AFileThatLeftTheList_DropsOutOfTheSelection()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-refresh-gone");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        SelectUnstaged(vm, "a.txt", "b.txt");
        await repo.GitAsync("restore", "b.txt");
        await vm.RefreshWorkingStateAsync();

        Assert.Equal(["a.txt"], vm.SelectedUnstagedFiles.Select(f => f.Path));
    }

    /// <summary>
    /// Focus moving to the other list clears this one's selection outright: the buttons on
    /// each side read that side's whole selection, and one left standing arms an action the
    /// reader is no longer looking at.
    /// </summary>
    [Fact]
    public async Task FocusingTheOtherList_ClearsThisOnesSelection()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-exclusive");
        await repo.GitAsync("add", "a.txt");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        SelectUnstaged(vm, "b.txt", "c.txt");
        vm.SetStagedSelection([vm.StagedFiles.Single()], null);

        Assert.Empty(vm.SelectedUnstagedFiles);
        Assert.Null(vm.SelectedUnstagedFile);
        Assert.Single(vm.SelectedStagedFiles);
    }

    /// <summary>The diff pane follows the row just added, not the first row of the selection.</summary>
    [Fact]
    public async Task TheDiffPane_FollowsTheRowJustAdded()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-focus");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        var a = vm.UnstagedFiles.First(f => f.Path == "a.txt");
        var c = vm.UnstagedFiles.First(f => f.Path == "c.txt");
        vm.SetUnstagedSelection([a, c], c);

        Assert.Same(c, vm.SelectedUnstagedFile);
        await vm.DiffRefresh;
        Assert.Equal("c.txt", vm.DiffTitle);
    }

    [Fact]
    public async Task ClearingTheSelection_ClearsTheDiffPane()
    {
        using var repo = await ThreeEditedFilesAsync("vm-multi-clear");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        SelectUnstaged(vm, "a.txt");
        await vm.DiffRefresh;
        vm.SetUnstagedSelection([], null);

        Assert.Null(vm.SelectedUnstagedFile);
        Assert.Empty(vm.DiffLines);
        Assert.Equal("", vm.DiffTitle);
    }

    /// <summary>
    /// A reload of the same repository rebuilds the commit list from fresh objects. The
    /// selection is what the history surfaces read, so it is matched back by sha rather than
    /// dropped on every refresh the page performs.
    /// </summary>
    [Fact]
    public async Task TheSelectedCommit_SurvivesAReloadOfTheSameProject()
    {
        using var repo = await ThreeEditedFilesAsync("vm-commit-reselect");
        await repo.GitAsync("add", "-A");
        await repo.CommitAllAsync("second");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        var project = ProjectFor(repo);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path);
        await vm.SetProjectAsync(project);
        var wanted = vm.Commits.Last();
        vm.SelectedCommit = wanted;

        var reloaded = ProjectFor(repo);
        reloaded.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path);
        await vm.SetProjectAsync(reloaded);

        Assert.NotNull(vm.SelectedCommit);
        Assert.Equal(wanted.Ref, vm.SelectedCommit!.Ref);
        Assert.NotSame(wanted, vm.SelectedCommit);
    }

    /// <summary>A commit the reload no longer lists has no sha to match, and the selection clears.</summary>
    [Fact]
    public async Task ACommitTheReloadDropped_LeavesNoSelectionBehind()
    {
        using var repo = await ThreeEditedFilesAsync("vm-commit-gone");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        var project = ProjectFor(repo);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path);
        await vm.SetProjectAsync(project);
        vm.SelectedCommit = vm.Commits.First();

        var reloaded = ProjectFor(repo);
        reloaded.RecentCommits = [];
        await vm.SetProjectAsync(reloaded);

        Assert.Null(vm.SelectedCommit);
    }

    /// <summary>The subject picker reads the history already loaded — never a second git call.</summary>
    [Fact]
    public async Task TheRecentSubjects_ComeFromTheLoadedHistory()
    {
        using var repo = await ThreeEditedFilesAsync("vm-recent-subjects");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        var project = ProjectFor(repo);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path);
        await vm.SetProjectAsync(project);

        Assert.Contains("three files", vm.RecentSubjects);
        Assert.Equal(vm.RecentSubjects, vm.RecentSubjects.Distinct());
    }

    [Fact]
    public async Task PickingARecentSubject_FillsTheSubjectAndClearsThePicker()
    {
        using var repo = await ThreeEditedFilesAsync("vm-recent-pick");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        vm.CommitMessage = "draft\n\nbody kept";

        vm.SelectedRecentSubject = "three files";

        Assert.Equal("three files\n\nbody kept", vm.CommitMessage);
        Assert.Null(vm.SelectedRecentSubject);
    }

    [Fact]
    public async Task StagingEverything_OffersToUnstageItAgain()
    {
        using var repo = await ThreeEditedFilesAsync("vm-undo-stageall");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.True(vm.UndoOfferVisible);
        Assert.Equal("Unstage all", vm.UndoOfferLabel);

        await vm.RunUndoOfferCommand.ExecuteAsync(null);

        Assert.Empty(vm.StagedFiles);
        Assert.Equal(3, vm.UnstagedFiles.Count);
        Assert.False(vm.UndoOfferVisible);
    }

    /// <summary>
    /// Unstaging everything is the inverse of staging everything only from an empty index: it
    /// clears whatever else was staged first, which the offer would be claiming to restore. The
    /// offer is withheld rather than reworded, because no single command reverses the operation.
    /// </summary>
    [Fact]
    public async Task StagingEverythingOverAStagedFile_OffersNothing()
    {
        using var repo = await ThreeEditedFilesAsync("vm-undo-stageall-dirty");
        await repo.GitAsync("add", "b.txt");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        Assert.NotEmpty(vm.StagedFiles);

        await vm.StageAllCommand.ExecuteAsync(null);

        Assert.Equal("Stage all done.", vm.SyncStatusText);
        Assert.Equal(3, vm.StagedFiles.Count);
        Assert.False(vm.UndoOfferVisible);
    }

    [Fact]
    public async Task UnstagingEverything_OffersToStageItAgain()
    {
        using var repo = await ThreeEditedFilesAsync("vm-undo-unstageall");
        await repo.GitAsync("add", "-A");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        await vm.UnstageAllCommand.ExecuteAsync(null);
        Assert.Equal("Stage all again", vm.UndoOfferLabel);

        await vm.RunUndoOfferCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.StagedFiles.Count);
    }

    [Fact]
    public async Task StagingASelection_OffersToUnstageThoseFilesOnly()
    {
        using var repo = await ThreeEditedFilesAsync("vm-undo-selection");
        await repo.GitAsync("add", "b.txt");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        SelectUnstaged(vm, "a.txt", "c.txt");
        await vm.StageSelectedCommand.ExecuteAsync(null);
        Assert.Equal("Unstage those 2 files", vm.UndoOfferLabel);

        await vm.RunUndoOfferCommand.ExecuteAsync(null);

        // b.txt was staged before any of this and is not the offer's business.
        Assert.Equal(["b.txt"], vm.StagedFiles.Select(f => f.Path));
    }

    /// <summary>
    /// A discard removes content no git command can reconstruct, so nothing is offered after
    /// one — an offer there would promise a recovery that does not exist.
    /// </summary>
    [Fact]
    public async Task ADiscard_OffersNoUndo()
    {
        using var repo = await ThreeEditedFilesAsync("vm-undo-none");
        var vm = await OpenAsync(new ConfirmingViewModel(new GitService(), answer: true), repo);

        SelectUnstaged(vm, "a.txt");
        await vm.DiscardSelectedCommand.ExecuteAsync(null);

        Assert.False(vm.UndoOfferVisible);
    }

    /// <summary>The offer describes one outcome; the next operation replaces that outcome.</summary>
    [Fact]
    public async Task TheNextOperation_TakesTheOfferWithIt()
    {
        using var repo = await ThreeEditedFilesAsync("vm-undo-superseded");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.True(vm.UndoOfferVisible);

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.False(vm.UndoOfferVisible);
    }

    [Fact]
    public async Task SwitchingProjects_TakesTheOfferWithIt()
    {
        using var repoA = await ThreeEditedFilesAsync("vm-undo-switch-a");
        using var repoB = await ThreeEditedFilesAsync("vm-undo-switch-b");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repoA);

        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.True(vm.UndoOfferVisible);

        await vm.SetProjectAsync(ProjectFor(repoB));

        Assert.False(vm.UndoOfferVisible);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("unstage")]
    [InlineData("discard")]
    public async Task ACommandWithNothingSelected_SaysSoRatherThanDoingNothing(string verb)
    {
        using var repo = await ThreeEditedFilesAsync("vm-notice-empty");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        await (verb switch
        {
            "stage" => vm.StageSelectedCommand.ExecuteAsync(null),
            "unstage" => vm.UnstageSelectedCommand.ExecuteAsync(null),
            _ => vm.DiscardSelectedCommand.ExecuteAsync(null)
        });

        Assert.Equal($"Select a file to {verb} first.", vm.SyncStatusText);
    }

    [Fact]
    public async Task ACommandRefusedByTheBusyGate_SaysSo()
    {
        using var repo = await ThreeEditedFilesAsync("vm-notice-busy");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        vm.IsBusy = true;

        await vm.StageAllCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.BusyNotice("Stage all"), vm.SyncStatusText);
        Assert.Empty(vm.StagedFiles);
    }

    [Fact]
    public async Task SwitchingToABranchAlreadyCheckedOut_SaysSo()
    {
        using var repo = await ThreeEditedFilesAsync("vm-notice-branch");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        await vm.LoadBranchesCommand.ExecuteAsync(null);

        await vm.SwitchBranchCommand.ExecuteAsync(vm.Branches.Single(b => b.IsCurrent));

        Assert.StartsWith("Already on ", vm.SyncStatusText);
    }

    [Fact]
    public async Task CreatingABranchWithNoName_SaysSo()
    {
        using var repo = await ThreeEditedFilesAsync("vm-notice-noname");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        await vm.CreateBranchCommand.ExecuteAsync(null);

        Assert.Equal("Enter a branch name first.", vm.SyncStatusText);
    }

    // ── Ignoring a working file ─────────────────────────────────────────────

    /// <summary>A repository with one untracked file of each kind the ignore actions distinguish.</summary>
    private static async Task<TempRepo> UntrackedFilesAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("debug.log", "noise\n");
        repo.WriteFile("trace.log", "more noise\n");
        repo.WriteFile("Makefile", "all:\n");
        return repo;
    }

    private static void SelectOneUnstaged(ProjectDetailViewModel vm, string path) =>
        vm.SetUnstagedSelection([vm.UnstagedFiles.Single(f => f.Path == path)], null);

    [Fact]
    public async Task IgnoringAnUntrackedFile_WritesTheRuleAndTheFileLeavesTheList()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-file");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        SelectOneUnstaged(vm, "debug.log");

        await vm.IgnoreSelectedFileCommand.ExecuteAsync(null);

        Assert.Contains("/debug.log\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, ".gitignore")));
        Assert.DoesNotContain(vm.UnstagedFiles, f => f.Path == "debug.log");
        // Only that file: the rule names one path, not a shape.
        Assert.Contains(vm.UnstagedFiles, f => f.Path == "trace.log");
        Assert.Contains("/debug.log", vm.SyncStatusText);
    }

    [Fact]
    public async Task IgnoringByExtension_WritesTheGlobAndEveryFileOfThatKindLeaves()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-ext");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        SelectOneUnstaged(vm, "debug.log");

        await vm.IgnoreSelectedExtensionCommand.ExecuteAsync(null);

        Assert.Contains("*.log\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, ".gitignore")));
        Assert.DoesNotContain(vm.UnstagedFiles, f => f.Path == "debug.log");
        Assert.DoesNotContain(vm.UnstagedFiles, f => f.Path == "trace.log");
        Assert.Contains(vm.UnstagedFiles, f => f.Path == "Makefile");
    }

    [Fact]
    public async Task IgnoringByExtension_AFileWithoutOne_IsRefused()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-noext");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        SelectOneUnstaged(vm, "Makefile");

        await vm.IgnoreSelectedExtensionCommand.ExecuteAsync(null);

        Assert.Contains("no extension", vm.SyncStatusText);
        Assert.False(File.Exists(Path.Combine(repo.Path, ".gitignore")));
    }

    /// <summary>
    /// git keeps tracking a file already in the index whatever .gitignore says, so the rule would
    /// change nothing and the row would stay where it is. The refusal names that, rather than
    /// writing a line and leaving the reader to work out why the file did not move.
    /// </summary>
    [Fact]
    public async Task IgnoringATrackedFile_IsRefusedAndSaysTheFileStaysUntilItIsUntracked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("vm-ignore-tracked");
        repo.WriteFile("kept.log", "one\n");
        await repo.CommitAllAsync("track a log");
        repo.WriteFile("kept.log", "two\n");

        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        SelectOneUnstaged(vm, "kept.log");

        await vm.IgnoreSelectedFileCommand.ExecuteAsync(null);

        Assert.Contains("tracked", vm.SyncStatusText);
        Assert.Contains("untrack", vm.SyncStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(repo.Path, ".gitignore")));
        Assert.Contains(vm.UnstagedFiles, f => f.Path == "kept.log");
    }

    /// <summary>
    /// The list is a read taken at some earlier moment; a rule added since covers the row before
    /// the click lands. A second identical line is not written, and the reader is told why.
    /// </summary>
    [Fact]
    public async Task IgnoringAFileARuleAlreadyCovers_IsRefusedRatherThanAppendingASecondLine()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-already");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);
        SelectOneUnstaged(vm, "debug.log");

        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".gitignore"), "*.log\n");

        await vm.IgnoreSelectedFileCommand.ExecuteAsync(null);

        Assert.Contains("already ignored", vm.SyncStatusText);
        Assert.Equal("*.log\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, ".gitignore")));
    }

    /// <summary>
    /// Routes the global-excludes location to a test-owned file and answers the confirmation
    /// without a window — the real global config must never be read or written by a test.
    /// </summary>
    private sealed class FixedExcludesGitService(string? excludesPath) : GitService
    {
        public override Task<string?> GetGlobalExcludesPathAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult(excludesPath);
    }

    private sealed class GlobalIgnoreViewModel(GitService git, bool answer) : ProjectDetailViewModel(null!, git, null!)
    {
        public int Confirmations { get; private set; }

        public string LastMessage { get; private set; } = "";

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastMessage = message;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task IgnoringANameEverywhere_ConfirmsThenWritesTheUnanchoredNameToTheExcludesFile()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-global");
        var excludes = Path.Combine(TestEnv.NewDir("global-excludes"), "git", "ignore");
        var vm = await OpenAsync(new GlobalIgnoreViewModel(new FixedExcludesGitService(excludes), answer: true), repo);
        SelectOneUnstaged(vm, "debug.log");

        await vm.IgnoreSelectedFileEverywhereCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains(excludes, vm.LastMessage);
        // The name, unanchored — the global file has no repository root for a path to anchor to.
        Assert.Equal("debug.log\n", await File.ReadAllTextAsync(excludes));
        Assert.Contains(excludes, vm.SyncStatusText);
    }

    [Fact]
    public async Task IgnoringEverywhere_Declined_WritesNothing()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-global-no");
        var excludes = Path.Combine(TestEnv.NewDir("global-excludes-no"), "git", "ignore");
        var vm = await OpenAsync(new GlobalIgnoreViewModel(new FixedExcludesGitService(excludes), answer: false), repo);
        SelectOneUnstaged(vm, "debug.log");

        await vm.IgnoreSelectedExtensionEverywhereCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.False(File.Exists(excludes));
    }

    /// <summary>An unanswerable location refuses before the confirmation — there is nothing to name in one.</summary>
    [Fact]
    public async Task IgnoringEverywhere_WhenTheExcludesLocationCannotBeRead_RefusesAndSaysSo()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-global-unknown");
        var vm = await OpenAsync(new GlobalIgnoreViewModel(new FixedExcludesGitService(null), answer: true), repo);
        SelectOneUnstaged(vm, "debug.log");

        await vm.IgnoreSelectedFileEverywhereCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Confirmations);
        Assert.Contains("could not be read", vm.SyncStatusText);
    }

    [Fact]
    public async Task IgnoringWithNothingSelected_SaysSoRatherThanDoingNothing()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-none");
        var vm = await OpenAsync(new ProjectDetailViewModel(null!, new GitService(), null!), repo);

        await vm.IgnoreSelectedFileCommand.ExecuteAsync(null);

        Assert.Equal("Select a file to ignore first.", vm.SyncStatusText);
    }

    /// <summary>
    /// The append is a working-tree write and goes through the same runner every other one does,
    /// so the ledger gets it without the command recording anything of its own.
    /// </summary>
    [Fact]
    public async Task IgnoringAFile_IsRecordedByTheSameRunnerEveryWorkingWriteUses()
    {
        using var repo = await UntrackedFilesAsync("vm-ignore-ledger");
        var history = new Services.Safety.OperationHistory(TestEnv.NewDir("ignore-ops"));
        var vm = await OpenAsync(
            new ProjectDetailViewModel(null!, new GitService(), null!, history: history), repo);
        SelectOneUnstaged(vm, "debug.log");

        await vm.IgnoreSelectedFileCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(Services.Safety.OperationCategory.Working, record.Category);
        Assert.Equal(Services.Safety.OperationOutcome.Succeeded, record.Outcome);
        Assert.Contains("/debug.log", record.Label);
    }
}
