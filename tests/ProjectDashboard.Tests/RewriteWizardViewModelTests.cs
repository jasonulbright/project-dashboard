using System.Text;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The wizard's gates, driven against a fixture repo with a stub engine session. No real
/// rewrite runs here — the engine has its own coverage; what is asserted is that the surface
/// cannot execute without a dry run, cannot execute without the typed phrase, and reports a
/// refusal and an undo honestly.
/// </summary>
public class RewriteWizardViewModelTests
{
    // ── Stub engine ──────────────────────────────────────────────────────────

    private sealed class StubSession : IRewriteSession
    {
        public RewriteRequest? LastRequest { get; private set; }
        public int PreviewCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int UndoCount { get; private set; }

        public RewritePreviewOutcome PreviewResult { get; set; } = new(NewReport(), null);
        public RewriteExecutionResult ExecuteResult { get; set; } = new() { Success = true, Report = NewReport() };
        public RestoreResult RestoreResult { get; set; } = new(true, "restored 3 refs");
        public bool CanUndo { get; set; }
        public bool Disposed { get; private set; }

        public Task<RewritePreviewOutcome> PreviewAsync(RewriteRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            PreviewCount++;
            return Task.FromResult(PreviewResult);
        }

        public Task<RewriteExecutionResult> ExecuteAsync(CancellationToken ct = default)
        {
            ExecuteCount++;
            CanUndo = true;
            return Task.FromResult(ExecuteResult);
        }

        public Task<RestoreResult> UndoAsync(CancellationToken ct = default)
        {
            UndoCount++;
            return Task.FromResult(RestoreResult);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class StubFactory(StubSession session) : IRewriteSessionFactory
    {
        public IRewriteSession Create() => session;
    }

    private static RewriteReport NewReport(params ScrubCheckResult[] checks) => new()
    {
        SourceRepository = @"C:\repo",
        TargetBareRepository = @"C:\temp\t.git",
        CommitCount = 4,
        BlobsChanged = 2,
        BytesDelta = -10,
        BinarySkips = [],
        CommitMap = new Dictionary<string, string> { ["a"] = "b", ["c"] = "d" },
        CommitsWithChangedTrees = ["a"],
        FsckOutput = "",
        ScrubChecks = checks.Length > 0
            ? checks
            : [new ScrubCheckResult
              {
                  Kind = "literal", Needle = "SECRET", Performed = true, Complete = true,
                  CommitsChecked = 4, Hits = [],
              }],
    };

    // ── Harness ──────────────────────────────────────────────────────────────

    private static ProjectDetailViewModel NewVm(StubSession session) =>
        new(null!, new GitService(), null!, new StubFactory(session));

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static async Task<(TempRepo Repo, ProjectDetailViewModel Vm, StubSession Session)> OpenWizardAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        var session = new StubSession();
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        return (repo, vm, session);
    }

    /// <summary>Operation → Scope → Preview (which runs the dry run).</summary>
    private static async Task AdvanceToPreviewAsync(ProjectDetailViewModel vm)
    {
        await vm.RewriteNextCommand.ExecuteAsync(null);
        await vm.RewriteNextCommand.ExecuteAsync(null);
    }

    // ── R-02: no Execute without a dry run ───────────────────────────────────

    [Fact]
    public async Task ExecuteAffordance_DoesNotExistUntilADryRunHasProducedAReport()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-gate");
        using var _ = repo;

        Assert.True(vm.RewriteWizardVisible);
        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.RewriteShowExecute);
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));

        // Executing anyway must not reach the engine.
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ExecuteCount);

        await AdvanceToPreviewAsync(vm);
        Assert.Equal(1, session.PreviewCount);
        Assert.True(vm.RewritePreviewAvailable);
        // Still on the dry-run step: the Execute control appears only on the confirm step.
        Assert.False(vm.RewriteShowExecute);

        await vm.RewriteNextCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteStepIsConfirm);
        Assert.True(vm.RewriteShowExecute);
    }

    [Fact]
    public async Task OpeningTheWizard_StartsOnStepOneWithItsOwnTitleAndAffordances()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-open");
        using var __ = repo;

        Assert.True(vm.RewriteStepIsOperation);
        Assert.Contains("Step 1 of 4", vm.RewriteStepTitle);
        Assert.True(vm.RewriteShowNext);
        Assert.False(vm.RewriteShowBack);
        Assert.False(vm.RewriteShowExecute);
    }

    [Fact]
    public async Task PreviewStep_CannotBeSkipped_NextFromThePreviewStepRefusesWithoutAReport()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-skip");
        using var _ = repo;
        session.PreviewResult = new RewritePreviewOutcome(null, "engine refused");

        await AdvanceToPreviewAsync(vm);
        Assert.False(vm.RewritePreviewAvailable);

        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.True(vm.RewriteStepIsPreview);
        Assert.False(vm.RewriteShowExecute);
        Assert.Contains("Run the dry run", vm.RewriteErrorText);
    }

    [Fact]
    public async Task EditingAnInputAfterTheDryRun_DisarmsExecuteAndReturnsToThePreviewStep()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-stale");
        using var __ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        Assert.True(vm.ExecuteRewriteCommand.CanExecute(null));

        // The report on screen no longer describes what would run.
        vm.RewriteFindText = "SOMETHING-ELSE";

        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.RewriteShowExecute);
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));
        Assert.True(vm.RewriteStepIsPreview);
        Assert.Empty(vm.RewriteFacts);
    }

    // ── R-05: typed confirmation ─────────────────────────────────────────────

    [Fact]
    public async Task TypedConfirm_RejectsAWrongNameAndAcceptsTheRepositoryName()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-confirm");
        using var _ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.Equal(System.IO.Path.GetFileName(repo.Path), vm.RewriteConfirmPhrase);
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));

        vm.RewriteConfirmInput = "some-other-repo";
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ExecuteCount);

        // Case must match exactly — a near miss is not a confirmation.
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase.ToUpperInvariant() + "X";
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));

        vm.RewriteConfirmInput = "  " + vm.RewriteConfirmPhrase + "  ";
        Assert.True(vm.ExecuteRewriteCommand.CanExecute(null));

        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.Equal(1, session.ExecuteCount);
        Assert.True(vm.RewriteStepIsResult);
        Assert.True(vm.RewriteResultSucceeded);
    }

    [Fact]
    public async Task ConfirmMessage_StatesTheDivergenceAndThatThisAppNeverPushes()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-message");
        using var __ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.Contains("stops matching the remote", vm.RewriteConfirmMessage);
        Assert.Contains("force-push it yourself", vm.RewriteConfirmMessage);
        Assert.Contains("never pushes", vm.RewriteConfirmMessage);
        Assert.Contains("Undo restores", vm.RewriteConfirmMessage);
        Assert.Contains($"Type {vm.RewriteConfirmPhrase} below", vm.RewriteConfirmMessage);
    }

    // ── Pre-flight refusals ──────────────────────────────────────────────────

    [Fact]
    public async Task PreflightRefusal_RendersAsActionableTextAndLeavesExecuteUnavailable()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-refusal");
        using var _ = repo;
        session.PreviewResult = new RewritePreviewOutcome(null,
            "preflight: nested tags are unsupported — refs/tags/outer point(s) at another tag object");

        await AdvanceToPreviewAsync(vm);

        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.RewriteShowExecute);
        Assert.Contains("cannot round-trip", vm.RewriteErrorText);
        Assert.Contains("refs/tags/outer", vm.RewriteErrorText);
        Assert.Contains("Dry run refused", vm.RewriteStatusText);
    }

    [Fact]
    public async Task DirtyTreeRefusalFromExecute_KeepsTheFileListAndReportsFailure()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-dirty");
        using var _ = repo;
        session.ExecuteResult = new RewriteExecutionResult
        {
            Success = false,
            FailureReason = "working tree has 2 uncommitted change(s) — refusing the rewrite (stash or commit first): a.txt, b.txt",
        };

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);

        Assert.True(vm.RewriteStepIsResult);
        Assert.False(vm.RewriteResultSucceeded);
        Assert.Contains("Commit or stash", vm.RewriteErrorText);
        Assert.Contains("a.txt, b.txt", vm.RewriteErrorText);
    }

    /// <summary>
    /// A post-backup failure hands back the PREVIEW's report, which describes history that was
    /// never applied. Left on screen it sits under the failure banner reading as a description
    /// of the repository — while the content the rewrite was asked to remove is still there.
    /// </summary>
    [Fact]
    public async Task FailedExecute_LeavesNoVerificationBlockDescribingTheRepository()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-failed-report");
        using var _ = repo;
        session.ExecuteResult = new RewriteExecutionResult
        {
            Success = false,
            FailureReason = "swap failed: fetch into the source repository failed",
            Report = NewReport(), // the dry run's report, echoed back by the coordinator
        };

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteHasReport);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);

        Assert.True(vm.RewriteStepIsResult);
        Assert.False(vm.RewriteResultSucceeded);
        Assert.False(vm.RewriteHasReport);
        Assert.Empty(vm.RewriteFacts);
        Assert.Empty(vm.RewriteScrubLines);
        Assert.Empty(vm.RewriteSkipLines);
        Assert.Null(vm.RewriteOverallVerdict);
        Assert.Contains("did not complete", vm.RewriteStatusText);
        Assert.Contains("fetch into the source repository failed", vm.RewriteErrorText);
    }

    // ── Undo ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Undo_ConfirmsFirstAndSurfacesTheDiscardedChangeCount()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-undo");
        using var _ = repo;
        session.RestoreResult = new RestoreResult(true, "restored 3 refs", WorktreeWasDirty: true, DiscardedChangeCount: 4);

        var prompts = 0;
        vm.ConfirmPrompt = (_, message, _) =>
        {
            prompts++;
            // The prompt must name the reset and the work it throws away before it is answered.
            Assert.Contains("reset --hard", message);
            Assert.Contains("discarded", message);
            return Task.FromResult(true);
        };

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteUndoAvailable);

        await vm.UndoRewriteCommand.ExecuteAsync(null);

        Assert.Equal(1, prompts);
        Assert.Equal(1, session.UndoCount);
        Assert.Contains("4 uncommitted change(s) were discarded", vm.RewriteUndoText);
        Assert.False(vm.RewriteUndoAvailable);
    }

    [Fact]
    public async Task Undo_DeclinedAtTheConfirm_DoesNotRestore()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-undo-no");
        using var _ = repo;
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(false);

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);

        await vm.UndoRewriteCommand.ExecuteAsync(null);

        Assert.Equal(0, session.UndoCount);
        Assert.True(vm.RewriteUndoAvailable);
        Assert.Equal("", vm.RewriteUndoText);
    }

    [Fact]
    public void DescribeRestore_StatesACleanTreeRatherThanStayingSilent()
    {
        Assert.Contains("no uncommitted work was discarded",
            ProjectDetailViewModel.DescribeRestore(new RestoreResult(true, "ok")));
        Assert.Contains("2 uncommitted change(s) were discarded",
            ProjectDetailViewModel.DescribeRestore(new RestoreResult(true, "ok", true, 2)));
        Assert.Contains("was not changed",
            ProjectDetailViewModel.DescribeRestore(new RestoreResult(false, "bundle missing")));
    }

    // ── Result honesty ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResultScreen_WithAnIncompleteScrub_NeverReadsAsClean()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-honesty");
        using var _ = repo;
        var incomplete = new ScrubCheckResult
        {
            Kind = "literal", Needle = "SECRET", Performed = true, Complete = false,
            CommitsChecked = 4, Hits = [], Note = "1 blob skipped as binary",
        };
        session.PreviewResult = new RewritePreviewOutcome(NewReport(incomplete), null);
        session.ExecuteResult = new RewriteExecutionResult { Success = true, Report = NewReport(incomplete) };

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);

        Assert.True(vm.RewriteResultSucceeded); // the swap applied
        var verdict = Assert.Single(vm.RewriteScrubLines);
        Assert.False(verdict.ClaimsClean);
        Assert.Equal(ScrubVerdict.NotVerified, verdict.Verdict);
        Assert.NotNull(vm.RewriteOverallVerdict);
        Assert.False(vm.RewriteOverallVerdict!.ClaimsClean);
    }

    // ── Request construction ─────────────────────────────────────────────────

    [Fact]
    public async Task LiteralReplaceWithExplicitPaths_BuildsTheMatchingEngineRequest()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-request");
        using var _ = repo;
        vm.RewriteReplacementText = "[REDACTED]";
        vm.RewriteScopeIsAllHistory = false;
        vm.RewriteScopeIsExplicitPaths = true;
        vm.RewriteScopePathsText = "docs/keys.md\nsrc/config";

        await AdvanceToPreviewAsync(vm);

        var options = session.LastRequest!.Options;
        Assert.Equal(repo.Path, session.LastRequest.RepoPath);
        var op = Assert.IsType<LiteralReplace>(Assert.Single(options.ContentOps));
        Assert.Equal("SECRET", Encoding.UTF8.GetString(op.Find));
        Assert.Equal("[REDACTED]", Encoding.UTF8.GetString(op.Replace));
        var scope = Assert.IsType<ExplicitPathsScope>(options.FileScope);
        Assert.Equal(["docs/keys.md", "src/config"], scope.Paths);
        Assert.True(options.CommitScope.IsAllHistory);
    }

    [Fact]
    public async Task PurgeWithoutAWildcard_ScopesByPathSubtree_AndWithOneSwitchesToGlobs()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-purge");
        using var _ = repo;
        vm.RewriteOperationIsReplaceText = false;
        vm.RewriteOperationIsPurgePath = true;
        vm.RewritePurgePathsText = "assets/blobs";

        await AdvanceToPreviewAsync(vm);
        Assert.IsType<ExplicitPathsScope>(session.LastRequest!.Options.Purge!.Paths);

        vm.RewritePurgePathsText = "assets/**/*.bin";
        await vm.RunRewritePreviewCommand.ExecuteAsync(null);
        Assert.IsType<GlobScope>(session.LastRequest!.Options.Purge!.Paths);
    }

    [Fact]
    public async Task ExplicitCommitScope_CollectsPickedCommitsAndTypedRefs()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-commits");
        using var _ = repo;
        vm.RewriteScopeIsAllHistory = false;
        vm.RewriteScopeIsExplicitCommits = true;
        vm.RewriteScopePickedCommit = new GitCommit { ShortHash = "abc1234", Message = "initial commit" };
        vm.AddRewriteScopeCommitCommand.Execute(null);
        vm.RewriteScopeCommitDraft = "v1.0";
        vm.AddRewriteScopeCommitCommand.Execute(null);

        Assert.Equal(2, vm.RewriteScopeCommits.Count);

        await AdvanceToPreviewAsync(vm);

        var scope = Assert.IsType<ExplicitCommitsScope>(session.LastRequest!.Options.CommitScope);
        Assert.Equal(2, scope.Commits.Count);
        Assert.Contains("v1.0", scope.Commits);
        Assert.Contains("the 2 selected commit(s)", vm.DescribeScope());
    }

    [Fact]
    public async Task MessageOperation_SaysAFileScopeDoesNotNarrowIt()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-scope-note");
        using var __ = repo;
        vm.RewriteOperationIsReplaceText = false;
        vm.RewriteOperationIsMessages = true;
        vm.RewriteMessageFindText = "ticket-42";
        vm.RewriteScopeIsAllHistory = false;
        vm.RewriteScopeIsGlobs = true;
        vm.RewriteScopeGlobsText = "src/**";

        Assert.Contains("do not restrict message or identity rewrites", vm.DescribeScope());
    }

    // ── Input validation and cancellation ────────────────────────────────────

    [Fact]
    public async Task EmptyFindText_IsRefusedBeforeTheEngineIsTouched()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-empty");
        using var _ = repo;
        var session = new StubSession();
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);

        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.True(vm.RewriteStepIsOperation);
        Assert.Equal("Enter the text to find.", vm.RewriteErrorText);
        Assert.Equal(0, session.PreviewCount);
    }

    [Fact]
    public async Task InvalidRegex_IsRefusedByTheOptionsValidationNotMidRun()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-badregex");
        using var _ = repo;
        vm.RewriteUseRegex = true;
        vm.RewriteFindText = "([unclosed";

        await AdvanceToPreviewAsync(vm);

        Assert.Equal(0, session.PreviewCount);
        Assert.False(vm.RewritePreviewAvailable);
        Assert.NotEqual("", vm.RewriteErrorText);
    }

    [Fact]
    public async Task ClosingTheWizard_DropsTheHeldDryRunAndEveryTypedField()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-close");
        using var _ = repo;

        await AdvanceToPreviewAsync(vm);
        Assert.True(vm.RewritePreviewAvailable);

        vm.CloseRewriteWizardCommand.Execute(null);

        Assert.False(vm.RewriteWizardVisible);
        Assert.False(vm.RewritePreviewAvailable);
        Assert.Equal("", vm.RewriteFindText);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task SwitchingProject_ClearsTheWizardSoItCannotExecuteAgainstTheNewRepository()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-switch-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-switch-b");
        using var __ = repoB;

        var session = new StubSession();
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        Assert.True(vm.ExecuteRewriteCommand.CanExecute(null));

        await vm.SetProjectAsync(ProjectFor(repoB));

        Assert.False(vm.RewriteWizardVisible);
        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));
        Assert.Equal("", vm.RewriteConfirmInput);
    }
}
