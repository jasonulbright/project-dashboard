using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Hunk staging as the Changes tab drives it (L-06): the diff pane's selected row names a hunk,
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
