using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The tag viewer. What is asserted is that the list reports what git records for both kinds of
/// tag, that creation lands on the commit the surface names rather than on HEAD by default, that a
/// delete is confirmed and reported as the local-only thing it is, and that checking a tag out
/// adds a branch without moving the tag.
/// </summary>
public class TagsSurfaceTests
{
    /// <summary>Carries the recent commits discovery would have loaded, which is what the History list binds to.</summary>
    private static async Task<ProjectInfo> ProjectForAsync(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo
        {
            DirectoryName = name,
            DisplayName = name,
            FullPath = repo.Path,
            RecentCommits = await new GitService().GetRecentCommitsAsync(repo.Path, 50)
        };
    }

    /// <summary>Answers the confirmation without a window and records what it was asked.</summary>
    private sealed class TagViewModel(bool confirm = true, GitService? git = null)
        : ProjectDetailViewModel(null!, git ?? new GitService(), null!, null, new RepoBusyRegistry())
    {
        public int Confirmations { get; private set; }
        public string LastConfirmMessage { get; private set; } = "";

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastConfirmMessage = message;
            return Task.FromResult(confirm);
        }
    }

    private static async Task<TagViewModel> OpenedOn(TempRepo repo, bool confirm = true)
    {
        var vm = new TagViewModel(confirm);
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(await ProjectForAsync(repo));
        await vm.LoadBranchesCommand.ExecuteAsync(null);
        await vm.OpenTagsCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task TheList_ReportsNameKindTargetSubjectAndDate()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-list");
        await repo.GitAsync("tag", "v0-light");
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("the second commit");
        await repo.GitAsync("tag", "-a", "v1", "-m", "first release");

        var vm = await OpenedOn(repo);

        Assert.True(vm.TagsVisible);
        Assert.False(vm.SafetyOverlayHidden);
        Assert.False(vm.TagsEmpty);
        Assert.Equal(2, vm.Tags.Count);

        var annotated = vm.Tags.Single(t => t.Name == "v1");
        Assert.Equal("annotated", annotated.KindLabel);
        Assert.Equal("the second commit", annotated.TargetSubject);
        Assert.NotNull(annotated.DisplayDate);

        var light = vm.Tags.Single(t => t.Name == "v0-light");
        Assert.Equal("lightweight", light.KindLabel);
        Assert.Equal("initial commit", light.TargetSubject);
        Assert.NotNull(light.DisplayDate);
    }

    [Fact]
    public async Task ARepositoryWithNoTags_ShowsAnEmptyStateThatDoesNotOverclaim()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-none");

        var vm = await OpenedOn(repo);

        Assert.Empty(vm.Tags);
        Assert.True(vm.TagsEmpty);
        var markup = await File.ReadAllTextAsync(ViewSource("TagsView.xaml"));
        Assert.Contains("none have been fetched yet", markup);
    }

    /// <summary>
    /// A ref read git refuses exits non-zero rather than throwing, so the list arrives empty and
    /// the viewer used to state "no tags" about a repository it never read. The empty state
    /// belongs to a read that answered; a refused one gets the error instead.
    /// </summary>
    [Fact]
    public async Task ATagReadThatExitsNonZero_ReportsTheFailureInsteadOfClaimingTheRepositoryHasNoTags()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-exit");
        await repo.GitAsync("tag", "v1");

        var vm = new TagViewModel(git: new TagReadRefusingGit());
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(await ProjectForAsync(repo));
        await vm.LoadBranchesCommand.ExecuteAsync(null);
        await vm.OpenTagsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Tags);
        Assert.False(vm.TagsEmpty);
        Assert.Contains("Could not read this repository's tags", vm.TagsErrorText);
        Assert.Contains("refused by the fixture", vm.TagsErrorText);
    }

    /// <summary>Exits the tag listing non-zero the way git does, leaving every other read real.</summary>
    private sealed class TagReadRefusingGit : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            return list is ["for-each-ref", "refs/tags", ..]
                ? Task.FromResult(new ProcessResult(128, "", "refused by the fixture", TimedOut: false))
                : base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    /// <summary>
    /// The viewer runs two reads with separate answers. A refused remote read says nothing about
    /// the tags, so the tags render; what it does explain is a push dropdown with no targets in
    /// it, which is reported on its own line rather than as a tag failure that blanks the list.
    /// </summary>
    [Fact]
    public async Task ARemoteReadThatExitsNonZero_LeavesTheTagsListedAndReportsTheRemoteFailureSeparately()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-remote-exit");
        await repo.GitAsync("tag", "v1");

        var vm = new TagViewModel(git: new RemoteReadRefusingGit());
        vm.ConfirmPrompt = vm.ConfirmAsync;
        await vm.SetProjectAsync(await ProjectForAsync(repo));
        await vm.LoadBranchesCommand.ExecuteAsync(null);
        await vm.OpenTagsCommand.ExecuteAsync(null);

        Assert.Single(vm.Tags);
        Assert.Equal("v1", vm.Tags[0].Name);
        Assert.False(vm.TagsEmpty);
        Assert.Equal("", vm.TagsErrorText);
        Assert.Empty(vm.TagRemoteNames);
        Assert.Contains("Could not read this repository's remotes", vm.TagsStatusText);
        Assert.Contains("refused by the fixture", vm.TagsStatusText);
    }

    /// <summary>Exits the remote listing non-zero the way git does, leaving every other read real.</summary>
    private sealed class RemoteReadRefusingGit : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            return list is ["remote", "-v"]
                ? Task.FromResult(new ProcessResult(128, "", "refused by the fixture", TimedOut: false))
                : base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    [Fact]
    public async Task CreatingATag_LandsOnTheSelectedCommitRatherThanHead()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-target");
        var first = await repo.HeadShaAsync();
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("the second commit");
        var head = await repo.HeadShaAsync();

        var vm = await OpenedOn(repo);
        vm.SelectedCommit = vm.Commits.Single(c => c.Ref == first);
        Assert.Contains("initial commit", vm.TagTargetLabel);

        vm.NewTagName = "v0";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.Equal(first, (await repo.GitAsync("rev-parse", "v0^{commit}")).Trim());
        Assert.NotEqual(head, first);
        Assert.Contains("nothing was pushed", vm.TagsStatusText);
        Assert.Equal("", vm.NewTagName);
        Assert.Single(vm.Tags);
    }

    [Fact]
    public async Task CreatingATag_WithNoCommitSelected_LandsOnHead()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-head");
        var head = await repo.HeadShaAsync();

        var vm = await OpenedOn(repo);
        vm.SelectedCommit = null;
        Assert.Contains("HEAD", vm.TagTargetLabel);

        vm.NewTagName = "here";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.Equal(head, (await repo.GitAsync("rev-parse", "here^{commit}")).Trim());
    }

    [Fact]
    public async Task AMessage_MakesTheTagAnnotatedAndAnEmptyOneLeavesItLightweight()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-kind");
        var vm = await OpenedOn(repo);

        vm.NewTagName = "plain";
        await vm.CreateTagCommand.ExecuteAsync(null);

        vm.NewTagName = "described";
        vm.NewTagMessage = "with a message";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.False(vm.Tags.Single(t => t.Name == "plain").IsAnnotated);
        var annotated = vm.Tags.Single(t => t.Name == "described");
        Assert.True(annotated.IsAnnotated);
        Assert.Equal("with a message", annotated.Subject);
        Assert.Equal("", vm.NewTagMessage);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("two..dots")]
    [InlineData("-leading-dash")]
    [InlineData("trailing.lock")]
    public async Task AnInvalidTagName_IsRefusedBeforeGitIsAskedToWriteAnything(string name)
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-name");
        var vm = await OpenedOn(repo);

        vm.NewTagName = name;
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.Contains("is not a valid tag name", vm.TagsErrorText);
        Assert.Equal("", (await repo.GitAsync("tag", "--list")).Trim());
        Assert.Equal(name, vm.NewTagName);
    }

    [Fact]
    public async Task AnExistingTagName_IsRefusedRatherThanReportedAsAGitFailure()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-dupe");
        await repo.GitAsync("tag", "taken");
        var vm = await OpenedOn(repo);

        vm.NewTagName = "taken";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.Contains("already exists here", vm.TagsErrorText);
        Assert.Single(vm.Tags);
    }

    [Fact]
    public async Task DeletingATag_IsRefusedWithoutTheConfirmation()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-declined");
        await repo.GitAsync("tag", "keep-me");
        var vm = await OpenedOn(repo, confirm: false);

        vm.SelectedTag = vm.Tags.Single();
        await vm.DeleteTagCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("keep-me", await repo.GitAsync("tag", "--list"));
        Assert.Equal("", vm.TagsStatusText);
    }

    [Fact]
    public async Task DeletingATag_RemovesItHereAndSaysTheRemoteCopyIsUntouched()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-del-seed");
        await seed.GitAsync("tag", "shared");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-del-clone");

        var vm = await OpenedOn(clone);
        vm.SelectedTag = vm.Tags.Single(t => t.Name == "shared");
        await vm.DeleteTagCommand.ExecuteAsync(null);

        Assert.Equal("", (await clone.GitAsync("tag", "--list")).Trim());
        // The remote's copy is a separate ref that only a push could remove, and none ran.
        Assert.Contains("shared", await Git.RunAsync(bare.Path, "tag", "--list"));
        Assert.Contains("origin", vm.LastConfirmMessage);
        Assert.Contains("only sends tags", vm.TagsStatusText);
        Assert.Empty(vm.Tags);
    }

    /// <summary>
    /// With no remotes configured, this repository knows of no other copy — which is not the same
    /// as there being none, and the wording claims only the former.
    /// </summary>
    [Fact]
    public void TheRemoteNotice_ClaimsOnlyWhatThisRepositoryKnows()
    {
        var noRemotes = ProjectDetailViewModel.RemoteTagNotice([]);
        Assert.Contains("nothing here knows of another copy", noRemotes);
        Assert.DoesNotContain("nowhere else", noRemotes);
        var withRemotes = ProjectDetailViewModel.RemoteTagNotice(["origin", "mirror"]);
        Assert.Contains("origin, mirror", withRemotes);
        Assert.Contains("takes a push", withRemotes);
    }

    // ── Push ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePushTarget_DefaultsToOriginWhereItIsConfigured()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-target-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-target-clone");
        await clone.GitAsync("remote", "add", "mirror", "https://example.test/m.git");
        await clone.GitAsync("tag", "v1");

        var vm = await OpenedOn(clone);

        Assert.Contains("origin", vm.TagRemoteNames);
        Assert.Contains("mirror", vm.TagRemoteNames);
        Assert.Equal("origin", vm.SelectedTagRemote);
    }

    [Fact]
    public async Task ThePushTarget_FallsBackToTheOnlyRemoteWhenItIsNotCalledOrigin()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-target-alt");
        await repo.GitAsync("remote", "add", "mirror", "https://example.test/m.git");
        await repo.GitAsync("tag", "v1");

        var vm = await OpenedOn(repo);

        Assert.Equal("mirror", vm.SelectedTagRemote);
    }

    [Fact]
    public async Task WithNoRemoteConfigured_ThereIsNowhereToPushAndNeitherPushCanRun()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-no-remote");
        await repo.GitAsync("tag", "v1");

        var vm = await OpenedOn(repo);

        Assert.Null(vm.SelectedTagRemote);
        Assert.False(vm.PushTagCommand.CanExecute(null));
        Assert.False(vm.PushAllTagsCommand.CanExecute(null));
    }

    [Fact]
    public async Task PushingTheSelectedTag_PutsItOnTheRemoteAndLeavesTheTagHereWhereItIs()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-push-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-push-clone");
        await clone.GitAsync("tag", "v1");
        await clone.GitAsync("tag", "v2");

        var vm = await OpenedOn(clone);
        vm.SelectedTag = vm.Tags.Single(t => t.Name == "v1");
        await vm.PushTagCommand.ExecuteAsync(null);

        var onRemote = await Git.RunAsync(bare.Path, "tag", "--list");
        Assert.Contains("v1", onRemote);
        // Only the selected tag went; the other is still local-only.
        Assert.DoesNotContain("v2", onRemote);
        Assert.Contains("Pushed v1 to origin", vm.TagsStatusText);
        Assert.Equal("", vm.TagsErrorText);
        Assert.Equal(2, vm.Tags.Count);
    }

    [Fact]
    public async Task PushingAllTags_SendsEveryTagThisRepositoryHolds()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-pushall-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-pushall-clone");
        await clone.GitAsync("tag", "v1");
        await clone.GitAsync("tag", "-a", "v2", "-m", "second");

        var vm = await OpenedOn(clone);
        await vm.PushAllTagsCommand.ExecuteAsync(null);

        var onRemote = await Git.RunAsync(bare.Path, "tag", "--list");
        Assert.Contains("v1", onRemote);
        Assert.Contains("v2", onRemote);
        Assert.Contains("All 2 tags here are now on origin", vm.TagsStatusText);
        Assert.Equal("", vm.TagsErrorText);
        // The whole set goes in one action, so the dialog names what it is about to publish.
        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("v1", vm.LastConfirmMessage);
        Assert.Contains("v2", vm.LastConfirmMessage);
    }

    [Fact]
    public async Task PushingAllTags_IsRefusedWithoutTheConfirmationAndLeavesTheRemoteWithNone()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-pushall-no-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-pushall-no-clone");
        await clone.GitAsync("tag", "v1");

        var vm = await OpenedOn(clone, confirm: false);
        await vm.PushAllTagsCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Equal("", (await Git.RunAsync(bare.Path, "tag", "--list")).Trim());
        Assert.Equal("", vm.TagsStatusText);
        Assert.Equal("", vm.TagsErrorText);
    }

    /// <summary>
    /// Pushing one tag is the same risk class as pushing commits and asks nothing. Pushing the
    /// set is the action that publishes everything at once, and it is the one that asks.
    /// </summary>
    [Fact]
    public async Task PushingASingleTag_AsksNothingWhilePushingTheWholeSetDoes()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-confirm-split-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-confirm-split-clone");
        await clone.GitAsync("tag", "v1");

        var vm = await OpenedOn(clone);
        vm.SelectedTag = vm.Tags.Single();
        await vm.PushTagCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Confirmations);
        Assert.Contains("v1", await Git.RunAsync(bare.Path, "tag", "--list"));

        await vm.PushAllTagsCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
    }

    [Fact]
    public void ThePushAllConfirmation_NamesTheTagsAndCountsTheRestWhenTheListRunsLong()
    {
        var many = Enumerable.Range(1, 15).Select(i => $"v{i}").ToList();

        var message = ProjectDetailViewModel.PushAllTagsMessage("origin", many);

        Assert.Contains("Push all 15 tags here to origin?", message);
        Assert.Contains("v12", message);
        Assert.DoesNotContain("v13", message);
        Assert.Contains("…and 3 more", message);
        Assert.Contains("takes a deletion on the remote itself", message);
    }

    /// <summary>
    /// A remote that refuses the ref for its own protection is the one failure neither retrying
    /// nor renaming answers, and git's own wording does not say so. Every other refusal keeps
    /// git's text alone.
    /// </summary>
    [Fact]
    public async Task ATagTheRemoteRefusesForItsProtection_IsNamedAsProtectedRatherThanLeftGeneric()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-protected-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-protected-clone");
        RefuseEveryPush(bare, "GH006: Protected tag update failed for refs/tags/v1.");
        await clone.GitAsync("tag", "v1");

        var vm = await OpenedOn(clone);
        vm.SelectedTag = vm.Tags.Single();
        await vm.PushTagCommand.ExecuteAsync(null);

        Assert.DoesNotContain("v1", await Git.RunAsync(bare.Path, "tag", "--list"));
        Assert.Contains("protected on the remote", vm.TagsErrorText);
        Assert.Contains("v1 was not pushed", vm.TagsStatusText);
        Assert.Single(vm.Tags);
    }

    [Fact]
    public async Task ATagTheRemoteRefusesForAnyOtherReason_KeepsGitsOwnWordingWithoutClaimingProtection()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-refused-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-refused-clone");
        RefuseEveryPush(bare, "the fixture declines this one");
        await clone.GitAsync("tag", "v1");

        var vm = await OpenedOn(clone);
        vm.SelectedTag = vm.Tags.Single();
        await vm.PushTagCommand.ExecuteAsync(null);

        Assert.Contains("Push v1 to origin failed", vm.TagsErrorText);
        Assert.DoesNotContain("protected on the remote", vm.TagsErrorText);
    }

    /// <summary>Makes the bare origin reject every incoming push, echoing the given remote-side text.</summary>
    private static void RefuseEveryPush(TempRepo bare, string remoteText) =>
        File.WriteAllText(Path.Combine(bare.Path, "hooks", "pre-receive"),
            $"#!/bin/sh\necho \"{remoteText}\" >&2\nexit 1\n");

    [Theory]
    // The remote named its protection alongside the rejection.
    [InlineData("remote: GH006: Protected tag update failed for refs/tags/v1.\n" +
                " ! [remote rejected] v1 -> v1 (pre-receive hook declined)", true)]
    [InlineData("remote: GH013: Repository rule violations found\n" +
                "remote: - Cannot create ref due to creations being restricted\n" +
                " ! [remote rejected] v1 -> v1 (push declined due to repository rule violations)", true)]
    [InlineData(" ! [remote rejected] v1 -> v1 (protected tag hook declined)", true)]
    // A tag already on the remote at another commit is rejected too, and that one is answerable here.
    [InlineData(" ! [rejected] v1 -> v1 (already exists)\nerror: failed to push some refs", false)]
    [InlineData(" ! [remote rejected] v1 -> v1 (pre-receive hook declined)", false)]
    [InlineData("", false)]
    public void OnlyARefusalThatNamesAProtection_IsReadAsOne(string output, bool protectedRef)
        => Assert.Equal(protectedRef, ProjectDetailViewModel.IsProtectedRefRefusal(output));

    [Fact]
    public async Task ClosingTheViewer_DropsThePushTargetTheRepositoryChose()
    {
        using var seed = await TempRepo.CreateWithCommitAsync("tags-push-close-seed");
        using var bare = await TempRepo.CreateBareFromAsync(seed);
        using var clone = await TempRepo.CloneFromAsync(bare, "tags-push-close-clone");
        await clone.GitAsync("tag", "v1");

        var vm = await OpenedOn(clone);
        Assert.Equal("origin", vm.SelectedTagRemote);

        vm.CloseTagsCommand.Execute(null);

        Assert.Null(vm.SelectedTagRemote);
        Assert.False(vm.PushAllTagsCommand.CanExecute(null));
    }

    /// <summary>
    /// The overlay's own copy told the reader nothing here pushes. With a push on the surface
    /// that sentence would be false, and the one that replaces it has to keep saying what a push
    /// still does not do.
    /// </summary>
    [Fact]
    public async Task TheOverlayCopy_DescribesThePushRatherThanDisclaimingIt()
    {
        var markup = await File.ReadAllTextAsync(ViewSource("TagsView.xaml"));

        Assert.DoesNotContain("no action on this surface pushes", markup);
        Assert.Contains("Pushing sends a tag to the remote you choose", markup);
        Assert.Contains("nothing on this surface removes a tag from a remote", markup);
    }

    [Fact]
    public async Task CheckingATagOut_CreatesABranchAtItsCommitAndLeavesTheTagWhereItIs()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-checkout");
        var first = await repo.HeadShaAsync();
        await repo.GitAsync("tag", "v0");
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("the second commit");

        var vm = await OpenedOn(repo);
        vm.SelectedTag = vm.Tags.Single(t => t.Name == "v0");
        vm.TagBranchName = "from-v0";
        await vm.CheckOutTagAsBranchCommand.ExecuteAsync(null);

        Assert.Equal(first, (await repo.GitAsync("rev-parse", "refs/heads/from-v0")).Trim());
        Assert.Equal("from-v0", (await repo.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
        // The tag is a ref of its own and nothing here moves it.
        Assert.Equal(first, (await repo.GitAsync("rev-parse", "v0^{commit}")).Trim());
        Assert.Contains("did not move", vm.TagsStatusText);
        Assert.Equal("", vm.TagBranchName);
    }

    [Fact]
    public async Task CheckingATagOut_RefusesABranchNameAlreadyInUse()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-checkout-dupe");
        await repo.GitAsync("tag", "v0");
        var vm = await OpenedOn(repo);

        vm.SelectedTag = vm.Tags.Single();
        vm.TagBranchName = "main";
        await vm.CheckOutTagAsBranchCommand.ExecuteAsync(null);

        Assert.Contains("already exists here", vm.TagsErrorText);
        Assert.Equal("main", (await repo.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
    }

    [Fact]
    public async Task ClosingTheViewer_DropsEverythingItHeld()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-close");
        await repo.GitAsync("tag", "v0");
        var vm = await OpenedOn(repo);
        vm.NewTagName = "half-typed";

        vm.CloseTagsCommand.Execute(null);

        Assert.False(vm.TagsVisible);
        Assert.Empty(vm.Tags);
        Assert.Null(vm.SelectedTag);
        Assert.Equal("", vm.NewTagName);
        Assert.True(vm.SafetyOverlayHidden);
    }

    /// <summary>
    /// The viewer describes one repository. Left open across a switch it would offer that
    /// repository's tags as actions against the one now on screen.
    /// </summary>
    [Fact]
    public async Task SwitchingProjects_ClosesTheViewer()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("tags-switch-a");
        using var other = await TempRepo.CreateWithCommitAsync("tags-switch-b");
        await repo.GitAsync("tag", "v0");
        var vm = await OpenedOn(repo);
        Assert.True(vm.TagsVisible);

        await vm.SetProjectAsync(await ProjectForAsync(other));

        Assert.False(vm.TagsVisible);
        Assert.Empty(vm.Tags);
    }

    /// <summary>
    /// A signing choice with no button is a tag the reader can never create: the gate refuses
    /// every attempt and the only two answers live on these two commands.
    /// </summary>
    [Fact]
    public async Task TheTagSigningOfferAndChip_AreReachableAndAnnounced()
    {
        var markup = await File.ReadAllTextAsync(ViewSource("TagsView.xaml"));

        var offer = System.Text.RegularExpressions.Regex.Match(markup,
            @"<StackPanel x:Name=""TagSigningOffer"".*?</StackPanel>\s*</StackPanel>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(offer.Success, "the tag signing offer was not found");
        Assert.Contains("{Binding TagSigningOfferText}", offer.Value);
        Assert.Contains("{Binding CreateTagSignedCommand}", offer.Value);
        Assert.Contains("{Binding CreateTagUnsignedCommand}", offer.Value);

        var chip = System.Text.RegularExpressions.Regex.Match(markup,
            @"<Border x:Name=""TagSigningChip"".*?</Border>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(chip.Success, "the tag signing chip was not found");
        Assert.Contains("{Binding TagSigningChipText}", chip.Value);
        Assert.Contains("AutomationProperties.Name=\"{Binding TagSigningChipTooltip}\"", chip.Value);
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", chip.Value);
    }

    private static string ViewSource(string name, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
