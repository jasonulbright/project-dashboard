using System.Collections.ObjectModel;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The per-file viewer as the detail page drives it (L-04): one path's history beside a blame of
/// its current content, and the two jump-throughs that connect them to the page's History list.
/// The service's own parsing is proven in <see cref="GitServiceFileHistoryTests"/>.
/// </summary>
public class ProjectDetailViewModelFileHistoryTests
{
    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo ProjectFor(TempRepo repo) =>
        new()
        {
            DirectoryName = Path.GetFileName(repo.Path),
            DisplayName = Path.GetFileName(repo.Path),
            FullPath = repo.Path
        };

    private static Task CommitAsAsync(TempRepo repo, string author, string subject) =>
        repo.GitAsync("commit", "-m", subject, $"--author={author}");

    /// <summary>Two authors, one file, one line each — enough to attribute a blame row to a known commit.</summary>
    private static async Task<TempRepo> BlameRepoAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("code.txt", "alpha\nbeta\ngamma\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Alice <alice@example.test>", "root by alice");
        repo.WriteFile("code.txt", "alpha\nBETA\ngamma\n");
        await repo.GitAsync("add", "-A");
        await CommitAsAsync(repo, "Bob <bob@example.test>", "bob edits middle");
        return repo;
    }

    private static async Task<ProjectDetailViewModel> PageOnAsync(TempRepo repo)
    {
        var project = ProjectFor(repo);
        project.RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path, 50);
        var vm = NewVm();
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;
        return vm;
    }

    [Fact]
    public async Task OpenFileHistory_ReadsThePathsCommitsAndItsBlameTogether()
    {
        using var repo = await BlameRepoAsync("fh-open");
        var vm = await PageOnAsync(repo);

        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");

        Assert.True(vm.FileHistoryVisible);
        Assert.Equal("code.txt", vm.FileHistoryPath);
        Assert.Equal(["bob edits middle", "root by alice"], vm.FileHistoryCommits.Select(c => c.Message));
        Assert.Equal(3, vm.BlameLines.Count);
        Assert.False(vm.FileHistoryEmpty);
        Assert.False(vm.BlameEmpty);
        Assert.False(vm.FileHistoryLoading);
        Assert.False(vm.BlameLoading);
        Assert.Equal("", vm.FileHistoryErrorText);
        // A pane covering the page must disable what it covers.
        Assert.False(vm.SafetyOverlayHidden);
    }

    [Fact]
    public async Task ABlameRow_SelectsTheCommitThatLastTouchedThatLine()
    {
        using var repo = await BlameRepoAsync("fh-blame-jump");
        var vm = await PageOnAsync(repo);
        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");

        vm.SelectedBlameLine = vm.BlameLines.Single(l => l.Text == "BETA");
        Assert.NotNull(vm.SelectedFileHistoryCommit);
        Assert.Equal("bob edits middle", vm.SelectedFileHistoryCommit!.Message);

        vm.SelectedBlameLine = vm.BlameLines.Single(l => l.Text == "alpha");
        Assert.Equal("root by alice", vm.SelectedFileHistoryCommit!.Message);
        Assert.Equal("", vm.FileHistoryStatusText);
    }

    /// <summary>
    /// The history is a bounded read; a line attributed to a commit older than it has no row to
    /// select, and the click must say so rather than look ignored.
    /// </summary>
    [Fact]
    public async Task ABlameRowOlderThanTheLoadedHistory_SaysSoInsteadOfDoingNothing()
    {
        using var repo = await BlameRepoAsync("fh-blame-outside");
        var vm = await PageOnAsync(repo);
        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");

        var older = vm.FileHistoryCommits.Last();
        vm.FileHistoryCommits = new ObservableCollection<GitCommit>(vm.FileHistoryCommits.Take(1));
        vm.SelectedFileHistoryCommit = null;

        vm.SelectedBlameLine = vm.BlameLines.Single(l => l.Text == "alpha");
        Assert.Null(vm.SelectedFileHistoryCommit);
        Assert.Contains(older.Ref[..8], vm.FileHistoryStatusText);
        Assert.Contains("older than the loaded history", vm.FileHistoryStatusText);
    }

    [Fact]
    public async Task SelectInHistory_MovesThePagesSelectionAndClosesTheViewer()
    {
        using var repo = await BlameRepoAsync("fh-select");
        var vm = await PageOnAsync(repo);
        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");

        vm.SelectedFileHistoryCommit = vm.FileHistoryCommits.Last();
        var wanted = vm.SelectedFileHistoryCommit!.Ref;
        vm.SelectFileHistoryCommitInListCommand.Execute(null);

        Assert.False(vm.FileHistoryVisible);
        Assert.NotNull(vm.SelectedCommit);
        Assert.Equal(wanted, vm.SelectedCommit!.Ref);
    }

    /// <summary>
    /// A path can carry commits that the branch the History list walks does not. Closing onto a
    /// row that is not there would leave the reader looking at a different commit than the one
    /// they picked.
    /// </summary>
    [Fact]
    public async Task SelectInHistory_RefusesACommitTheHistoryListDoesNotHold()
    {
        using var repo = await BlameRepoAsync("fh-offbranch");
        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("code.txt", "alpha\nBETA\nGAMMA\n");
        await repo.CommitAllAsync("side edits gamma");
        var sideTip = await repo.HeadShaAsync();
        await repo.GitAsync("switch", "main");

        var vm = await PageOnAsync(repo);
        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");
        // The viewer reads the path's history from HEAD too, so the off-branch commit is added
        // to its list directly: what is under test is the refusal, not how it got there.
        var offBranch = new GitCommit { Hash = sideTip, ShortHash = sideTip[..7], Message = "side edits gamma" };
        vm.FileHistoryCommits.Add(offBranch);
        vm.SelectedFileHistoryCommit = offBranch;

        vm.SelectFileHistoryCommitInListCommand.Execute(null);

        Assert.True(vm.FileHistoryVisible);
        Assert.Null(vm.SelectedCommit);
        Assert.Contains(offBranch.ShortHash, vm.FileHistoryStatusText);
    }

    [Fact]
    public async Task OpenFileHistory_OnAPathWithNoHistoryShowsBothEmptyStates()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("fh-empty");
        var vm = await PageOnAsync(repo);

        await vm.OpenFileHistoryCommand.ExecuteAsync("never-existed.txt");

        Assert.True(vm.FileHistoryVisible);
        Assert.True(vm.FileHistoryEmpty);
        Assert.True(vm.BlameEmpty);
        Assert.Empty(vm.FileHistoryCommits);
        Assert.Empty(vm.BlameLines);
    }

    [Fact]
    public async Task OpenFileHistory_RefusesWhileAnotherFullPagePaneIsUp()
    {
        using var repo = await BlameRepoAsync("fh-stacked");
        var vm = await PageOnAsync(repo);
        await vm.OpenReflogCommand.ExecuteAsync(null);
        Assert.True(vm.ReflogVisible);

        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");

        Assert.False(vm.FileHistoryVisible);
        Assert.Equal("", vm.FileHistoryPath);
    }

    [Fact]
    public async Task ClosingTheViewer_DropsEverythingItWasShowing()
    {
        using var repo = await BlameRepoAsync("fh-close");
        var vm = await PageOnAsync(repo);
        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");

        vm.CloseFileHistoryCommand.Execute(null);

        Assert.False(vm.FileHistoryVisible);
        Assert.Empty(vm.FileHistoryCommits);
        Assert.Empty(vm.BlameLines);
        Assert.Equal("", vm.FileHistoryPath);
        Assert.True(vm.SafetyOverlayHidden);
    }

    /// <summary>A viewer left open across a switch would describe a file of a repository the page no longer shows.</summary>
    [Fact]
    public async Task AProjectSwitch_ClosesTheViewer()
    {
        using var repo = await BlameRepoAsync("fh-switch-a");
        using var other = await TempRepo.CreateWithCommitAsync("fh-switch-b");
        var vm = await PageOnAsync(repo);
        await vm.OpenFileHistoryCommand.ExecuteAsync("code.txt");
        Assert.True(vm.FileHistoryVisible);

        await vm.SetProjectAsync(ProjectFor(other));
        await vm.WorkingStateRefresh;

        Assert.False(vm.FileHistoryVisible);
        Assert.Empty(vm.BlameLines);
    }
}
