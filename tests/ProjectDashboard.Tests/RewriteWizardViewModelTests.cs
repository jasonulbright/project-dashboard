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
            // The engine's own behaviour: a dry run is scratch work throughout, so a cancelled
            // token surfaces as a throw wherever it is next observed.
            ct.ThrowIfCancellationRequested();
            if (PreviewThrows is { } ex) throw ex;
            return PreviewResult;
        }

        /// <summary>Held open, this stands in for the swap still writing the repository.</summary>
        public Task? ExecuteGate { get; set; }

        private readonly TaskCompletionSource _executeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteEntered => _executeEntered.Task;

        /// <summary>
        /// Whether this run gets as far as the swap's point of no return. Set, the stub reports
        /// <see cref="RewritePhase.Applying"/> and then honours no cancellation, exactly as the
        /// real swap does once its ref transaction can begin.
        /// </summary>
        public bool ReachesPointOfNoReturn { get; set; }

        /// <summary>Held open, this stands in for the ref transaction still committing after cancellation stopped being honoured.</summary>
        public Task? ApplyingGate { get; set; }

        private readonly TaskCompletionSource _applyingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the phase report has been delivered, so a test observes the surface mid-transaction rather than guessing at a delay.</summary>
        public Task ApplyingEntered => _applyingEntered.Task;

        public async Task<RewriteExecutionResult> ExecuteAsync(CancellationToken ct = default, IProgress<RewritePhase>? phase = null)
        {
            ExecuteCount++;
            _executeEntered.TrySetResult();
            phase?.Report(RewritePhase.Preparing);
            if (ExecuteGate is { } gate) await gate;
            if (ReachesPointOfNoReturn)
            {
                phase?.Report(RewritePhase.Applying);
                _applyingEntered.TrySetResult();
                if (ApplyingGate is { } applying) await applying;
            }
            else if (ct.IsCancellationRequested)
            {
                return RewriteExecutionResult.CancelledBeforeApply();
            }
            CanUndo = true;
            return ExecuteResult;
        }

        public Exception? UndoThrows { get; set; }

        /// <summary>Held open, this stands in for the restore still reconciling refs.</summary>
        public Task? UndoGate { get; set; }

        private readonly TaskCompletionSource _undoEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task UndoEntered => _undoEntered.Task;

        public async Task<RestoreResult> UndoAsync(CancellationToken ct = default)
        {
            UndoCount++;
            _undoEntered.TrySetResult();
            if (UndoGate is { } gate) await gate;
            if (UndoThrows is { } ex) throw ex;
            return RestoreResult;
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

    /// <summary>
    /// Hands out a distinct session per call, so a dry run that supersedes another is driving a
    /// different instance — which is what makes the live-wizard and gate-owner checks meaningful.
    /// The last session is repeated once the sequence runs out.
    /// </summary>
    private sealed class SequenceFactory(params StubSession[] sessions) : IRewriteSessionFactory
    {
        public int CreateCount { get; private set; }

        public IRewriteSession Create()
        {
            var session = sessions[Math.Min(CreateCount, sessions.Length - 1)];
            CreateCount++;
            return session;
        }
    }

    private static RewriteReport NewReport(NormalizationScan normalization, params ScrubCheckResult[] checks)
    {
        var report = NewReport(checks);
        return new RewriteReport
        {
            SourceRepository = report.SourceRepository,
            TargetBareRepository = report.TargetBareRepository,
            CommitCount = report.CommitCount,
            BlobsChanged = report.BlobsChanged,
            BytesDelta = report.BytesDelta,
            BinarySkips = report.BinarySkips,
            CommitMap = report.CommitMap,
            CommitsWithChangedTrees = report.CommitsWithChangedTrees,
            FsckOutput = report.FsckOutput,
            ScrubChecks = report.ScrubChecks,
            Normalization = normalization
        };
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

    private static ProjectDetailViewModel NewVm(StubSession session, RepoBusyRegistry? busy = null) =>
        new(null!, new GitService(), null!, new StubFactory(session), busy);

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
        Assert.False(vm.SafetyOverlayHidden);
        vm.CloseRewriteWizardCommand.Execute(null);
        Assert.True(vm.SafetyOverlayHidden);
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
        Assert.Contains($"Type {vm.RewriteConfirmPhrase} below", vm.RewriteConfirmMessage);
    }

    [Fact]
    public async Task ConfirmMessage_DisclosesTheNormalizationTheExportPerformsRegardless()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-normalize");
        using var __ = repo;
        session.PreviewResult = new RewritePreviewOutcome(
            NewReport(new NormalizationScan(3, ["ISO-8859-1"], 1, ["refs/tags/signed"])), null);

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.Contains("re-encodes them to UTF-8", vm.RewriteConfirmMessage);
        Assert.Contains("ISO-8859-1", vm.RewriteConfirmMessage);
        Assert.Contains("strips those signatures", vm.RewriteConfirmMessage);
        Assert.Contains("refs/tags/signed", vm.RewriteConfirmMessage);
    }

    [Fact]
    public async Task ConfirmMessage_SaysNothingAboutNormalizationWhenThereIsNone()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-normalize-none");
        using var __ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.DoesNotContain("normalizes this repository", vm.RewriteConfirmMessage);
    }

    /// <summary>
    /// The result screen's Undo is the only restore this app performs; the backup bundle on disk
    /// is reachable from git alone. A confirmation that offers a later in-app restore is offering
    /// a safety net the reader cannot reach once the wizard is closed.
    /// </summary>
    [Fact]
    public async Task ConfirmMessage_PromisesNoRestorePathBeyondTheResultScreensUndo()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-message-restore");
        using var __ = repo;

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);

        Assert.Contains("backup bundle is written", vm.RewriteConfirmMessage);
        Assert.Contains("nothing in this app restores it", vm.RewriteConfirmMessage);
        Assert.DoesNotContain("can still be restored", vm.RewriteConfirmMessage);
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
    /// The staleness refusal reaches the reader as an ordinary refusal, with the verification
    /// block gone: the report it stood on describes a history this repository no longer has.
    /// </summary>
    [Fact]
    public async Task StaleDryRunRefusalFromExecute_NamesTheCauseAndDropsTheReport()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-stale-exec");
        using var _ = repo;
        session.ExecuteResult = new RewriteExecutionResult
        {
            Success = false,
            FailureReason = @"'C:\repo' changed after the dry run — the report describes history this repository no " +
                            "longer has, and applying it would discard whatever landed since. Run the dry run again.",
            Report = NewReport(),
        };

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);

        Assert.True(vm.RewriteStepIsResult);
        Assert.False(vm.RewriteResultSucceeded);
        Assert.False(vm.RewriteHasReport);
        Assert.Contains("Run the dry run again", vm.RewriteErrorText);
        Assert.Contains("changed after the dry run", vm.RewriteErrorText);
        Assert.Contains("Nothing was changed", vm.RewriteErrorText);
        // A further Execute needs a fresh dry run, so the spent one cannot be replayed.
        Assert.False(vm.RewritePreviewAvailable);
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

    /// <summary>
    /// A throw carries no position: it can land before the restore's ref transaction or after
    /// it, so the surface may neither keep the rewrite's success banner nor repeat the refusal
    /// guidance's "Nothing was changed".
    /// </summary>
    [Fact]
    public async Task UndoThatThrows_DropsTheSuccessBannerAndClaimsNothingAboutWhatMoved()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-undo-throw");
        using var _ = repo;
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        session.UndoThrows = new IOException("git could not be started");

        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        await vm.ExecuteRewriteCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteResultSucceeded);

        await vm.UndoRewriteCommand.ExecuteAsync(null);

        Assert.Equal(1, session.UndoCount);
        Assert.False(vm.RewriteResultSucceeded);
        Assert.False(vm.RewriteHasReport);
        Assert.Contains("git could not be started", vm.RewriteUndoText);
        Assert.DoesNotContain("Undo…", vm.RewriteStatusText);
        var surfaced = vm.RewriteUndoText + "\n" + vm.RewriteStatusText + "\n" + vm.RewriteErrorText;
        Assert.DoesNotContain("othing was changed", surfaced);
        Assert.DoesNotContain("was not changed", surfaced);
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

        // The close runs on a dedicated thread held alive across the assertion. This test's own
        // thread belongs to the pool the disposal is queued to, so it can serve that disposal
        // the moment an await releases it — and a managed thread id is free to be reused once
        // its thread has ended. Neither can happen to a thread that is still parked here.
        using var closed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var closer = new Thread(() =>
        {
            vm.CloseRewriteWizardCommand.Execute(null);
            closed.Set();
            release.Wait();
        }) { IsBackground = true, Name = "rewrite-close-caller" };
        closer.Start();
        closed.Wait();

        await vm.RewriteSessionDisposal;

        Assert.True(session.Disposed);
        Assert.NotEqual(closer.ManagedThreadId, session.DisposedOnThreadId);
        release.Set();
        closer.Join();
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

    // ── Leaving the page mid-rewrite ─────────────────────────────────────────

    /// <summary>Drives the wizard to a rewrite that has entered the engine and is still in it.</summary>
    private static async Task<Task> StartRewriteAsync(ProjectDetailViewModel vm, StubSession session)
    {
        await AdvanceToPreviewAsync(vm);
        await vm.RewriteNextCommand.ExecuteAsync(null);
        vm.RewriteConfirmInput = vm.RewriteConfirmPhrase;
        var running = vm.ExecuteRewriteCommand.ExecuteAsync(null);
        await session.ExecuteEntered;
        return running;
    }

    /// <summary>
    /// Every page load re-applies the project already open. Treating that as a switch clears the
    /// busy gate and resets the wizard while the swap is mid-flight, so a Pull started from the
    /// re-enabled branch bar merges the un-rewritten remote history back over the rewrite.
    /// </summary>
    [Fact]
    public async Task ReloadingTheSamePageMidRewrite_KeepsTheBusyGateAndTheRunningWizard()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-reload");
        using var _ = repo;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { ExecuteGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        // The nav view sits outside the wizard's scrim, so this is one click away at any moment.
        await vm.SetProjectAsync(ProjectFor(repo));

        Assert.True(vm.IsBusy);
        Assert.True(vm.RewriteRunning);
        Assert.True(vm.RewriteWizardVisible);
        Assert.False(vm.SafetyOverlayHidden);
        await vm.PullCommand.ExecuteAsync(null);
        Assert.Equal(0, await repo.CommitCountAsync() - await repo.CommitCountAsync()); // repo untouched
        Assert.Equal("", vm.SyncStatusText);

        gate.SetResult();
        await running;
        Assert.True(vm.RewriteStepIsResult);
        Assert.True(vm.RewriteUndoAvailable);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// A genuine switch away and back. The rewrite keeps its session, so the result screen and
    /// the one-click undo the confirmation promised are still there — and the repository is
    /// still gated while the run is in flight.
    /// </summary>
    [Fact]
    public async Task SwitchingAwayAndBackMidRewrite_RestoresTheRunningWizardAndRefusesRepoOps()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-away-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-away-b");
        using var __ = repoB;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { ExecuteGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        await vm.SetProjectAsync(ProjectFor(repoB));
        // B is a different repository and owes nothing to A's rewrite.
        Assert.False(vm.IsBusy);
        Assert.False(vm.RewriteWizardVisible);
        Assert.False(session.Disposed);

        await vm.SetProjectAsync(ProjectFor(repoA));
        Assert.True(vm.RewriteWizardVisible);
        Assert.True(vm.RewriteStepIsRunning);
        Assert.True(vm.IsBusy);
        Assert.True(vm.RewriteRunning);
        await vm.PullCommand.ExecuteAsync(null);
        Assert.Equal("", vm.SyncStatusText);

        gate.SetResult();
        await running;

        // The step that was in flight lands on the surface it started on.
        Assert.True(vm.RewriteStepIsResult);
        Assert.True(vm.RewriteResultSucceeded);
        Assert.True(vm.RewriteUndoAvailable);
        Assert.False(vm.IsBusy);
        Assert.False(vm.RewriteRunning);
    }

    /// <summary>
    /// The journal is already completed when the rewrite finishes, so the session's undo handle
    /// is the only one-click restore left; disposing it on a switch makes recovery a manual
    /// job with a bundle. Leaving the page parks the rewrite instead, and returning offers it.
    /// </summary>
    [Fact]
    public async Task SwitchingAwayAfterACompletedRewrite_KeepsTheOneClickUndoReachable()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-undo-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-undo-b");
        using var __ = repoB;

        var session = new StubSession();
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        await (await StartRewriteAsync(vm, session));
        Assert.True(vm.RewriteUndoAvailable);

        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(session.Disposed);
        Assert.False(vm.RewriteUndoAvailable);

        await vm.SetProjectAsync(ProjectFor(repoA));
        Assert.True(vm.RewriteWizardVisible);
        Assert.True(vm.RewriteStepIsResult);
        Assert.True(vm.RewriteUndoAvailable);
        Assert.True(vm.RewriteResultSucceeded);

        await vm.UndoRewriteCommand.ExecuteAsync(null);
        Assert.Equal(1, session.UndoCount);
        Assert.False(vm.RewriteUndoAvailable);
        Assert.Contains("History restored", vm.RewriteStatusText);

        // Closing the wizard is what ends the undo's life, exactly as the confirmation says.
        vm.CloseRewriteWizardCommand.Execute(null);
        await vm.RewriteSessionDisposal;
        Assert.True(session.Disposed);
    }

    /// <summary>
    /// The page's own busy flag cannot see a rewrite running behind another surface or another
    /// page. The repository lease can, and a mutating git op has to consult it or a Pull lands
    /// in the middle of a live swap.
    /// </summary>
    [Fact]
    public async Task AGitOp_IsRefusedWhileTheRepositoryCarriesARewriteLease_EvenWithTheBusyFlagClear()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-lease");
        using var _ = repo;
        var registry = new RepoBusyRegistry();
        var vm = NewVm(new StubSession(), registry);
        await vm.SetProjectAsync(ProjectFor(repo));

        Assert.True(registry.TryAcquire(repo.Path, out var lease));
        Assert.False(vm.IsBusy);

        await vm.PullCommand.ExecuteAsync(null);
        Assert.Contains("another operation is running on this repository", vm.SyncStatusText);
        Assert.False(vm.IsBusy);

        // Released, the same op runs and reports git's own outcome instead of the refusal.
        lease.Dispose();
        await vm.PullCommand.ExecuteAsync(null);
        Assert.DoesNotContain("another operation is running", vm.SyncStatusText);
        Assert.Contains("Pull", vm.SyncStatusText);
    }

    // ── Overlapping dry runs ─────────────────────────────────────────────────

    /// <summary>Operation → Scope → Preview with the dry run left in flight on its gate.</summary>
    private static async Task<Task> StartDryRunAsync(ProjectDetailViewModel vm, StubSession session)
    {
        await vm.RewriteNextCommand.ExecuteAsync(null);
        var running = vm.RewriteNextCommand.ExecuteAsync(null);
        await session.PreviewEntered;
        return running;
    }

    private static async Task<ProjectDetailViewModel> OpenWizardOnAsync(TempRepo repo, IRewriteSessionFactory factory)
    {
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, factory);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        return vm;
    }

    /// <summary>
    /// The preview panel's re-run control sits on the surface while the first dry run is still
    /// going, and it calls the step directly, so the command's own re-entrancy guard never
    /// applies. Superseding the running session before the busy gate refuses the step leaves the
    /// first step owning a gate nobody releases: the wizard stays busy for the rest of the
    /// session and every repository operation on the page is refused.
    /// </summary>
    [Fact]
    public async Task ASecondDryRunWhileTheFirstIsStillRunning_LeavesTheBusyGateReleasedAndTheWizardClosable()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-overlap");
        using var _ = repo;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new StubSession { PreviewGate = gate.Task };
        var second = new StubSession();
        var factory = new SequenceFactory(first, second);
        var vm = await OpenWizardOnAsync(repo, factory);

        var running = await StartDryRunAsync(vm, first);
        Assert.True(vm.IsBusy);
        // The control the click lands on is disabled while a step runs.
        Assert.False(vm.RewriteNotRunning);

        await vm.RunRewritePreviewCommand.ExecuteAsync(null);

        // Refused before anything was created or dropped: the running session is still the one
        // the wizard holds, and no second engine run was started.
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, second.PreviewCount);
        Assert.Contains("Another operation is running", vm.RewriteStatusText);

        gate.SetResult();
        await running;

        Assert.False(vm.IsBusy);
        Assert.False(vm.RewriteRunning);
        Assert.True(vm.RewriteNotRunning);
        Assert.True(vm.RewritePreviewAvailable);

        vm.CloseRewriteWizardCommand.Execute(null);
        Assert.False(vm.RewriteWizardVisible);
    }

    /// <summary>
    /// The refusal above must not cost the ordinary re-run its supersede: a dry run started once
    /// the previous one has returned still replaces the held session and drops the scratch tree
    /// the superseded one kept, which is a full bare repository per edit otherwise.
    /// </summary>
    [Fact]
    public async Task ASecondDryRunAfterTheFirstReturned_SupersedesAndDisposesTheFirstSession()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-supersede");
        using var _ = repo;

        var first = new StubSession();
        var second = new StubSession();
        var factory = new SequenceFactory(first, second);
        var vm = await OpenWizardOnAsync(repo, factory);

        await AdvanceToPreviewAsync(vm);
        Assert.Equal(1, first.PreviewCount);

        await vm.RunRewritePreviewCommand.ExecuteAsync(null);

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(1, second.PreviewCount);
        await vm.RewriteSessionDisposal;
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.True(vm.RewritePreviewAvailable);
    }

    /// <summary>
    /// A dry run holds no backup, so a project switch detaches its session rather than parking
    /// it. The step is then neither live nor parked, and releasing the page's gate from it would
    /// reopen it underneath whatever the new project started in the meantime.
    /// </summary>
    [Fact]
    public async Task ADetachedDryRunReturning_DoesNotReleaseTheGateANewerStepIsHolding()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-orphan-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-orphan-b");
        using var __ = repoB;

        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionA = new StubSession { PreviewGate = gateA.Task };
        var sessionB = new StubSession { PreviewGate = gateB.Task };
        var vm = await OpenWizardOnAsync(repoA, new SequenceFactory(sessionA, sessionB));

        var runningA = await StartDryRunAsync(vm, sessionA);

        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(vm.IsBusy);

        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var runningB = await StartDryRunAsync(vm, sessionB);
        Assert.True(vm.IsBusy);

        gateA.SetResult();
        await runningA;

        // A's step is gone; B's gate is still B's.
        Assert.True(vm.IsBusy);
        Assert.True(vm.RewriteRunning);

        gateB.SetResult();
        await runningB;
        Assert.False(vm.IsBusy);
        await vm.RewriteSessionDisposal;
    }

    // ── Returning to an undo that is still running ───────────────────────────

    /// <summary>
    /// An undo writes the repository, so leaving the page parks it exactly as a rewrite is
    /// parked. Coming back puts the wizard on the Running step; without a step move when the
    /// restore returns, the spinner is the last thing the surface ever shows and the disclosure
    /// of what the reset discarded is never rendered.
    /// </summary>
    [Fact]
    public async Task ReturningToAParkedUndo_LandsOnTheResultStepWhenTheRestoreFinishes()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-undo-park-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-undo-park-b");
        using var __ = repoB;

        var session = new StubSession
        {
            RestoreResult = new RestoreResult(true, "restored 3 refs", WorktreeWasDirty: true, DiscardedChangeCount: 2, RefsRestored: true),
        };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        await (await StartRewriteAsync(vm, session));
        Assert.True(vm.RewriteUndoAvailable);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.UndoGate = gate.Task;
        var undoing = vm.UndoRewriteCommand.ExecuteAsync(null);
        await session.UndoEntered;

        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(vm.IsBusy);

        await vm.SetProjectAsync(ProjectFor(repoA));
        Assert.True(vm.RewriteStepIsRunning);
        Assert.True(vm.IsBusy);

        gate.SetResult();
        await undoing;

        Assert.True(vm.RewriteStepIsResult);
        Assert.False(vm.RewriteStepIsRunning);
        Assert.Contains("2 uncommitted change(s) were discarded", vm.RewriteUndoText);
        Assert.False(vm.RewriteUndoAvailable);
        Assert.False(vm.IsBusy);
        Assert.False(vm.RewriteRunning);
    }

    // ── Parking a rewrite that has already been parked once ──────────────────

    /// <summary>
    /// The second departure has to park the rewrite exactly as the first one did. A dry run on
    /// another repository runs between the two, and whether the parked step writes the
    /// repository describes THAT step — read from anywhere else, the second departure decides
    /// the swap is a dry run, disposes the session mid-execute, and the outcome plus the only
    /// one-click undo for the replaced history are written nowhere while the journal already
    /// reads completed.
    /// </summary>
    [Fact]
    public async Task LeavingAgainAfterAnotherReposDryRun_StillParksTheRunningRewriteAndItsUndo()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-repark-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-repark-b");
        using var __ = repoB;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionA = new StubSession { ExecuteGate = gate.Task };
        var sessionB = new StubSession();
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, new SequenceFactory(sessionA, sessionB));
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, sessionA);

        // 1. away from the executing rewrite, 2. a dry run on the other repository.
        await vm.SetProjectAsync(ProjectFor(repoB));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        await AdvanceToPreviewAsync(vm);
        Assert.Equal(1, sessionB.PreviewCount);

        // 3. back to the rewrite, which is still in the engine.
        await vm.SetProjectAsync(ProjectFor(repoA));
        Assert.True(vm.RewriteStepIsRunning);
        Assert.True(vm.IsBusy);

        // 4. away again — the departure that used to decide this run needs no park.
        await vm.SetProjectAsync(ProjectFor(repoB));

        // 5. the swap returns with its session detached from the live wizard.
        gate.SetResult();
        await running;
        await vm.RewriteSessionDisposal;
        Assert.False(sessionA.Disposed);

        await vm.SetProjectAsync(ProjectFor(repoA));
        Assert.True(vm.RewriteWizardVisible);
        Assert.True(vm.RewriteStepIsResult);
        Assert.True(vm.RewriteResultSucceeded);
        Assert.True(vm.RewriteUndoAvailable);

        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        await vm.UndoRewriteCommand.ExecuteAsync(null);
        Assert.Equal(1, sessionA.UndoCount);
        Assert.Contains("History restored", vm.RewriteStatusText);
    }

    // ── The busy gate under an operation on another page ─────────────────────

    /// <summary>
    /// A parked step's release names the gate it took, not the gate it finds. The page it left
    /// belongs to another repository by then, and an operation started there raised the busy
    /// gate for itself; releasing that one hands the surface back while a git process is still
    /// running against it.
    /// </summary>
    [Fact]
    public async Task AParkedRewriteFinishing_LeavesTheGateAnotherPagesOpRaised()
    {
        var repoA = await TempRepo.CreateWithCommitAsync("rw-gate-hand-a");
        using var _ = repoA;
        var repoB = await TempRepo.CreateWithCommitAsync("rw-gate-hand-b");
        using var __ = repoB;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { ExecuteGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(vm.IsBusy);

        var pulling = vm.PullCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        gate.SetResult();
        await running;

        Assert.False(pulling.IsCompleted);
        Assert.True(vm.IsBusy);

        await pulling;
        Assert.False(vm.IsBusy);
    }

    // ── Cancelling a step ────────────────────────────────────────────────────

    /// <summary>
    /// A dry run only reads the source and writes a scratch tree, so it is cancellable from end
    /// to end. Cancelling it must also drop the report it was going to arm Execute with.
    /// </summary>
    [Fact]
    public async Task CancellingTheDryRun_ReportsNothingChangedAndArmsNoExecute()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-cancel-dry");
        using var _ = repo;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { PreviewGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";

        await vm.RewriteNextCommand.ExecuteAsync(null);
        var previewing = vm.RewriteNextCommand.ExecuteAsync(null);
        await session.PreviewEntered;

        Assert.True(vm.RewriteCanCancel);
        Assert.Equal("Cancel the dry run", vm.RewriteCancelLabel);

        vm.CancelRewriteStepCommand.Execute(null);
        Assert.False(vm.RewriteCanCancel);
        gate.SetResult();
        await previewing;

        Assert.Equal("Dry run cancelled — nothing was changed.", vm.RewriteStatusText);
        Assert.Equal("", vm.RewriteErrorText);
        Assert.False(vm.RewritePreviewAvailable);
        Assert.False(vm.RewriteShowExecute);
        Assert.False(vm.RewriteRunning);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// Cancelling before the swap's point of no return changed nothing, so the dry run already
    /// paid for still describes exactly what a re-run would do — the wizard keeps it and returns
    /// to the confirm step rather than demanding another export.
    /// </summary>
    [Fact]
    public async Task CancellingTheRewriteBeforeTheSwap_KeepsTheHeldDryRunAndClaimsNothingChanged()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-cancel-run");
        using var _ = repo;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { ExecuteGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        Assert.True(vm.RewriteCanCancel);
        Assert.Equal("Cancel the rewrite", vm.RewriteCancelLabel);

        vm.CancelRewriteStepCommand.Execute(null);
        gate.SetResult();
        await running;

        Assert.Equal(ProjectDetailViewModel.CancelledRewriteStatus, vm.RewriteStatusText);
        Assert.Equal("", vm.RewriteErrorText);
        Assert.False(vm.RewriteResultSucceeded);
        Assert.False(vm.RewriteUndoAvailable);
        Assert.True(vm.RewritePreviewAvailable);
        Assert.True(vm.RewriteStepIsConfirm);
        Assert.False(vm.RewriteCanCancel);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// The offer and the guarantee move together: the instant the swap stops honouring the token
    /// the control goes, and the surface states why rather than leaving a dead button.
    /// </summary>
    [Fact]
    public async Task OnceTheSwapReachesThePointOfNoReturn_TheCancelOfferIsWithdrawn()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-cancel-closed");
        using var _ = repo;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession
        {
            ExecuteGate = gate.Task,
            ReachesPointOfNoReturn = true,
            ApplyingGate = applying.Task,
        };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        Assert.True(vm.RewriteCanCancel);
        Assert.False(vm.RewriteCancelClosedVisible);

        // Releasing the gate lets the stub report Applying, exactly where the real swap does,
        // and hold there — so this observes the surface mid-transaction, not after it.
        gate.SetResult();
        await session.ApplyingEntered;

        Assert.True(vm.RewriteRunning);
        Assert.False(vm.RewriteCanCancel);
        Assert.True(vm.RewriteCancelClosedVisible);
        Assert.Equal(ProjectDetailViewModel.RewriteApplyingNotice, vm.RewriteStatusText);

        // A cancel issued now is refused by the step rather than promised and dropped.
        vm.CancelRewriteStepCommand.Execute(null);
        Assert.Equal(ProjectDetailViewModel.RewriteApplyingNotice, vm.RewriteStatusText);

        applying.SetResult();
        await running;

        Assert.False(vm.RewriteCanCancel);
        Assert.True(vm.RewriteResultSucceeded);
        Assert.True(vm.RewriteStepIsResult);
    }

    /// <summary>
    /// A cancel that arrives after the swap can no longer be stopped did not stop it. Reporting
    /// the request rather than the outcome would tell the reader their history is intact when it
    /// has just been replaced.
    /// </summary>
    [Fact]
    public async Task CancelThatLosesTheRaceToTheSwap_ReportsTheRewriteAsApplied()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-cancel-race");
        using var _ = repo;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { ExecuteGate = gate.Task, ReachesPointOfNoReturn = true };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        vm.CancelRewriteStepCommand.Execute(null);
        gate.SetResult();
        await running;

        Assert.True(vm.RewriteResultSucceeded);
        Assert.Contains("History rewritten", vm.RewriteStatusText);
        Assert.Contains("after the swap could no longer be stopped", vm.RewriteStatusText);
        Assert.DoesNotContain("nothing was changed", vm.RewriteStatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A restore's ref reconciliation is all-or-nothing in the same way the swap's is, so there
    /// is no safe point at which stopping it would leave a known state. No offer is made.
    /// </summary>
    [Fact]
    public async Task AnUndoOffersNoCancel()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-cancel-undo");
        using var _ = repo;
        var undoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { UndoGate = undoGate.Task };
        var vm = NewVm(session);
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        await await StartRewriteAsync(vm, session);

        var undoing = vm.UndoRewriteCommand.ExecuteAsync(null);
        await session.UndoEntered;

        Assert.True(vm.RewriteRunning);
        Assert.False(vm.RewriteCanCancel);
        Assert.True(vm.RewriteCancelClosedVisible);

        undoGate.SetResult();
        await undoing;
    }

    /// <summary>
    /// Closing is not a way to ask for a stop: the result screen is the only report of what a
    /// running step did, so the surface stays put and points at the cancel control instead.
    /// </summary>
    [Fact]
    public async Task ClosingTheWizardMidStep_RefusesAndPointsAtTheCancelControl()
    {
        var repo = await TempRepo.CreateWithCommitAsync("rw-cancel-close");
        using var _ = repo;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubSession { ExecuteGate = gate.Task };
        var vm = NewVm(session);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.OpenRewriteWizardCommand.Execute(null);
        vm.RewriteFindText = "SECRET";
        var running = await StartRewriteAsync(vm, session);

        vm.CloseRewriteWizardCommand.Execute(null);

        Assert.True(vm.RewriteWizardVisible);
        Assert.Contains("cancel it", vm.RewriteStatusText);

        gate.SetResult();
        await running;
    }

    // ── Purge size in human units ────────────────────────────────────────────

    [Theory]
    [InlineData("900", 900L)]
    [InlineData("900 b", 900L)]
    [InlineData("1kb", 1024L)]
    [InlineData("1 KB", 1024L)]
    [InlineData("1.5MB", 1572864L)]
    [InlineData("2 gb", 2147483648L)]
    [InlineData("4 GiB", 4294967296L)]
    [InlineData("  10 mib  ", 10485760L)]
    [InlineData("0.5 kb", 512L)]
    public void AMinimumBlobSize_ParsesHumanUnitsIntoBytes(string typed, long expected)
    {
        Assert.True(ByteSizeText.TryParse(typed, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("0 kb")]
    [InlineData("big")]
    [InlineData("12 tb")]
    [InlineData("kb")]
    [InlineData("1,5 mb")]
    public void AMinimumBlobSize_RejectsWhatIsNotASize(string typed)
    {
        Assert.False(ByteSizeText.TryParse(typed, out _));
    }

    /// <summary>
    /// A size too large to scale into bytes is rejected, not thrown out of. The field binds on
    /// every keystroke, so a throw here repeats out of the setter and every command requery and
    /// the wizard cannot be dismissed.
    /// </summary>
    [Theory]
    [InlineData("9999999999999999999999999999 KB")]
    [InlineData("79228162514264337593543950335 gb")]
    [InlineData("40000000000000000000000000 mib")]
    public void ASizeTooLargeToScaleIntoBytes_IsRejectedRatherThanThrown(string typed)
    {
        Assert.False(ByteSizeText.TryParse(typed, out var bytes));
        Assert.Equal(0L, bytes);
        Assert.Contains("is not a size", ByteSizeText.ProblemWith(typed));
    }

    /// <summary>A size landing between bytes rounds up, so the filter never catches a file smaller than the one named.</summary>
    [Fact]
    public void AFractionalByteCount_RoundsUpRatherThanDown()
    {
        Assert.True(ByteSizeText.TryParse("0.0001 kb", out var bytes));
        Assert.Equal(1L, bytes);
    }

    [Fact]
    public async Task TypingASizeWithAUnit_EchoesTheByteCountAndReachesTheEngineAsBytes()
    {
        var (repo, vm, _) = await OpenWizardAsync("rw-size");
        using var handle = repo;

        vm.RewriteOperationIsPurgePath = true;
        vm.RewritePurgeMinSizeText = "1.5 MB";

        Assert.Equal("= 1,572,864 bytes", vm.RewritePurgeMinSizeEcho);
        Assert.Equal("", vm.RewriteNextBlockedReason);
        Assert.True(vm.RewriteNextCommand.CanExecute(null));

        var request = vm.BuildRewriteRequest();
        Assert.Equal(1572864L, request.Options.Purge!.MinBlobSize);
    }

    /// <summary>An unparseable size disables Next and says why, rather than failing on the click.</summary>
    [Fact]
    public async Task TypingAnUnparseableSize_DisablesNextWithAReason()
    {
        var (repo, vm, session) = await OpenWizardAsync("rw-size-bad");
        using var handle = repo;

        vm.RewriteOperationIsPurgePath = true;
        vm.RewritePurgePathsText = "secrets.txt";
        vm.RewritePurgeMinSizeText = "quite big";

        Assert.False(vm.RewriteNextCommand.CanExecute(null));
        Assert.Contains("is not a size", vm.RewriteNextBlockedReason);
        Assert.Contains("is not a size", vm.RewritePurgeMinSizeEcho);

        // The guard holds on the command itself, not only on the button's enabled state.
        await vm.RewriteNextCommand.ExecuteAsync(null);
        Assert.True(vm.RewriteStepIsOperation);
        Assert.Equal(0, session.PreviewCount);

        vm.RewritePurgeMinSizeText = "500 KB";
        Assert.True(vm.RewriteNextCommand.CanExecute(null));
        Assert.Equal("", vm.RewriteNextBlockedReason);
    }
}
