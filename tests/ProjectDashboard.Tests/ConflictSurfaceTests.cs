using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The conflict panel as a reader drives it: what the banner offers, which rows carry which
/// refusal, what the marker guard does to a stage, and where a resolve-then-continue leaves the
/// repository. The git-level round trips are proven in <see cref="ConflictResolverTests"/>; what
/// is proven here is everything between the reader and them.
/// </summary>
public class ConflictSurfaceTests
{
    private static string Markup => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

    private sealed class PanelViewModel : ProjectDetailViewModel
    {
        public PanelViewModel(GitService git, bool confirm = true)
            : base(null!, git, null!) => _confirm = confirm;

        private readonly bool _confirm;

        public int Confirmations { get; private set; }
        public string LastConfirmMessage { get; private set; } = "";
        public string? OpenedFile { get; private set; }

        /// <summary>What the rebase-origin probe answers, for the states a fixture cannot stage.</summary>
        public RebaseDriver.StoppedRebaseOrigin Origin { get; set; } =
            RebaseDriver.StoppedRebaseOrigin.StartedElsewhere;

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastConfirmMessage = message;
            return Task.FromResult(_confirm);
        }

        internal override RebaseDriver.StoppedRebaseOrigin InspectStoppedRebase(string repoPath) => Origin;

        internal override void OpenRepoFile(string repoPath, string relativePath) =>
            OpenedFile = relativePath;
    }

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static async Task<PanelViewModel> OpenedOnAsync(TempRepo repo, bool confirm = true)
    {
        var git = new GitService();
        var vm = new PanelViewModel(git, confirm) { Conflicts = new ConflictResolver(git) };
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        return vm;
    }

    private static async Task<PanelViewModel> WithPanelOpenAsync(TempRepo repo, bool confirm = true)
    {
        var vm = await OpenedOnAsync(repo, confirm);
        await vm.OpenConflictsCommand.ExecuteAsync(null);
        return vm;
    }

    private static async Task<string> UnmergedAsync(TempRepo repo) =>
        string.Join('\n', (await repo.GitAsync("status", "--porcelain=v2")).Split('\n')
            .Where(l => l.StartsWith("u ", StringComparison.Ordinal)));

    // ── The banner is the entry point ───────────────────────────────────────

    [Fact]
    public async Task AConflictedMergeOffersThePanelAndSaysBothRoutesAreOpen()
    {
        using var repo = await ConflictFixtures.MergeAsync();

        var vm = await OpenedOnAsync(repo);

        Assert.True(vm.ConflictPanelOffered);
        Assert.Equal("Merge in progress with conflicts — resolve them here, or in a terminal.", vm.StateBannerText);
    }

    [Fact]
    public async Task ABisectKeepsTheTerminalOnlyBannerAndOffersNoPanel()
    {
        using var repo = await ConflictFixtures.BisectAsync();

        var vm = await OpenedOnAsync(repo);

        Assert.False(vm.ConflictPanelOffered);
        Assert.Equal("Bisect in progress — finish it in a terminal.", vm.StateBannerText);
    }

    [Fact]
    public async Task ARebaseStartedElsewhereSaysWhichHalfOfThePanelApplies()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();

        var vm = await OpenedOnAsync(repo);

        Assert.Contains("started outside this app", vm.StateBannerText);
        Assert.Contains("abort here", vm.StateBannerText);
    }

    [Fact]
    public async Task ARebaseThisAppStoppedOffersBothHalves()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();
        var git = new GitService();
        var vm = new PanelViewModel(git) { Conflicts = new ConflictResolver(git) };
        vm.Origin = RebaseDriver.StoppedRebaseOrigin.StartedHere;

        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;

        Assert.Equal("Rebase in progress — continue or abort it here, or in a terminal.", vm.StateBannerText);
    }

    [Fact]
    public void TheBannerAndThePanelAreBothWiredIntoTheDetailPage()
    {
        Assert.Contains("OpenConflictsCommand", Markup);
        Assert.Contains("ConflictPanelOffered", Markup);
        Assert.Contains("<pages:ConflictsView", Markup);
        // The escape hatch never leaves the banner, whatever the panel offers beside it.
        Assert.Contains("OpenRepoInTerminalCommand", Markup);
    }

    // ── Rows and refusals ───────────────────────────────────────────────────

    [Fact]
    public async Task EachUnmergedPathIsListedWithTheShapeGitRecordedForIt()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        var vm = await WithPanelOpenAsync(repo);

        var byPath = vm.ConflictRows.ToDictionary(r => r.Path, StringComparer.Ordinal);
        Assert.Equal("both modified", byPath["file.txt"].KindLabel);
        Assert.Equal("both added", byPath["added.txt"].KindLabel);
        Assert.Equal("deleted by them", byPath["doomed.txt"].KindLabel);
        Assert.True(byPath["file.txt"].CanTakeOurs && byPath["file.txt"].CanTakeTheirs);
        // Their side deleted it, so taking theirs is a removal and says so on the button.
        Assert.Equal("Take theirs (delete)", byPath["doomed.txt"].TakeTheirsLabel);
    }

    [Fact]
    public void ASubmoduleConflictIsRefusedWithItsReason()
    {
        var rows = ProjectDetailViewModel.BuildConflictRows(null, new Dictionary<string, ConflictStages>
        {
            ["lib/dep"] = new(HasBase: true, HasOurs: true, HasTheirs: true, IsGitlink: true)
        });

        var row = Assert.Single(rows);
        Assert.Equal(ProjectDetailViewModel.GitlinkRefusal, row.Refusal);
        Assert.False(row.CanTakeOurs);
        Assert.False(row.CanTakeTheirs);
    }

    [Fact]
    public void APathNeitherSideKeptIsRefusedWithItsReason()
    {
        var rows = ProjectDetailViewModel.BuildConflictRows(null, new Dictionary<string, ConflictStages>
        {
            ["gone.txt"] = new(HasBase: true, HasOurs: false, HasTheirs: false, IsGitlink: false)
        });

        var row = Assert.Single(rows);
        Assert.Equal(ProjectDetailViewModel.NoContentRefusal, row.Refusal);
        Assert.False(row.CanTakeOurs);
        Assert.False(row.CanTakeTheirs);
    }

    [Fact]
    public async Task ABinaryConflictOffersBothSidesAndDisclosesThatItCannotBePreviewed()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();
        var vm = await WithPanelOpenAsync(repo);

        vm.SelectedConflictRow = vm.ConflictRows.First(r => r.Path == "pic.bin");
        vm.ConflictComparison = ConflictComparison.OursToTheirs;
        await vm.ConflictPreviewRefresh;

        Assert.Equal(ProjectDetailViewModel.BinaryPreviewNote, vm.ConflictPreviewNote);
        Assert.Empty(vm.ConflictDiffLines);
        Assert.True(vm.SelectedConflictRow.CanTakeOurs && vm.SelectedConflictRow.CanTakeTheirs);
    }

    [Fact]
    public async Task ContinueIsRefusedWhileAnythingIsStillUnresolved()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        var vm = await WithPanelOpenAsync(repo);

        Assert.False(vm.ConflictContinueOffered);
        Assert.Contains("still unresolved", vm.ConflictContinueRefusal);
        // Abort is the way out of every state this panel can produce.
        Assert.True(vm.ConflictAbortOffered);
    }

    [Fact]
    public async Task ContinueIsRefusedOnARebaseStartedElsewhereAndAbortIsNot()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();
        var vm = await WithPanelOpenAsync(repo);

        await vm.TakeTheirsCommand.ExecuteAsync(vm.ConflictRows.Single());

        Assert.Empty(vm.ConflictRows);
        Assert.False(vm.ConflictContinueOffered);
        Assert.Equal(ProjectDetailViewModel.ForeignRebaseRefusal, vm.ConflictContinueRefusal);
        Assert.True(vm.ConflictAbortOffered);
    }

    [Fact]
    public async Task ContinueIsRefusedWhenTheStoppedRebasesMessagesHaveBeenReclaimed()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();
        var git = new GitService();
        var vm = new PanelViewModel(git) { Conflicts = new ConflictResolver(git) };
        vm.Origin = RebaseDriver.StoppedRebaseOrigin.MessagesReclaimed;
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        await vm.OpenConflictsCommand.ExecuteAsync(null);

        await vm.TakeTheirsCommand.ExecuteAsync(vm.ConflictRows.Single());

        Assert.Equal(ProjectDetailViewModel.ReclaimedRebaseRefusal, vm.ConflictContinueRefusal);
        Assert.True(vm.ConflictAbortOffered);
    }

    [Fact]
    public async Task AnOperationAlreadyRunningOnTheRepositoryRefusesTheResolution()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var vm = await WithPanelOpenAsync(repo);
        var row = vm.ConflictRows.Single();
        vm.IsBusy = true;

        await vm.TakeOursCommand.ExecuteAsync(row);

        Assert.Equal(ProjectDetailViewModel.BusyNotice("Resolve"), vm.ConflictErrorText);
        Assert.Contains("file.txt", await UnmergedAsync(repo));
    }

    // ── The marker guard ────────────────────────────────────────────────────

    [Fact]
    public async Task StagingAFileThatStillHoldsMarkersIsRefusedAndNothingIsStaged()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var vm = await WithPanelOpenAsync(repo);

        await vm.StageResolvedCommand.ExecuteAsync(vm.ConflictRows.Single());

        Assert.Contains("still contains conflict markers", vm.ConflictErrorText);
        Assert.Contains("file.txt", await UnmergedAsync(repo));
        Assert.Single(vm.ConflictRows);
    }

    [Fact]
    public async Task StagingSucceedsOnceTheMarkersAreGone()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var vm = await WithPanelOpenAsync(repo);
        repo.WriteFile("file.txt", "merged by hand\n");

        await vm.StageResolvedCommand.ExecuteAsync(vm.ConflictRows.Single());

        Assert.Equal("", vm.ConflictErrorText);
        Assert.Empty(vm.ConflictRows);
        Assert.True(vm.ConflictContinueOffered);
    }

    [Fact]
    public async Task OpenInEditorNamesTheConflictedFileItself()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var vm = await WithPanelOpenAsync(repo);

        vm.OpenConflictInEditorCommand.Execute(vm.ConflictRows.Single());

        Assert.Equal("file.txt", vm.OpenedFile);
    }

    [Fact]
    public void APathThatClimbsOutOfTheRepositoryIsNotOpened() =>
        Assert.Null(ProjectDetailViewModel.ResolveInsideRepo(@"C:\repo", @"..\outside.txt"));

    // ── Resolve, then drive the sequencer ───────────────────────────────────

    [Fact]
    public async Task TakingASideResolvesThePathAndTheContinueFinishesTheMerge()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var vm = await WithPanelOpenAsync(repo);

        await vm.TakeOursCommand.ExecuteAsync(vm.ConflictRows.Single());
        Assert.Empty(vm.ConflictRows);
        Assert.Equal("ours\n", repo.ReadFile("file.txt").Replace("\r\n", "\n"));

        await vm.ContinueSequenceCommand.ExecuteAsync(null);

        Assert.Equal(RepoActivity.None, vm.ConflictActivity);
        Assert.Contains("finished", vm.ConflictStatusText);
        Assert.Contains("Merge branch 'side'", await repo.HeadSubjectAsync());
    }

    [Fact]
    public async Task AnEditedMessageIsWhatTheContinueCommits()
    {
        using var repo = await ConflictFixtures.CherryPickStopAsync();
        var vm = await WithPanelOpenAsync(repo);
        await vm.TakeTheirsCommand.ExecuteAsync(vm.ConflictRows.Single());

        Assert.Equal("side change", vm.ConflictMessage);
        vm.ConflictMessage = "picked, with a message of my own";
        await vm.ContinueSequenceCommand.ExecuteAsync(null);

        Assert.Equal("picked, with a message of my own", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, vm.ConflictActivity);
    }

    [Fact]
    public async Task AbortingPutsTheRepositoryBackAndSaysSo()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var before = await repo.HeadShaAsync();
        var vm = await WithPanelOpenAsync(repo);

        await vm.AbortSequenceCommand.ExecuteAsync(null);

        Assert.Equal(before, await repo.HeadShaAsync());
        Assert.Equal(RepoActivity.None, vm.ConflictActivity);
        Assert.Contains("back where it started", vm.ConflictStatusText);
    }

    [Fact]
    public async Task ADeclinedConfirmationChangesNothing()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var vm = await WithPanelOpenAsync(repo, confirm: false);

        await vm.TakeOursCommand.ExecuteAsync(vm.ConflictRows.Single());
        await vm.AbortSequenceCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Confirmations);
        Assert.Contains("file.txt", await UnmergedAsync(repo));
        Assert.Equal(RepoActivity.Merging, vm.ConflictActivity);
    }

    // ── Signing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A continue writes a commit, so a repository that signs asks the question first. Without
    /// this the run reaches git and waits on a passphrase prompt no window shows until the
    /// operation timeout kills it mid-sequence.
    /// </summary>
    [Fact]
    public async Task AContinueInASigningRepositoryAsksTheSigningQuestionBeforeItRuns()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        await repo.GitAsync("config", "commit.gpgsign", "true");
        var vm = await WithPanelOpenAsync(repo);
        await vm.SigningRefresh;
        await vm.TakeOursCommand.ExecuteAsync(vm.ConflictRows.Single());
        var before = await repo.HeadShaAsync();

        await vm.ContinueSequenceCommand.ExecuteAsync(null);

        Assert.True(vm.CommitSigningOfferVisible);
        Assert.Contains("Continue needs a decision first", vm.CommitSigningOfferText);
        Assert.Equal(before, await repo.HeadShaAsync());
        Assert.Equal(RepoActivity.Merging, vm.ConflictActivity);
    }

    /// <summary>The unsigned answer runs the continue that was held, not the commit box's.</summary>
    [Fact]
    public async Task TheUnsignedAnswerRunsTheHeldContinue()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        await repo.GitAsync("config", "commit.gpgsign", "true");
        var vm = await WithPanelOpenAsync(repo);
        await vm.SigningRefresh;
        await vm.TakeOursCommand.ExecuteAsync(vm.ConflictRows.Single());
        await vm.ContinueSequenceCommand.ExecuteAsync(null);

        await vm.CommitUnsignedCommand.ExecuteAsync(null);

        Assert.False(vm.CommitSigningOfferVisible);
        Assert.Equal(RepoActivity.None, vm.ConflictActivity);
        Assert.Contains("Merge branch 'side'", await repo.HeadSubjectAsync());
    }

    // ── Panel lifetime ──────────────────────────────────────────────────────

    [Fact]
    public async Task LeavingTheProjectClosesThePanelItBelongedTo()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        using var other = await TempRepo.CreateWithCommitAsync("conflict-other");
        var vm = await WithPanelOpenAsync(repo);
        Assert.True(vm.ConflictsVisible);

        await vm.SetProjectAsync(ProjectFor(other));

        Assert.False(vm.ConflictsVisible);
        Assert.Empty(vm.ConflictRows);
        Assert.True(vm.SafetyOverlayHidden);
    }
}
