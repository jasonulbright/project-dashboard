using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A read the capture budget cut short reaches its reader as a prefix. Silently partial content
/// is the failure being prevented: the diff pane says the rows stop early and refuses the hunk
/// actions that would slice a patch out of half a hunk, and the blame pane says its lines do.
/// </summary>
public class TruncatedReadSurfaceTests
{
    private const string FifteenLines =
        "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nl15\n";
    private const string FifteenEdited =
        "L1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n";

    /// <summary>Runs git for real and flags the reads named by <paramref name="command"/> truncated.</summary>
    private sealed class TruncatingGitService(string command) : GitService
    {
        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            var result = await base.RunAsync(repoPath, list, environment, ct, timeout);
            return list.Contains(command) ? result with { Truncated = true } : result;
        }
    }

    /// <summary>
    /// Whole on the read that fills the pane, truncated on every read after it — the file
    /// crossing the budget between the diff a reader saw and the click they made on it.
    /// </summary>
    private sealed class TruncatingAfterTheFirstDiffGitService : GitService
    {
        private int _diffReads;

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            var result = await base.RunAsync(repoPath, list, environment, ct, timeout);
            if (!list.Contains("diff")) return result;
            return Interlocked.Increment(ref _diffReads) > 1 ? result with { Truncated = true } : result;
        }
    }

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static async Task<TempRepo> TwoHunkRepoAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("file.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        repo.WriteFile("file.txt", FifteenEdited);
        return repo;
    }

    private static async Task<ProjectDetailViewModel> OpenOnFileAsync(TempRepo repo, GitService git)
    {
        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "file.txt");
        await vm.DiffRefresh;
        return vm;
    }

    [Fact]
    public async Task ATruncatedDiffRead_CarriesTheFlagToTheParsedDiff()
    {
        using var repo = await TwoHunkRepoAsync("diff-truncated-model");
        var git = new TruncatingGitService("diff");

        var diff = await git.GetFileDiffAsync(
            repo.Path, new WorkingFile { Path = "file.txt" }, staged: false);

        Assert.NotNull(diff);
        Assert.True(diff.Truncated);
    }

    [Fact]
    public async Task AWholeDiffRead_IsNotFlaggedTruncated()
    {
        using var repo = await TwoHunkRepoAsync("diff-whole-model");

        var diff = await new GitService().GetFileDiffAsync(
            repo.Path, new WorkingFile { Path = "file.txt" }, staged: false);

        Assert.NotNull(diff);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public async Task ATruncatedDiff_SaysSoInThePaneAndRefusesHunkActions()
    {
        using var repo = await TwoHunkRepoAsync("diff-truncated-pane");
        var vm = await OpenOnFileAsync(repo, new TruncatingGitService("diff"));

        Assert.True(vm.DiffIsTruncated);
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.HunkIndex >= 0);

        Assert.False(vm.StageHunkCommand.CanExecute(null));
        Assert.False(vm.DiscardHunkCommand.CanExecute(null));
        Assert.Contains("too large to read in full", vm.StageHunkBlockedReason);
        Assert.Contains("too large to read in full", vm.DiscardHunkBlockedReason);
    }

    [Fact]
    public async Task AWholeDiff_LeavesTheHunkActionsAvailable()
    {
        using var repo = await TwoHunkRepoAsync("diff-whole-pane");
        var vm = await OpenOnFileAsync(repo, new GitService());

        Assert.False(vm.DiffIsTruncated);
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.HunkIndex >= 0);

        Assert.True(vm.StageHunkCommand.CanExecute(null));
        Assert.Null(vm.StageHunkBlockedReason);
    }

    /// <summary>
    /// The gate reads the diff on display; the patch is sliced from a fresh read taken at the
    /// click. A file that crosses the budget in between passes the gate and would be sliced out
    /// of a prefix, so the fresh read is checked again and the operation refused, exactly as a
    /// diff that moved underneath is refused.
    /// </summary>
    [Fact]
    public async Task ADiffTruncatedBetweenTheDisplayAndTheClick_IsRefusedAndReloaded()
    {
        using var repo = await TwoHunkRepoAsync("hunk-truncated-fresh");
        var vm = await OpenOnFileAsync(repo, new TruncatingAfterTheFirstDiffGitService());

        // The pane showed a whole diff, so nothing stops the reader from choosing a hunk.
        Assert.False(vm.DiffIsTruncated);
        vm.SelectedDiffLine = vm.DiffLines.First(l => l.HunkIndex >= 0);
        Assert.True(vm.StageHunkCommand.CanExecute(null));

        await vm.StageHunkCommand.ExecuteAsync(null);

        Assert.Contains("too large to read in full", vm.SyncStatusText);

        // Nothing was staged: the index is still empty.
        var staged = await new GitService().RunAsync(repo.Path, ["diff", "--cached", "--name-only"]);
        Assert.True(staged.Success, staged.FirstError);
        Assert.Equal("", staged.StdOut.Trim());

        // The reload put the truncated state on the pane, so the buttons are down now.
        Assert.True(vm.DiffIsTruncated);
        Assert.False(vm.StageHunkCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATruncatedCommitDiffRead_CarriesTheFlagToTheResultAndThePane()
    {
        using var repo = await TwoHunkRepoAsync("commit-diff-truncated");
        await repo.CommitAllAsync("edit the fifteen lines");
        var git = new TruncatingGitService("show");

        var head = await git.RunAsync(repo.Path, ["rev-parse", "HEAD"]);
        var hash = head.StdOut.Trim();

        var diff = await git.GetCommitFileDiffAsync(repo.Path, hash, "file.txt");
        Assert.NotNull(diff);
        Assert.True(diff.Truncated);

        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        vm.SelectedCommit = new GitCommit { Hash = hash };
        await vm.CommitFilesRefresh;
        vm.SelectedCommitFile = vm.CommitFiles.First(f => f.Path == "file.txt");
        await vm.CommitDiffRefresh;

        Assert.True(vm.CommitDiffIsTruncated);
        Assert.NotEmpty(vm.CommitDiffLines);
    }

    [Fact]
    public async Task AWholeCommitDiffRead_LeavesThePaneUnflagged()
    {
        using var repo = await TwoHunkRepoAsync("commit-diff-whole");
        await repo.CommitAllAsync("edit the fifteen lines");
        var git = new GitService();

        var head = await git.RunAsync(repo.Path, ["rev-parse", "HEAD"]);
        var diff = await git.GetCommitFileDiffAsync(repo.Path, head.StdOut.Trim(), "file.txt");

        Assert.NotNull(diff);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public async Task ATruncatedStashDiffRead_CarriesTheFlagToTheParsedDiffAndThePane()
    {
        using var repo = await TwoHunkRepoAsync("stash-diff-truncated");
        var git = new TruncatingGitService("show");

        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await vm.StashChangesCommand.ExecuteAsync(null);

        var files = await git.GetStashDiffAsync(repo.Path, "stash@{0}");
        Assert.NotNull(files);
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(f.Truncated));

        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;

        Assert.True(vm.StashDiffIsTruncated);
        Assert.NotEmpty(vm.StashDiffLines);
    }

    [Fact]
    public async Task AWholeStashDiffRead_LeavesThePaneUnflagged()
    {
        using var repo = await TwoHunkRepoAsync("stash-diff-whole");
        var git = new GitService();

        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await vm.StashChangesCommand.ExecuteAsync(null);

        var files = await git.GetStashDiffAsync(repo.Path, "stash@{0}");
        Assert.NotNull(files);
        Assert.All(files, f => Assert.False(f.Truncated));

        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;

        Assert.False(vm.StashDiffIsTruncated);
        Assert.NotEmpty(vm.StashDiffLines);
    }

    /// <summary>
    /// The flag belongs to the selection that set it: a truncated stash left on the pane while
    /// the reader moves to another entry would say the next stash's whole preview stops early.
    /// </summary>
    [Fact]
    public async Task MovingOffATruncatedStash_ClearsTheFlagWithThePreview()
    {
        using var repo = await TwoHunkRepoAsync("stash-diff-truncated-clear");
        var git = new TruncatingGitService("show");

        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await vm.StashChangesCommand.ExecuteAsync(null);

        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;
        Assert.True(vm.StashDiffIsTruncated);

        vm.SelectedStash = null;

        Assert.False(vm.StashDiffIsTruncated);
        Assert.Empty(vm.StashDiffLines);
    }

    [Fact]
    public async Task ATruncatedBlameRead_CarriesTheFlagToTheResultAndThePane()
    {
        using var repo = await TwoHunkRepoAsync("blame-truncated");
        var git = new TruncatingGitService("blame");

        var blame = await git.GetBlameAsync(repo.Path, "file.txt");
        Assert.False(blame.HasError);
        Assert.True(blame.Truncated);

        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await vm.OpenFileHistoryCommand.ExecuteAsync("file.txt");

        Assert.True(vm.BlameTruncated);
    }

    [Fact]
    public async Task AWholeBlameRead_LeavesThePaneUnflagged()
    {
        using var repo = await TwoHunkRepoAsync("blame-whole");
        var git = new GitService();

        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await vm.OpenFileHistoryCommand.ExecuteAsync("file.txt");

        Assert.False(vm.BlameTruncated);
        Assert.NotEmpty(vm.BlameLines);
    }
}
