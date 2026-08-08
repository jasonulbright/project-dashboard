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
/// <see cref="RewriteCoordinator"/> directly because the coordinator's preview handle and
/// undo handle have internal constructors, so no test could supply them.
/// </summary>
public interface IRewriteSession : IDisposable
{
    /// <summary>True once an execution took a backup that can still be restored.</summary>
    bool CanUndo { get; }

    /// <summary>Runs the engine without touching the repository and keeps the result for <see cref="ExecuteAsync"/>.</summary>
    Task<RewritePreviewOutcome> PreviewAsync(RewriteRequest request, CancellationToken ct = default);

    /// <summary>Applies the history proved by the last successful preview. Fails when no preview is held.</summary>
    Task<RewriteExecutionResult> ExecuteAsync(CancellationToken ct = default);

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

    public async Task<RewriteExecutionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var preview = _preview;
        if (preview is null)
            return new RewriteExecutionResult { Success = false, FailureReason = "no preview has been run for this rewrite" };
        var result = await coordinator.ExecuteAsync(preview, ct);
        _undo ??= result.Undo;
        // The handle is spent either way — the wizard requires a fresh dry run before any
        // further execute — so holding it only keeps its scratch tree alive.
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
    /// The session whose step raised the page's busy gate, or null while no step holds it. Only
    /// that session's step may lower the gate: a step whose session has left both the live
    /// wizard and the parked lanes still owns the gate it took, and a step that never took the
    /// gate must not reopen it under whichever run holds it now.
    /// </summary>
    private IRewriteSession? _rewriteStepGateOwner;

    /// <summary>
    /// True while the step in flight is one that writes the repository. A dry run writes
    /// nothing and holds no backup, so leaving the page ends it; a rewrite or an undo is
    /// parked instead, because ending it would drop the only restore for what it replaced.
    /// </summary>
    private bool _rewriteStepWritesRepo;

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
        public required Task StepInFlight { get; set; }
        public bool Running { get; set; }
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
    internal Func<string, string, string, Task<bool>> ConfirmPrompt { get; set; } = ConfirmAsync;

    // ── Wizard shell ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _rewriteWizardVisible;

    /// <summary>
    /// Bound to the IsEnabled of every surface the wizard's scrim covers: the work-area tabs,
    /// the state banner, and the branch bar. The scrim stops the mouse only; without this, a
    /// keystroke and a screen reader still reach the discard, stage, branch-delete, and Pull
    /// controls behind it — and a Pull merges the un-rewritten remote history back in.
    /// </summary>
    public bool RewriteWizardHidden => !RewriteWizardVisible;

    partial void OnRewriteWizardVisibleChanged(bool value) => OnPropertyChanged(nameof(RewriteWizardHidden));
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

    partial void OnRewriteRunningChanged(bool value) => OnPropertyChanged(nameof(RewriteNotRunning));

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
            InvalidateRewritePreview();
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
        // A rewrite in flight owns the busy gate and a half-applied swap has no cancel here;
        // closing the surface would hide the only report of what happened.
        if (RewriteRunning)
        {
            RewriteStatusText = "The rewrite is still running — wait for it to finish.";
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
        if (!session.CanUndo && !(RewriteRunning && _rewriteStepWritesRepo)) return;

        _rewriteSession = null;
        _parkedRewrites[RepoKey.For(repo)] = new ParkedRewrite
        {
            Session = session,
            StepInFlight = _rewriteStepInFlight,
            Running = RewriteRunning,
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
        // The gate comes back with the step, so the step that raised it is the one entitled to
        // lower it; a step run for another repository while this one was parked took ownership.
        if (parked.Running) _rewriteStepGateOwner = parked.Session;
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

    [RelayCommand]
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

        await RunRewriteStepAsync("Dry run", session, writesRepo: false, async () =>
        {
            var outcome = await session.PreviewAsync(request);
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

    private string BuildConfirmMessage()
    {
        var name = RewriteConfirmPhrase;
        var commits = (_rewriteReport?.CommitMap.Count ?? 0).ToString("N0");
        return
            $"{BuildOperationSummary()}\n" +
            $"Applies to: {DescribeScope()}\n\n" +
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
        await RunRewriteStepAsync("Rewrite", session, writesRepo: true, async () =>
        {
            var result = await session.ExecuteAsync();
            if (!OwnsLiveWizard(session))
            {
                // The reader left mid-rewrite. The outcome — and the undo it carries — belongs
                // to the repository it ran against, so it waits on that repository's lane.
                if (ParkedLaneFor(session) is { } lane) ApplyExecuteResult(lane, result, session);
                return;
            }

            RewriteResultSucceeded = result.Success;
            RewriteUndoAvailable = session.CanUndo;

            if (result.Success)
            {
                if (result.Report is not null)
                    ShowReport(result.Report);
                RewriteErrorText = "";
                RewriteStatusText = "History rewritten. The remote still holds the old history.";
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
            RewritePreviewAvailable = false; // the held bare is spent; a further run needs a fresh dry run
            RewriteStep = RewriteWizardStep.Result;
        }
    }

    /// <summary>
    /// The same outcome the live result screen would show, written onto a parked lane. A failed
    /// run drops the report for the reason the live path does: it describes history that was
    /// never applied, and beside a failure banner it reads as a description of the repository.
    /// </summary>
    private static void ApplyExecuteResult(ParkedRewrite lane, RewriteExecutionResult result, IRewriteSession session)
    {
        lane.Succeeded = result.Success;
        lane.UndoAvailable = session.CanUndo;
        if (result.Success)
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

        await RunRewriteStepAsync("Undo", session, writesRepo: true, async () =>
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
            "The minimum blob size must be a whole number of bytes above zero.",
        RewriteOperationKind.RewriteMessages when RewriteMessageFindText.Length == 0 =>
            "Enter the message text to find.",
        RewriteOperationKind.RewriteIdentity when RewriteOldName.Trim().Length == 0 && RewriteOldEmail.Trim().Length == 0 =>
            "Enter the name or the email address to match.",
        RewriteOperationKind.RewriteIdentity when RewriteNewName.Trim().Length == 0 && RewriteNewEmail.Trim().Length == 0 =>
            "Enter the replacement name or email address.",
        _ => null,
    };

    private long? ParseMinSize() =>
        long.TryParse(RewritePurgeMinSizeText.Trim(), out var size) && size > 0 ? size : null;

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
    private async Task RunRewriteStepAsync(string label, IRewriteSession session, bool writesRepo, Func<Task> body)
    {
        if (RefuseRewriteStepWhileBusy()) return;
        var repo = RepoPath;
        IsBusy = true;
        RewriteRunning = true;
        _rewriteStepGateOwner = session;
        _rewriteStepWritesRepo = writesRepo;
        RewriteStatusText = $"{label}…";
        RewriteErrorText = "";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rewriteStepInFlight = done.Task;
        try
        {
            await body();
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
            if (ReferenceEquals(_rewriteStepGateOwner, session))
            {
                _rewriteStepGateOwner = null;
                IsBusy = false;
                RewriteRunning = false;
                _rewriteStepWritesRepo = false;
            }
            if (!OwnsLiveWizard(session) && ParkedLaneFor(session) is { } lane)
                lane.Running = false;
            done.SetResult();
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
