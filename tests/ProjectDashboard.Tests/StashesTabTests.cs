using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Stashes tab as a reader reaches it: taking a snapshot with a message and a choice about
/// untracked files, and reading what an entry holds before applying it. The git calls under both
/// are covered by <see cref="GitServiceStashDepthTests"/>; what is asserted here is that the tab
/// invokes them, reports what really happened, and puts the result on screen.
/// </summary>
public class StashesTabTests
{
    private static string Markup => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static async Task<ProjectDetailViewModel> OpenedOnAsync(TempRepo repo)
    {
        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        return vm;
    }

    // ── Taking a stash ──────────────────────────────────────────────────────

    [Fact]
    public async Task StashChanges_WithAMessageAndUntracked_SnapshotsTheTreeAndListsTheEntry()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-push");
        repo.WriteFile("file.txt", "modified\n");
        repo.WriteFile("scratch.txt", "untracked\n");

        var vm = await OpenedOnAsync(repo);
        vm.NewStashMessage = "wip snapshot";
        vm.StashIncludeUntracked = true;

        await vm.StashChangesCommand.ExecuteAsync(null);

        Assert.Equal("Stash changes done.", vm.SyncStatusText);
        Assert.Equal("", vm.NewStashMessage);
        Assert.Equal("line one\n", repo.ReadFile("file.txt"));
        Assert.False(repo.FileExists("scratch.txt"));

        var entry = Assert.Single(vm.Stashes);
        Assert.Contains("wip snapshot", entry.Subject);
    }

    [Fact]
    public async Task StashChanges_WithoutTheUntrackedChoice_LeavesUntrackedFilesInPlace()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-tracked");
        repo.WriteFile("file.txt", "modified\n");
        repo.WriteFile("scratch.txt", "untracked\n");

        var vm = await OpenedOnAsync(repo);
        vm.NewStashMessage = "tracked only";

        await vm.StashChangesCommand.ExecuteAsync(null);

        Assert.Equal("Stash changes done.", vm.SyncStatusText);
        Assert.Equal("line one\n", repo.ReadFile("file.txt"));
        Assert.True(repo.FileExists("scratch.txt"));
        Assert.Single(vm.Stashes);
    }

    [Fact]
    public async Task StashChanges_OnACleanTree_SaysNothingWasTakenRatherThanReportingASnapshot()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-clean");

        var vm = await OpenedOnAsync(repo);
        await vm.StashChangesCommand.ExecuteAsync(null);

        Assert.Equal("Nothing to stash — the working tree is clean.", vm.SyncStatusText);
        Assert.Empty(await new GitService().GetStashesAsync(repo.Path));
    }

    /// <summary>
    /// `git stash push` exits zero over a tree that holds only untracked files, having saved
    /// nothing. Reported from the exit code, the tab would say a snapshot exists and the reader
    /// would find an empty stack.
    /// </summary>
    [Fact]
    public async Task StashChanges_OnlyUntrackedAndTheChoiceOff_RefusesAndNamesTheChoice()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-untracked");
        repo.WriteFile("scratch.txt", "untracked\n");

        var vm = await OpenedOnAsync(repo);
        await vm.StashChangesCommand.ExecuteAsync(null);

        Assert.Contains("Include untracked files", vm.SyncStatusText);
        Assert.Empty(await new GitService().GetStashesAsync(repo.Path));
        Assert.True(repo.FileExists("scratch.txt"));
    }

    [Fact]
    public async Task StashChanges_WhileAnotherOpHoldsTheGate_IsRefusedWithANotice()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-busy");
        repo.WriteFile("file.txt", "modified\n");

        var vm = await OpenedOnAsync(repo);
        vm.IsBusy = true;

        await vm.StashChangesCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.BusyNotice("Stash changes"), vm.SyncStatusText);
        Assert.Empty(await new GitService().GetStashesAsync(repo.Path));
    }

    // ── Reading a stash before applying it ──────────────────────────────────

    [Fact]
    public async Task SelectingAStash_ShowsTheChangeItHolds()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-diff");
        repo.WriteFile("file.txt", "line one\nline two\n");

        var vm = await OpenedOnAsync(repo);
        await vm.StashChangesCommand.ExecuteAsync(null);

        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;

        Assert.Equal("", vm.StashDiffError);
        Assert.Equal("file.txt", Assert.Single(vm.StashDiffFiles).Path);
        Assert.Contains(vm.StashDiffLines, l => l is { Kind: DiffLineKind.Added, Text: "line two" });
    }

    /// <summary>
    /// An unreadable stash reported as an empty diff would tell a reader the entry holds nothing
    /// — about work that is still in the stack and about to be applied over their tree.
    /// </summary>
    [Fact]
    public async Task AFailedStashRead_SaysSoRatherThanShowingAnEmptyDiff()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-diff-fail");

        var vm = await OpenedOnAsync(repo);
        vm.SelectedStash = new StashEntry { Ref = "stash@{9}", Subject = "not a stash" };
        await vm.StashDiffRefresh;

        Assert.NotEqual("", vm.StashDiffError);
        Assert.Empty(vm.StashDiffFiles);
        Assert.Empty(vm.StashDiffLines);
    }

    [Fact]
    public async Task MovingOffAStash_ClearsThePreviewItLeftBehind()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-diff-clear");
        repo.WriteFile("file.txt", "line one\nline two\n");

        var vm = await OpenedOnAsync(repo);
        await vm.StashChangesCommand.ExecuteAsync(null);
        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;
        Assert.NotEmpty(vm.StashDiffLines);

        vm.SelectedStash = null;

        Assert.Empty(vm.StashDiffFiles);
        Assert.Empty(vm.StashDiffLines);
        Assert.Equal("", vm.StashDiffError);
    }

    /// <summary>
    /// The view model is a singleton across projects, so a preview and a half-typed message left
    /// standing would describe the repository the reader just left.
    /// </summary>
    [Fact]
    public async Task SwitchingProjects_ClearsTheStashMessageChoiceAndPreview()
    {
        using var repoA = await TempRepo.CreateWithCommitAsync("stash-ui-switch-a");
        using var repoB = await TempRepo.CreateWithCommitAsync("stash-ui-switch-b");
        repoA.WriteFile("file.txt", "line one\nline two\n");

        var vm = await OpenedOnAsync(repoA);
        vm.StashIncludeUntracked = true;
        await vm.StashChangesCommand.ExecuteAsync(null);
        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;
        vm.NewStashMessage = "half typed";

        await vm.SetProjectAsync(ProjectFor(repoB));

        Assert.Null(vm.SelectedStash);
        Assert.Empty(vm.Stashes);
        Assert.Empty(vm.StashDiffFiles);
        Assert.Empty(vm.StashDiffLines);
        Assert.Equal("", vm.NewStashMessage);
        Assert.False(vm.StashIncludeUntracked);
    }

    [Fact]
    public async Task TheSideBySideLayout_RendersTheStashPreviewToo()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stash-ui-sbs");
        repo.WriteFile("file.txt", "line one\nline two\n");

        var vm = await OpenedOnAsync(repo);
        await vm.StashChangesCommand.ExecuteAsync(null);
        vm.ApplyDiffLayout(true);
        vm.SelectedStash = Assert.Single(vm.Stashes);
        await vm.StashDiffRefresh;

        Assert.NotEmpty(vm.StashDiffRows);
    }

    // ── Reachable from the shipped markup ───────────────────────────────────

    [Theory]
    [InlineData("{Binding StashChangesCommand}")]
    [InlineData("{Binding NewStashMessage, UpdateSourceTrigger=PropertyChanged}")]
    [InlineData("{Binding StashIncludeUntracked}")]
    public void TheStashAction_IsReachableFromTheStashesTab(string binding)
    {
        Assert.Contains(binding, Markup);
    }

    [Theory]
    [InlineData(@"ItemsSource=""{Binding StashDiffFiles}""")]
    [InlineData(@"ItemsSource=""{Binding StashDiffLines}""")]
    [InlineData(@"ItemsSource=""{Binding StashDiffRows}""")]
    [InlineData("{Binding StashDiffError}")]
    public void TheStashPreview_IsRenderedByTheStashesTab(string binding)
    {
        Assert.Contains(binding, Markup);
    }

    [Theory]
    [InlineData(@"AutomationProperties.AutomationId=""StashDiffTruncatedNotice""")]
    [InlineData("{Binding StashDiffIsTruncated, Converter={StaticResource BooleanToVisibilityConverter}}")]
    public void TheTruncationNotice_IsRenderedByTheStashesTab(string markup)
    {
        Assert.Contains(markup, Markup);
    }
}
