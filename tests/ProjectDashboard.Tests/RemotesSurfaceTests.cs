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
    private sealed class RemotesViewModel(bool confirm = true, GitService? git = null)
        : ProjectDetailViewModel(null!, git ?? new GitService(), null!, null, new RepoBusyRegistry())
    {
        /// <summary>Null stands for a cancelled typed prompt.</summary>
        public string? Typed { get; set; }

        public int Prompts { get; private set; }
        public string LastPromptMessage { get; private set; } = "";

        public int Confirmations { get; private set; }
        public string LastConfirmMessage { get; private set; } = "";

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastConfirmMessage = message;
            return Task.FromResult(confirm);
        }

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
        Assert.True(vm.RemotesEmpty);
        Assert.True(vm.RemoteBranchesEmpty);

        var markup = await File.ReadAllTextAsync(PageSource());
        var remotes = EmptyStateMarkup(markup, "RemotesEmptyState");
        Assert.Contains("This repository has no remotes configured", remotes);
        // The claim is made from a read that answered, never from a count an error also produces.
        Assert.Contains("Binding RemotesEmpty,", remotes);
        Assert.DoesNotContain("Remotes.Count", remotes);

        var branches = EmptyStateMarkup(markup, "RemoteBranchesEmptyState");
        Assert.Contains("has never fetched reads the same way", branches);
        Assert.Contains("Binding RemoteBranchesEmpty,", branches);
        Assert.DoesNotContain("RemoteBranches.Count", branches);
    }

    /// <summary>
    /// The lists are empty after a read that failed too, and the error beside them says so — a
    /// confident "no remotes configured" alongside it would contradict it.
    /// </summary>
    [Fact]
    public async Task AFailedRemotesRead_DoesNotAlsoClaimTheRepositoryHasNone()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-read-fails");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");

        var vm = new RemotesViewModel(git: new RemoteReadFailingGit());
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);

        Assert.Empty(vm.Remotes);
        Assert.False(vm.RemotesEmpty);
        Assert.False(vm.RemoteBranchesEmpty);
        Assert.Contains("Could not read this repository's remotes", vm.RemotesErrorText);
    }

    /// <summary>Throws on the remote listing alone, leaving every other read to run for real.</summary>
    private sealed class RemoteReadFailingGit : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            return list is ["remote", "-v"]
                ? throw new IOException("the remote listing could not be read")
                : base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    /// <summary>
    /// The failure git actually produces is a non-zero exit, not a throw: the process runner
    /// returns it as an unsuccessful result. Only the throwing shape was caught before, so a
    /// refused listing reached the pane as a plain empty list and the tab claimed the repository
    /// has no remotes configured.
    /// </summary>
    [Fact]
    public async Task ARemoteReadThatExitsNonZero_ReportsTheFailureInsteadOfClaimingNoneAreConfigured()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-exit-remote");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");

        var vm = new RemotesViewModel(git: new RemoteReadRefusingGit("remote"));
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);

        Assert.Empty(vm.Remotes);
        Assert.False(vm.RemotesEmpty);
        Assert.False(vm.RemoteBranchesEmpty);
        Assert.Contains("Could not read this repository's remotes", vm.RemotesErrorText);
        Assert.Contains("refused by the fixture", vm.RemotesErrorText);
    }

    /// <summary>
    /// The remote-tracking read is the other half, and its failure says nothing about which
    /// remotes are configured: the remotes render, and the failure is reported on the panel whose
    /// upstream and delete-on-remote pickers it feeds — which is what explains their being empty.
    /// Neither list may claim "none configured" off a read that never answered.
    /// </summary>
    [Fact]
    public async Task ARemoteBranchReadThatExitsNonZero_LeavesTheRemotesListedAndReportsItsOwnFailure()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-exit-remote-branches");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");

        var vm = new RemotesViewModel(git: new RemoteReadRefusingGit("remote-branches"));
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);

        Assert.Equal("origin", Assert.Single(vm.Remotes).Name);
        Assert.False(vm.RemotesEmpty);
        Assert.Equal("", vm.RemotesErrorText);

        Assert.Empty(vm.RemoteBranches);
        Assert.False(vm.RemoteBranchesEmpty);
        Assert.Empty(vm.UpstreamChoices);
        Assert.Contains("Could not read this repository's remote branches", vm.BranchExtrasErrorText);
        Assert.Contains("refused by the fixture", vm.BranchExtrasErrorText);
    }

    /// <summary>Exits one listing non-zero the way git does, leaving every other read real.</summary>
    private sealed class RemoteReadRefusingGit(string failing) : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            var refused = failing switch
            {
                "remote" => list is ["remote", "-v"],
                _ => list is ["for-each-ref", "refs/remotes", ..],
            };
            return refused
                ? Task.FromResult(new ProcessResult(128, "", "refused by the fixture", TimedOut: false))
                : base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    /// <summary>
    /// A read still in flight when the reader moves on carries the previous repository's answer.
    /// Marking the tab loaded from that continuation asserts the NEW repository's empty lists as
    /// fact — nothing has been read about it yet.
    /// </summary>
    [Fact]
    public async Task SwitchingProjectsMidRead_LeavesTheIncomingProjectsTabUnloaded()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("remotes-race-a");
        using var other = await TempRepo.CreateWithCommitAsync("remotes-race-b");
        await repo.GitAsync("remote", "add", "origin", "https://example.test/a.git");

        var git = new SwitchMidReadGitService();
        var vm = new RemotesViewModel(git: git);
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(repo));

        git.OnNextCall = () => vm.SetProjectAsync(ProjectFor(other));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);

        Assert.False(vm.BranchesTabLoaded);
        Assert.Empty(vm.Remotes);
        Assert.False(vm.RemotesEmpty);
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

    // ── Pruning a remote ────────────────────────────────────────────────────

    /// <summary>Clone whose origin has lost the branches the clone still holds tracking refs for.</summary>
    private static async Task<(TempRepo Seed, TempRepo Bare, TempRepo Clone)> StaleTrackingRefsAsync(string prefix)
    {
        var seed = await TempRepo.CreateWithCommitAsync($"{prefix}-seed");
        await seed.GitAsync("branch", "gone");
        await seed.GitAsync("branch", "also-gone");
        var bare = await TempRepo.CreateBareFromAsync(seed);
        var clone = await TempRepo.CloneFromAsync(bare, $"{prefix}-clone");
        await Git.RunAsync(bare.Path, "branch", "-D", "gone");
        await Git.RunAsync(bare.Path, "branch", "-D", "also-gone");
        return (seed, bare, clone);
    }

    [Fact]
    public async Task PruningARemote_NamesTheStaleRefsInTheConfirmationAndDropsOnlyThisRepositorysCopies()
    {
        var (seed, bare, clone) = await StaleTrackingRefsAsync("prune-vm");
        using var _ = seed;
        using var __ = bare;
        using var ___ = clone;

        var vm = await OpenedOn(clone);
        Assert.Contains("origin/gone", vm.RemoteBranches);

        await vm.PruneRemoteCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("origin/gone", vm.LastConfirmMessage);
        Assert.Contains("origin/also-gone", vm.LastConfirmMessage);
        Assert.DoesNotContain("origin/gone", vm.RemoteBranches);
        Assert.DoesNotContain("origin/also-gone", vm.RemoteBranches);
        Assert.Contains("Pruned 2 remote-tracking branches", vm.RemotesStatusText);
        // The branch the remote still publishes is not stale and is left alone.
        Assert.Contains("origin/main", vm.RemoteBranches);
        Assert.Contains("main", await Git.RunAsync(bare.Path, "branch", "--list"));
    }

    /// <summary>
    /// The count names what this prune dropped. Measured against the list the pane was holding,
    /// a ref something outside the app removed since the tab loaded is counted as this prune's
    /// work — a removal it is reporting but never performed.
    /// </summary>
    [Fact]
    public async Task PruningARemote_CountsWhatItDropped_NotWhatWentBeforeItRan()
    {
        var (seed, bare, clone) = await StaleTrackingRefsAsync("prune-stale-count");
        using var _ = seed;
        using var __ = bare;
        using var ___ = clone;

        var vm = await OpenedOn(clone);
        Assert.Contains("origin/gone", vm.RemoteBranches);
        Assert.Contains("origin/also-gone", vm.RemoteBranches);

        // Something outside the app drops one of the two between the tab's read and the click.
        await clone.GitAsync("update-ref", "-d", "refs/remotes/origin/also-gone");

        await vm.PruneRemoteCommand.ExecuteAsync(null);

        Assert.Contains("Pruned 1 remote-tracking branch under origin", vm.RemotesStatusText);
        Assert.DoesNotContain("origin/gone", vm.RemoteBranches);
    }

    /// <summary>
    /// Without the list to measure against there is no count to report. Falling back to the
    /// pane's own list is what produces a number naming removals this prune never made, so the
    /// prune is reported as done and the count is left unstated.
    /// </summary>
    [Fact]
    public async Task PruningARemote_WhenTheRefsCannotBeCounted_ReportsThePruneWithoutANumber()
    {
        var (seed, bare, clone) = await StaleTrackingRefsAsync("prune-uncountable");
        using var _ = seed;
        using var __ = bare;
        using var ___ = clone;

        var vm = new RemotesViewModel(git: new RemoteReadRefusingGit("remote-branches"));
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(clone));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);

        await vm.PruneRemoteCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("Pruned origin", vm.RemotesStatusText);
        Assert.Contains("could not be counted", vm.RemotesStatusText);
        Assert.DoesNotContain("Pruned 0", vm.RemotesStatusText);
        // The prune itself ran: the stale refs are gone from disk.
        Assert.DoesNotContain("origin/gone",
            await clone.GitAsync("for-each-ref", "--format=%(refname:short)", "refs/remotes"));
    }

    [Fact]
    public async Task PruningARemote_CancelledAtTheConfirmation_LeavesTheStaleRefsInPlace()
    {
        var (seed, bare, clone) = await StaleTrackingRefsAsync("prune-cancel");
        using var _ = seed;
        using var __ = bare;
        using var ___ = clone;

        var vm = await OpenedOn(clone, confirm: false);

        await vm.PruneRemoteCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("origin/gone", vm.RemoteBranches);
        Assert.Contains("origin/gone",
            (await clone.GitAsync("for-each-ref", "--format=%(refname:short)", "refs/remotes")));
        Assert.Equal("", vm.RemotesStatusText);
    }

    [Fact]
    public async Task PruningARemoteWithNothingStale_SaysSoWithoutAskingForAConfirmation()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("prune-clean-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "prune-clean-clone");

        var vm = await OpenedOn(clone);
        await vm.PruneRemoteCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Confirmations);
        Assert.Contains("Nothing to prune", vm.RemotesStatusText);
        Assert.Equal("", vm.RemotesErrorText);
    }

    /// <summary>
    /// The dry run is what the confirmation is composed from. A run that answered nothing is not
    /// a remote with nothing stale, and offering the prune off it would name no refs at all.
    /// </summary>
    [Fact]
    public async Task APruneDryRunThatFails_RefusesTheOfferRatherThanConfirmingAgainstAnEmptyList()
    {
        var (seed, bare, clone) = await StaleTrackingRefsAsync("prune-read-fails");
        using var _ = seed;
        using var __ = bare;
        using var ___ = clone;

        var vm = new RemotesViewModel(git: new PruneDryRunRefusingGit());
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(ProjectFor(clone));
        await vm.LoadBranchesTabCommand.ExecuteAsync(null);

        await vm.PruneRemoteCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Confirmations);
        Assert.Contains("Could not read what pruning origin would drop", vm.RemotesErrorText);
        Assert.Contains("refused by the fixture", vm.RemotesErrorText);
        Assert.Equal("", vm.RemotesStatusText);
        Assert.Contains("origin/gone", vm.RemoteBranches);
    }

    /// <summary>Exits the prune dry run non-zero, leaving the prune itself and every read real.</summary>
    private sealed class PruneDryRunRefusingGit : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            return list is ["remote", "prune", _, "--dry-run"]
                ? Task.FromResult(new ProcessResult(128, "", "refused by the fixture", TimedOut: false))
                : base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    [Fact]
    public void ThePruneConfirmation_NamesTheRefsAndCountsTheRestWhenTheListRunsLong()
    {
        var many = Enumerable.Range(1, 15).Select(i => $"origin/topic-{i}").ToList();

        var message = ProjectDetailViewModel.PruneMessage("origin", many);

        Assert.Contains("origin/topic-1", message);
        Assert.Contains("origin/topic-12", message);
        Assert.DoesNotContain("origin/topic-13", message);
        Assert.Contains("…and 3 more", message);
        Assert.Contains("Nothing on the remote changes", message);
    }

    [Fact]
    public void ThePruneConfirmation_ReadsAsOneBranchWhenThereIsOnlyOne()
    {
        var message = ProjectDetailViewModel.PruneMessage("origin", ["origin/gone"]);

        Assert.Contains("1 remote-tracking branch here is no longer on origin", message);
    }

    // ── Checking out a branch from a remote ─────────────────────────────────

    [Fact]
    public async Task SelectingARemoteBranch_ProposesTheLocalNameWithTheRemotePrefixStripped()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("crb-name-seed");
        await seed.GitAsync("branch", "feature/one");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "crb-name-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/feature/one";

        // Only the remote name goes; a branch name with slashes in it keeps them.
        Assert.Equal("feature/one", vm.RemoteBranchLocalName);
    }

    [Fact]
    public async Task CheckingOutARemoteBranch_CreatesALocalBranchTrackingItAndSwitchesToIt()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("crb-vm-seed");
        await seed.GitAsync("switch", "-c", "release");
        seed.WriteFile("r.txt", "release\n");
        await seed.CommitAllAsync("release work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "crb-vm-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/release";
        Assert.Equal("release", vm.RemoteBranchLocalName);

        await vm.CheckoutRemoteBranchCommand.ExecuteAsync(null);

        Assert.Equal("release", (await clone.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
        Assert.Equal("origin/release", vm.Branches.Single(b => b.Name == "release").Upstream);
        Assert.Contains("nothing was fetched", vm.RemotesStatusText);
        Assert.Equal("", vm.RemotesErrorText);
    }

    [Fact]
    public async Task CheckingOutARemoteBranch_UnderAnEditedName_UsesTheNameTyped()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("crb-edit-seed");
        await seed.GitAsync("branch", "release");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "crb-edit-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/release";
        vm.RemoteBranchLocalName = "staging";
        await vm.CheckoutRemoteBranchCommand.ExecuteAsync(null);

        Assert.Equal("staging", (await clone.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
        Assert.Equal("origin/release", vm.Branches.Single(b => b.Name == "staging").Upstream);
        Assert.DoesNotContain(vm.Branches, b => b.Name == "release");
    }

    /// <summary>
    /// git owns the collision answer here, as it does for the sibling create-branch action. What
    /// matters is that the failure is reported and the branch already holding the name is left
    /// exactly where it was.
    /// </summary>
    [Fact]
    public async Task CheckingOutARemoteBranch_UnderANameAlreadyHere_FailsAndLeavesThatBranchAlone()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("crb-clash-seed");
        await seed.GitAsync("switch", "-c", "release");
        seed.WriteFile("r.txt", "release\n");
        await seed.CommitAllAsync("release work");
        await seed.GitAsync("switch", "main");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "crb-clash-clone");
        var mainSha = (await clone.GitAsync("rev-parse", "refs/heads/main")).Trim();

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/release";
        vm.RemoteBranchLocalName = "main";
        await vm.CheckoutRemoteBranchCommand.ExecuteAsync(null);

        Assert.Contains("Check out main failed", vm.RemotesErrorText);
        Assert.Equal(mainSha, (await clone.GitAsync("rev-parse", "refs/heads/main")).Trim());
        Assert.Equal("main", (await clone.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("two..dots")]
    public async Task CheckingOutARemoteBranch_UnderAnInvalidName_IsRefusedBeforeGitIsAsked(string name)
    {
        using var seed = await TempRepo.CreateWithCommitAsync("crb-badname-seed");
        await seed.GitAsync("branch", "release");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "crb-badname-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedRemoteBranch = "origin/release";
        vm.RemoteBranchLocalName = name;
        await vm.CheckoutRemoteBranchCommand.ExecuteAsync(null);

        Assert.Contains("is not a valid branch name", vm.RemotesErrorText);
        Assert.Equal("main", (await clone.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
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
