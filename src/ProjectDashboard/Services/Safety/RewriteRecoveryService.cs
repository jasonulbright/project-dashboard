using Microsoft.Extensions.Hosting;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Startup detector for interrupted history rewrites. Registered as
/// the FIRST hosted service so its StartAsync runs before ApplicationHostService shows
/// the window: a pending journal entry must be observed before the UI becomes
/// interactive, or a periodic refresh could read a repo mid-recovery. It only DETECTS —
/// it logs every pending entry and exposes them via <see cref="Pending"/> / <see
/// cref="PendingDetected"/>; it never auto-restores. A later UI stage prompts the user.
///
/// The journal holds one entry per repository, so more than one can be pending at a time and
/// all of them are surfaced: reporting only the first would silently strand the others' backups.
/// </summary>
public sealed class RewriteRecoveryService : IHostedService
{
    private readonly RewriteJournal _journal;

    public RewriteRecoveryService(RewriteJournal journal) => _journal = journal;

    /// <summary>The interrupted ops found at startup, one per repository. Empty when the last shutdown was clean.</summary>
    public IReadOnlyList<RewriteJournalEntry> Pending { get; private set; } = [];

    /// <summary>True once startup detection has run, regardless of whether anything was pending.</summary>
    public bool DetectionComplete { get; private set; }

    /// <summary>Raised on startup once per interrupted op found.</summary>
    public event Action<RewriteJournalEntry>? PendingDetected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Pending = await _journal.ReadAllPendingAsync(cancellationToken);
            foreach (var entry in Pending)
            {
                Log.Error($"Interrupted history rewrite detected for '{entry.RepoPath}' " +
                          $"(phase '{entry.Phase}', started {entry.UtcStamp}) — awaiting restore decision.");
                try { PendingDetected?.Invoke(entry); }
                catch (Exception ex) { Log.Warn("Pending-rewrite subscriber threw", ex); }
            }
        }
        catch (Exception ex)
        {
            // Detection must never block startup: a broken journal read is logged and the
            // app still comes up. The entry survives on disk for a later manual recovery.
            Log.Error("Rewrite-recovery detection failed", ex);
        }
        finally
        {
            DetectionComplete = true;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
