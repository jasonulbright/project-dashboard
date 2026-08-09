using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Hunk staging as the Changes tab drives it: the diff pane's selected row names a hunk,
/// and the operation slices that hunk out of a freshly read raw diff.
///
/// The service's own round-trips are proven in <see cref="GitServiceHunkTests"/>; what is proven
/// here is everything between the reader and that service — which hunk a row names, which
/// direction each side allows, that a stale view is refused instead of applied, and that the
/// refresh a hunk operation triggers does not throw the reader back to the top of the diff.
/// </summary>
public class ProjectDetailViewModelHunkTests
{
    private const string FifteenLines =
        "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nl15\n";
    // First and last lines edited; 3 lines of context leaves two separate hunks.
    private const string FifteenEdited =
        "L1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n";
    // The same two hunks with the FIRST one's content changed again, in place: the line counts
    // hold, so every hunk header is the one the pane is already showing.
    private const string FifteenEditedAgain =
        "X1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n";
    // And with the LAST one's content changed again instead.
    private const string FifteenEditedTail =
        "L1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nX15\n";
    // A third state of the first hunk, so a burst of refreshes has two answers to tell apart.
    private const string FifteenEditedOnceMore =
        "Y1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n";

    /// <summary>
    /// Twenty-five numbered lines with the named ones edited in place. Three edits spaced past
    /// the context window leave three independent hunks, which is one more than a discard needs
    /// to leave the pane a choice of rows to land on.
    /// </summary>
    private static string TwentyFive(char marker, params int[] edited)
    {
        var text = new System.Text.StringBuilder();
        for (var n = 1; n <= 25; n++) text.Append(edited.Contains(n) ? $"{marker}{n}\n" : $"l{n}\n");
        return text.ToString();
    }

    private static string TwentyFive(params int[] edited) => TwentyFive('L', edited);

    private sealed class ConfirmingViewModel(GitService git, bool answer)
        : ProjectDetailViewModel(null!, git, null!)
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

    /// <summary>Confirms without a dialog and posts inline, so a watcher signal reaches the page.</summary>
    private sealed class ConfirmingWatchedViewModel(GitService git)
        : ProjectDetailViewModel(null!, git, null!, uiPost: callback => callback())
    {
        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Answers the confirmation only once an edit made outside the app has been signalled and
    /// the refresh it triggers has finished — the interleave a dialog that holds no busy gate
    /// is open for.
    /// </summary>
    private sealed class RefreshingConfirmViewModel(GitService git, Action edit, string repoDir)
        : ProjectDetailViewModel(null!, git, null!, uiPost: callback => callback())
    {
        internal override async Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            edit();
            OnWatchedReposChanged([repoDir]);
            await WatcherRefresh;
            return true;
        }
    }

    /// <summary>
    /// Holds `git status` and `git diff` reads open until the test lets each answer, and answers
    /// with what the repository held when that read was TAKEN. Two things need this. Holding a
    /// status read open is how two watcher signals are made to join ONE working-state read, which
    /// is the interleave that resumes both of them together. Holding a diff read open then keeps
    /// the pane's first read in flight while the second signal resumes, so whether a second diff
    /// read joins it beside it is decided structurally rather than by how fast git is.
    /// Reads taken before <see cref="Armed"/> is set run ungated: a test's setup is not a set of
    /// gates to manage.
    /// </summary>
    private sealed class GatedDiffGitService : GitService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _release = [];
        private readonly List<TaskCompletionSource> _taken = [];
        private int _inFlight;

        public bool Armed { get; set; }

        public int DiffReads { get; private set; }
        public int StatusReads { get; private set; }

        /// <summary>The most diff reads ever in flight together. One means they were serialized.</summary>
        public int PeakConcurrentDiffReads { get; private set; }

        /// <summary>Completes once the nth gated diff read (1-based) has read the repository.</summary>
        public Task DiffTaken(int read)
        {
            lock (_gate) { Grow(_taken, read); return _taken[read - 1].Task; }
        }

        /// <summary>Lets the nth gated diff read answer. A read not yet issued is released in advance.</summary>
        public void ReleaseDiff(int read)
        {
            lock (_gate) { Grow(_release, read); _release[read - 1].TrySetResult(); }
        }

        private readonly TaskCompletionSource _statusGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets every held status read answer.</summary>
        public void ReleaseStatus() => _statusGate.TrySetResult();

        private readonly TaskCompletionSource _firstStatusTaken =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a gated status read is waiting on <see cref="ReleaseStatus"/>.</summary>
        public Task StatusHeld => _firstStatusTaken.Task;

        private static void Grow(List<TaskCompletionSource> slots, int upTo)
        {
            while (slots.Count < upTo)
                slots.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            if (!Armed) return await base.RunAsync(repoPath, argv, environment, ct, timeout);

            if (argv is ["status", ..])
            {
                lock (_gate) StatusReads++;
                var status = await base.RunAsync(repoPath, argv, environment, ct, timeout);
                _firstStatusTaken.TrySetResult();
                await _statusGate.Task;
                return status;
            }

            if (argv is not ["diff", "--no-color", ..])
                return await base.RunAsync(repoPath, argv, environment, ct, timeout);

            int read;
            Task release;
            lock (_gate)
            {
                read = ++DiffReads;
                PeakConcurrentDiffReads = Math.Max(PeakConcurrentDiffReads, ++_inFlight);
                Grow(_taken, read);
                Grow(_release, read);
                release = _release[read - 1].Task;
            }

            var captured = await base.RunAsync(repoPath, argv, environment, ct, timeout);
            lock (_gate) _taken[read - 1].TrySetResult();
            await release;
            lock (_gate) _inFlight--;
            return captured;
        }
    }

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    /// <summary>Repo whose file.txt has two independent hunks pending in the working tree.</summary>
    private static async Task<TempRepo> TwoHunkRepoAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("file.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        repo.WriteFile("file.txt", FifteenEdited);
        return repo;
    }

    /// <summary>Two tracked files, each carrying the same two independent pending hunks.</summary>
    private static async Task<TempRepo> TwoFileTwoHunkRepoAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("file.txt", FifteenLines);
        repo.WriteFile("other.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines each");
        repo.WriteFile("file.txt", FifteenEdited);
        repo.WriteFile("other.txt", FifteenEdited);
        return repo;
    }

    /// <summary>Opens the pane on the unstaged side of file.txt and lands on the given hunk's header.</summary>
    private static async Task SelectUnstagedHunkAsync(ProjectDetailViewModel vm, int hunkIndex)
    {
        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "file.txt");
        await vm.DiffRefresh;
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.IsHunkStart && l.HunkIndex == hunkIndex);
    }

    private static bool DiffTouches(IEnumerable<DiffLine> lines, string text) =>
        lines.Any(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed && l.Text == text);

    [Fact]
    public async Task StageHunk_StagesOnlyTheHunkTheSelectedRowNames()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-stage");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 0);
        Assert.Null(vm.StageHunkBlockedReason);
        await vm.StageHunkCommand.ExecuteAsync(null);

        var git = new GitService();
        var state = await git.GetWorkingStateAsync(repo.Path);
        var stagedDiff = await git.GetFileDiffAsync(repo.Path, state!.Staged.Single(), staged: true);
        Assert.True(DiffTouches(stagedDiff!.Lines, "L1"));
        Assert.False(DiffTouches(stagedDiff.Lines, "L15"));
    }

    /// <summary>
    /// Staging refreshes the working state, which rebuilds every diff row. A reader who staged
    /// the third hunk of a long file must not be returned to the first.
    /// </summary>
    [Fact]
    public async Task StageHunk_LeavesThePaneOnAHunkRowRatherThanAtTheTop()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-focus");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 1);
        await vm.StageHunkCommand.ExecuteAsync(null);
        await vm.DiffRefresh;

        // Only the first hunk is left unstaged, and the pane is on its header — not on nothing.
        Assert.NotNull(vm.SelectedDiffLine);
        Assert.True(vm.SelectedDiffLine!.IsHunkStart);
        Assert.True(DiffTouches(vm.DiffLines, "L1"));
        Assert.False(DiffTouches(vm.DiffLines, "L15"));
    }

    [Fact]
    public async Task UnstageHunk_ReversesThatHunkOutOfTheIndex()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-unstage");
        var git = new GitService();
        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 0);
        await vm.StageHunkCommand.ExecuteAsync(null);

        vm.SelectedStagedFile = vm.StagedFiles.First(f => f.Path == "file.txt");
        await vm.DiffRefresh;
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.IsHunkStart);
        Assert.Null(vm.UnstageHunkBlockedReason);
        await vm.UnstageHunkCommand.ExecuteAsync(null);

        var state = await git.GetWorkingStateAsync(repo.Path);
        Assert.Empty(state!.Staged);
    }

    [Fact]
    public async Task DiscardHunk_IsConfirmedAndRevertsOnlyThatHunk()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-discard");
        var vm = new ConfirmingViewModel(new GitService(), answer: true);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 0);
        await vm.DiscardHunkCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("file.txt", vm.LastMessage);
        var content = repo.ReadFile("file.txt");
        Assert.StartsWith("l1\n", content);
        Assert.EndsWith("L15\n", content);
    }

    [Fact]
    public async Task DiscardHunk_DeclinedChangesNothing()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-decline");
        var vm = new ConfirmingViewModel(new GitService(), answer: false);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 0);
        await vm.DiscardHunkCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Equal(FifteenEdited, repo.ReadFile("file.txt"));
    }

    /// <summary>
    /// A hunk index survives an edit that renumbers the file's hunks. Slicing index N out of a
    /// diff the reader never saw would stage a change they never looked at, so the slice's own
    /// header is checked against the row on screen first.
    /// </summary>
    [Fact]
    public async Task StageHunk_RefusesWhenTheFileChangedUnderTheRenderedDiff()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-stale");
        var git = new GitService();
        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 0);
        // Ten lines removed from the top: what was hunk 0 keeps its index and loses its ranges.
        repo.WriteFile("file.txt", "L1\nl12\nl13\nl14\nL15\n");

        await vm.StageHunkCommand.ExecuteAsync(null);

        Assert.Contains("changed since this diff was shown", vm.SyncStatusText);
        var state = await git.GetWorkingStateAsync(repo.Path);
        Assert.Empty(state!.Staged);
    }

    [Fact]
    public async Task HunkActions_AreRefusedOnTheSideThatCannotPerformThem()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-sides");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 0);
        Assert.Null(vm.StageHunkBlockedReason);
        Assert.Null(vm.DiscardHunkBlockedReason);
        Assert.Equal("This hunk is not staged yet.", vm.UnstageHunkBlockedReason);

        await vm.StageHunkCommand.ExecuteAsync(null);
        vm.SelectedStagedFile = vm.StagedFiles.First(f => f.Path == "file.txt");
        await vm.DiffRefresh;
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.IsHunkStart);

        Assert.Null(vm.UnstageHunkBlockedReason);
        Assert.Equal("This hunk is already staged.", vm.StageHunkBlockedReason);
        Assert.Equal("Unstage this hunk first — discard works on the working tree.", vm.DiscardHunkBlockedReason);
    }

    /// <summary>
    /// An untracked file's pane shows a synthesized preview, not a diff git can slice. Its rows
    /// carry no hunk index, and the actions say why rather than failing on an empty patch.
    /// </summary>
    [Fact]
    public async Task HunkActions_AreRefusedForAnUntrackedFile()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("vm-hunk-untracked");
        repo.WriteFile("fresh.txt", "one\ntwo\n");

        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "fresh.txt");
        await vm.DiffRefresh;

        Assert.DoesNotContain(vm.DiffLines, l => l.IsHunkStart);
        Assert.Equal("An untracked file has no hunks — stage the whole file instead.", vm.StageHunkBlockedReason);
        Assert.False(vm.StageHunkCommand.CanExecute(null));
    }

    [Fact]
    public void HunkActions_SayWhatToDoFirstWhenNothingIsSelected()
    {
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        Assert.Equal("Select a changed file first.", vm.StageHunkBlockedReason);
        Assert.False(vm.StageHunkCommand.CanExecute(null));
        Assert.False(vm.UnstageHunkCommand.CanExecute(null));
        Assert.False(vm.DiscardHunkCommand.CanExecute(null));
    }

    /// <summary>
    /// A path is a pathspec to git, so a name holding a bracket range also selects the paths it
    /// globs — here its own sibling, which sorts first. The pane would then render the sibling's
    /// rows under the selected file's title, and every hunk index would name the sibling's hunk.
    /// </summary>
    private static async Task<TempRepo> GlobNameRepoAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("notes1.txt", "sibling one\n");
        repo.WriteFile("notes[1].txt", "bracket one\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "both files");
        repo.WriteFile("notes1.txt", "SIBLING TWO\n");
        repo.WriteFile("notes[1].txt", "BRACKET TWO\n");
        return repo;
    }

    [Fact]
    public async Task TheDiffPane_ShowsTheSelectedFileNotThePathItGlobs()
    {
        using var repo = await GlobNameRepoAsync("vm-hunk-glob-pane");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "notes[1].txt");
        await vm.DiffRefresh;

        Assert.Equal("notes[1].txt", vm.DiffTitle);
        Assert.True(DiffTouches(vm.DiffLines, "BRACKET TWO"));
        Assert.False(DiffTouches(vm.DiffLines, "SIBLING TWO"));
    }

    /// <summary>
    /// The irreversible one: the content a discard removes was never committed and is in no
    /// index, and the confirmation names the file the reader picked.
    /// </summary>
    [Fact]
    public async Task DiscardHunk_RevertsTheSelectedFileNotThePathItGlobs()
    {
        using var repo = await GlobNameRepoAsync("vm-hunk-glob-discard");
        var vm = new ConfirmingViewModel(new GitService(), answer: true);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "notes[1].txt");
        await vm.DiffRefresh;
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.IsHunkStart);
        await vm.DiscardHunkCommand.ExecuteAsync(null);

        Assert.Contains("notes[1].txt", vm.LastMessage);
        Assert.Equal("bracket one\n", repo.ReadFile("notes[1].txt"));
        Assert.Equal("SIBLING TWO\n", repo.ReadFile("notes1.txt"));
    }

    /// <summary>
    /// The pane reads a staged rename with both of its paths and gets the rename diff; the slice
    /// is read with the new path alone and gets a whole-file add. No header can match across the
    /// two, so every operation would refuse with a staleness message that is not true — and a
    /// reverse-applied add would unstage the rename rather than the hunk. The gate says what it is.
    /// </summary>
    [Fact]
    public async Task HunkActions_AreRefusedForAStagedRename()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("vm-hunk-rename");
        repo.WriteFile("before.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        await repo.GitAsync("mv", "before.txt", "after.txt");
        repo.WriteFile("after.txt", FifteenEdited);
        await repo.GitAsync("add", "after.txt");

        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        var renamed = vm.StagedFiles.First(f => f.Path == "after.txt");
        Assert.Equal("before.txt", renamed.OrigPath);
        vm.SelectedStagedFile = renamed;
        await vm.DiffRefresh;
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.IsHunkStart);

        const string expected = "This is a staged rename — unstage the file to work on its hunks.";
        Assert.Equal(expected, vm.StageHunkBlockedReason);
        Assert.Equal(expected, vm.UnstageHunkBlockedReason);
        Assert.Equal(expected, vm.DiscardHunkBlockedReason);
        Assert.False(vm.UnstageHunkCommand.CanExecute(null));
    }

    /// <summary>Rename staged on its own, then edited again: one file on both sides at once.</summary>
    private static async Task<TempRepo> RenamedAndEditedRepoAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("before.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        await repo.GitAsync("mv", "before.txt", "after.txt");
        repo.WriteFile("after.txt", FifteenEdited);
        return repo;
    }

    /// <summary>
    /// The rename lives in the index, so the unstaged side of the same file is the worktree edit
    /// and nothing else: its pane read and its slice read return the same diff, and the headers
    /// match. Refusing there would offer advice — unstage the file — that undoes the rename.
    /// </summary>
    [Fact]
    public async Task HunkActions_AreAllowedOnTheUnstagedSideOfAStagedRename()
    {
        using var repo = await RenamedAndEditedRepoAsync("vm-hunk-rename-unstaged");
        var git = new GitService();
        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        var renamed = vm.UnstagedFiles.First(f => f.Path == "after.txt");
        Assert.Equal("before.txt", renamed.OrigPath);
        Assert.Contains(vm.StagedFiles, f => f.Path == "after.txt");

        vm.SelectedUnstagedFile = renamed;
        await vm.DiffRefresh;
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.IsHunkStart && l.HunkIndex == 0);

        Assert.Null(vm.StageHunkBlockedReason);
        Assert.Null(vm.DiscardHunkBlockedReason);
        // The one reason left on this side is the direction, not the rename.
        Assert.Equal("This hunk is not staged yet.", vm.UnstageHunkBlockedReason);

        await vm.StageHunkCommand.ExecuteAsync(null);

        var state = await git.GetWorkingStateAsync(repo.Path);
        var staged = state!.Staged.Single(f => f.Path == "after.txt");
        Assert.Equal("before.txt", staged.OrigPath);
        var stagedDiff = await git.GetFileDiffAsync(repo.Path, staged, staged: true);
        Assert.True(DiffTouches(stagedDiff!.Lines, "L1"));
        Assert.False(DiffTouches(stagedDiff.Lines, "L15"));

        var unstagedDiff = await git.GetFileDiffAsync(repo.Path, state.Unstaged.Single(f => f.Path == "after.txt"), staged: false);
        Assert.True(DiffTouches(unstagedDiff!.Lines, "L15"));
    }

    /// <summary>
    /// The hunk a refresh should land on belongs to one file and one side. A quick switch to
    /// another file must not consume it: the same index there names a change the reader never
    /// staged, discarded, or looked at.
    /// </summary>
    [Fact]
    public async Task AHunkOperationsFocus_IsNotRestoredOntoADifferentFile()
    {
        using var repo = await TwoFileTwoHunkRepoAsync("vm-hunk-focus-switch");

        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 1);
        await vm.StageHunkCommand.ExecuteAsync(null);

        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "other.txt");
        await vm.DiffRefresh;

        Assert.Null(vm.SelectedDiffLine);
    }

    /// <summary>
    /// The gates read the selected row, and a row of the file the pane was showing before names
    /// a hunk of the new one the moment the selection moves. The row is dropped as the selection
    /// changes, not once the read that replaces the pane returns.
    /// </summary>
    [Fact]
    public async Task SwitchingFiles_DropsTheSelectedRowBeforeTheNewDiffIsRead()
    {
        using var repo = await TwoFileTwoHunkRepoAsync("vm-hunk-row-switch");

        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        await SelectUnstagedHunkAsync(vm, 1);
        Assert.NotNull(vm.SelectedDiffLine);

        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "other.txt");
        Assert.Null(vm.SelectedDiffLine);
        Assert.Equal("Select a line inside a hunk first.", vm.StageHunkBlockedReason);

        await vm.DiffRefresh;
    }

    // ── A refresh landing while a hunk dialog is open ────────────────────────────

    /// <summary>
    /// The confirmation names one hunk of one file, and OK has to run that hunk. The dialog
    /// holds no busy gate, so a refresh — an external edit anywhere else in the repository —
    /// lands while it is open and rebuilds the rows the pane is holding. An operation that
    /// re-reads the live selection after the dialog finds none and returns silently: a
    /// confirmed, irreversible action that reports neither success nor refusal.
    /// </summary>
    [Fact]
    public async Task AConfirmedHunkDiscard_RunsTheHunkTheDialogNamedThroughARefreshThatLandsWhileItIsOpen()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-discard-refresh");
        var vm = new RefreshingConfirmViewModel(
            new GitService(),
            () => repo.WriteFile("elsewhere.txt", "touched outside the app\n"),
            Path.GetFileName(repo.Path));
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 0);

        await vm.DiscardHunkCommand.ExecuteAsync(null);

        // The first hunk is reverted and the second is left exactly as it was.
        var text = repo.ReadFile("file.txt");
        Assert.StartsWith("l1\n", text);
        Assert.EndsWith("L15\n", text);
    }

    /// <summary>
    /// The harder interleave of the same shape: the edit landing while the dialog is open is to
    /// the SHOWN file, so the refresh re-renders the pane the reader chose the hunk in and every
    /// row behind the dialog is replaced. No gate is needed for it — the operation captured its
    /// file, side, hunk and header before the dialog opened, and the fresh raw read it applies
    /// is checked against that captured header — so what OK runs is still the hunk the
    /// confirmation named, and the edit made meanwhile is left alone.
    /// </summary>
    [Fact]
    public async Task AConfirmedHunkDiscard_RunsTheHunkTheDialogNamedThroughAReRenderOfItsOwnPane()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-discard-rerender");
        var vm = new RefreshingConfirmViewModel(
            new GitService(),
            () => repo.WriteFile("file.txt", FifteenEditedTail),
            Path.GetFileName(repo.Path));
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 0);

        await vm.DiscardHunkCommand.ExecuteAsync(null);

        // The confirmed hunk is reverted; the second hunk carries the edit made behind the dialog.
        var text = repo.ReadFile("file.txt");
        Assert.StartsWith("l1\n", text);
        Assert.EndsWith("X15\n", text);
    }

    /// <summary>
    /// A refresh caused by an edit to another file moves nothing about the file the pane is
    /// showing, so it keeps its rows and the hunk the reader is on. The re-read taken of it
    /// describes what is already on screen and writes nothing: rebuilding the rows would throw
    /// the reader back to the top of the diff on every unrelated save in the repository.
    /// </summary>
    [Fact]
    public async Task ARefreshForAnEditElsewhere_KeepsTheRowsAndTheHunkThePaneIsShowing()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-unrelated-refresh");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, uiPost: callback => callback());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 1);

        var row = vm.SelectedDiffLine;
        var rows = vm.DiffLines;
        repo.WriteFile("elsewhere.txt", "touched outside the app\n");
        vm.OnWatchedReposChanged([Path.GetFileName(repo.Path)]);
        await vm.WatcherRefresh;

        Assert.Equal(2, vm.UnstagedFiles.Count);
        Assert.Same(row, vm.SelectedDiffLine);
        Assert.Same(rows, vm.DiffLines);
        Assert.Null(vm.StageHunkBlockedReason);
    }

    /// <summary>
    /// The other direction: the file the pane is showing really did move — staged from a
    /// terminal, it is no longer on the unstaged side — and the selection and the diff of it
    /// go with it rather than describing a row that is gone.
    /// </summary>
    [Fact]
    public async Task ARefreshAfterTheShownFileMoves_DropsTheSelectionAndItsDiff()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-moved-refresh");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, uiPost: callback => callback());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 1);

        await repo.GitAsync("add", "file.txt");
        vm.OnWatchedReposChanged([Path.GetFileName(repo.Path)]);
        await vm.WatcherRefresh;

        Assert.Empty(vm.UnstagedFiles);
        Assert.Null(vm.SelectedUnstagedFile);
        Assert.Null(vm.SelectedDiffLine);
        Assert.Empty(vm.DiffLines);
    }

    /// <summary>
    /// The case neither of the two above covers: the file the pane is showing was edited outside
    /// the app and stayed on the same side of the index in the same state. Nothing about the row
    /// it is listed as moved, so no selection handler fires and the pane would go on rendering
    /// the file as it was until the reader reselected it or pressed refresh.
    /// </summary>
    [Fact]
    public async Task ARefreshAfterTheShownFileIsEditedOutside_ReRendersItAndKeepsTheHunkTheReaderIsOn()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-shown-edited");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, uiPost: callback => callback());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 1);

        var shownFile = vm.SelectedUnstagedFile;
        var header = vm.SelectedDiffLine!.Text;
        Assert.True(DiffTouches(vm.DiffLines, "L1"));

        // In place, so the file keeps its status and its row: the first hunk's content changes
        // and every hunk header — the second one's included — is the one already on screen.
        repo.WriteFile("file.txt", FifteenEditedAgain);
        vm.OnWatchedReposChanged([Path.GetFileName(repo.Path)]);
        await vm.WatcherRefresh;

        // Carried forward by the working-state read, and re-rendered anyway.
        Assert.Same(shownFile, vm.SelectedUnstagedFile);
        Assert.True(DiffTouches(vm.DiffLines, "X1"));
        Assert.False(DiffTouches(vm.DiffLines, "L1"));

        // The reader is still on their hunk — matched back by its header, since every row is new.
        Assert.NotNull(vm.SelectedDiffLine);
        Assert.True(vm.SelectedDiffLine!.IsHunkStart);
        Assert.Equal(header, vm.SelectedDiffLine.Text);
        Assert.Equal(1, vm.SelectedDiffLine.HunkIndex);
        Assert.Null(vm.StageHunkBlockedReason);
    }

    /// <summary>
    /// The same re-render when the hunk the reader was on is not in the re-read at all: an
    /// outside edit reverted it. Landing on whatever hunk now sits at that index would arm the
    /// staging buttons on a change the reader never chose, so the pane lands on no row.
    /// </summary>
    [Fact]
    public async Task ARefreshAfterTheShownHunkIsRevertedOutside_ClearsTheSelectionRatherThanMovingIt()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-shown-reverted");
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, uiPost: callback => callback());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 0);

        // Only the last line stays edited, so the hunk the pane is on is gone from the file.
        repo.WriteFile("file.txt", "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n");
        vm.OnWatchedReposChanged([Path.GetFileName(repo.Path)]);
        await vm.WatcherRefresh;

        Assert.False(DiffTouches(vm.DiffLines, "L1"));
        Assert.True(DiffTouches(vm.DiffLines, "L15"));
        Assert.Null(vm.SelectedDiffLine);
        Assert.Equal("Select a line inside a hunk first.", vm.StageHunkBlockedReason);
    }

    /// <summary>
    /// A hunk operation leaves the file on the side it was already on whenever hunks of it
    /// remain, so the working-state read carries its row forward and nothing re-renders the
    /// pane. The rows would then still show the hunk the operation just reverted.
    /// </summary>
    [Fact]
    public async Task DiscardHunk_ReRendersThePaneItLeftOnTheSameSideOfTheIndex()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-discard-rerender-pane");
        var vm = new ConfirmingViewModel(new GitService(), answer: true);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await SelectUnstagedHunkAsync(vm, 0);

        await vm.DiscardHunkCommand.ExecuteAsync(null);
        await vm.DiffRefresh;

        // file.txt is still unstaged and still selected — and the reverted hunk is off the pane.
        Assert.Same(vm.UnstagedFiles.Single(f => f.Path == "file.txt"), vm.SelectedUnstagedFile);
        Assert.False(DiffTouches(vm.DiffLines, "L1"));
        Assert.True(DiffTouches(vm.DiffLines, "L15"));
        // The hunk that followed the reverted one is where the operation left the reader.
        Assert.NotNull(vm.SelectedDiffLine);
        Assert.True(vm.SelectedDiffLine!.IsHunkStart);
    }

    /// <summary>
    /// Two signals of one burst join the same working-state read, so both resume together. Run
    /// side by side their diff reads both write the pane, and the one taken first can answer
    /// last — leaving the rows describing a file the repository has already moved past, with no
    /// further signal coming to correct it. The second follow-up is owed behind the first
    /// instead, so the last write is always the newest read.
    /// </summary>
    [Fact]
    public async Task TwoRefreshesInOneBurst_LeaveThePaneOnTheNewestReadNotTheOneThatAnsweredLast()
    {
        using var repo = await TwoHunkRepoAsync("vm-hunk-burst-order");
        using var pump = new SingleThreadContext();
        var git = new GatedDiffGitService();

        pump.Run(async () =>
        {
            var vm = new ProjectDetailViewModel(null!, git, null!, uiPost: callback => callback());
            await vm.SetProjectAsync(ProjectFor(repo));
            await vm.WorkingStateRefresh;
            await SelectUnstagedHunkAsync(vm, 1);

            var header = vm.SelectedDiffLine!.Text;
            var dir = Path.GetFileName(repo.Path);
            repo.WriteFile("file.txt", FifteenEditedAgain);
            git.Armed = true;

            // Both signals of the burst join ONE working-state read, held open here, so both are
            // resumed by the same completion — the interleave the pane's read has to survive.
            vm.OnWatchedReposChanged([dir]);
            await git.StatusHeld;
            vm.OnWatchedReposChanged([dir]);
            git.ReleaseStatus();

            // The pane's first read is taken on X1 and held; a second one issued beside it is
            // taken on X1 too, and neither would ever see what the file becomes next.
            await git.DiffTaken(1);
            repo.WriteFile("file.txt", FifteenEditedOnceMore);
            git.ReleaseDiff(1);
            git.ReleaseDiff(2);

            await vm.WatcherRefresh;
            await vm.DiffRefresh;

            // The pane ends on what the file holds now, not on the answer that arrived last.
            Assert.True(DiffTouches(vm.DiffLines, "Y1"));
            Assert.False(DiffTouches(vm.DiffLines, "X1"));
            // And the burst still left the reader on their hunk.
            Assert.Equal(header, vm.SelectedDiffLine?.Text);
            // Which holds because the second read is owed behind the first, never beside it.
            Assert.Equal(1, git.PeakConcurrentDiffReads);
        });
    }

    /// <summary>
    /// The focus a hunk operation places names an INDEX, and it is spent by the one render that
    /// consumes it — the hunk it moved is gone, so applying it a second time would name a hunk
    /// the reader never acted on. Two follow-ups running side by side would each snapshot that
    /// focus before either spent it; serialized, the second sees it already spent.
    /// The busy gate keeps a hunk operation's own follow-up from overlapping one today, so what
    /// fails here without the drain is the overlap itself — the condition the double-apply needs.
    /// </summary>
    [Fact]
    public async Task ABurstAfterAHunkOperation_RunsItsFollowUpsInTurnAndLeavesTheReaderWhereTheyAre()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("vm-hunk-burst-focus");
        repo.WriteFile("file.txt", TwentyFive());
        await repo.CommitAllAsync("twenty-five lines");
        repo.WriteFile("file.txt", TwentyFive(1, 13, 25));

        using var pump = new SingleThreadContext();
        var git = new GatedDiffGitService();

        pump.Run(async () =>
        {
            var vm = new ConfirmingWatchedViewModel(git);
            await vm.SetProjectAsync(ProjectFor(repo));
            await vm.WorkingStateRefresh;
            await SelectUnstagedHunkAsync(vm, 0);

            // The discard spends an index focus: two hunks are left and the pane lands on the first.
            await vm.DiscardHunkCommand.ExecuteAsync(null);
            await vm.DiffRefresh;
            Assert.Equal(2, vm.DiffLines.Count(l => l.IsHunkStart));

            // The reader then moves to the LAST hunk — a row the spent focus does not name.
            vm.SelectedDiffLine = vm.DiffLines.Last(l => l.IsHunkStart);
            var header = vm.SelectedDiffLine.Text;

            var dir = Path.GetFileName(repo.Path);
            git.Armed = true;
            // In place, so both remaining hunks change content and neither header moves.
            repo.WriteFile("file.txt", TwentyFive('M', 13, 25));

            vm.OnWatchedReposChanged([dir]);
            await git.StatusHeld;
            vm.OnWatchedReposChanged([dir]);
            git.ReleaseStatus();

            await git.DiffTaken(1);
            git.ReleaseDiff(1);
            git.ReleaseDiff(2);
            await vm.WatcherRefresh;
            await vm.DiffRefresh;

            Assert.Equal(1, git.PeakConcurrentDiffReads);
            Assert.Equal(2, git.DiffReads);
            Assert.Equal(header, vm.SelectedDiffLine?.Text);
        });
    }

    [Theory]
    [InlineData("@@ -1,4 +1,4 @@", true)]
    [InlineData("@@ -1,4 +1,4 @@ void Main()", false)]
    [InlineData("@@ -9,4 +9,4 @@", false)]
    public void HeaderMatches_ComparesTheWholeHeaderLine(string shown, bool expected)
    {
        const string patch =
            "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,4 +1,4 @@\n-a\n+A\n b\n c\n";
        Assert.Equal(expected, ProjectDetailViewModel.HeaderMatches(patch, shown));
    }

    [Fact]
    public void HeaderMatches_RefusesAPatchWithNoHunkAndARowWithNoHeader()
    {
        Assert.False(ProjectDetailViewModel.HeaderMatches("diff --git a/f b/f\n", "@@ -1 +1 @@"));
        Assert.False(ProjectDetailViewModel.HeaderMatches("@@ -1 +1 @@\n-a\n+b\n", ""));
    }
}
