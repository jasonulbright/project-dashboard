using System.IO;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Rewrite;

/// <summary>
/// Orchestrates a gated history rewrite for a wizard to drive. <see cref="PreviewAsync"/>
/// runs the engine WITHOUT swapping, so the report can be shown before anything is
/// committed to. <see cref="ExecuteAsync(RewriteRequest, CancellationToken, IProgress{RewritePhase})"/>
/// runs the safety-railed pipeline: acquire the busy lease, refuse a dirty tree, take a verified
/// backup, journal the in-flight op, rewrite, then swap — each step refusable, and no
/// history reaches the repository without a restorable backup behind it. Nothing is ever
/// auto-pushed.
///
/// Cancellation is a safe-point contract: the whole pipeline is freely cancellable until the
/// swap's point of no return, and refused after it, so a cancelled run has changed nothing.
///
/// Sealed. Substitution happens at two seams, and every behaviour of this type is reachable
/// through them: a view model fakes the whole session through
/// <see cref="ViewModels.Pages.IRewriteSession"/>, and a service-level test overrides the
/// virtual <see cref="SwapService.ApplySwapAsync"/> or <see cref="GitService.RunAsync"/> to
/// fail or perturb one step while the real coordinator runs against a fixture repository.
/// </summary>
public sealed class RewriteCoordinator
{
    private readonly BackupService _backup;
    private readonly RepoBusyRegistry _busy;
    private readonly GitService _git;
    private readonly SwapService _swap;
    private readonly RewriteJournal _journal;
    private readonly OperationHistory _history;
    private readonly string _gitExe;
    private readonly string _workRoot;

    /// <summary>
    /// How long a scratch tree must have sat untouched before the sweep treats it as a leak.
    /// No rewrite state on the repository names its scratch tree, so the write time is the only
    /// liveness signal — and the root's write time stops advancing once work/ and target.git/
    /// exist, so a live rewrite in a second process looks idle for as long as it runs. This must
    /// therefore exceed everything one run can spend end to end: the backup bundle,
    /// <see cref="RewriteRequest.ExportTimeout"/>, <see cref="RewriteRequest.ImportTimeout"/>,
    /// the <see cref="HistoryRewriteRequest.VerificationTimeout"/> that fsck and each scrub grep
    /// get one at a time, and the swap's own fetch, fsck, and reset budgets. Below that total a
    /// sweep can delete the tree a running rewrite is writing into.
    /// </summary>
    private static readonly TimeSpan ScratchGrace = TimeSpan.FromDays(1);

    private static readonly TimeSpan RefTimeout = TimeSpan.FromSeconds(60);

    public RewriteCoordinator(
        BackupService backup,
        RepoBusyRegistry busy,
        GitService git,
        SwapService swap,
        RewriteJournal? journal = null,
        string? gitExecutable = null,
        string? workRoot = null,
        OperationHistory? history = null)
    {
        _backup = backup;
        _busy = busy;
        _git = git;
        _swap = swap;
        _journal = journal ?? new RewriteJournal();
        _history = history ?? new OperationHistory();
        _gitExe = gitExecutable ?? HistoryPipeline.ResolveGitExecutable();
        _workRoot = workRoot ?? Path.Combine(AppPaths.LocalDir, "rewrite-work");
        ScratchSweep = Task.Run(SweepStaleScratch);
    }

    /// <summary>
    /// The stale-scratch reclamation this instance started. Held so a caller — and a headless
    /// test — can wait for it instead of polling the work root, which is a wall-clock guess.
    /// </summary>
    internal Task ScratchSweep { get; }

    /// <summary>
    /// Reclaims scratch trees no disposal ever reached — a crash, a kill, or a process exit
    /// while a release was still queued on the pool.
    ///
    /// The GUID <see cref="NewScratch"/> gives every tree plus <see cref="ScratchGrace"/> — not
    /// when this sweep runs — is what keeps a tree a live session owns out of reach.
    /// </summary>
    private void SweepStaleScratch()
    {
        try
        {
            if (!Directory.Exists(_workRoot)) return;
            var cutoff = DateTime.UtcNow - ScratchGrace;
            foreach (var dir in Directory.GetDirectories(_workRoot))
            {
                if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                RewriteScratch.TryDeleteTree(dir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not sweep the rewrite scratch root {_workRoot}", ex);
        }
    }

    /// <summary>
    /// The dry run: rewrites into a temp bare and returns the report without touching
    /// the source. The returned handle keeps the temp bare for a later
    /// <see cref="ExecuteAsync(PreviewHandle, CancellationToken)"/>; dispose it to discard the
    /// scratch tree.
    /// </summary>
    public async Task<PreviewHandle> PreviewAsync(RewriteRequest request, CancellationToken ct = default)
    {
        request.Options.Validate();
        // Read before the export, so a commit landing during the export leaves the handle
        // describing a source that no longer matches and the execute refuses it.
        var sourceState = await ReadSourceStateAsync(request.RepoPath, ct)
            ?? throw new InvalidOperationException($"repository '{request.RepoPath}' could not be read by git");
        var scratch = NewScratch(request.RepoPath);
        try
        {
            var report = await RunEngineAsync(request, scratch, ct);
            return new PreviewHandle(request, report, scratch.WorkDir, scratch.TempBare, scratch.Dir, sourceState);
        }
        catch
        {
            RewriteScratch.TryDeleteTree(scratch.Dir);
            throw;
        }
    }

    /// <summary>Runs the full gated pipeline, rewriting fresh. Equivalent to previewing and executing in one call, without keeping the temp bare.</summary>
    public Task<RewriteExecutionResult> ExecuteAsync(
        RewriteRequest request, CancellationToken ct = default, IProgress<RewritePhase>? phase = null) =>
        ExecuteCoreAsync(request, preview: null, ct, phase);

    /// <summary>Runs the full gated pipeline reusing a preview's already-rewritten temp bare, so the engine is not run twice.</summary>
    public Task<RewriteExecutionResult> ExecuteAsync(
        PreviewHandle preview, CancellationToken ct = default, IProgress<RewritePhase>? phase = null) =>
        ExecuteCoreAsync(preview.Request, preview, ct, phase);

    /// <summary>
    /// Runs the pipeline and records the attempt. The record is written whatever the outcome —
    /// a refused run explains a button that appeared to do nothing — and a failed write never
    /// changes what the run reports.
    /// </summary>
    private async Task<RewriteExecutionResult> ExecuteCoreAsync(
        RewriteRequest request, PreviewHandle? preview, CancellationToken ct, IProgress<RewritePhase>? phase)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await RunPipelineAsync(request, preview, ct, phase);
        _history.Append(OperationRecord.For(
            request.RepoPath, OperationCategory.Rewrite, "History rewrite",
            result.Cancelled ? OperationOutcome.Cancelled
                : result.Success ? OperationOutcome.Succeeded
                : result.Refused ? OperationOutcome.Refused
                : result.FailureReason is null ? OperationOutcome.Unknown
                : OperationOutcome.Failed,
            result.FailureReason ?? (result.Cancelled ? "Cancelled before the swap; nothing was changed." : ""),
            started,
            backupStamp: result.Undo?.Backup.UtcStamp));
        return result;
    }

    /// <summary>
    /// Clears the recovery marker after the swap has already landed. A throw from this write is
    /// logged and swallowed: the history is rewritten either way, so letting it escape would
    /// report a completed rewrite as an exception and skip the record of it. The cost of the
    /// failed clear is a stale marker, which the next launch offers as an interrupted operation
    /// whose backup is intact.
    /// </summary>
    private async Task ClearJournalAfterSuccessAsync(string repo, CancellationToken ct)
    {
        try { await _journal.CompleteAsync(repo, ct); }
        catch (Exception ex) { Log.Warn($"could not clear the rewrite journal for {repo} after a successful swap", ex); }
    }

    private async Task<RewriteExecutionResult> RunPipelineAsync(
        RewriteRequest request, PreviewHandle? preview, CancellationToken ct, IProgress<RewritePhase>? phase)
    {
        request.Options.Validate();
        var repo = request.RepoPath;

        // 1. Busy gate: a second op on the same repo is refused, not queued.
        if (!_busy.TryAcquire(repo, out var lease))
            return RewriteExecutionResult.RefusedByGate($"repository is busy with another operation: {repo}");

        string? ownedScratch = null;
        UndoHandle? undo = null;
        var journalled = false;
        try
        {
            phase?.Report(RewritePhase.Preparing);

            // 1b. Staleness gate: a preview's bare was exported when the dry run ran and is
            // installed verbatim, so a ref that moved since is absent from it and the swap
            // would erase whatever landed. Read under the lease, before the backup, so the
            // refusal leaves the repository and the journal untouched.
            if (preview is not null)
            {
                var current = await ReadSourceStateAsync(repo, ct);
                if (current is null)
                    return RewriteExecutionResult.RefusedByGate($"repository '{repo}' could not be read by git");
                if (!string.Equals(current, preview.SourceState, StringComparison.Ordinal))
                    return RewriteExecutionResult.RefusedByGate(
                        $"'{repo}' changed after the dry run — the report describes history this repository no longer " +
                        "has, and applying it would discard whatever landed since. Run the dry run again.");
            }

            // 2. Clean-tree gate: a dirty tree is refused so the swap's reset never
            // discards uncommitted work — the caller decides whether to stash.
            var state = await _git.GetWorkingStateAsync(repo, ct);
            if (state is null)
                return RewriteExecutionResult.RefusedByGate($"repository '{repo}' could not be read by git");
            if (state.IsDirty)
                return RewriteExecutionResult.RefusedByGate(DirtyMessage(state));

            // 3. Verified backup: no rewrite proceeds without one.
            BackupHandle backup;
            try
            {
                backup = await _backup.CreateBackupAsync(repo, "History rewrite", ct);
            }
            catch (BackupException ex)
            {
                return RewriteExecutionResult.RefusedByGate($"backup failed — no rewrite attempted: {ex.Message}");
            }
            undo = new UndoHandle(_backup, _busy, backup);

            // 4. Journal: the in-flight op is on disk before the swap, so a crash between
            // here and completion is detectable at the next launch.
            await _journal.BeginAsync(new RewriteJournalEntry
            {
                RepoPath = repo,
                BackupHandle = backup,
                Phase = "swap",
                UtcStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
            }, ct);
            journalled = true;

            // 5. Engine rewrite → temp bare + report (reuse the preview's bare when provided).
            RewriteReport report;
            string tempBare;
            try
            {
                if (preview is not null)
                {
                    report = preview.Report;
                    tempBare = preview.TempBareRepo;
                }
                else
                {
                    var scratch = NewScratch(repo);
                    ownedScratch = scratch.Dir;
                    report = await RunEngineAsync(request, scratch, ct);
                    tempBare = scratch.TempBare;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing in the source changed. Leave the journal and backup for undo/recovery.
                return RewriteExecutionResult.Failed($"history rewrite failed before swap: {ex.Message}", undo);
            }

            // 6. Swap: the only writer of rewritten history. Atomic and pre-flight-guarded.
            SwapResult swap;
            try
            {
                swap = await _swap.ApplySwapAsync(repo, tempBare, phase, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A throw from the swap leaves the journal+backup so recovery/undo works; the
                // swap's own guarantees mean the refs are unchanged unless the reset-tail failed.
                return RewriteExecutionResult.Failed($"swap failed: {ex.Message}", undo, report);
            }

            if (!swap.Success)
                // The swap distinguishes its own gates — dirty tree, unrepresentable Windows path,
                // failed fsck, a ref transaction that committed nothing — from the reset that runs
                // after the refs have already moved. Only the second changed the repository.
                return new RewriteExecutionResult
                {
                    Success = false,
                    Refused = swap.NothingMoved,
                    FailureReason = swap.RefusalReason,
                    Report = report,
                    Swap = swap,
                    Undo = undo
                };

            // 7. Success: clear the journal, hand back the report and a one-click undo.
            await ClearJournalAfterSuccessAsync(repo, ct);
            return new RewriteExecutionResult { Success = true, Report = report, Swap = swap, Undo = undo };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The swap refuses cancellation past its point of no return, so reaching here means
            // no ref moved. The journal records operations that were INTERRUPTED and may need
            // recovery; a run that stopped at a safe point is neither, and leaving the entry
            // would raise a crash-recovery prompt at the next launch over a repository nothing
            // touched. Only a run that wrote its own entry clears one: the journal is keyed per
            // repository, so a cancellation before the Begin above would otherwise delete an
            // earlier crashed run's entry and orphan the backup it names. Cleared under an
            // uncancellable token, or the clear would be cancelled too.
            if (journalled)
                await _journal.CompleteAsync(repo, CancellationToken.None);
            return RewriteExecutionResult.CancelledBeforeApply();
        }
        finally
        {
            lease.Dispose();
            if (ownedScratch is not null)
                RewriteScratch.TryDeleteTree(ownedScratch);
        }
    }

    /// <summary>
    /// The source's ref layout as the swap sees it: every non-remote ref, sorted, plus the oid
    /// HEAD resolves to so a detached-HEAD move that leaves no ref behind still registers, plus
    /// HEAD's symbolic target — two branches on one commit resolve to the same oid and leave
    /// the ref list identical, so a checkout between the dry run and the execute is invisible to
    /// the oids alone while the swap installs the export's HEAD over it. Remote-tracking refs
    /// are excluded because the swap never reconciles them, so a fetch between the dry run and
    /// the execute is not a reason to refuse. Null when git could not read the repository.
    /// </summary>
    private async Task<string?> ReadSourceStateAsync(string repo, CancellationToken ct)
    {
        var refs = await _git.RunAsync(repo, ["for-each-ref", "--format=%(objectname) %(refname)"], ct, RefTimeout);
        if (!refs.Success)
            return null;
        var lines = new List<string>();
        foreach (var raw in refs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var sp = line.IndexOf(' ');
            if (sp <= 0 || line.AsSpan(sp + 1).StartsWith("refs/remotes/"))
                continue;
            lines.Add(line);
        }
        lines.Sort(StringComparer.Ordinal);
        var head = await _git.RunAsync(repo, ["rev-parse", "--verify", "-q", "HEAD"], ct, RefTimeout);
        lines.Add("HEAD " + head.StdOut.Trim());
        // Empty output is a detached HEAD, which is itself a state the execute must not cross.
        var symbolic = await _git.RunAsync(repo, ["symbolic-ref", "-q", "HEAD"], ct, RefTimeout);
        lines.Add("HEAD-symbolic " + (symbolic.Success ? symbolic.StdOut.Trim() : ""));
        return string.Join("\n", lines);
    }

    private Task<RewriteReport> RunEngineAsync(RewriteRequest request, ScratchPaths scratch, CancellationToken ct) =>
        new HistoryRewriter(_gitExe).RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = request.RepoPath,
            WorkingDirectory = scratch.WorkDir,
            TargetBareRepository = scratch.TempBare,
            ExportTimeout = request.ExportTimeout,
            ImportTimeout = request.ImportTimeout,
            Rewrite = request.Options,
            GitExecutable = _gitExe
        }, ct);

    private readonly record struct ScratchPaths(string Dir, string WorkDir, string TempBare);

    /// <summary>A fresh unique scratch tree under the work root. The engine creates the work dir and the fresh bare; only the parent is made here.</summary>
    private ScratchPaths NewScratch(string repo)
    {
        var dir = Path.Combine(_workRoot, RepoKey.For(repo) + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return new ScratchPaths(dir, Path.Combine(dir, "work"), Path.Combine(dir, "target.git"));
    }

    private static string DirtyMessage(Models.WorkingState state)
    {
        var names = state.Files.Take(10).Select(f => f.Path).ToList();
        var listed = string.Join(", ", names);
        if (state.Files.Count > names.Count)
            listed += $", … (+{state.Files.Count - names.Count} more)";
        return $"working tree has {state.Files.Count} uncommitted change(s) — refusing the rewrite (stash or commit first): {listed}";
    }
}
