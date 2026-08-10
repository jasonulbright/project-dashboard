using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Repo tab's administration of the repository itself — rename, archive/unarchive, sync
/// fork. What is asserted is that each gate holds before any gh call is spawned, that the two
/// operations which write this machine take the repository lease, that a comparison which never
/// answered offers no sync, and that every one of them lands in the operation ledger under the
/// repository it acted on.
///
/// No test here spawns gh: every remote call comes from an overridden seam on the view model,
/// the same way the sibling surfaces are driven.
/// </summary>
public class RepoAdminSurfaceTests
{
    private static ProcessResult Ok() => new(0, "", "", TimedOut: false);
    private static ProcessResult Failed(string error) => new(1, "", error, TimedOut: false);

    private static RepoSettings Settings(string name = "tool", bool archived = false, string parent = "",
        string defaultBranch = "main") =>
        new()
        {
            Name = name,
            Visibility = "public",
            IsArchived = archived,
            DefaultBranch = defaultBranch,
            ParentSlug = parent
        };

    private static ProjectInfo ProjectFor(TempRepo repo, string remoteUrl)
    {
        var name = Path.GetFileName(repo.Path);
        var project = new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
        project.GitStatus.RemoteUrl = remoteUrl;
        return project;
    }

    /// <summary>A project with a slug and no clone on disk: the remote-only shape.</summary>
    private static ProjectInfo RemoteOnlyProject()
    {
        var project = new ProjectInfo { DirectoryName = "tool", DisplayName = "tool", FullPath = "" };
        project.GitStatus.RemoteUrl = "https://github.com/me/tool.git";
        return project;
    }

    private static async Task<TempRepo> RepoWithOrigin(string prefix, string url)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        await repo.GitAsync("remote", "add", "origin", url);
        return repo;
    }

    private static async Task<string> OriginUrl(TempRepo repo) =>
        (await new GitService().GetRemotesAsync(repo.Path)).Remotes.Single(r => r.Name == "origin").FetchUrl;

    // ── Rename ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_WithoutTheTypedSlug_NeverSpawnsTheCall()
    {
        using var repo = await RepoWithOrigin("rename-gate", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Typed = "tool";                       // the bare name, not the slug
        vm.RepoRenameDraft = "renamed";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.RenameAttempts);
        Assert.Contains("that isn't me/tool", vm.GitHubStatusText);
        Assert.Equal("https://github.com/me/tool.git", await OriginUrl(repo));
    }

    [Fact]
    public async Task Rename_RefusesANameCarryingAnOwner()
    {
        using var repo = await RepoWithOrigin("rename-owner", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Typed = "me/tool";
        vm.RepoRenameDraft = "someone/tool";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.RenameAttempts);
        Assert.Equal(0, vm.Prompts);              // refused before the confirmation, not after it
        Assert.Contains("cannot contain '/'", vm.GitHubStatusText);
    }

    [Fact]
    public async Task Rename_OffersTheLocalUrlAndAppliesItWhenAccepted()
    {
        using var repo = await RepoWithOrigin("rename-origin", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Typed = "me/tool";
        vm.Confirm = true;
        vm.RepoRenameDraft = "renamed";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.RenameAttempts);
        Assert.Equal("https://github.com/me/renamed.git", await OriginUrl(repo));
        Assert.Contains("me/renamed.git", vm.RepoRenameNotice);
        // The slug every later gh call on this tab addresses comes off origin's URL.
        Assert.Equal("me/renamed", vm.Project!.GitHubSlug);
    }

    [Fact]
    public async Task Rename_LeavesOriginAloneWhenTheOfferIsDeclined()
    {
        using var repo = await RepoWithOrigin("rename-declined", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Typed = "me/tool";
        vm.Confirm = false;
        vm.RepoRenameDraft = "renamed";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.RenameAttempts);
        Assert.Equal("https://github.com/me/tool.git", await OriginUrl(repo));
        Assert.Equal(ProjectDetailViewModel.RenameRemoteDeclinedNotice, vm.RepoRenameNotice);
        Assert.Equal("me/tool", vm.Project!.GitHubSlug);
    }

    [Fact]
    public async Task Rename_SaysSoWhenNoOriginNamesTheRenamedRepository()
    {
        using var repo = await RepoWithOrigin("rename-elsewhere", "https://github.com/someone/other.git");
        var vm = await Opened(repo, Settings());
        vm.Typed = "me/tool";
        vm.Confirm = true;
        vm.RepoRenameDraft = "renamed";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.RenameAttempts);
        Assert.Equal(0, vm.Confirms);              // nothing to offer, so nothing is asked
        Assert.Equal("https://github.com/someone/other.git", await OriginUrl(repo));
        Assert.Contains("No origin remote here names me/tool", vm.RepoRenameNotice);
    }

    [Fact]
    public async Task ARenameCollision_IsNamedRatherThanToasted()
    {
        using var repo = await RepoWithOrigin("rename-taken", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Typed = "me/tool";
        vm.RenameResult = Failed("HTTP 422: Repository creation failed. name already exists on this account");
        vm.RepoRenameDraft = "renamed";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Contains("already has a repository named renamed", vm.GitHubStatusText);
        Assert.Equal("https://github.com/me/tool.git", await OriginUrl(repo));
        Assert.Equal("", vm.RepoRenameNotice);     // no offer is made after a rename that did not land
    }

    [Fact]
    public async Task Rename_RefusedWhileTheRepositoryIsUnderAnotherOperation()
    {
        using var repo = await RepoWithOrigin("rename-leased", "https://github.com/me/tool.git");
        var registry = new RepoBusyRegistry();
        var vm = await Opened(repo, Settings(), registry: registry);
        vm.Typed = "me/tool";
        vm.RepoRenameDraft = "renamed";

        Assert.True(registry.TryAcquire(repo.Path, out var lease));
        using (lease) await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.RenameAttempts);
        Assert.Contains("another operation is running", vm.GitHubStatusText);
    }

    [Fact]
    public async Task AProjectWithNoClone_RenamesWithoutOfferingALocalUrl()
    {
        var vm = new RepoAdminViewModel { Settings = Settings(), Typed = "me/tool" };
        await vm.SetProjectAsync(RemoteOnlyProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        vm.RepoRenameDraft = "renamed";

        await vm.RenameRepoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.RenameAttempts);
        Assert.Equal(0, vm.Confirms);
        Assert.Equal(ProjectDetailViewModel.RenameNoCloneNotice, vm.RepoRenameNotice);
    }

    [Theory]
    [InlineData("https://github.com/me/tool.git", "https://github.com/me/renamed.git")]
    [InlineData("https://github.com/me/tool", "https://github.com/me/renamed")]
    [InlineData("git@github.com:me/tool.git", "git@github.com:me/renamed.git")]
    [InlineData("ssh://git@github.com/me/tool.git", "ssh://git@github.com/me/renamed.git")]
    [InlineData("https://github.com/ME/TOOL.git", "https://github.com/ME/renamed.git")]
    public void ARenamedUrl_KeepsEverythingButTheRepositorySegment(string url, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.RenamedRemoteUrl(url, "me/tool", "renamed"));

    [Theory]
    [InlineData("https://github.com/someone/tool.git")]   // a different owner's repository
    [InlineData("https://gitlab.example.com/me/tool.git")] // not GitHub
    [InlineData(@"C:\clones\tool")]                        // a local path, not a remote
    [InlineData("")]
    public void AUrlThatDoesNotNameTheRenamedRepository_IsLeftAlone(string url)
        => Assert.Null(ProjectDetailViewModel.RenamedRemoteUrl(url, "me/tool", "renamed"));

    [Fact]
    public void ARenamedUrl_RefusesANameCarryingAnOwner()
        => Assert.Null(ProjectDetailViewModel.RenamedRemoteUrl(
            "https://github.com/me/tool.git", "me/tool", "someone/renamed"));

    // ── Archive / unarchive ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheArchiveAction_NamesWhicheverDirectionApplies()
    {
        using var repo = await RepoWithOrigin("archive-label", "https://github.com/me/tool.git");
        var live = await Opened(repo, Settings());
        Assert.False(live.RepoIsArchived);
        Assert.True(live.RepoEditsEnabled);
        Assert.Equal("Archive repository", live.RepoArchiveActionLabel);

        var archived = await Opened(repo, Settings(archived: true));
        Assert.True(archived.RepoIsArchived);
        Assert.False(archived.RepoEditsEnabled);
        Assert.Equal("Unarchive repository", archived.RepoArchiveActionLabel);
    }

    [Fact]
    public async Task Archiving_StatesTheReadOnlyConsequenceInTheConfirmation()
    {
        using var repo = await RepoWithOrigin("archive-confirm", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Confirm = true;

        await vm.ToggleRepoArchiveCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.ArchiveAttempts);
        Assert.Equal(0, vm.UnarchiveAttempts);
        Assert.Contains("read-only", vm.LastConfirmMessage);
        Assert.Contains("Pushes are refused", vm.LastConfirmMessage);
    }

    [Fact]
    public async Task DecliningTheConfirmation_LeavesTheRepositoryAsItWas()
    {
        using var repo = await RepoWithOrigin("archive-declined", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Confirm = false;

        await vm.ToggleRepoArchiveCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.ArchiveAttempts);
        Assert.False(vm.RepoIsArchived);
    }

    [Fact]
    public async Task AnArchivedRepository_UnarchivesInsteadOfArchivingAgain()
    {
        using var repo = await RepoWithOrigin("unarchive", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings(archived: true));
        vm.Confirm = true;

        await vm.ToggleRepoArchiveCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.UnarchiveAttempts);
        Assert.Equal(0, vm.ArchiveAttempts);
    }

    /// <summary>
    /// A flag moved before the call returned would grey out every editor on a repository that is
    /// still live, and the reader's next save would be refused by an app that had already agreed
    /// with itself.
    /// </summary>
    [Fact]
    public async Task AFailedArchive_LeavesTheFlagAndTheEditorsWhereTheyWere()
    {
        using var repo = await RepoWithOrigin("archive-failed", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());
        vm.Confirm = true;
        vm.ArchiveResult = Failed("HTTP 403: Must have admin rights to Repository.");

        await vm.ToggleRepoArchiveCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.ArchiveAttempts);
        Assert.False(vm.RepoIsArchived);
        Assert.True(vm.RepoEditsEnabled);
        Assert.Equal("Archive repository", vm.RepoArchiveActionLabel);
        Assert.Contains("Must have admin rights", vm.GitHubStatusText);
    }

    [Fact]
    public async Task AnArchivedRepository_RefusesEveryEditorRatherThanSpawningARefusedWrite()
    {
        using var repo = await RepoWithOrigin("archive-guard", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings(archived: true));
        vm.Typed = "me/tool";
        vm.Confirm = true;
        vm.RepoRenameDraft = "renamed";
        vm.RepoDescriptionDraft = "changed";
        vm.RepoDefaultBranchDraft = "trunk";

        await vm.RenameRepoCommand.ExecuteAsync(null);
        Assert.Contains("archived and read-only", vm.GitHubStatusText);

        await vm.SaveRepoDetailsCommand.ExecuteAsync(null);
        Assert.Contains("archived and read-only", vm.GitHubStatusText);

        await vm.ChangeDefaultBranchCommand.ExecuteAsync(null);
        Assert.Contains("archived and read-only", vm.GitHubStatusText);

        Assert.Equal(0, vm.RenameAttempts);
        Assert.Equal(0, vm.Prompts);
        Assert.Equal(0, vm.Confirms);
    }

    // ── Fork divergence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ANonForkRepository_ShowsNoForkCardAndReadsNoComparison()
    {
        using var repo = await RepoWithOrigin("fork-none", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings());

        await vm.LoadRepoTabCommand.ExecuteAsync(null);

        Assert.False(vm.RepoIsFork);
        Assert.Equal(0, vm.DivergenceReads);
        Assert.False(vm.ForkSyncOfferable);
    }

    [Fact]
    public async Task AFork_ReportsHowFarItStandsFromItsParent()
    {
        using var repo = await RepoWithOrigin("fork-behind", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings(parent: "upstream/tool"));
        vm.ParentSettings = Settings(name: "tool", defaultBranch: "main");
        vm.Divergence = new ForkDivergence(Ahead: 0, Behind: 7);

        await vm.LoadForkDivergenceCommand.ExecuteAsync(null);

        Assert.True(vm.RepoIsFork);
        Assert.Equal("upstream/tool", vm.RepoParentSlug);
        Assert.Equal("main", vm.ComparedBranch);
        Assert.Equal("upstream", vm.ComparedParentOwner);
        Assert.Equal("me", vm.ComparedForkOwner);
        Assert.Contains("7 commits behind", vm.ForkDivergenceText);
        Assert.True(vm.ForkSyncOfferable);
    }

    /// <summary>
    /// A comparison that failed and a fork that matches its parent leave the same counts on
    /// screen if a null is allowed to read as zero; only one of them may offer a sync.
    /// </summary>
    [Fact]
    public async Task AComparisonThatNeverAnswered_OffersNoSyncAndSaysWhy()
    {
        using var repo = await RepoWithOrigin("fork-unknown", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings(parent: "upstream/tool"));
        vm.ParentSettings = Settings(name: "tool", defaultBranch: "main");
        vm.Divergence = null;

        await vm.LoadForkDivergenceCommand.ExecuteAsync(null);

        Assert.False(vm.ForkSyncOfferable);
        Assert.Contains("Couldn't compare this fork", vm.ForkDivergenceText);
        Assert.DoesNotContain("0 commits", vm.ForkDivergenceText);

        await vm.SyncForkCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.SyncAttempts);
        Assert.Equal(ProjectDetailViewModel.ForkSyncUnreadNotice, vm.GitHubStatusText);
    }

    [Fact]
    public async Task AnUnreadableParent_LeavesTheComparisonUnknownRatherThanCallingCompare()
    {
        using var repo = await RepoWithOrigin("fork-noparent", "https://github.com/me/tool.git");
        var vm = await Opened(repo, Settings(parent: "upstream/tool"));
        vm.ParentSettings = null;

        await vm.LoadForkDivergenceCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.DivergenceReads);
        Assert.False(vm.ForkSyncOfferable);
        Assert.Contains("Couldn't compare this fork", vm.ForkDivergenceText);
    }

    // ── Sync fork ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AForkOnlyBehind_TakesAPlainConfirmationAndNoForce()
    {
        using var repo = await RepoWithOrigin("sync-ff", "https://github.com/me/tool.git");
        var vm = await ForkOpened(repo, new ForkDivergence(Ahead: 0, Behind: 4));
        vm.Confirm = true;

        await vm.SyncForkCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.SyncAttempts);
        Assert.False(vm.SyncedWithForce);
        Assert.Equal(0, vm.Prompts);                        // plain confirmation, nothing typed
        Assert.Contains("4 commits", vm.LastConfirmMessage);
        Assert.Contains("Nothing local is discarded", vm.LastConfirmMessage);
    }

    [Fact]
    public async Task AForkAheadOfItsParent_TakesATypedConfirmationNamingWhatIsDiscarded()
    {
        using var repo = await RepoWithOrigin("sync-diverged", "https://github.com/me/tool.git");
        var vm = await ForkOpened(repo, new ForkDivergence(Ahead: 2, Behind: 5));
        vm.Typed = "me/tool";

        await vm.SyncForkCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.SyncAttempts);
        Assert.True(vm.SyncedWithForce);
        Assert.Equal(0, vm.Confirms);                       // the typed prompt replaces the yes/no one
        Assert.Contains("2 commits ahead", vm.LastPromptMessage);
        Assert.Contains("discards them", vm.LastPromptMessage);
        Assert.Contains("Type me/tool to confirm", vm.LastPromptMessage);
    }

    [Fact]
    public async Task AMistypedSlug_LeavesTheDivergedForkAlone()
    {
        using var repo = await RepoWithOrigin("sync-mistyped", "https://github.com/me/tool.git");
        var vm = await ForkOpened(repo, new ForkDivergence(Ahead: 2, Behind: 5));
        vm.Typed = "tool";

        await vm.SyncForkCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.SyncAttempts);
        Assert.Contains("that isn't me/tool", vm.GitHubStatusText);
    }

    [Fact]
    public async Task AForkWithNothingToCatchUpOn_IsNotSynced()
    {
        using var repo = await RepoWithOrigin("sync-current", "https://github.com/me/tool.git");
        var vm = await ForkOpened(repo, new ForkDivergence(Ahead: 3, Behind: 0));
        vm.Typed = "me/tool";
        vm.Confirm = true;

        await vm.SyncForkCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.SyncAttempts);
        Assert.Equal(0, vm.Prompts);
        Assert.Equal(0, vm.Confirms);
        Assert.Contains("nothing to sync", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SyncFork_RefusedWhileTheRepositoryIsUnderAnotherOperation()
    {
        using var repo = await RepoWithOrigin("sync-leased", "https://github.com/me/tool.git");
        var registry = new RepoBusyRegistry();
        var vm = await ForkOpened(repo, new ForkDivergence(Ahead: 0, Behind: 4), registry);
        vm.Confirm = true;

        Assert.True(registry.TryAcquire(repo.Path, out var lease));
        using (lease) await vm.SyncForkCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.SyncAttempts);
        Assert.Contains("another operation is running", vm.GitHubStatusText);
    }

    [Theory]
    [InlineData("can't sync because there are diverging changes; use `--force` to overwrite the destination branch",
                "commits the parent does not")]
    [InlineData("refusing to sync due to uncommitted/untracked local changes", "Commit or stash them")]
    [InlineData("dial tcp: lookup api.github.com: no such host", "no such host")]
    public void EachSyncRefusal_IsToldApartFromTheOthers(string error, string expected)
        => Assert.Contains(expected, ProjectDetailViewModel.ForkSyncFailureMessage(error));

    [Fact]
    public void ADivergenceThatCouldNotBeMeasured_IsNotReportedAsMatching()
        => Assert.Contains("Couldn't compare",
            ProjectDetailViewModel.DescribeForkDivergence("upstream/tool", "main", null));

    [Fact]
    public void OneCommitEitherWay_IsCountedInTheSingular()
        => Assert.Equal("main is 1 commit behind and 1 commit ahead of upstream/tool",
            ProjectDetailViewModel.DescribeForkDivergence("upstream/tool", "main", new ForkDivergence(1, 1))
                .TrimEnd('.'));

    // ── The ledger ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachRepoAdminOperation_IsRecordedUnderTheRepositoryItActedOn()
    {
        using var repo = await RepoWithOrigin("admin-ledger", "https://github.com/me/tool.git");
        var history = new OperationHistory(TestEnv.NewDir("admin-ledger-store"));
        var vm = await Opened(repo, Settings(), history: history);
        vm.Confirm = true;

        await vm.ToggleRepoArchiveCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal("Archive me/tool", record.Label);
        Assert.Equal(OperationCategory.GitHub, record.Category);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
    }

    [Fact]
    public async Task AFailedRepoAdminOperation_IsRecordedWithTheErrorAndRaisesTheHistoryHint()
    {
        using var repo = await RepoWithOrigin("admin-ledger-fail", "https://github.com/me/tool.git");
        var history = new OperationHistory(TestEnv.NewDir("admin-ledger-fail-store"));
        var vm = await Opened(repo, Settings(), history: history);
        vm.Typed = "me/tool";
        vm.RepoRenameDraft = "renamed";
        vm.RenameResult = Failed("HTTP 422: name already exists on this account");

        await vm.RenameRepoCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal("Rename me/tool to renamed", record.Label);
        Assert.Equal(OperationCategory.GitHub, record.Category);
        Assert.Equal(OperationOutcome.Failed, record.Outcome);
        Assert.Contains("already exists", record.Detail);
        Assert.True(vm.OperationHistoryHintVisible);
    }

    [Fact]
    public async Task ARefusedRepoAdminOperation_IsRecordedRatherThanReturningSilently()
    {
        using var repo = await RepoWithOrigin("admin-ledger-refused", "https://github.com/me/tool.git");
        var history = new OperationHistory(TestEnv.NewDir("admin-ledger-refused-store"));
        var registry = new RepoBusyRegistry();
        var vm = await Opened(repo, Settings(), registry: registry, history: history);
        vm.Typed = "me/tool";
        vm.RepoRenameDraft = "renamed";

        Assert.True(registry.TryAcquire(repo.Path, out var lease));
        using (lease) await vm.RenameRepoCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationOutcome.Refused, record.Outcome);
        Assert.Contains("another operation is running", record.Detail);
    }

    // ── Markup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fork card is gated on what the API said the repository is, never on a parent slug
    /// being non-empty in some other place, and the sync button is gated on a comparison having
    /// answered rather than on the counts it produced.
    /// </summary>
    [Fact]
    public async Task TheForkAndArchiveMarkup_BindToTheHonestFlags()
    {
        var markup = await File.ReadAllTextAsync(PageSource());

        var fork = FromAutomationId(markup, "ForkSyncCard", following: 2400);
        Assert.Contains("Binding RepoIsFork,", fork);
        Assert.Contains("Binding ForkSyncOfferable", fork);
        Assert.Contains("Binding SyncForkCommand", fork);

        var archived = FromAutomationId(markup, "RepoArchivedNotice");
        Assert.Contains("Binding RepoIsArchived,", archived);
        Assert.Contains("read-only", archived);

        // Every editor on the tab is switched off by the same flag the notice is drawn from:
        // description, homepage, topics and their save; the rename box and its button; the three
        // feature checkboxes and their save; the default-branch box and its button; the
        // visibility picker and its button. An editor added without the gate moves this count.
        Assert.Equal(14, CountOccurrences(markup, "IsEnabled=\"{Binding RepoEditsEnabled}\""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>The markup around an element's automation id, for asserting what gates it.</summary>
    private static string FromAutomationId(string markup, string automationId, int following = 900)
    {
        var at = markup.IndexOf($"AutomationId=\"{automationId}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{automationId} is not in the markup");
        var start = Math.Max(0, at - 600);
        return markup[start..(at + Math.Min(following, markup.Length - at))];
    }

    private static string PageSource([System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages",
            "ProjectDetailPage.xaml"));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static async Task<RepoAdminViewModel> Opened(TempRepo repo, RepoSettings settings,
        RepoBusyRegistry? registry = null, OperationHistory? history = null)
    {
        var vm = new RepoAdminViewModel(registry, history) { Settings = settings };
        await vm.SetProjectAsync(ProjectFor(repo, "https://github.com/me/tool.git"));
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        return vm;
    }

    private static async Task<RepoAdminViewModel> ForkOpened(TempRepo repo, ForkDivergence divergence,
        RepoBusyRegistry? registry = null)
    {
        var vm = await Opened(repo, Settings(parent: "upstream/tool"), registry);
        vm.ParentSettings = Settings(name: "tool", defaultBranch: "main");
        vm.Divergence = divergence;
        await vm.LoadForkDivergenceCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>
    /// Answers the dialogs and every remote call without a window or gh, recording what each
    /// surface asked for. Local git runs for real against the fixture: the remote-URL update a
    /// rename offers is the half of that operation this file exists to hold to account.
    /// </summary>
    private sealed class RepoAdminViewModel(RepoBusyRegistry? registry = null, OperationHistory? history = null)
        : ProjectDetailViewModel(null!, new GitService(), null!, null,
            registry ?? new RepoBusyRegistry(), history: history)
    {
        public RepoSettings? Settings { get; init; }

        /// <summary>The parent's settings, which is where the compared branch name comes from.</summary>
        public RepoSettings? ParentSettings { get; set; }

        public ForkDivergence? Divergence { get; set; }

        /// <summary>Null stands for a cancelled typed prompt.</summary>
        public string? Typed { get; set; }
        public bool Confirm { get; set; }

        public ProcessResult RenameResult { get; set; } = new(0, "", "", TimedOut: false);
        public ProcessResult ArchiveResult { get; set; } = new(0, "", "", TimedOut: false);
        public ProcessResult SyncResult { get; set; } = new(0, "", "", TimedOut: false);

        public int Prompts { get; private set; }
        public string LastPromptMessage { get; private set; } = "";
        public int Confirms { get; private set; }
        public string LastConfirmMessage { get; private set; } = "";

        public int RenameAttempts { get; private set; }
        public int ArchiveAttempts { get; private set; }
        public int UnarchiveAttempts { get; private set; }
        public int SyncAttempts { get; private set; }
        public bool SyncedWithForce { get; private set; }
        public int DivergenceReads { get; private set; }
        public string ComparedBranch { get; private set; } = "";
        public string ComparedParentOwner { get; private set; } = "";
        public string ComparedForkOwner { get; private set; } = "";

        internal override Task<RepoSettings?> FetchRepoSettingsAsync(string slug) =>
            Task.FromResult(slug == (Project?.GitHubSlug ?? "") ? Settings : ParentSettings);

        internal override Task<ProcessResult> RenameRepoRemoteAsync(string slug, string newName)
        {
            RenameAttempts++;
            return Task.FromResult(RenameResult);
        }

        internal override Task<ProcessResult> ArchiveRepoRemoteAsync(string slug)
        {
            ArchiveAttempts++;
            return Task.FromResult(ArchiveResult);
        }

        internal override Task<ProcessResult> UnarchiveRepoRemoteAsync(string slug)
        {
            UnarchiveAttempts++;
            return Task.FromResult(ArchiveResult);
        }

        internal override Task<ProcessResult> SyncForkRemoteAsync(string repoPath, bool force)
        {
            SyncAttempts++;
            SyncedWithForce = force;
            return Task.FromResult(SyncResult);
        }

        internal override Task<ForkDivergence?> FetchForkDivergenceAsync(string parentSlug, string parentOwner,
            string forkOwner, string branch)
        {
            DivergenceReads++;
            ComparedParentOwner = parentOwner;
            ComparedForkOwner = forkOwner;
            ComparedBranch = branch;
            return Task.FromResult(Divergence);
        }

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirms++;
            LastConfirmMessage = message;
            return Task.FromResult(Confirm);
        }

        internal override Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
        {
            Prompts++;
            LastPromptMessage = message;
            return Task.FromResult(Typed);
        }

        /// <summary>The Repo tab never loads notifications in these tests; a null read is the honest answer.</summary>
        internal override Task<List<GitHubNotification>?> FetchNotificationsAsync(string slug) =>
            Task.FromResult<List<GitHubNotification>?>([]);
    }
}
