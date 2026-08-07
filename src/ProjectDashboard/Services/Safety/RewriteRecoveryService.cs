using Microsoft.Extensions.Hosting;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Startup detector for an interrupted history rewrite (R-06 recovery). Registered as
/// the FIRST hosted service so its StartAsync runs before ApplicationHostService shows
/// the window: a pending journal entry must be observed before the UI becomes
/// interactive, or a periodic refresh could read a repo mid-recovery. It only DETECTS —
/// it logs the pending entry and exposes it via <see cref="Pending"/> / <see
/// cref="PendingDetected"/>; it never auto-restores. A later UI stage prompts the user.
/// </summary>
public sealed class RewriteRecoveryService : IHostedService
{
    private readonly RewriteJournal _journal;

    public RewriteRecoveryService(RewriteJournal journal) => _journal = journal;

    /// <summary>The interrupted op found at startup, or null when the last shutdown was clean.</summary>
    public RewriteJournalEntry? Pending { get; private set; }

    /// <summary>True once startup detection has run, regardless of whether anything was pending.</summary>
    public bool DetectionComplete { get; private set; }

    /// <summary>Raised on startup when (and only when) an interrupted op is found.</summary>
    public event Action<RewriteJournalEntry>? PendingDetected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Pending = await _journal.ReadPendingAsync(cancellationToken);
            if (Pending is not null)
            {
                Log.Error($"Interrupted history rewrite detected for '{Pending.RepoPath}' " +
                          $"(phase '{Pending.Phase}', started {Pending.UtcStamp}) — awaiting restore decision.");
                try { PendingDetected?.Invoke(Pending); }
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
