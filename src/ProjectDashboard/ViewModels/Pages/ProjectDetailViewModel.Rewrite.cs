using System.ComponentModel;
using System.Text;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>Which transformation the wizard is configuring. Only operations the engine executes appear here.</summary>
public enum RewriteOperationKind
{
    ReplaceText,
    PurgePath,
    RewriteMessages,
    RewriteIdentity,
}

/// <summary>
/// The scope the wizard offers. Each choice drives exactly one engine axis: the three path
/// choices set <see cref="RewriteOptions.FileScope"/>, the two commit choices set
/// <see cref="RewriteOptions.CommitScope"/>, and the other axis stays unrestricted.
/// </summary>
public enum RewriteScopeChoice
{
    AllHistory,
    Globs,
    ExplicitPaths,
    ExplicitCommits,
    CommitRange,
}

public enum RewriteWizardStep
{
    Operation,
    Scope,
    Preview,
    Confirm,
    Running,
    Result,
}

/// <summary>What a dry run proved, or why it was refused. Carries no engine handle, so a headless test can produce one.</summary>
public sealed record RewritePreviewOutcome(RewriteReport? Report, string? FailureReason);

/// <summary>
/// One repository's rewrite, from dry run to undo. The wizard drives this rather than
/// <see cref="RewriteCoordinator"/> directly because the coordinator's handles are bound to
/// real state — a preview handle names a rewritten temp bare on disk, an undo handle names a
/// backup bundle and the lease registry guarding it — so every wizard-behaviour test would
/// otherwise have to run the engine over a fixture repository to reach one screen.
/// </summary>
public interface IRewriteSession : IDisposable
{
    /// <summary>True once an execution took a backup that can still be restored.</summary>
    bool CanUndo { get; }

    /// <summary>Runs the engine without touching the repository and keeps the result for <see cref="ExecuteAsync"/>.</summary>
    Task<RewritePreviewOutcome> PreviewAsync(RewriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// Applies the history proved by the last successful preview. Fails when no preview is held.
    /// <paramref name="phase"/> is reported at the point where cancellation stops being honoured.
    /// </summary>
    Task<RewriteExecutionResult> ExecuteAsync(CancellationToken ct = default, IProgress<RewritePhase>? phase = null);

    Task<RestoreResult> UndoAsync(CancellationToken ct = default);
}

public interface IRewriteSessionFactory
{
    IRewriteSession Create();
}

/// <summary>Session backed by the real coordinator: preview keeps the temp bare, execute reuses it so the engine runs once.</summary>
internal sealed class CoordinatorRewriteSession(RewriteCoordinator coordinator) : IRewriteSession
{
    private PreviewHandle? _preview;
    private UndoHandle? _undo;

    /// <summary>
    /// The off-thread deletion of EVERY handle this session has released, joined. Both release
    /// sites run on the thread that started the wizard step — for <see cref="PreviewAsync"/>
    /// before its first await, for <see cref="ExecuteAsync"/> on the resumed continuation —
    /// which in the app is the dispatcher. A dry run, an edit, and a second dry run leave two
    /// releases pending at once; replacing the task instead of joining it would let
    /// <see cref="Dispose"/> return with the first release's scratch tree still on disk.
    /// </summary>
    internal Task ScratchDisposal { get; private set; } = Task.CompletedTask;

    public bool CanUndo => _undo is not null;

    public async Task<RewritePreviewOutcome> PreviewAsync(RewriteRequest request, CancellationToken ct = default)
    {
        // A superseded preview's scratch tree is dropped here; keeping it would leave a bare
        // repo per edit under the work root for the process lifetime.
        ReleaseSpentPreview();
        try
        {
            _preview = await coordinator.PreviewAsync(request, ct);
            return new RewritePreviewOutcome(_preview.Report, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RewritePreviewOutcome(null, ex.Message);
        }
    }

    public async Task<RewriteExecutionResult> ExecuteAsync(CancellationToken ct = default, IProgress<RewritePhase>? phase = null)
    {
        var preview = _preview;
        if (preview is null)
            return new RewriteExecutionResult { Success = false, FailureReason = "no preview has been run for this rewrite" };
        var result = await coordinator.ExecuteAsync(preview, ct, phase);
        _undo ??= result.Undo;
        // A cancelled run never installed the bare, and the staleness gate re-reads the source
        // on the next attempt, so the handle is still good and the reader keeps the dry run they
        // already paid for. Any other outcome spends it — the wizard requires a fresh dry run
        // before a further execute — so holding it only keeps its scratch tree alive.
        if (!result.Cancelled)
            ReleaseSpentPreview();
        return result;
    }

    public Task<RestoreResult> UndoAsync(CancellationToken ct = default) =>
        _undo is null
            ? Task.FromResult(new RestoreResult(false, "no backup was taken for this rewrite"))
            : _undo.RestoreAsync(ct);

    /// <summary>
    /// Hands a spent handle to the pool. Deleting its scratch tree enumerates every file,
    /// rewrites the attributes of each, deletes recursively, and sleeps between retries while a
    /// just-exited git still holds handles, so it never runs on the thread that released it.
    /// </summary>
    private void ReleaseSpentPreview()
    {
        var spent = _preview;
        _preview = null;
        if (spent is null) return;
        ScratchDisposal = Task.WhenAll(ScratchDisposal, Task.Run(spent.Dispose));
    }

    /// <summary>
    /// Deletes in place: every caller detaches a session off the dispatcher already. Joining
    /// the pending release keeps a completed disposal meaning the scratch trees are gone.
    /// </summary>
    public void Dispose()
    {
        _preview?.Dispose();
        _preview = null;
        ScratchDisposal.GetAwaiter().GetResult();
    }
}

internal sealed class CoordinatorRewriteSessionFactory(RewriteCoordinator coordinator) : IRewriteSessionFactory
{
    public IRewriteSession Create() => new CoordinatorRewriteSession(coordinator);
}

/// <summary>
/// The history-rewrite wizard: operation, scope, mandatory dry run, typed confirmation,
/// execution, and undo. Two invariants hold the surface honest — an Execute affordance
/// exists only while a preview for the CURRENT inputs is held, and no result text claims
/// content is gone unless the report's own scrub check says so.
/// </summary>
public partial class ProjectDetailViewModel
{
    private IRewriteSession? _rewriteSession;

    /// <summary>The report the wizard is currently displaying: the dry run's, then the execution's.</summary>
    private RewriteReport? _rewriteReport;

    /// <summary>Completes when the step in flight returns. A session detached mid-run waits on it before disposal.</summary>
    private Task _rewriteStepInFlight = Task.CompletedTask;

    /// <summary>
    /// The page's busy gate as one rewrite step holds it. <see cref="RewriteStepGate.WritesRepo"/>
    /// travels with the holder because it describes that one step: a dry run writes nothing and
    /// holds no backup, so leaving the page ends it, while a rewrite or an undo is parked
    /// instead, because ending it would drop the only restore for what it replaced. Read from a
    /// page-wide field instead, a dry run on another repository answers for a swap on this one.
    /// </summary>
    private sealed class RewriteStepGate(IRewriteSession session, bool writesRepo)
    {
        public IRewriteSession Session { get; } = session;

        public bool WritesRepo { get; } = writesRepo;

        /// <summary>
        /// Cancels this step and no other. Held on the gate rather than on the page: a step
        /// parked by a project switch keeps its own source, so a cancel issued after the reader
        /// returns still reaches the step that raised the gate.
        /// </summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>
        /// True once the step has passed the point where cancellation stops being honoured, or
        /// once it has returned. Either way a cancel request would be a promise the step cannot
        /// keep, so the offer is withdrawn instead.
        /// </summary>
        public bool CancelClosed { get; set; }
    }

    /// <summary>
    /// Rewrites whose repository is not the one on screen, keyed by <see cref="RepoKey"/>. A
    /// session that has taken a backup owns the only one-click restore for the history it
    /// replaced, and the wizard's result screen is the only surface offering it, so leaving the
    /// page parks the session here instead of disposing it. Sessions leave the park only by
    /// being restored and then closed.
    /// </summary>
    private readonly Dictionary<string, ParkedRewrite> _parkedRewrites = new(StringComparer.Ordinal);

    /// <summary>
    /// One repository's rewrite while its page is not on screen: the session plus the result
    /// screen it is entitled to when the reader comes back. Only what the result screen renders
    /// is held — the operation and scope inputs describe a run that already happened, and the
    /// confirm message they built is carried as text.
    /// </summary>
    private sealed class ParkedRewrite
    {
        public required IRewriteSession Session { get; init; }

        /// <summary>The gate the parked step took, or null when no step of its own is in flight.</summary>
        public required RewriteStepGate? Gate { get; init; }

        public required Task StepInFlight { get; set; }
        public bool Running { get; set; }
        public bool CanCancel { get; set; }
        public string CancelLabel { get; set; } = "";
        public RewriteReport? Report { get; set; }
        public bool Succeeded { get; set; }
        public bool UndoAvailable { get; set; }
        public string UndoText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string ErrorText { get; set; } = "";
        public string ConfirmPhrase { get; set; } = "";
    }

    /// <summary>The off-thread disposal of the last detached session; held so a headless test can await it.</summary>
    internal Task RewriteSessionDisposal { get; private set; } = Task.CompletedTask;

    /// <summary>Null when the host did not supply an engine; the wizard then refuses instead of pretending to work.</summary>
    private readonly IRewriteSessionFactory? _rewriteSessions;

    /// <summary>
    /// Confirmation seam. The app shows the Fluent dialog; a headless test replaces it to
    /// drive the confirmed path of an irreversible action.
    /// </summary>
    internal Func<string, string, string, Task<bool>> ConfirmPrompt { get; set; }

    // ── Wizard shell ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _rewriteWizardVisible;

    /// <summary>
    /// <see cref="SafetyOverlayHidden"/> is bound to the IsEnabled of every surface a safety
    /// overlay's scrim covers: the work-area tabs, the state banner, the branch bar, and the
    /// recovery banner. The scrim stops the mouse only; without this, a keystroke and a screen
    /// reader still reach the discard, stage, branch-delete, and Pull controls behind it — and a
    /// Pull merges the un-rewritten remote history back in.
    /// </summary>
    partial void OnRewriteWizardVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));
    [ObservableProperty] private RewriteWizardStep _rewriteStep;
    [ObservableProperty] private bool _rewriteStepIsOperation = true;
    [ObservableProperty] private bool _rewriteStepIsScope;
    [ObservableProperty] private bool _rewriteStepIsPreview;
    [ObservableProperty] private bool _rewriteStepIsConfirm;
    [ObservableProperty] private bool _rewriteStepIsRunning;
    [ObservableProperty] private bool _rewriteStepIsResult;
    [ObservableProperty] private string _rewriteStepTitle = "Choose an operation";

    [ObservableProperty] private bool _rewriteShowBack;
    [ObservableProperty] private bool _rewriteShowNext = true;

    /// <summary>
    /// The Execute control is bound to this, so without a held dry run the control is absent
    /// from the surface entirely rather than merely disabled.
    /// </summary>
    [ObservableProperty] private bool _rewriteShowExecute;

    [ObservableProperty] private bool _rewriteHasReport;

    [ObservableProperty] private string _rewriteStatusText = "";
    [ObservableProperty] private string _rewriteErrorText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteRewriteCommand))]
    private bool _rewriteRunning;

    /// <summary>
    /// Bound to the IsEnabled of the dry-run re-run control, which invokes the step directly
    /// rather than through a command whose re-entrancy guard would refuse it.
    /// </summary>
    public bool RewriteNotRunning => !RewriteRunning;

    partial void OnRewriteRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(RewriteNotRunning));
        OnPropertyChanged(nameof(RewriteCancelClosedVisible));
    }

    /// <summary>
    /// Whether the step in flight can still be stopped. Cleared the moment the rewrite passes
    /// the swap's point of no return, so the control is never offered for a stop that cannot
    /// happen. An undo never sets it: a restore is itself an all-or-nothing ref transaction.
    /// </summary>
    [ObservableProperty] private bool _rewriteCanCancel;

    /// <summary>Names what the cancel control would stop, so "Cancel" never reads as "close the wizard".</summary>
    [ObservableProperty] private string _rewriteCancelLabel = "";

    partial void OnRewriteCanCancelChanged(bool value) => OnPropertyChanged(nameof(RewriteCancelClosedVisible));

    /// <summary>True while a step that can no longer be stopped is still running — the surface says so rather than showing a dead control.</summary>
    public bool RewriteCancelClosedVisible => RewriteRunning && !RewriteCanCancel;

    /// <summary>The wording every surface uses once the swap owns the repository, so the claim is made in exactly one place.</summary>
    internal const string RewriteApplyingNotice = "Applying — the rewrite can no longer be cancelled.";

    // ── Step 1: operation ───────────────────────────────────────────────────────

    [ObservableProperty] private bool _rewriteOperationIsReplaceText = true;
    [ObservableProperty] private bool _rewriteOperationIsPurgePath;
    [ObservableProperty] private bool _rewriteOperationIsMessages;
    [ObservableProperty] private bool _rewriteOperationIsIdentity;

    [ObservableProperty] private string _rewriteFindText = "";
    [ObservableProperty] private string _rewriteReplacementText = "";
    [ObservableProperty] private bool _rewriteUseRegex;

    [ObservableProperty] private string _rewritePurgePathsText = "";
    [ObservableProperty] private string _rewritePurgeMinSizeText = "";

    [ObservableProperty] private string _rewriteMessageFindText = "";
    [ObservableProperty] private string _rewriteMessageReplacementText = "";
    [ObservableProperty] private bool _rewriteMessageUseRegex;

    [ObservableProperty] private string _rewriteOldName = "";
    [ObservableProperty] private string _rewriteOldEmail = "";
    [ObservableProperty] private string _rewriteNewName = "";
    [ObservableProperty] private string _rewriteNewEmail = "";

    // ── Step 2: scope ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool _rewriteScopeIsAllHistory = true;
    [ObservableProperty] private bool _rewriteScopeIsGlobs;
    [ObservableProperty] private bool _rewriteScopeIsExplicitPaths;
    [ObservableProperty] private bool _rewriteScopeIsExplicitCommits;
    [ObservableProperty] private bool _rewriteScopeIsCommitRange;

    [ObservableProperty] private string _rewriteScopeGlobsText = "";
    [ObservableProperty] private string _rewriteScopePathsText = "";
    [ObservableProperty] private ObservableCollection<string> _rewriteScopeCommits = [];
    [ObservableProperty] private string _rewriteScopeCommitDraft = "";
    [ObservableProperty] private GitCommit? _rewriteScopePickedCommit;
    [ObservableProperty] private string _rewriteRangeFromText = "";
    [ObservableProperty] private string _rewriteRangeToText = "";

    // ── Step 3: preview ─────────────────────────────────────────────────────────

    /// <summary>
    /// Gate for the entire Execute affordance. Set only by a successful dry run and cleared by
    /// any edit to an operation or scope input, so no Execute can ever apply options the
    /// displayed report does not describe.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteRewriteCommand))]
    private bool _rewritePreviewAvailable;

    [ObservableProperty] private ObservableCollection<RewriteFact> _rewriteFacts = [];
    [ObservableProperty] private ObservableCollection<ScrubVerdictLine> _rewriteScrubLines = [];
    [ObservableProperty] private ObservableCollection<string> _rewriteSkipLines = [];
    [ObservableProperty] private ScrubVerdictLine? _rewriteOverallVerdict;

    // ── Step 4: typed confirmation ──────────────────────────────────────────────

    /// <summary>The exact text the reader must type. The repository folder name, never a generic word.</summary>
    [ObservableProperty] private string _rewriteConfirmPhrase = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteRewriteCommand))]
    private string _rewriteConfirmInput = "";

    [ObservableProperty] private string _rewriteConfirmMessage = "";

    // ── Step 5: result ──────────────────────────────────────────────────────────

    [ObservableProperty] private bool _rewriteResultSucceeded;
    [ObservableProperty] private bool _rewriteUndoAvailable;
    [ObservableProperty] private string _rewriteUndoText = "";

    /// <summary>
    /// Input fields whose edit invalidates a held preview. Membership is checked in
    /// <see cref="OnPropertyChanged"/>; none of the properties the invalidation writes appear
    /// here, so the notification cannot re-enter.
    /// </summary>
    private static readonly HashSet<string> RewriteInputProperties =
    [
        nameof(RewriteOperationIsReplaceText), nameof(RewriteOperationIsPurgePath),
        nameof(RewriteOperationIsMessages), nameof(RewriteOperationIsIdentity),
        nameof(RewriteFindText), nameof(RewriteReplacementText), nameof(RewriteUseRegex),
        nameof(RewritePurgePathsText), nameof(RewritePurgeMinSizeText),
        nameof(RewriteMessageFindText), nameof(RewriteMessageReplacementText), nameof(RewriteMessageUseRegex),
        nameof(RewriteOldName), nameof(RewriteOldEmail), nameof(RewriteNewName), nameof(RewriteNewEmail),
        nameof(RewriteScopeIsAllHistory), nameof(RewriteScopeIsGlobs), nameof(RewriteScopeIsExplicitPaths),
        nameof(RewriteScopeIsExplicitCommits), nameof(RewriteScopeIsCommitRange),
        nameof(RewriteScopeGlobsText), nameof(RewriteScopePathsText),
        nameof(RewriteRangeFromText), nameof(RewriteRangeToText),
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && RewriteInputProperties.Contains(name))
        {
            InvalidateRewritePreview();
            RefreshRewriteInputValidity();
        }
        HandleSurgeryPropertyChanged(e);
        HandleHunkPropertyChanged(e);
        HandleHistoryDepthPropertyChanged(e);
    }

    // ── Derived selections ──────────────────────────────────────────────────────

    /// <summary>
    /// The chosen operation. The radio buttons own the booleans; a fixed priority keeps the
    /// answer deterministic even if two were ever set at once.
    /// </summary>
    internal RewriteOperationKind RewriteOperation =>
        RewriteOperationIsPurgePath ? RewriteOperationKind.PurgePath
        : RewriteOperationIsMessages ? RewriteOperationKind.RewriteMessages
        : RewriteOperationIsIdentity ? RewriteOperationKind.RewriteIdentity
        : RewriteOperationKind.ReplaceText;

    internal RewriteScopeChoice RewriteScopeSelection =>
        RewriteScopeIsGlobs ? RewriteScopeChoice.Globs
        : RewriteScopeIsExplicitPaths ? RewriteScopeChoice.ExplicitPaths
        : RewriteScopeIsExplicitCommits ? RewriteScopeChoice.ExplicitCommits
        : RewriteScopeIsCommitRange ? RewriteScopeChoice.CommitRange
        : RewriteScopeChoice.AllHistory;

    partial void OnRewriteStepChanged(RewriteWizardStep value) => ApplyRewriteStep(value);

    /// <summary>
    /// Derives every step-dependent flag. Called directly on reset as well: the first step is
    /// the enum's default, so the change notification never fires for it.
    /// </summary>
    private void ApplyRewriteStep(RewriteWizardStep value)
    {
        RewriteStepIsOperation = value == RewriteWizardStep.Operation;
        RewriteStepIsScope = value == RewriteWizardStep.Scope;
        RewriteStepIsPreview = value == RewriteWizardStep.Preview;
        RewriteStepIsConfirm = value == RewriteWizardStep.Confirm;
        RewriteStepIsRunning = value == RewriteWizardStep.Running;
        RewriteStepIsResult = value == RewriteWizardStep.Result;
        RewriteStepTitle = value switch
        {
            RewriteWizardStep.Operation => "Step 1 of 4 — choose an operation",
            RewriteWizardStep.Scope => "Step 2 of 4 — choose what it applies to",
            RewriteWizardStep.Preview => "Step 3 of 4 — dry run",
            RewriteWizardStep.Confirm => "Step 4 of 4 — confirm",
            RewriteWizardStep.Running => "Rewriting history",
            _ => "Result",
        };
        UpdateRewriteAffordances();
        RefreshRewriteInputValidity();
    }

    partial void OnRewritePreviewAvailableChanged(bool value) => UpdateRewriteAffordances();

    private void UpdateRewriteAffordances()
    {
        RewriteShowBack = RewriteStep is RewriteWizardStep.Scope or RewriteWizardStep.Preview or RewriteWizardStep.Confirm;
        RewriteShowNext = RewriteStep is RewriteWizardStep.Operation or RewriteWizardStep.Scope
            || (RewriteStep == RewriteWizardStep.Preview && RewritePreviewAvailable);
        RewriteShowExecute = RewriteStep == RewriteWizardStep.Confirm && RewritePreviewAvailable;
    }

    // ── Entry and exit ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenRewriteWizard()
    {
        if (RepoPath.Length == 0 || IsBusy) return;
        ResetRewriteState();
        RewriteConfirmPhrase = RepoDisplayName();
        RewriteWizardVisible = true;
    }

    [RelayCommand]
    private void CloseRewriteWizard()
    {
        // A step in flight owns the busy gate, and closing the surface would hide the only report
        // of what it did. Stopping it is the cancel control's job, which says whether stopping is
        // still possible; closing is never a way to ask for it.
        if (RewriteRunning)
        {
            RewriteStatusText = RewriteCanCancel
                ? "This step is still running — cancel it, or wait for it to finish."
                : "The rewrite is still running — wait for it to finish.";
            return;
        }
        RewriteWizardVisible = false;
        ResetRewriteState();
    }

    /// <summary>Drops every wizard field and the held preview. Called on open, on close, and on a project switch.</summary>
    private void ResetRewriteState()
    {
        // Before RewriteRunning is cleared below: the detach reads it to decide whether the
        // session is still in use.
        DetachRewriteSession();
        _rewriteReport = null;

        RewriteWizardVisible = false;
        RewriteStep = RewriteWizardStep.Operation;
        RewriteStatusText = "";
        RewriteErrorText = "";
        RewriteRunning = false;
        RewriteCanCancel = false;
        RewriteCancelLabel = "";

        RewriteOperationIsReplaceText = true;
        RewriteOperationIsPurgePath = false;
        RewriteOperationIsMessages = false;
        RewriteOperationIsIdentity = false;
        RewriteFindText = "";
        RewriteReplacementText = "";
        RewriteUseRegex = false;
        RewritePurgePathsText = "";
        RewritePurgeMinSizeText = "";
        RewriteMessageFindText = "";
        RewriteMessageReplacementText = "";
        RewriteMessageUseRegex = false;
        RewriteOldName = "";
        RewriteOldEmail = "";
        RewriteNewName = "";
        RewriteNewEmail = "";

        RewriteScopeIsAllHistory = true;
        RewriteScopeIsGlobs = false;
        RewriteScopeIsExplicitPaths = false;
        RewriteScopeIsExplicitCommits = false;
        RewriteScopeIsCommitRange = false;
        RewriteScopeGlobsText = "";
        RewriteScopePathsText = "";
        RewriteScopeCommits = [];
        RewriteScopeCommitDraft = "";
        RewriteScopePickedCommit = null;
        RewriteRangeFromText = "";
        RewriteRangeToText = "";

        RewritePreviewAvailable = false;
        RewriteHasReport = false;
        RewriteFacts = [];
        RewriteScrubLines = [];
        RewriteSkipLines = [];
        RewriteOverallVerdict = null;

        RewriteConfirmPhrase = "";
        RewriteConfirmInput = "";
        RewriteConfirmMessage = "";

        RewriteResultSucceeded = false;
        RewriteUndoAvailable = false;
        RewriteUndoText = "";

        ApplyRewriteStep(RewriteStep);
    }

    /// <summary>
    /// Gives up the session without ending the step that is using it. Disposing a session
    /// deletes its scratch bare, which the swap reads across several git invocations, so a
    /// session detached mid-run is disposed only after the step in flight has returned.
    /// Every detach runs on the UI thread and the deletion walks the whole scratch tree,
    /// clearing attributes and sleeping between retries while a just-exited git or a scanner
    /// still holds handles, so the disposal itself never runs on the dispatcher.
    /// </summary>
    private void DetachRewriteSession()
    {
        var session = _rewriteSession;
        _rewriteSession = null;
        if (session is null) return;
        if (!RewriteRunning)
        {
            RewriteSessionDisposal = Task.Run(session.Dispose);
            return;
        }
        var pending = _rewriteStepInFlight;
        RewriteSessionDisposal = DisposeAfterAsync(pending, session);
    }

    /// <summary>
    /// Moves this repository's rewrite out of the page's way without ending it. A session that
    /// has reached execution holds a backup and the only undo for it; a project switch is not
    /// consent to give that up, so it is parked rather than disposed and the reader gets its
    /// result screen back on return. A dry-run-only session took no backup and changed nothing,
    /// so it is left for <see cref="ResetRewriteState"/> to dispose.
    /// </summary>
    private void ParkRewriteSessionForThisRepo()
    {
        var repo = RepoPath;
        var session = _rewriteSession;
        if (repo.Length == 0 || session is null) return;
        var gate = _busyGateHolder as RewriteStepGate;
        if (gate is not null && !ReferenceEquals(gate.Session, session)) gate = null;
        if (!session.CanUndo && !(RewriteRunning && gate is { WritesRepo: true })) return;

        _rewriteSession = null;
        _parkedRewrites[RepoKey.For(repo)] = new ParkedRewrite
        {
            Session = session,
            Gate = gate,
            StepInFlight = _rewriteStepInFlight,
            Running = RewriteRunning,
            CanCancel = RewriteCanCancel,
            CancelLabel = RewriteCancelLabel,
            Report = _rewriteReport,
            Succeeded = RewriteResultSucceeded,
            UndoAvailable = RewriteUndoAvailable,
            UndoText = RewriteUndoText,
            StatusText = RewriteStatusText,
            ErrorText = RewriteErrorText,
            ConfirmPhrase = RewriteConfirmPhrase,
        };
    }

    /// <summary>
    /// Puts this repository's parked rewrite back on screen. The wizard reopens over the work
    /// area it was covering, so the surfaces the scrim disables stay disabled for a run that is
    /// still going, and the busy gate comes back with it.
    /// </summary>
    private void RestoreParkedRewrite()
    {
        if (RepoPath.Length == 0 || !_parkedRewrites.Remove(RepoKey.For(RepoPath), out var parked)) return;

        _rewriteSession = parked.Session;
        _rewriteStepInFlight = parked.StepInFlight;
        RewriteConfirmPhrase = parked.ConfirmPhrase;
        if (parked.Report is { } report) ShowReport(report);
        RewriteResultSucceeded = parked.Succeeded;
        RewriteUndoAvailable = parked.UndoAvailable;
        RewriteUndoText = parked.UndoText;
        RewriteStatusText = parked.StatusText;
        RewriteErrorText = parked.ErrorText;
        RewriteRunning = parked.Running;
        IsBusy = parked.Running;
        // The same gate object comes back, so the step that raised the page's gate is again the
        // one entitled to lower it, and it answers for its own step's writes — including whether
        // the cancel it was offering is still one the step can keep.
        if (parked.Running && parked.Gate is { } gate) _busyGateHolder = gate;
        RewriteCanCancel = parked.Running && parked.CanCancel && parked.Gate is { CancelClosed: false };
        RewriteCancelLabel = RewriteCanCancel ? parked.CancelLabel : "";
        RewriteStep = parked.Running ? RewriteWizardStep.Running : RewriteWizardStep.Result;
        RewriteWizardVisible = true;
    }

    /// <summary>The parked lane a step's own session sits in, or null when that session is not parked.</summary>
    private ParkedRewrite? ParkedLaneFor(IRewriteSession session) =>
        _parkedRewrites.Values.FirstOrDefault(p => ReferenceEquals(p.Session, session));

    /// <summary>
    /// Whether a step that ran on <paramref name="session"/> may still write the live surface.
    /// Session identity, not the page generation: a rewrite outlives a project switch, so the
    /// generation moves twice on a switch away and back while the step is still the one this
    /// wizard is showing. Restoring a parked rewrite re-attaches the same instance, which is
    /// what makes that round trip land on screen instead of in a lane nobody reads.
    /// </summary>
    private bool OwnsLiveWizard(IRewriteSession session) => ReferenceEquals(_rewriteSession, session);

    private static async Task DisposeAfterAsync(Task pending, IRewriteSession session)
    {
        try
        {
            // Without this the continuation posts back to the dispatcher the detach came from.
            await pending.ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>Clears the dry run so the Execute affordance disappears the moment the inputs stop matching it.</summary>
    private void InvalidateRewritePreview()
    {
        if (!RewritePreviewAvailable && RewriteFacts.Count == 0) return;
        RewritePreviewAvailable = false;
        ClearRewriteReport();
        RewriteConfirmInput = "";
        RewriteConfirmMessage = "";
        if (RewriteStep == RewriteWizardStep.Confirm)
            RewriteStep = RewriteWizardStep.Preview;
    }

    /// <summary>
    /// Drops the report and every row derived from it. A report describes one history at one
    /// moment; the moment it stops describing the repository on screen it has to leave the
    /// screen, because the reader has no way to tell a stale verification from a live one.
    /// </summary>
    private void ClearRewriteReport()
    {
        _rewriteReport = null;
        RewriteHasReport = false;
        RewriteFacts = [];
        RewriteScrubLines = [];
        RewriteSkipLines = [];
        RewriteOverallVerdict = null;
    }

    private string RepoDisplayName()
    {
        var name = Project?.DirectoryName ?? "";
        return name.Length > 0 ? name : System.IO.Path.GetFileName(RepoPath.TrimEnd('\\', '/'));
    }

    // ── Navigation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Why the surface will not move on from the operation step, or empty when it will. Live, so
    /// a half-typed size says what is wrong with it before the reader reaches for Next.
    /// </summary>
    [ObservableProperty] private string _rewriteNextBlockedReason = "";

    /// <summary>
    /// The operation step's inputs are the only ones Next validates: the scope step's choices are
    /// all runnable, and the preview step's Next is governed by the held dry run instead.
    /// </summary>
    private bool CanRewriteNext() =>
        RewriteStep != RewriteWizardStep.Operation || DescribeOperationProblem() is null;

    private void RefreshRewriteInputValidity()
    {
        RewriteNextBlockedReason = RewriteStep == RewriteWizardStep.Operation
            ? DescribeOperationProblem() ?? ""
            : "";
        RewriteNextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRewriteNext))]
    private async Task RewriteNext()
    {
        switch (RewriteStep)
        {
            case RewriteWizardStep.Operation:
                if (DescribeOperationProblem() is { } problem)
                {
                    RewriteErrorText = problem;
                    return;
                }
                RewriteErrorText = "";
                RewriteStep = RewriteWizardStep.Scope;
                break;
            case RewriteWizardStep.Scope:
                RewriteStep = RewriteWizardStep.Preview;
                await RunRewritePreview();
                break;
            case RewriteWizardStep.Preview:
                // The confirm step, and with it the only Execute affordance, is unreachable
                // until a dry run for these exact inputs succeeded.
                if (!RewritePreviewAvailable)
                {
                    RewriteErrorText = "Run the dry run before continuing.";
                    return;
                }
                RewriteConfirmMessage = BuildConfirmMessage();
                RewriteStep = RewriteWizardStep.Confirm;
                break;
        }
    }

    [RelayCommand]
    private void RewriteBack()
    {
        if (RewriteRunning) return;
        RewriteErrorText = "";
        RewriteStep = RewriteStep switch
        {
            RewriteWizardStep.Scope => RewriteWizardStep.Operation,
            RewriteWizardStep.Preview => RewriteWizardStep.Scope,
            RewriteWizardStep.Confirm => RewriteWizardStep.Preview,
            _ => RewriteStep,
        };
    }

    [RelayCommand]
    private void AddRewriteScopeCommit()
    {
        var draft = RewriteScopeCommitDraft.Trim();
        var value = draft.Length > 0 ? draft : RewriteScopePickedCommit?.ShortHash ?? "";
        if (value.Length == 0) return;
        if (!RewriteScopeCommits.Contains(value))
            RewriteScopeCommits.Add(value);
        RewriteScopeCommitDraft = "";
        InvalidateRewritePreview();
    }

    [RelayCommand]
    private void RemoveRewriteScopeCommit(string? commit)
    {
        if (commit is null) return;
        RewriteScopeCommits.Remove(commit);
        InvalidateRewritePreview();
    }

    // ── Step 3: the mandatory dry run ───────────────────────────────────────────

    [RelayCommand]
    private async Task RunRewritePreview()
    {
        var factory = _rewriteSessions;
        if (factory is null)
        {
            RewriteErrorText = "History rewriting is unavailable — the rewrite engine was not configured for this session.";
            return;
        }

        // Ahead of the supersede below, not only inside the step: replacing the held session
        // while its step is still running strands that step's busy gate, because the step then
        // finds itself neither the live wizard's nor any parked lane's.
        if (RefuseRewriteStepWhileBusy()) return;

        RewriteRequest request;
        try
        {
            request = BuildRewriteRequest();
        }
        catch (Exception ex)
        {
            RewriteErrorText = ex.Message;
            return;
        }

        // Off-thread for the same reason a detach is: the superseded session's disposal
        // deletes its scratch tree, with a sleeping retry while handles are still held.
        if (_rewriteSession is { } superseded)
            RewriteSessionDisposal = Task.Run(superseded.Dispose);
        var session = factory.Create();
        _rewriteSession = session;

        // The dry run only reads the source and writes a scratch tree, so it is cancellable end
        // to end with no safe point to respect.
        await RunRewriteStepAsync("Dry run", session, writesRepo: false, "Cancel the dry run", async (ct, _) =>
        {
            var outcome = await session.PreviewAsync(request, ct);
            // A dry run is never parked — it holds no backup — so a session that has left the
            // live wizard has been disposed, and its report describes a scratch tree that is gone.
            if (!OwnsLiveWizard(session)) return;

            if (outcome.Report is null)
            {
                // A re-run can be refused with the inputs untouched, so an earlier run's report
                // and the Execute it armed can outlive the refusal that contradicts them.
                InvalidateRewritePreview();
                RewriteErrorText = RewriteScrubVerdict.DescribeRefusal(outcome.FailureReason);
                RewriteStatusText = "Dry run refused — nothing was changed.";
                return;
            }

            ShowReport(outcome.Report);
            RewritePreviewAvailable = true;
            RewriteStatusText = "Dry run complete. Nothing has been changed yet.";
        });
    }

    private void ShowReport(RewriteReport report)
    {
        _rewriteReport = report;
        RewriteHasReport = true;
        RewriteFacts = new ObservableCollection<RewriteFact>(RewriteReportFacts.For(report));
        RewriteScrubLines = new ObservableCollection<ScrubVerdictLine>(
            report.ScrubChecks.Select(c => RewriteScrubVerdict.Describe(c, report.BinarySkips)));
        RewriteSkipLines = new ObservableCollection<string>(RewriteReportFacts.SkipLines(report));
        RewriteOverallVerdict = RewriteScrubVerdict.Overall(report);
    }

    // ── Step 4: typed confirmation ──────────────────────────────────────────────

    /// <summary>
    /// The phrase is required for every rewrite, not only whole-history ones: a commit-scoped
    /// edit still propagates into every descendant that does not re-touch the path, and every
    /// rewrite makes the local history diverge from the remote.
    /// </summary>
    internal bool RewriteConfirmSatisfied =>
        RewriteConfirmPhrase.Length > 0
        && string.Equals(RewriteConfirmInput.Trim(), RewriteConfirmPhrase, StringComparison.Ordinal);

    private bool CanExecuteRewrite() => RewritePreviewAvailable && RewriteConfirmSatisfied && !RewriteRunning;

    /// <summary>
    /// The normalization the export performs whatever the reader chose — a non-UTF-8 commit
    /// message re-encoded, a tag signature stripped — as its own paragraph on the confirm screen.
    /// Empty when the dry run found nothing to normalize.
    /// </summary>
    private string BuildNormalizationParagraph()
    {
        if (_rewriteReport?.Normalization is not { Any: true } scan) return "";
        return "Before any operation runs, the export normalizes this repository:\n" +
               string.Join("\n", RewriteReportFacts.NormalizationLines(scan).Select(l => "• " + l)) +
               "\n\n";
    }

    private string BuildConfirmMessage()
    {
        var name = RewriteConfirmPhrase;
        var commits = (_rewriteReport?.CommitMap.Count ?? 0).ToString("N0");
        return
            $"{BuildOperationSummary()}\n" +
            $"Applies to: {DescribeScope()}\n\n" +
            BuildNormalizationParagraph() +
            $"This rewrites {commits} commit(s) in {name}. Every rewritten commit gets a new hash, so this " +
            "local history stops matching the remote until you force-push it yourself — Project Dashboard never pushes.\n\n" +
            "A verified backup bundle is written first, and Undo restores the exact refs this repository had before the " +
            "rewrite. The Undo button lives on the result screen and goes away when you close this wizard; the bundle stays " +
            "on disk afterwards, but nothing in this app restores it — that is a manual git job.\n\n" +
            $"Type {name} below to confirm.";
    }

    [RelayCommand(CanExecute = nameof(CanExecuteRewrite))]
    private async Task ExecuteRewrite()
    {
        // Re-checked here, not only through CanExecute: the affordance is the guard a reader
        // sees, this is the guard that actually holds.
        if (!CanExecuteRewrite()) return;
        var session = _rewriteSession;
        if (session is null)
        {
            RewriteErrorText = "The dry run is no longer held — run it again before executing.";
            RewritePreviewAvailable = false;
            return;
        }

        RewriteStep = RewriteWizardStep.Running;
        var cancelled = false;
        await RunRewriteStepAsync("Rewrite", session, writesRepo: true, "Cancel the rewrite", async (ct, phase) =>
        {
            var result = await session.ExecuteAsync(ct, phase);
            cancelled = result.Cancelled;
            if (!OwnsLiveWizard(session))
            {
                // The reader left mid-rewrite. The outcome — and the undo it carries — belongs
                // to the repository it ran against, so it waits on that repository's lane.
                if (ParkedLaneFor(session) is { } lane) ApplyExecuteResult(lane, result, session);
                return;
            }

            RewriteResultSucceeded = result.Success;
            RewriteUndoAvailable = session.CanUndo;

            if (result.Cancelled)
            {
                // The dry run and its report survive: nothing was applied, so what the report
                // describes is still exactly what a re-run would do.
                RewriteErrorText = "";
                RewriteStatusText = CancelledRewriteStatus;
                return;
            }

            if (result.Success)
            {
                if (result.Report is not null)
                    ShowReport(result.Report);
                RewriteErrorText = "";
                // A cancel that lost the race to the point of no return must not leave the reader
                // believing it landed; the outcome is what happened, not what was asked for.
                RewriteStatusText = ct.IsCancellationRequested
                    ? "History rewritten — the cancel arrived after the swap could no longer be stopped. " +
                      "The remote still holds the old history."
                    : "History rewritten. The remote still holds the old history.";
                await ReloadCommitsAsync();
                await SafeRefreshWorkingStateAsync();
            }
            else
            {
                // A failure after the backup hands back the dry run's report, which describes
                // history that was never applied. Beside a failure banner it would read as a
                // description of this repository, whose content the rewrite did not touch.
                ClearRewriteReport();
                RewriteErrorText = RewriteScrubVerdict.DescribeRefusal(result.FailureReason);
                RewriteStatusText = "The rewrite did not complete.";
            }
        });

        if (OwnsLiveWizard(session))
        {
            if (cancelled)
            {
                // Back to the step the Execute was issued from, with the held dry run intact:
                // the reader can run it again without paying for another export.
                RewriteStep = RewriteWizardStep.Confirm;
                return;
            }
            RewritePreviewAvailable = false; // the held bare is spent; a further run needs a fresh dry run
            RewriteStep = RewriteWizardStep.Result;
        }
    }

    /// <summary>
    /// What a cancelled rewrite is allowed to claim. The swap refuses cancellation once its ref
    /// transaction can begin, so a cancelled outcome means no ref, commit, or file moved.
    /// </summary>
    internal const string CancelledRewriteStatus =
        "Cancelled — nothing was changed. No commit, ref, or file in this repository was touched.";

    /// <summary>
    /// The same outcome the live result screen would show, written onto a parked lane. A failed
    /// run drops the report for the reason the live path does: it describes history that was
    /// never applied, and beside a failure banner it reads as a description of the repository.
    /// </summary>
    private static void ApplyExecuteResult(ParkedRewrite lane, RewriteExecutionResult result, IRewriteSession session)
    {
        lane.Succeeded = result.Success;
        lane.UndoAvailable = session.CanUndo;
        if (result.Cancelled)
        {
            lane.ErrorText = "";
            lane.StatusText = CancelledRewriteStatus;
        }
        else if (result.Success)
        {
            lane.Report = result.Report ?? lane.Report;
            lane.ErrorText = "";
            lane.StatusText = "History rewritten. The remote still holds the old history.";
        }
        else
        {
            lane.Report = null;
            lane.ErrorText = RewriteScrubVerdict.DescribeRefusal(result.FailureReason);
            lane.StatusText = "The rewrite did not complete.";
        }
    }

    // ── Undo ────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task UndoRewrite()
    {
        var session = _rewriteSession;
        if (session is null || !session.CanUndo || RewriteRunning) return;
        if (IsBusy)
        {
            RewriteStatusText = "Another operation is running on this repository — wait for it to finish, then undo.";
            return;
        }

        var confirmed = await ConfirmPrompt(
            "Restore the pre-rewrite backup?",
            "The restore ends in `git reset --hard`, so any uncommitted change in this working copy is " +
            "discarded — the backup captured committed history only.\n\n" +
            "Refs and HEAD return to exactly where they were before the rewrite.",
            "Restore");
        if (!confirmed) return;

        // No cancel label: the restore's own ref reconciliation is all-or-nothing, so there is no
        // safe point between its start and its end at which stopping would leave a known state.
        await RunRewriteStepAsync("Undo", session, writesRepo: true, cancelLabel: null, async (_, _) =>
        {
            RestoreResult restore;
            try
            {
                restore = await session.UndoAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Handled here rather than by the step's own catch: that one renders a rewrite
                // refusal, whose guidance ends in "Nothing was changed" — a claim a throw from
                // either side of the restore's ref transaction cannot support.
                Log.Warn($"Undo threw for {RepoPath}", ex);
                if (!OwnsLiveWizard(session))
                {
                    if (ParkedLaneFor(session) is { } threwLane)
                    {
                        threwLane.Report = null;
                        threwLane.Succeeded = false;
                        threwLane.UndoText = "Undo failed before it could report where it stopped, so the refs may be " +
                                             $"pre-rewrite, rewritten, or partly restored. {ex.Message}";
                        threwLane.StatusText = "Undo failed. Check this repository's refs before running anything else against it.";
                    }
                    return;
                }
                ClearRewriteReport();
                RewriteResultSucceeded = false;
                RewriteUndoText = "Undo failed before it could report where it stopped, so the refs may be " +
                                  $"pre-rewrite, rewritten, or partly restored. {ex.Message}";
                RewriteStatusText = "Undo failed. Check this repository's refs before running anything else against it.";
                await ReloadCommitsAsync();
                await SafeRefreshWorkingStateAsync();
                return;
            }
            if (!OwnsLiveWizard(session))
            {
                if (ParkedLaneFor(session) is { } lane)
                {
                    lane.Report = null;
                    lane.Succeeded = false;
                    if (restore.Success) lane.UndoAvailable = false;
                    lane.UndoText = DescribeRestore(restore);
                    lane.StatusText = restore.Success
                        ? "History restored. The rewrite was undone."
                        : restore.RefsRestored
                            ? "Undo did not finish: the pre-rewrite refs are back, but the working tree was not reset to them."
                            : "Undo failed. The repository was left as the rewrite made it.";
                }
                return;
            }

            // Retired before the outcome is read, for every outcome: a restore that put the
            // refs back makes the executed run's verification describe discarded history, and
            // an unsuccessful restore cannot tell that case from one that moved nothing.
            ClearRewriteReport();
            RewriteUndoText = DescribeRestore(restore);
            if (restore.Success)
            {
                RewriteUndoAvailable = false;
                RewriteStatusText = "History restored. The rewrite was undone.";
            }
            else
            {
                RewriteStatusText = restore.RefsRestored
                    ? "Undo did not finish: the pre-rewrite refs are back, but the working tree was not reset to them."
                    : "Undo failed. The repository was left as the rewrite made it.";
            }
            // The banner styles the rewrite's own outcome; once an undo has been attempted the
            // rewrite's history is no longer what this repository is known to hold.
            RewriteResultSucceeded = false;
            if (restore.Success || restore.RefsRestored)
            {
                await ReloadCommitsAsync();
                await SafeRefreshWorkingStateAsync();
            }
        });

        // An undo that was parked and restored comes back on the Running step, and nothing else
        // moves the wizard off it; without this the spinner is the last thing the surface shows
        // and the text naming what the reset discarded is never rendered.
        if (OwnsLiveWizard(session))
            RewriteStep = RewriteWizardStep.Result;
    }

    /// <summary>
    /// Names what the restore's reset threw away; a clean tree is stated too, so silence never
    /// stands in for it. "Not changed" is claimed only for a failure that never reached the ref
    /// transaction, because a later failure leaves the pre-rewrite refs in place.
    /// </summary>
    internal static string DescribeRestore(RestoreResult restore)
    {
        if (!restore.Success)
            return restore.RefsRestored
                ? "Restore did not finish — the pre-rewrite refs are back, but the working tree was " +
                  $"not reset to them. {restore.Message}"
                : $"Restore failed — the repository was not changed. {restore.Message}";
        var discarded = restore.WorktreeWasDirty
            ? $"The working tree was dirty: {restore.DiscardedChangeCount} uncommitted change(s) were discarded by the reset."
            : "The working tree was clean, so no uncommitted work was discarded.";
        return $"History restored to its pre-rewrite state. {discarded} {restore.Message}".TrimEnd();
    }

    // ── Request construction ────────────────────────────────────────────────────

    /// <summary>The first thing wrong with the chosen operation's inputs, or null when it is runnable.</summary>
    private string? DescribeOperationProblem() => RewriteOperation switch
    {
        RewriteOperationKind.ReplaceText when RewriteFindText.Length == 0 =>
            "Enter the text to find.",
        RewriteOperationKind.PurgePath when SplitList(RewritePurgePathsText).Count == 0 && RewritePurgeMinSizeText.Trim().Length == 0 =>
            "Enter at least one path to purge, or a minimum blob size.",
        RewriteOperationKind.PurgePath when RewritePurgeMinSizeText.Trim().Length > 0 && ParseMinSize() is null =>
            ByteSizeText.ProblemWith(RewritePurgeMinSizeText),
        RewriteOperationKind.RewriteMessages when RewriteMessageFindText.Length == 0 =>
            "Enter the message text to find.",
        RewriteOperationKind.RewriteIdentity when RewriteOldName.Trim().Length == 0 && RewriteOldEmail.Trim().Length == 0 =>
            "Enter the name or the email address to match.",
        RewriteOperationKind.RewriteIdentity when RewriteNewName.Trim().Length == 0 && RewriteNewEmail.Trim().Length == 0 =>
            "Enter the replacement name or email address.",
        _ => null,
    };

    private long? ParseMinSize() => ByteSizeText.TryParse(RewritePurgeMinSizeText, out var size) ? size : null;

    /// <summary>
    /// What the typed size resolved to, or why it did not. Rendered beside the field so the byte
    /// count the engine will actually compare against is never left to be inferred from a unit.
    /// </summary>
    [ObservableProperty] private string _rewritePurgeMinSizeEcho = "";

    partial void OnRewritePurgeMinSizeTextChanged(string value) => UpdateMinSizeEcho();

    private void UpdateMinSizeEcho() =>
        RewritePurgeMinSizeEcho = RewritePurgeMinSizeText.Trim().Length == 0
            ? ""
            : ByteSizeText.TryParse(RewritePurgeMinSizeText, out var size)
                ? $"= {size:N0} bytes"
                : ByteSizeText.ProblemWith(RewritePurgeMinSizeText);

    internal RewriteRequest BuildRewriteRequest()
    {
        var repo = RepoPath;
        if (repo.Length == 0)
            throw new InvalidOperationException("No repository is open.");
        if (DescribeOperationProblem() is { } problem)
            throw new InvalidOperationException(problem);

        var operation = RewriteOperation;
        var options = new RewriteOptions
        {
            ContentOps = operation == RewriteOperationKind.ReplaceText
                ? [TextOp(RewriteFindText, RewriteReplacementText, RewriteUseRegex)]
                : [],
            MessageOps = operation == RewriteOperationKind.RewriteMessages
                ? [TextOp(RewriteMessageFindText, RewriteMessageReplacementText, RewriteMessageUseRegex)]
                : [],
            IdentityMappings = operation == RewriteOperationKind.RewriteIdentity
                ? [new IdentityMapping
                    {
                        OldName = NullIfEmpty(RewriteOldName),
                        OldEmail = NullIfEmpty(RewriteOldEmail),
                        NewName = NullIfEmpty(RewriteNewName),
                        NewEmail = NullIfEmpty(RewriteNewEmail),
                    }]
                : [],
            Purge = operation == RewriteOperationKind.PurgePath ? BuildPurge() : null,
            FileScope = BuildFileScope(),
            CommitScope = BuildCommitScope(),
        };

        // Refused here, before any export work, so a bad pattern or an empty scope costs nothing.
        options.Validate();
        return new RewriteRequest { RepoPath = repo, Options = options };
    }

    private PurgeSpec BuildPurge()
    {
        var entries = SplitList(RewritePurgePathsText);
        return new PurgeSpec
        {
            Paths = entries.Count > 0 ? PathScope(entries) : null,
            MinBlobSize = ParseMinSize(),
        };
    }

    private FileScope BuildFileScope() => RewriteScopeSelection switch
    {
        RewriteScopeChoice.Globs => new GlobScope { Patterns = SplitList(RewriteScopeGlobsText) },
        RewriteScopeChoice.ExplicitPaths => new ExplicitPathsScope { Paths = SplitList(RewriteScopePathsText) },
        _ => new AllFilesScope(),
    };

    private CommitScope BuildCommitScope() => RewriteScopeSelection switch
    {
        RewriteScopeChoice.ExplicitCommits => new ExplicitCommitsScope { Commits = RewriteScopeCommits.ToList() },
        RewriteScopeChoice.CommitRange => new CommitRangeScope
        {
            FromRef = NullIfEmpty(RewriteRangeFromText),
            ToRef = RewriteRangeToText.Trim(),
        },
        _ => new AllHistoryScope(),
    };

    /// <summary>
    /// An entry carrying no wildcard is taken as a path and its subtree, which is what a
    /// reader typing a folder name means; a wildcard anywhere switches the whole list to globs.
    /// </summary>
    private static FileScope PathScope(IReadOnlyList<string> entries) =>
        entries.Any(e => e.Contains('*') || e.Contains('?'))
            ? new GlobScope { Patterns = entries }
            : new ExplicitPathsScope { Paths = entries };

    private static ContentOp TextOp(string find, string replacement, bool useRegex) => useRegex
        ? new RegexReplace { Pattern = find, Replacement = replacement }
        : new LiteralReplace { Find = Encoding.UTF8.GetBytes(find), Replace = Encoding.UTF8.GetBytes(replacement) };

    private static string? NullIfEmpty(string value) => value.Trim().Length == 0 ? null : value.Trim();

    /// <summary>Comma- or newline-separated entries, trimmed, empties dropped.</summary>
    internal static List<string> SplitList(string text) =>
        text.Replace("\r", "").Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    internal string BuildOperationSummary() => RewriteOperation switch
    {
        RewriteOperationKind.ReplaceText =>
            $"Replace {(RewriteUseRegex ? "regex" : "text")} “{RewriteFindText}” with " +
            (RewriteReplacementText.Length == 0 ? "nothing (deletes it)" : $"“{RewriteReplacementText}”") +
            " in file contents.",
        RewriteOperationKind.PurgePath =>
            "Remove from history: " + DescribePurgeTargets() + ".",
        RewriteOperationKind.RewriteMessages =>
            $"Replace {(RewriteMessageUseRegex ? "regex" : "text")} “{RewriteMessageFindText}” with " +
            (RewriteMessageReplacementText.Length == 0 ? "nothing (deletes it)" : $"“{RewriteMessageReplacementText}”") +
            " in commit and tag messages.",
        _ =>
            $"Rewrite the identity matching {DescribeIdentityMatch()} to {DescribeIdentityReplacement()}.",
    };

    private string DescribePurgeTargets()
    {
        var parts = new List<string>();
        var entries = SplitList(RewritePurgePathsText);
        if (entries.Count > 0) parts.Add(string.Join(", ", entries));
        if (ParseMinSize() is { } size) parts.Add($"every file of {size:N0} bytes or more");
        return string.Join("; ", parts);
    }

    private string DescribeIdentityMatch()
    {
        var parts = new List<string>();
        if (NullIfEmpty(RewriteOldName) is { } n) parts.Add($"name “{n}”");
        if (NullIfEmpty(RewriteOldEmail) is { } e) parts.Add($"email “{e}”");
        return string.Join(" and ", parts);
    }

    private string DescribeIdentityReplacement()
    {
        var parts = new List<string>();
        if (NullIfEmpty(RewriteNewName) is { } n) parts.Add($"name “{n}”");
        if (NullIfEmpty(RewriteNewEmail) is { } e) parts.Add($"email “{e}”");
        return string.Join(" and ", parts);
    }

    /// <summary>
    /// The scope in the reader's own words. Message and identity operations honour only the
    /// commit axis, so a file scope chosen alongside them is named as the no-op it is.
    /// </summary>
    internal string DescribeScope()
    {
        var operation = RewriteOperation;
        var messageOrIdentity = operation is RewriteOperationKind.RewriteMessages or RewriteOperationKind.RewriteIdentity;
        return RewriteScopeSelection switch
        {
            RewriteScopeChoice.Globs => messageOrIdentity
                ? "all history — file patterns do not restrict message or identity rewrites"
                : $"files matching {string.Join(", ", SplitList(RewriteScopeGlobsText))}, across all history",
            RewriteScopeChoice.ExplicitPaths => messageOrIdentity
                ? "all history — file paths do not restrict message or identity rewrites"
                : $"the paths {string.Join(", ", SplitList(RewriteScopePathsText))}, across all history",
            RewriteScopeChoice.ExplicitCommits =>
                $"the {RewriteScopeCommits.Count} selected commit(s): {string.Join(", ", RewriteScopeCommits)}",
            RewriteScopeChoice.CommitRange =>
                $"the commit range {(RewriteRangeFromText.Trim().Length == 0 ? "(root)" : RewriteRangeFromText.Trim())}..{RewriteRangeToText.Trim()}",
            _ => "every commit and every file in this repository",
        };
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one wizard step for <paramref name="session"/> under the page's busy gate. The body
    /// must consult <see cref="OwnsLiveWizard"/> before writing UI state: a rewrite outlives a
    /// project switch, and its result belongs to the repository it ran against. A step whose
    /// session is parked releases the gate on the parked lane instead, so returning to that
    /// repository finds a finished rewrite finished rather than permanently busy.
    /// </summary>
    private async Task RunRewriteStepAsync(
        string label, IRewriteSession session, bool writesRepo, string? cancelLabel,
        Func<CancellationToken, IProgress<RewritePhase>, Task> body)
    {
        if (RefuseRewriteStepWhileBusy()) return;
        var repo = RepoPath;
        var gate = new RewriteStepGate(session, writesRepo);
        IsBusy = true;
        RewriteRunning = true;
        _busyGateHolder = gate;
        RewriteCancelLabel = cancelLabel ?? "";
        RewriteCanCancel = cancelLabel is not null;
        RewriteStatusText = $"{label}…";
        RewriteErrorText = "";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rewriteStepInFlight = done.Task;
        try
        {
            await body(gate.Cancellation.Token, new RewritePhaseReporter(p => OnRewritePhase(gate, session, p)));
        }
        catch (OperationCanceledException) when (gate.Cancellation.IsCancellationRequested)
        {
            // Only steps that throw on cancel land here — the execute reports its cancellation
            // as an outcome instead. Nothing was applied either way, which is why this states it.
            if (OwnsLiveWizard(session))
            {
                InvalidateRewritePreview();
                RewriteErrorText = "";
                RewriteStatusText = $"{label} cancelled — nothing was changed.";
            }
            else if (ParkedLaneFor(session) is { } cancelledLane)
            {
                cancelledLane.Report = null;
                cancelledLane.Succeeded = false;
                cancelledLane.ErrorText = "";
                cancelledLane.StatusText = $"{label} cancelled — nothing was changed.";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} failed for {repo}", ex);
            if (OwnsLiveWizard(session))
            {
                // Where the throw left the step is unknown, so nothing derived from a report
                // may stay on screen and no Execute may stay armed behind the failure.
                InvalidateRewritePreview();
                RewriteErrorText = RewriteScrubVerdict.DescribeRefusal(ex.Message);
                RewriteStatusText = $"{label} failed.";
            }
            else if (ParkedLaneFor(session) is { } lane)
            {
                lane.Report = null;
                lane.Succeeded = false;
                lane.ErrorText = RewriteScrubVerdict.DescribeRefusal(ex.Message);
                lane.StatusText = $"{label} failed.";
            }
        }
        finally
        {
            // Closed before the gate is released, so a cancel arriving between the two finds an
            // offer already withdrawn rather than a disposed source.
            gate.CancelClosed = true;
            if (ReferenceEquals(_busyGateHolder, gate))
            {
                _busyGateHolder = null;
                IsBusy = false;
                RewriteRunning = false;
                RewriteCanCancel = false;
                RewriteCancelLabel = "";
            }
            if (!OwnsLiveWizard(session) && ParkedLaneFor(session) is { } lane)
            {
                lane.Running = false;
                lane.CanCancel = false;
                lane.CancelLabel = "";
            }
            gate.Cancellation.Dispose();
            done.SetResult();
        }
    }

    /// <summary>
    /// Stops the step in flight, if it is still stoppable. The cancel reaches only the step this
    /// wizard's own session raised the gate for: another repository's parked rewrite holds its
    /// own source, and a step that has passed the swap's point of no return holds none that
    /// would be honoured.
    /// </summary>
    [RelayCommand]
    private void CancelRewriteStep()
    {
        var session = _rewriteSession;
        if (session is null) return;
        if (_busyGateHolder is not RewriteStepGate gate || !ReferenceEquals(gate.Session, session)) return;
        if (gate.CancelClosed)
        {
            RewriteCanCancel = false;
            RewriteStatusText = RewriteApplyingNotice;
            return;
        }
        RewriteCanCancel = false;
        RewriteStatusText = "Cancelling — waiting for the current step to stop…";
        gate.Cancellation.Cancel();
    }

    /// <summary>
    /// Carries a rewrite's phase back to the thread the step was started from — the swap reports
    /// from whatever pool thread its git call resumed on, and every handler here writes bound
    /// state. <see cref="Progress{T}"/> posts unconditionally, which would leave a report made on
    /// the captured context itself queued behind the very step that is waiting to observe it; a
    /// report already on that context therefore runs inline.
    /// </summary>
    private sealed class RewritePhaseReporter(Action<RewritePhase> handler) : IProgress<RewritePhase>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(RewritePhase value)
        {
            if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context)) handler(value);
            else _context.Post(_ => handler(value), null);
        }
    }

    /// <summary>
    /// Withdraws the cancel offer the instant the swap stops honouring it. The gate is closed
    /// too, so a click that raced the report is refused by the step rather than dropped.
    /// </summary>
    private void OnRewritePhase(RewriteStepGate gate, IRewriteSession session, RewritePhase phase)
    {
        if (phase != RewritePhase.Applying) return;
        gate.CancelClosed = true;
        if (OwnsLiveWizard(session))
        {
            RewriteCanCancel = false;
            RewriteStatusText = RewriteApplyingNotice;
        }
        else if (ParkedLaneFor(session) is { } lane)
        {
            lane.CanCancel = false;
            lane.StatusText = RewriteApplyingNotice;
        }
    }

    /// <summary>
    /// True when a step must not start because the page already has an operation in flight.
    /// Says so on the surface: the refusal is the only feedback a click that reaches here gets.
    /// </summary>
    private bool RefuseRewriteStepWhileBusy()
    {
        if (!IsBusy) return false;
        RewriteStatusText = "Another operation is running on this repository — wait for it to finish.";
        return true;
    }
}

/// <summary>
/// A byte count typed the way sizes are read: a plain number of bytes, or a decimal with a
/// KB/MB/GB unit (KiB/MiB/GiB accepted as the same thing). Units are binary — 1 KB is 1024
/// bytes — which is what a repository's object sizes are reported in, and the parsed byte count
/// is echoed back rather than left implied. Parsing is invariant-culture so a decimal point
/// means the same thing on every machine.
/// </summary>
internal static class ByteSizeText
{
    private const long Kilo = 1024L;

    private static readonly (string Suffix, long Multiplier)[] Units =
    [
        // Longest first: "kib" must not match as "k" with "ib" left over.
        ("kib", Kilo), ("mib", Kilo * Kilo), ("gib", Kilo * Kilo * Kilo),
        ("kb", Kilo), ("mb", Kilo * Kilo), ("gb", Kilo * Kilo * Kilo),
        ("k", Kilo), ("m", Kilo * Kilo), ("g", Kilo * Kilo * Kilo),
        ("b", 1L),
    ];

    /// <summary>The byte count the text names, or false when it names none. Zero and negatives are not sizes and are rejected.</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return false;

        var unit = 1L;
        foreach (var (suffix, multiplier) in Units)
        {
            if (trimmed.Length <= suffix.Length ||
                !trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            unit = multiplier;
            trimmed = trimmed[..^suffix.Length].TrimEnd();
            break;
        }

        if (!decimal.TryParse(trimmed, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;

        // Scaling a value this large overflows decimal, and the multiply throws rather than
        // returning a number the byte-count guard below could reject. The field binds on every
        // keystroke, so the throw would repeat out of the setter and every command requery.
        if (value > decimal.MaxValue / unit) return false;

        // Rounded up: a size that lands between bytes must not silently include a file smaller
        // than the one the reader named.
        var scaled = decimal.Ceiling(value * unit);
        if (scaled > long.MaxValue) return false;
        bytes = (long)scaled;
        return bytes > 0;
    }

    /// <summary>Why <paramref name="text"/> is not a size, phrased for the field it was typed into.</summary>
    public static string ProblemWith(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return "Enter a size, for example 500 KB.";
        return TryParse(trimmed, out _)
            ? ""
            : $"“{trimmed}” is not a size. Enter a number of bytes, or a size with a unit — 900, 500 KB, 1.5 MB, 2 GB.";
    }
}
