using System.IO;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Rewrite;

/// <summary>
/// How far a rewrite has got, for a surface that may only offer cancel while cancelling is
/// still safe. <see cref="SwapService.ApplySwapAsync"/> reports <see cref="Applying"/> on the
/// same line that stops honouring the token, so the offer and the guarantee move together.
/// </summary>
public enum RewritePhase
{
    /// <summary>Export, transform, import, verification, and the swap's pre-flight. All scratch work; cancelling changes no ref.</summary>
    Preparing,

    /// <summary>The swap's ref transaction. Cancellation is no longer honoured — the transaction is all-or-nothing.</summary>
    Applying,
}

/// <summary>What to rewrite and where: the target repository plus the content operations.</summary>
public sealed class RewriteRequest
{
    public required string RepoPath { get; init; }

    public required RewriteOptions Options { get; init; }

    public TimeSpan ExportTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan ImportTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// The result of a dry-run <see cref="RewriteCoordinator.PreviewAsync"/>: the rewritten
/// temp bare and its report, held for a subsequent <see cref="RewriteCoordinator.ExecuteAsync(PreviewHandle, CancellationToken)"/>
/// that reuses the same bare rather than rewriting again. Disposing deletes the scratch
/// tree; a handle already consumed by ExecuteAsync must still be disposed (the swap fetched
/// the objects it needed into the source, so the bare is then only scratch).
/// </summary>
public sealed class PreviewHandle : IDisposable
{
    private readonly string _scratchDir;
    private int _disposed;

    internal PreviewHandle(
        RewriteRequest request, RewriteReport report, string workDir, string tempBareRepo, string scratchDir, string sourceState)
    {
        Request = request;
        Report = report;
        WorkDir = workDir;
        TempBareRepo = tempBareRepo;
        _scratchDir = scratchDir;
        SourceState = sourceState;
    }

    public RewriteRequest Request { get; }

    /// <summary>
    /// The source's ref layout as it stood when the dry run exported it. The execute reuses this
    /// handle's bare verbatim, so anything committed into the source afterwards is absent from
    /// it and the swap would erase it; comparing this before the backup makes that a refusal.
    /// </summary>
    internal string SourceState { get; }

    /// <summary>What the rewrite proved about itself without touching the source: commits/blobs/bytes affected, binary skips, and the scrub Complete flag.</summary>
    public RewriteReport Report { get; }

    public string WorkDir { get; }

    public string TempBareRepo { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RewriteScratch.TryDeleteTree(_scratchDir);
    }
}

/// <summary>
/// One-click restore: calls <see cref="BackupService.RestoreAsync"/> on the backup taken
/// before the rewrite, returning refs (and the working tree) to their exact pre-rewrite
/// state. The <see cref="RestoreResult.WorktreeWasDirty"/> flag it surfaces reports whether
/// the restore's reset discarded uncommitted work.
///
/// The restore runs under the repository lease, so it is the gate every entry point inherits
/// by holding a handle. The operation that created the handle releases its own lease before
/// the handle reaches a caller, so the acquire here never contends with it.
/// </summary>
public sealed class UndoHandle
{
    private readonly BackupService _backup;
    private readonly RepoBusyRegistry _busy;

    internal UndoHandle(BackupService backup, RepoBusyRegistry busy, BackupHandle handle)
    {
        _backup = backup;
        _busy = busy;
        Backup = handle;
    }

    public BackupHandle Backup { get; }

    /// <summary>
    /// Restores under the repository lease. The restore unbundles, reconciles every ref,
    /// repositions HEAD and resets the working tree; unleased, a background sync reads the
    /// repository as idle and its fetch or pull lands part-way through that sequence.
    ///
    /// The restore runs over a dirty tree: every surface offering this undo confirms the hard
    /// reset's discard by name first, and the discarded count comes back in the result.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(CancellationToken ct = default)
    {
        if (!_busy.TryAcquire(Backup.RepoPath, out var lease))
            return new RestoreResult(false, $"Repository is busy with another operation: {Backup.RepoPath}");
        using (lease)
            return await _backup.RestoreAsync(Backup, allowDirty: true, ct);
    }
}

/// <summary>
/// The outcome of <see cref="RewriteCoordinator.ExecuteAsync(RewriteRequest, CancellationToken, IProgress{RewritePhase})"/>.
/// On success the source history is rewritten, <see cref="Report"/> describes it, and
/// <see cref="Undo"/> offers one-click restore. On failure <see cref="FailureReason"/> says
/// why; <see cref="Undo"/> is still present whenever a backup was taken, so a partially
/// applied swap can be reverted even though this stage guarantees the swap itself is atomic.
/// <see cref="Cancelled"/> is its own outcome, neither success nor failure, and carries no
/// undo because there is nothing to undo.
/// </summary>
public sealed class RewriteExecutionResult
{
    public required bool Success { get; init; }

    /// <summary>
    /// True when the run stopped because cancellation was requested. Cancellation is only
    /// observed before the swap's point of no return, so this outcome means no ref, commit, or
    /// file in the repository was changed.
    /// </summary>
    public bool Cancelled { get; init; }

    public string? FailureReason { get; init; }

    public RewriteReport? Report { get; init; }

    public SwapResult? Swap { get; init; }

    public UndoHandle? Undo { get; init; }

    /// <summary>
    /// True when a gate turned the run away before the engine ran, so nothing in the repository
    /// was touched. False on every failure that reached the engine or the swap, whose effect on
    /// the repository is what <see cref="Undo"/> exists for.
    /// </summary>
    public bool Refused { get; init; }

    internal static RewriteExecutionResult Failed(
        string reason, UndoHandle? undo = null, RewriteReport? report = null, SwapResult? swap = null) =>
        new() { Success = false, FailureReason = reason, Undo = undo, Report = report, Swap = swap };

    /// <summary>A gate refusal: the run never started, so the repository is exactly as it was.</summary>
    internal static RewriteExecutionResult RefusedByGate(string reason, UndoHandle? undo = null) =>
        new() { Success = false, Refused = true, FailureReason = reason, Undo = undo };

    /// <summary>
    /// The cancelled outcome. No undo travels with it: a cancellation is only observed while the
    /// repository is still untouched, so offering to restore the backup would offer to restore a
    /// state the repository is already in. The backup itself stays on disk for the Backups surface.
    /// </summary>
    internal static RewriteExecutionResult CancelledBeforeApply() =>
        new() { Success = false, Cancelled = true };
}

/// <summary>Scratch tree helpers for the rewrite work area under AppPaths — deletion clears the read-only bit git sets on object files.</summary>
internal static class RewriteScratch
{
    public static void TryDeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                {
                    Log.Warn($"could not delete rewrite scratch tree {path}", ex);
                    return;
                }
                Thread.Sleep(200);
            }
        }
    }
}
