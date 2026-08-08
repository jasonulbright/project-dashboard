using System.IO;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Rewrite;

/// <summary>
/// Orchestrates a gated history rewrite for a wizard to drive. <see cref="PreviewAsync"/>
/// runs the engine WITHOUT swapping, so the report can be shown before anything is
/// committed to. <see cref="ExecuteAsync(RewriteRequest, CancellationToken)"/> runs the
/// safety-railed pipeline: acquire the busy lease, refuse a dirty tree, take a verified
/// backup, journal the in-flight op, rewrite, then swap — each step refusable, and no
/// history reaches the repository without a restorable backup behind it. Nothing is ever
/// auto-pushed.
/// </summary>
public sealed class RewriteCoordinator
{
    private readonly BackupService _backup;
    private readonly RepoBusyRegistry _busy;
    private readonly GitService _git;
    private readonly SwapService _swap;
    private readonly RewriteJournal _journal;
    private readonly string _gitExe;
    private readonly string _workRoot;

    /// <summary>
    /// How long a scratch tree must have sat untouched before the sweep treats it as a leak.
    /// No rewrite state on the repository names its scratch tree, so the write time is the only
    /// liveness signal: a tree another process is still rewriting into is younger than this.
    /// </summary>
    private static readonly TimeSpan ScratchGrace = TimeSpan.FromDays(1);

    public RewriteCoordinator(
        BackupService backup,
        RepoBusyRegistry busy,
        GitService git,
        SwapService swap,
        RewriteJournal? journal = null,
        string? gitExecutable = null,
        string? workRoot = null)
    {
        _backup = backup;
        _busy = busy;
        _git = git;
        _swap = swap;
        _journal = journal ?? new RewriteJournal();
        _gitExe = gitExecutable ?? HistoryPipeline.ResolveGitExecutable();
        _workRoot = workRoot ?? Path.Combine(AppPaths.LocalDir, "rewrite-work");
        SweepStaleScratch();
    }

    /// <summary>
    /// Reclaims scratch trees no disposal ever reached — a crash, a kill, or a process exit
    /// while a release was still queued on the pool. Construction precedes every session this
    /// instance owns, so no tree it could sweep is one of its own; a tree a concurrently running
    /// process is still writing into is held by <see cref="ScratchGrace"/>.
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
        var scratch = NewScratch(request.RepoPath);
        try
        {
            var report = await RunEngineAsync(request, scratch, ct);
            return new PreviewHandle(request, report, scratch.WorkDir, scratch.TempBare, scratch.Dir);
        }
        catch
        {
            RewriteScratch.TryDeleteTree(scratch.Dir);
            throw;
        }
    }

    /// <summary>Runs the full gated pipeline, rewriting fresh. Equivalent to previewing and executing in one call, without keeping the temp bare.</summary>
    public Task<RewriteExecutionResult> ExecuteAsync(RewriteRequest request, CancellationToken ct = default) =>
        ExecuteCoreAsync(request, preview: null, ct);

    /// <summary>Runs the full gated pipeline reusing a preview's already-rewritten temp bare, so the engine is not run twice.</summary>
    public Task<RewriteExecutionResult> ExecuteAsync(PreviewHandle preview, CancellationToken ct = default) =>
        ExecuteCoreAsync(preview.Request, preview, ct);

    private async Task<RewriteExecutionResult> ExecuteCoreAsync(RewriteRequest request, PreviewHandle? preview, CancellationToken ct)
    {
        request.Options.Validate();
        var repo = request.RepoPath;

        // 1. Busy gate: a second op on the same repo is refused, not queued.
        if (!_busy.TryAcquire(repo, out var lease))
            return RewriteExecutionResult.Failed($"repository is busy with another operation: {repo}");

        string? ownedScratch = null;
        UndoHandle? undo = null;
        try
        {
            // 2. Clean-tree gate: a dirty tree is refused so the swap's reset never
            // discards uncommitted work — the caller decides whether to stash.
            var state = await _git.GetWorkingStateAsync(repo, ct);
            if (state is null)
                return RewriteExecutionResult.Failed($"repository '{repo}' could not be read by git");
            if (state.IsDirty)
                return RewriteExecutionResult.Failed(DirtyMessage(state));

            // 3. Verified backup: no rewrite proceeds without one.
            BackupHandle backup;
            try
            {
                backup = await _backup.CreateBackupAsync(repo, ct);
            }
            catch (BackupException ex)
            {
                return RewriteExecutionResult.Failed($"backup failed — no rewrite attempted: {ex.Message}");
            }
            undo = new UndoHandle(_backup, backup);

            // 4. Journal: the in-flight op is on disk before the swap, so a crash between
            // here and completion is detectable at the next launch.
            await _journal.BeginAsync(new RewriteJournalEntry
            {
                RepoPath = repo,
                BackupHandle = backup,
                Phase = "swap",
                UtcStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
            }, ct);

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
                swap = await _swap.ApplySwapAsync(repo, tempBare, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A throw from the swap leaves the journal+backup so recovery/undo works; the
                // swap's own guarantees mean the refs are unchanged unless the reset-tail failed.
                return RewriteExecutionResult.Failed($"swap failed: {ex.Message}", undo, report);
            }

            if (!swap.Success)
                return new RewriteExecutionResult
                {
                    Success = false,
                    FailureReason = swap.RefusalReason,
                    Report = report,
                    Swap = swap,
                    Undo = undo
                };

            // 7. Success: clear the journal, hand back the report and a one-click undo.
            await _journal.CompleteAsync(repo, ct);
            return new RewriteExecutionResult { Success = true, Report = report, Swap = swap, Undo = undo };
        }
        finally
        {
            lease.Dispose();
            if (ownedScratch is not null)
                RewriteScratch.TryDeleteTree(ownedScratch);
        }
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
