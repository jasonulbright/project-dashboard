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
    private sealed class TagViewModel(bool confirm = true)
        : ProjectDetailViewModel(null!, new GitService(), null!, null, new RepoBusyRegistry())
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
        Assert.Contains("never pushes tags", vm.TagsStatusText);
        Assert.Empty(vm.Tags);
    }

    [Fact]
    public void TheRemoteNotice_SaysNowhereElseOnlyWhenThereIsNowhereElse()
    {
        Assert.Contains("exists nowhere else", ProjectDetailViewModel.RemoteTagNotice([]));
        var withRemotes = ProjectDetailViewModel.RemoteTagNotice(["origin", "mirror"]);
        Assert.Contains("origin, mirror", withRemotes);
        Assert.Contains("takes a push", withRemotes);
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

    private static string ViewSource(string name, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
