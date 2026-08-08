using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Branches tab's remotes pane and selected-branch actions. What is asserted is that every
/// local action reports what it left standing on the remote, that the one outward-facing action —
/// deleting a branch on a remote — takes the exact ref typed before it runs, and that each
/// mutation refreshes the list it invalidated.
/// </summary>
public class RemotesSurfaceTests
{
    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    /// <summary>Answers the yes/no and typed confirmations without a window.</summary>
    private sealed class RemotesViewModel(bool confirm = true)
        : ProjectDetailViewModel(null!, new GitService(), null!, null, new RepoBusyRegistry())
    {
        /// <summary>Null stands for a cancelled typed prompt.</summary>
        public string? Typed { get; set; }

        public int Prompts { get; private set; }
        public string LastPromptMessage { get; private set; } = "";

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
            => Task.FromResult(confirm);

        internal override Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
        {
            Prompts++;
            LastPromptMessage = message;
            return Task.FromResult(Typed);
        }
    }

    private static async Task<RemotesViewModel> OpenedOn(TempRepo repo, bool confirm = true)
    {
        var vm = new RemotesViewModel(confirm);
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);
        return vm;
    }

    // ── Remotes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheList_ReportsEachRemotesFetchAndPushUrls()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-vm");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");
        await repo.GitAsync("remote", "set-url", "--push", "origin", "https://example.test/push.git");

        var vm = await OpenedOn(repo);

        Assert.True(vm.BranchesTabLoaded);
        var origin = Assert.Single(vm.Remotes);
        Assert.Equal("origin", origin.Name);
        Assert.Equal("https://example.test/a.git", origin.FetchUrl);
        Assert.Equal("https://example.test/push.git", origin.PushUrl);
        // The selection drives the edit boxes, so they have to describe the selected remote.
        Assert.Equal("origin", vm.RemoteRenameTo);
        Assert.Equal("https://example.test/a.git", vm.RemoteUrlEdit);
    }

    [Fact]
    public async Task ARepositoryWithNoRemotes_SaysSoRatherThanShowingAnEmptyList()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-empty");

        var vm = await OpenedOn(repo);

        Assert.Empty(vm.Remotes);
        Assert.True(vm.BranchesTabLoaded);
        var markup = await File.ReadAllTextAsync(PageSource());
        Assert.Contains("This repository has no remotes configured", markup);
        Assert.Contains("has never fetched reads the same way", markup);
    }

    [Fact]
    public async Task AddingARemote_ConfiguresItWithoutContactingIt()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-add");
        var vm = await OpenedOn(repo);

        vm.NewRemoteName = "origin";
        vm.NewRemoteUrl = "https://example.test/a.git";
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.Equal("origin", Assert.Single(vm.Remotes).Name);
        Assert.Contains("Nothing was fetched", vm.RemotesStatusText);
        Assert.Equal("", vm.NewRemoteName);
        Assert.Equal("", vm.NewRemoteUrl);
    }

    [Theory]
    [InlineData("team/origin", "https://example.test/a.git")]
    [InlineData("has space", "https://example.test/a.git")]
    [InlineData("-force", "https://example.test/a.git")]
    public async Task AnInvalidRemoteName_IsRefusedBeforeGitIsAsked(string name, string url)
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-badname");
        var vm = await OpenedOn(repo);

        vm.NewRemoteName = name;
        vm.NewRemoteUrl = url;
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.Contains("is not a valid remote name", vm.RemotesErrorText);
        Assert.Empty(vm.Remotes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--upload-pack=whatever")]
    [InlineData("https://example.test/a b.git")]
    public async Task AnUnusableRemoteUrl_IsRefusedBeforeGitIsAsked(string url)
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-badurl");
        var vm = await OpenedOn(repo);

        vm.NewRemoteName = "origin";
        vm.NewRemoteUrl = url;
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.Contains("cannot be empty, start with a dash", vm.RemotesErrorText);
        Assert.Empty(vm.Remotes);
    }

    [Fact]
    public async Task ARemoteNameAlreadyConfigured_IsRefusedRatherThanReportedAsAGitFailure()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-dupe");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");
        var vm = await OpenedOn(repo);

        vm.NewRemoteName = "origin";
        vm.NewRemoteUrl = "https://example.test/b.git";
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.Contains("is already configured here", vm.RemotesErrorText);
        Assert.Equal("https://example.test/a.git", Assert.Single(vm.Remotes).FetchUrl);
    }

    [Fact]
    public async Task RemovingARemote_IsRefusedWithoutTheConfirmation()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-keep");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");
        var vm = await OpenedOn(repo, confirm: false);

        await vm.RemoveRemoteCommand.ExecuteAsync(null);

        Assert.Single(vm.Remotes);
        Assert.Equal("", vm.RemotesStatusText);
    }

    [Fact]
    public async Task RemovingARemote_DropsItAndTheTrackingBranchesUnderIt()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("remotes-rm-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "remotes-rm-clone");

        var vm = await OpenedOn(clone);
        Assert.NotEmpty(vm.RemoteBranches);

        await vm.RemoveRemoteCommand.ExecuteAsync(null);

        Assert.Empty(vm.Remotes);
        Assert.Empty(vm.RemoteBranches);
        Assert.Contains("is untouched", vm.RemotesStatusText);
        // The branch list is re-read too: main lost the upstream the remote carried.
        Assert.Equal("", vm.Branches.Single(b => b.Name == "main").Upstream);
    }

    [Fact]
    public async Task RenamingARemote_MovesItsTrackingBranchesWithIt()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("remotes-mv-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "remotes-mv-clone");

        var vm = await OpenedOn(clone);
        vm.RemoteRenameTo = "upstream";
        await vm.RenameRemoteCommand.ExecuteAsync(null);

        Assert.Equal("upstream", Assert.Single(vm.Remotes).Name);
        Assert.Contains("upstream/main", vm.RemoteBranches);
        Assert.DoesNotContain("origin/main", vm.RemoteBranches);
    }

    [Fact]
    public async Task ChangingARemotesUrl_LeavesASeparatePushUrlAlone()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-url");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");
        await repo.GitAsync("remote", "set-url", "--push", "origin", "https://example.test/push.git");
        var vm = await OpenedOn(repo);

        vm.RemoteUrlEdit = "https://example.test/moved.git";
        await vm.SetRemoteUrlCommand.ExecuteAsync(null);

        var origin = Assert.Single(vm.Remotes);
        Assert.Equal("https://example.test/moved.git", origin.FetchUrl);
        Assert.Equal("https://example.test/push.git", origin.PushUrl);
        Assert.Contains("push URL was left alone", vm.RemotesStatusText);
    }

    // ── Deleting a branch on a remote ───────────────────────────────────────

    [Fact]
    public async Task DeletingABranchOnTheRemote_TakesTheExactRefTyped()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("rb-del-seed");
        await seed.GitAsync("branch", "throwaway");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "rb-del-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/throwaway";
        vm.Typed = "origin/throwaway";
        await vm.DeleteRemoteBranchCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Prompts);
        Assert.Contains("outward-facing", vm.LastPromptMessage);
        Assert.DoesNotContain("throwaway", await Git.RunAsync(bare.Path, "branch", "--list"));
        Assert.DoesNotContain("origin/throwaway", vm.RemoteBranches);
        Assert.Contains("still here", vm.RemotesStatusText);
    }

    [Fact]
    public async Task DeletingABranchOnTheRemote_DoesNothingWhenTheTypedRefIsWrong()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("rb-wrong-seed");
        await seed.GitAsync("branch", "throwaway");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "rb-wrong-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/throwaway";
        vm.Typed = "throwaway";
        await vm.DeleteRemoteBranchCommand.ExecuteAsync(null);

        Assert.Contains("throwaway", await Git.RunAsync(bare.Path, "branch", "--list"));
        Assert.Contains("that isn't origin/throwaway", vm.RemotesStatusText);
    }

    [Fact]
    public async Task DeletingABranchOnTheRemote_DoesNothingWhenThePromptIsCancelled()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("rb-cancel-seed");
        await seed.GitAsync("branch", "throwaway");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "rb-cancel-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/throwaway";
        vm.Typed = null;
        await vm.DeleteRemoteBranchCommand.ExecuteAsync(null);

        Assert.Contains("throwaway", await Git.RunAsync(bare.Path, "branch", "--list"));
        Assert.Equal("", vm.RemotesStatusText);
    }

    [Theory]
    [InlineData("origin/topic", "origin/topic", true)]
    [InlineData("origin/topic", " origin/topic ", true)]
    [InlineData("origin/topic", "Origin/Topic", false)]
    [InlineData("origin/topic", "topic", false)]
    [InlineData("origin/topic", null, false)]
    public void TheTypedRefMustMatchByteForByte(string trackingRef, string? typed, bool confirmed)
        => Assert.Equal(confirmed, ProjectDetailViewModel.TrackingRefConfirmed(typed, trackingRef));

    [Theory]
    [InlineData("origin/topic", "origin", "topic")]
    [InlineData("origin/feature/one", "origin", "feature/one")]
    [InlineData("origin", "origin", "")]
    public void ATrackingRefSplitsAtItsFirstSlash(string trackingRef, string remote, string branch)
        => Assert.Equal((remote, branch), ProjectDetailViewModel.SplitTrackingRef(trackingRef));

    // ── Branch extras ───────────────────────────────────────────────────────

    [Fact]
    public async Task SettingAnUpstream_LinksTheBranchAndFetchesNothing()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("ups-vm-seed");
        await seed.GitAsync("branch", "release");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "ups-vm-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "main");
        vm.SelectedUpstreamChoice = "origin/release";
        await vm.SetBranchUpstreamCommand.ExecuteAsync(null);

        Assert.Equal("origin/release", vm.Branches.Single(b => b.Name == "main").Upstream);
        Assert.Contains("Nothing was fetched or pushed", vm.BranchExtrasStatusText);
    }

    [Fact]
    public async Task ClearingAnUpstream_LeavesTheTrackingRefInPlace()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("ups-clear-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "ups-clear-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "main");
        await vm.UnsetBranchUpstreamCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Branches.Single(b => b.Name == "main").Upstream);
        Assert.Contains("origin/main", vm.RemoteBranches);
        Assert.Contains("still here", vm.BranchExtrasStatusText);
    }

    [Fact]
    public async Task ClearingAnUpstreamThatIsNotThere_SaysSoRatherThanRunningGit()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ups-none");
        var vm = await OpenedOn(repo);
        vm.SelectedBranch = vm.Branches.Single();

        await vm.UnsetBranchUpstreamCommand.ExecuteAsync(null);

        Assert.Contains("no upstream to clear", vm.BranchExtrasStatusText);
        Assert.Equal("", vm.BranchExtrasErrorText);
    }

    [Fact]
    public async Task RenamingABranch_SaysTheRemoteKeepsTheOldName()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("branch-mv");
        await repo.GitAsync("branch", "topic");
        var vm = await OpenedOn(repo);

        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "topic");
        vm.BranchRenameTo = "feature";
        await vm.RenameSelectedBranchCommand.ExecuteAsync(null);

        Assert.Contains(vm.Branches, b => b.Name == "feature");
        Assert.DoesNotContain(vm.Branches, b => b.Name == "topic");
        Assert.Contains("keeps its old name", vm.BranchExtrasStatusText);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("two..dots")]
    public async Task RenamingABranchToAnInvalidName_IsRefusedBeforeGitIsAsked(string name)
    {
        using var repo = await TempRepo.CreateWithCommitAsync("branch-mv-bad");
        await repo.GitAsync("branch", "topic");
        var vm = await OpenedOn(repo);

        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "topic");
        vm.BranchRenameTo = name;
        await vm.RenameSelectedBranchCommand.ExecuteAsync(null);

        Assert.Contains("is not a valid branch name", vm.BranchExtrasErrorText);
        Assert.Contains(vm.Branches, b => b.Name == "topic");
    }

    [Fact]
    public async Task ComparingBranches_CountsBothSidesAndExcludesTheBranchItself()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("branch-cmp");
        await repo.GitAsync("switch", "-c", "topic");
        repo.WriteFile("t.txt", "one\n");
        await repo.CommitAllAsync("topic one");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("m.txt", "main\n");
        await repo.CommitAllAsync("main one");

        var vm = await OpenedOn(repo);
        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "topic");
        // Measuring a branch against itself is always zero, so it is not offered.
        Assert.DoesNotContain("topic", vm.CompareBaseChoices);

        vm.SelectedCompareBase = "main";
        await vm.CompareSelectedBranchCommand.ExecuteAsync(null);

        Assert.Equal("topic is 1 commit ahead of and 1 commit behind main.", vm.BranchCompareText);
    }

    [Fact]
    public async Task SelectingAnotherBranch_DropsTheCountMeasuredForThePreviousOne()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("branch-cmp-reset");
        await repo.GitAsync("branch", "topic");
        var vm = await OpenedOn(repo);

        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "topic");
        vm.SelectedCompareBase = "main";
        await vm.CompareSelectedBranchCommand.ExecuteAsync(null);
        Assert.NotEqual("", vm.BranchCompareText);

        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "main");

        Assert.Equal("", vm.BranchCompareText);
    }

    [Theory]
    [InlineData(0, 0, "topic and main are at the same commit.")]
    [InlineData(1, 0, "topic is 1 commit ahead of main.")]
    [InlineData(0, 2, "topic is 2 commits behind main.")]
    [InlineData(3, 4, "topic is 3 commits ahead of and 4 commits behind main.")]
    public void AComparisonIsDescribedInCommits(int ahead, int behind, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.DescribeComparison("topic", "main",
            new RefComparison(ahead, behind)));

    [Fact]
    public void AComparisonThatCouldNotBeMeasured_IsNotReportedAsZero()
        => Assert.Contains("could not be compared",
            ProjectDetailViewModel.DescribeComparison("topic", "main", null));

    [Fact]
    public async Task SwitchingProjects_DropsTheRemotesOfTheOneThatLeft()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-switch-a");
        using var other = await TempRepo.CreateWithCommitAsync("remotes-switch-b");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");
        var vm = await OpenedOn(repo);
        Assert.Single(vm.Remotes);

        await vm.SetProjectAsync(ProjectFor(other));

        Assert.Empty(vm.Remotes);
        Assert.False(vm.BranchesTabLoaded);
        Assert.Equal("", vm.RemoteRenameTo);
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
