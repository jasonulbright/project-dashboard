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
        public Exception? PreviewThrows { get; set; }
        public RewriteExecutionResult ExecuteResult { get; set; } = new() { Success = true, Report = NewReport() };
        public RestoreResult RestoreResult { get; set; } = new(true, "restored 3 refs");
        public bool CanUndo { get; set; }
        public bool Disposed { get; private set; }
        public int DisposedOnThreadId { get; private set; }

        /// <summary>Held open, this stands in for the engine still reading the scratch bare.</summary>
        public Task? PreviewGate { get; set; }

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreviewEntered => _entered.Task;

        public async Task<RewritePreviewOutcome> PreviewAsync(RewriteRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            PreviewCount++;
            _entered.TrySetResult();
            if (PreviewGate is { } gate) await gate;
            if (PreviewThrows is { } ex) throw ex;
            return PreviewResult;
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

        public void Dispose()
        {
            DisposedOnThreadId = Environment.CurrentManagedThreadId;
            Disposed = true;
        }
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

    // ── No Execute without a dry run ─────────────────────────────────────────

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

        // The surface the scrim covers is disabled, so no keystroke reaches it.
        Assert.True(vm.RewriteWizardVisible);
        Assert.False(vm.RewriteWizardHidden);
        vm.CloseRewriteWizardCommand.Execute(null);
        Assert.True(vm.RewriteWizardHidden);
        vm.OpenRewriteWizardCommand.Execute(null);

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

    /// <summary>
    /// A re-run can be refused with the inputs untouched — the working tree goes dirty, another
    /// operation takes the repository lock. The refusal contradicts the report beside it, so
    /// the report has to go, and with it the armed Execute the held dry run was standing for.
    /// </summary>
    [Fact]
    public async Task RefusedRerun_ClearsTheContradictedReportAndDisarmsExecute()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-refused-rerun");
        using var _ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        Assert.True(vm.ExecuteRewriteCommand.CanExecute(null));

        session.PreviewResult = new RewritePreviewOutcome(null,
            "working tree has 1 uncommitted change(s) — refusing the rewrite (stash or commit first): a.txt");
        await vm.RunRewritePreviewCommand.ExecuteAsync(null);

        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.RewriteShowExecute);
        Assert.False(vm.ExecuteRewriteCommand.CanExecute(null));
        Assert.False(vm.RewriteHasReport);
        Assert.Empty(vm.RewriteFacts);
        Assert.Empty(vm.RewriteScrubLines);
        Assert.Null(vm.RewriteOverallVerdict);
        Assert.True(vm.RewriteStepIsPreview);
        Assert.Contains("Commit or stash", vm.RewriteErrorText);

        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ExecuteCount);
    }

    [Fact]
    public async Task ADryRunThatThrows_AlsoClearsTheReportAndDisarmsExecute()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-threw-rerun");
        using var _ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        Assert.True(vm.ExecuteRewriteCommand.CanExecute(null));

        session.PreviewThrows = new IOException("the scratch directory could not be created");
        await vm.RunRewritePreviewCommand.ExecuteAsync(null);

        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.RewriteShowExecute);
        Assert.False(vm.RewriteHasReport);
        Assert.Null(vm.RewriteOverallVerdict);
        Assert.Contains("scratch directory", vm.RewriteErrorText);

        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ExecuteCount);
    }

    // ── Typed confirmation ───────────────────────────────────────────────────

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
        // Closing the wizard drops the one-click undo, so the promise names its lifetime.
        Assert.Contains("goes away when you close this wizard", vm.RewriteConfirmMessage);
        Assert.Contains("backup itself is kept", vm.RewriteConfirmMessage);
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

    /// <summary>
    /// The restore puts the pre-rewrite refs back, so whatever the rewrite removed is present
    /// again. The executed run's verification block describes the history the restore just
    /// threw away, and the in-progress status label is not an outcome.
    /// </summary>
    [Fact]
    public async Task SuccessfulUndo_ClearsTheVerificationBlockAndReportsACompletedRestore()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-undo-report");
        using var __ = repo;
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteHasReport);
        Assert.True(vm.RewriteOverallVerdict!.ClaimsClean);

        await vm.UndoRewriteCommand.ExecuteAsync(null);

        Assert.False(vm.RewriteHasReport);
        Assert.Empty(vm.RewriteFacts);
        Assert.Empty(vm.RewriteScrubLines);
        Assert.Empty(vm.RewriteSkipLines);
        Assert.Null(vm.RewriteOverallVerdict);
        // Not the in-progress label the step set on entry.
        Assert.DoesNotContain("Undo…", vm.RewriteStatusText);
        Assert.Contains("restored", vm.RewriteStatusText);
    }

    /// <summary>
    /// The restore's ref transaction commits before its working-tree reset, so a reset failure
    /// returns unsuccessful with the pre-rewrite refs already back. The removed content is in
    /// the repository again, and the failure branch cannot tell that case from "nothing moved",
    /// so the executed run's verification block may not survive an attempted undo either way.
    /// </summary>
    [Fact]
    public async Task UndoThatRestoredTheRefsButFailedTheReset_DropsTheCleanBillAndSaysWhatMoved()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-undo-halfway");
        using var _ = repo;
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        session.RestoreResult = new RestoreResult(
            false, "Refs restored but working-tree reset failed: a.txt is locked",
            WorktreeWasDirty: false, DiscardedChangeCount: 0, RefsRestored: true);

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteOverallVerdict!.ClaimsClean);

        await vm.UndoRewriteCommand.ExecuteAsync(null);

        Assert.False(vm.RewriteHasReport);
        Assert.Empty(vm.RewriteScrubLines);
        Assert.Null(vm.RewriteOverallVerdict);
        // Neither line may say the rewrite's history is still in place.
        Assert.DoesNotContain("left as the rewrite made it", vm.RewriteStatusText);
        Assert.DoesNotContain("was not changed", vm.RewriteUndoText);
        Assert.Contains("refs", vm.RewriteUndoText, StringComparison.OrdinalIgnoreCase);
        // A half-done restore is not a success banner.
        Assert.False(vm.RewriteResultSucceeded);
    }

    /// <summary>A restore that reached nothing must still say plainly that nothing moved.</summary>
    [Fact]
    public async Task UndoThatChangedNothing_StillClearsTheCleanBillAndSaysNothingMoved()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-undo-nothing");
        using var _ = repo;
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        session.RestoreResult = new RestoreResult(false, "Bundle missing: C:\\backups\\x.bundle");

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);

        await vm.UndoRewriteCommand.ExecuteAsync(null);

        // An attempted undo always retires the report: the failure branch cannot prove the
        // verification still describes what is on disk.
        Assert.False(vm.RewriteHasReport);
        Assert.Contains("was not changed", vm.RewriteUndoText);
        Assert.Contains("left as the rewrite made it", vm.RewriteStatusText);
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
    public async Task Undo_WhileTheRepositoryIsBusy_SaysSoInsteadOfDoingNothing()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-undo-busy");
        using var _ = repo;
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteUndoAvailable);

        vm.IsBusy = true;
        await vm.UndoRewriteCommand.ExecuteAsync(null);

        Assert.Equal(0, session.UndoCount);
        Assert.Contains("Another operation is running", vm.RewriteStatusText);
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

        // A failure past the ref transaction is not an untouched repository.
        var halfway = ProjectDetailViewModel.DescribeRestore(
            new RestoreResult(false, "reset failed", RefsRestored: true));
        Assert.DoesNotContain("was not changed", halfway);
        Assert.Contains("pre-rewrite refs are back", halfway);
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
        // Off the calling thread: the disposal walks and deletes the scratch tree.
        await vm.RewriteSessionDisposal;
        Assert.True(session.Disposed);
    }

    /// <summary>
    /// Disposing a session deletes its scratch bare — an enumeration of every file, an attribute
    /// write per file, a recursive delete, and a sleeping retry when a handle is still held. On
    /// the dispatcher that is a frozen window, and the common case is closing the wizard after a
    /// dry run of a large repository.
    /// </summary>
    [Fact]
    public async Task ClosingTheWizard_DisposesTheSessionOffTheCallingThread()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-close-thread");
        using var _ = repo;
        await AdvanceToPreviewAsync(vm);

        var caller = Environment.CurrentManagedThreadId;
        vm.CloseRewriteWizardCommand.Execute(null);
        await vm.RewriteSessionDisposal;

        Assert.True(session.Disposed);
        Assert.NotEqual(caller, session.DisposedOnThreadId);
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

    /// <summary>
    /// Disposing a session deletes its scratch bare, which the swap reads across several git
    /// invocations. A switch that disposed it under a run either failed the fetch with nobody
    /// told, or applied the swap with nobody told and the one-click undo handle already gone.
    /// </summary>
    [Fact]
    public async Task SwitchingProjectMidRun_DisposesTheSessionOnlyAfterTheStepReturns()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-inflight-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-inflight-b");
        using var __ = repoB;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { PreviewGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";

        await vm.RewriteNextCommand.ExecuteAsync(null);
        var pending = vm.RewriteNextCommand.ExecuteAsync(null);
        await session.PreviewEntered;
        Assert.True(vm.RewriteRunning);

        await vm.SetProjectAsync(ProjectFor(repoB));

        Assert.False(session.Disposed);

        gate.SetResult();
        await pending;
        await vm.RewriteSessionDisposal;

        Assert.True(session.Disposed);
    }
}
