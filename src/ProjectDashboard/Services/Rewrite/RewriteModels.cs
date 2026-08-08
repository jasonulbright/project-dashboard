using System.IO;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Rewrite;

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
/// </summary>
public sealed class UndoHandle
{
    private readonly BackupService _backup;

    internal UndoHandle(BackupService backup, BackupHandle handle)
    {
        _backup = backup;
        Backup = handle;
    }

    public BackupHandle Backup { get; }

    public Task<RestoreResult> RestoreAsync(CancellationToken ct = default) => _backup.RestoreAsync(Backup, ct);
}

/// <summary>
/// The outcome of <see cref="RewriteCoordinator.ExecuteAsync(RewriteRequest, CancellationToken)"/>.
/// On success the source history is rewritten, <see cref="Report"/> describes it, and
/// <see cref="Undo"/> offers one-click restore. On failure <see cref="FailureReason"/> says
/// why; <see cref="Undo"/> is still present whenever a backup was taken, so a partially
/// applied swap can be reverted even though this stage guarantees the swap itself is atomic.
/// </summary>
public sealed class RewriteExecutionResult
{
    public required bool Success { get; init; }

    public string? FailureReason { get; init; }

    public RewriteReport? Report { get; init; }

    public SwapResult? Swap { get; init; }

    public UndoHandle? Undo { get; init; }

    internal static RewriteExecutionResult Failed(
        string reason, UndoHandle? undo = null, RewriteReport? report = null, SwapResult? swap = null) =>
        new() { Success = false, FailureReason = reason, Undo = undo, Report = report, Swap = swap };
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
