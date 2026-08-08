using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The detail page follows edits made to the open repository outside the app. The
/// watcher signal is delivered straight to the view model here — the debounce and the
/// filesystem plumbing are the watcher service's own tests — so what is exercised is
/// what the page does with one: which signals it acts on, which gates it yields to, and
/// what the resulting refresh is not allowed to take away from the reader.
/// </summary>
public class ProjectDetailViewModelWatcherTests
{
    /// <summary>
    /// No Application in the test host, so the default post target has no dispatcher and
    /// would drop every callback. Discovery and gh are unreachable from these paths.
    /// </summary>
    private static ProjectDetailViewModel NewVm(RepoBusyRegistry registry) =>
        new(null!, new GitService(), null!, busyRegistry: registry, uiPost: callback => callback());

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    [Fact]
    public async Task ASignalNamingTheOpenRepository_ReReadsTheWorkingTree()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("watch-open");
        var registry = new RepoBusyRegistry();
        var vm = NewVm(registry);
        var project = ProjectFor(repo);
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;
        Assert.Empty(vm.UnstagedFiles);

        // The edit an editor outside the app makes: no command on this page ran.
        repo.WriteFile("file.txt", "edited outside the app\n");
        vm.OnWatchedReposChanged([project.DirectoryName]);
        await vm.WatcherRefresh;

        Assert.Single(vm.UnstagedFiles);
        Assert.Equal("file.txt", vm.UnstagedFiles[0].Path);
    }

    /// <summary>
    /// The overflow signal names nothing because the watcher lost the events it would have
    /// named. Dropped for naming no repository, it would lose exactly the burst that
    /// overran the buffer.
    /// </summary>
    [Fact]
    public async Task TheOverflowSignal_ReReadsTheOpenRepositoryToo()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("watch-overflow");
        var vm = NewVm(new RepoBusyRegistry());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        repo.WriteFile("file.txt", "edited outside the app\n");
        vm.OnWatchedReposChanged([]);
        await vm.WatcherRefresh;

        Assert.Single(vm.UnstagedFiles);
    }

    [Fact]
    public async Task ASignalNamingAnotherRepository_LeavesThisPageAlone()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("watch-other");
        var vm = NewVm(new RepoBusyRegistry());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        repo.WriteFile("file.txt", "edited outside the app\n");
        vm.OnWatchedReposChanged(["some-other-repo"]);
        await vm.WatcherRefresh;

        Assert.Empty(vm.UnstagedFiles);
    }

    /// <summary>
    /// A rewrite holds the repository lease and this page's own flag is clear, so a refresh
    /// that consulted only the flag would read refs mid-swap. The signal is held rather
    /// than dropped: the edit that raised it is on disk whether or not the operation
    /// covering it reports one.
    /// </summary>
    [Fact]
    public async Task ASignalArrivingUnderARepositoryLease_IsHeldUntilTheLeaseIsReleased()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("watch-lease");
        var registry = new RepoBusyRegistry();
        var vm = NewVm(registry);
        var project = ProjectFor(repo);
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;

        var lease = registry.Acquire(repo.Path);
        repo.WriteFile("file.txt", "edited outside the app\n");
        vm.OnWatchedReposChanged([project.DirectoryName]);
        await vm.WatcherRefresh;

        Assert.Empty(vm.UnstagedFiles);

        lease.Dispose();
        await vm.WatcherRefresh;

        Assert.Single(vm.UnstagedFiles);
    }

    /// <summary>
    /// The page's own busy flag covers the ops the lease does not outlive. Releasing the
    /// lease first, an op's finally would drain against a flag still raised, so the flag's
    /// own transition has to be a second chance or the held signal never lands.
    /// </summary>
    [Fact]
    public async Task ASignalArrivingUnderThePagesBusyFlag_IsHeldUntilTheFlagDrops()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("watch-busy");
        var vm = NewVm(new RepoBusyRegistry());
        var project = ProjectFor(repo);
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;

        vm.IsBusy = true;
        repo.WriteFile("file.txt", "edited outside the app\n");
        vm.OnWatchedReposChanged([project.DirectoryName]);
        await vm.WatcherRefresh;

        Assert.Empty(vm.UnstagedFiles);

        vm.IsBusy = false;
        await vm.WatcherRefresh;

        Assert.Single(vm.UnstagedFiles);
    }

    /// <summary>
    /// The refresh runs behind the reader's back, so anything they are part way through
    /// building has to survive it: a commit message being composed, and the selection the
    /// staging buttons act on.
    /// </summary>
    [Fact]
    public async Task ARefreshFromTheWatcher_KeepsTheDraftMessageAndTheSelectionTheReaderBuilt()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("watch-draft");
        repo.WriteFile("first.txt", "one\n");
        var vm = NewVm(new RepoBusyRegistry());
        var project = ProjectFor(repo);
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;

        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single(f => f.Path == "first.txt");
        vm.CommitMessage = "half-typed subject";

        repo.WriteFile("second.txt", "two\n");
        vm.OnWatchedReposChanged([project.DirectoryName]);
        await vm.WatcherRefresh;

        Assert.Equal(2, vm.UnstagedFiles.Count);
        Assert.Equal("half-typed subject", vm.CommitMessage);
        Assert.NotNull(vm.SelectedUnstagedFile);
        Assert.Equal("first.txt", vm.SelectedUnstagedFile.Path);
        Assert.Equal(["first.txt"], vm.SelectedUnstagedFiles.Select(f => f.Path));
    }

    /// <summary>
    /// A signal held while the page was on one repository names that repository. Carried
    /// across a switch it would spend itself reading the repository switched to, which
    /// nothing has reported a change in.
    /// </summary>
    [Fact]
    public async Task AHeldSignal_IsDroppedWhenTheReaderSwitchesProjects()
    {
        using var first = await TempRepo.CreateWithCommitAsync("watch-switch-a");
        using var second = await TempRepo.CreateWithCommitAsync("watch-switch-b");
        var registry = new RepoBusyRegistry();
        var vm = NewVm(registry);
        var project = ProjectFor(first);
        await vm.SetProjectAsync(project);
        await vm.WorkingStateRefresh;

        var lease = registry.Acquire(first.Path);
        first.WriteFile("file.txt", "edited outside the app\n");
        vm.OnWatchedReposChanged([project.DirectoryName]);
        await vm.WatcherRefresh;

        await vm.SetProjectAsync(ProjectFor(second));
        await vm.WorkingStateRefresh;

        // Unreported, so only a signal that outlived the switch would ever read it.
        second.WriteFile("file.txt", "edited outside the app\n");
        lease.Dispose();
        await vm.WatcherRefresh;

        Assert.Empty(vm.UnstagedFiles);
    }

    /// <summary>
    /// Two triggers can land on an ungated refresh — a watcher signal and F5, or two signals
    /// with a slow git status between them. Run side by side they both write the file lists,
    /// and the one that started earlier can finish later: the pane is left describing a
    /// working tree that has already moved on. Every caller joins the one read in flight and
    /// leaves a pass owed behind it, so the last write is always the newest read.
    /// </summary>
    [Fact]
    public async Task OverlappingRefreshes_JoinTheOneReadInFlightAndRunAPassAfterIt()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("refresh-single-flight");
        var vm = NewVm(new RepoBusyRegistry());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        var first = vm.RefreshWorkingStateAsync();
        // Written while the first read is in flight: only a pass that starts after it can
        // see this, and that pass is what the second caller is owed.
        repo.WriteFile("file.txt", "edited outside the app\n");
        var second = vm.RefreshWorkingStateAsync();

        Assert.Same(first, second);

        await second;
        Assert.Single(vm.UnstagedFiles);
    }

    // ── Manual refresh ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRefreshCommand_ReReadsTheWorkingTree()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("refresh-cmd");
        var vm = NewVm(new RepoBusyRegistry());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        repo.WriteFile("file.txt", "edited outside the app\n");
        await vm.RefreshWorkingCopyCommand.ExecuteAsync(null);

        Assert.Single(vm.UnstagedFiles);
    }

    [Fact]
    public async Task TheRefreshCommand_RefusesOutLoudWhileTheRepositoryIsUnderAnOperation()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("refresh-cmd-busy");
        var registry = new RepoBusyRegistry();
        var vm = NewVm(registry);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        using var lease = registry.Acquire(repo.Path);
        repo.WriteFile("file.txt", "edited outside the app\n");
        await vm.RefreshWorkingCopyCommand.ExecuteAsync(null);

        Assert.Empty(vm.UnstagedFiles);
        Assert.Equal(
            "Refresh not started — another operation is running on this repository.",
            vm.SyncStatusText);
    }
}
