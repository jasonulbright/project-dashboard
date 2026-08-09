using Microsoft.Extensions.Hosting;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Startup detector for interrupted history rewrites. Registered as
/// the FIRST hosted service so its StartAsync runs before ApplicationHostService shows
/// the window: a pending journal entry must be observed before the UI becomes
/// interactive, or a periodic refresh could read a repo mid-recovery. It only DETECTS —
/// it logs every pending entry and exposes them via <see cref="Pending"/> / <see
/// cref="PendingDetected"/>; it never auto-restores. The UI prompts, and the reader decides.
///
/// Detection is complete before any window exists, so a surface reads <see cref="Pending"/>
/// rather than subscribing: a view model built after the window opened would subscribe to an
/// event that has already fired for every entry it needs.
///
/// The journal holds one entry per repository, so more than one can be pending at a time and
/// all of them are surfaced: reporting only the first would silently strand the others' backups.
///
/// An empty <see cref="Pending"/> is not proof that nothing was interrupted. The journal is
/// written without a retained backup copy, so a torn or unreadable file yields no entries at
/// all; the backups on disk are the record that survives that, and a surface reporting "nothing
/// pending" must say so.
/// </summary>
public sealed class RewriteRecoveryService : IHostedService
{
    private readonly RewriteJournal _journal;
    private readonly OperationHistory _history;

    public RewriteRecoveryService(RewriteJournal journal, OperationHistory? history = null)
    {
        _journal = journal;
        _history = history ?? new OperationHistory();
    }

    /// <summary>The interrupted ops found at startup, one per repository. Empty when the last shutdown was clean.</summary>
    public IReadOnlyList<RewriteJournalEntry> Pending { get; private set; } = [];

    /// <summary>True once startup detection has run, regardless of whether anything was pending.</summary>
    public bool DetectionComplete { get; private set; }

    /// <summary>Raised on startup once per interrupted op found.</summary>
    public event Action<RewriteJournalEntry>? PendingDetected;

    /// <summary>Raised after <see cref="Pending"/> changes, so every surface showing it agrees on what is still outstanding.</summary>
    public event Action? PendingChanged;

    /// <summary>That repository's interrupted operation as detected at startup, or null when it has none.</summary>
    public RewriteJournalEntry? PendingFor(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;
        var key = RepoKey.For(repoPath);
        return Pending.FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.RepoPath) && string.Equals(RepoKey.For(e.RepoPath), key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Drops one repository's recovery marker: the journal entry is removed and the repository
    /// leaves <see cref="Pending"/>. The backup itself is never touched — the marker says an
    /// operation was interrupted, the backup is what could undo it, and abandoning the first is
    /// not consent to destroy the second.
    /// </summary>
    public async Task ClearAsync(string repoPath, CancellationToken ct = default)
    {
        var entry = PendingFor(repoPath);
        await _journal.CompleteAsync(repoPath, ct);
        if (entry is null) return;
        RecordMarkerCleared(entry);
        Pending = Pending.Where(e => !ReferenceEquals(e, entry)).ToList();
        try { PendingChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn("Pending-rewrite change subscriber threw", ex); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Pending = await _journal.ReadAllPendingAsync(cancellationToken);
            foreach (var entry in Pending)
            {
                // Detection succeeding is not a failure: the entry is the journal doing its job,
                // and the backup it points at is intact until the reader rules on it.
                Log.Warn($"Interrupted history rewrite detected for '{entry.RepoPath}' " +
                         $"(phase '{entry.Phase}', started {entry.UtcStamp}) — awaiting restore decision.");
                RecordInterrupted(entry);
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

    /// <summary>
    /// Names an interrupted operation in the ledger from what the journal recorded.
    /// The label carries the phase, so it is part of what identifies the entry across launches.
    /// </summary>
    internal static string InterruptedLabel(RewriteJournalEntry entry) =>
        $"Interrupted history operation ({(entry.Phase.Length > 0 ? entry.Phase : "unrecorded phase")})";

    /// <summary>
    /// Identifies one interruption across launches, out of the fields the journal entry actually
    /// carries. The journal holds one entry per repository, so these three separate any two
    /// interruptions a repository can have had.
    /// </summary>
    internal static string InterruptedKey(RewriteJournalEntry entry) =>
        $"{entry.Phase}|{entry.UtcStamp}|{entry.BackupHandle?.UtcStamp}";

    /// <summary>
    /// Writes the interrupted operation once, not once per launch. The journal entry survives
    /// every launch until the reader rules on it, so an unconditional write would report the same
    /// interruption again on each start and make the ledger count launches instead of operations.
    ///
    /// The mark beside the ledger is what makes that hold: the ledger read is a bounded tail over
    /// a rotating file, so once the record scrolls out of the window a tail-only check would start
    /// writing a fresh record at every launch. The tail is still consulted as the fallback for a
    /// mark that could not be read.
    /// </summary>
    private void RecordInterrupted(RewriteJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RepoPath)) return;
        var key = InterruptedKey(entry);
        if (_history.ReadInterruptedMark(entry.RepoPath) is { } mark && mark.Key == key) return;

        var label = InterruptedLabel(entry);
        var stamp = entry.BackupHandle?.UtcStamp;
        var started = ParseStamp(entry.UtcStamp);
        if (FindInterrupted(entry.RepoPath, label, stamp, started) is { } existing)
        {
            _history.WriteInterruptedMark(entry.RepoPath, new OperationHistory.InterruptedMark(key, existing.Id));
            return;
        }

        var written = _history.Append(OperationRecord.For(
            entry.RepoPath, OperationCategory.Rewrite, label, OperationOutcome.Interrupted,
            stamp is null
                ? "Found on disk at launch. No backup is named by the record, so what it would restore is unknown."
                : "Found on disk at launch. Nothing has been restored; the backup it names is still on disk.",
            started ?? DateTimeOffset.UtcNow,
            backupStamp: stamp));
        _history.WriteInterruptedMark(entry.RepoPath, new OperationHistory.InterruptedMark(key, written.Id));
    }

    /// <summary>
    /// Records that the marker was abandoned and the backup retained — the two halves of what
    /// <see cref="ClearAsync"/> does, and the second is why the record is worth keeping.
    ///
    /// The link to the interruption comes from the mark, so it survives the interrupted record
    /// scrolling out of the ledger's tail; the tail is the fallback when there is no mark.
    /// </summary>
    private void RecordMarkerCleared(RewriteJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RepoPath)) return;
        var stamp = entry.BackupHandle?.UtcStamp;
        var key = InterruptedKey(entry);
        var mark = _history.ReadInterruptedMark(entry.RepoPath);
        var ofId = mark is not null && mark.Key == key
            ? mark.RecordId
            : FindInterrupted(entry.RepoPath, InterruptedLabel(entry), stamp, ParseStamp(entry.UtcStamp))?.Id ?? "";

        _history.Append(OperationRecord.For(
            entry.RepoPath, OperationCategory.Rewrite, "Interrupted-operation record discarded",
            OperationOutcome.Succeeded,
            stamp is null
                ? "The marker was dropped. It named no backup."
                : "The marker was dropped. The backup it named is still on disk.",
            DateTimeOffset.UtcNow,
            backupStamp: stamp,
            recovery: new RecoveryNote
            {
                Kind = RecoveryKind.MarkerCleared,
                AppliedUtc = DateTimeOffset.UtcNow,
                OfId = ofId
            }));

        // The interruption is no longer outstanding, so the mark must not suppress the record of a
        // later one that happens to reuse the same phase and stamp.
        _history.ClearInterruptedMark(entry.RepoPath);
    }

    private OperationRecord? FindInterrupted(
        string repoPath, string label, string? backupStamp, DateTimeOffset? started) =>
        _history.Tail(repoPath).Records.FirstOrDefault(r =>
            r.Outcome == OperationOutcome.Interrupted
            && string.Equals(r.Label, label, StringComparison.Ordinal)
            && string.Equals(r.BackupStamp ?? "", backupStamp ?? "", StringComparison.Ordinal)
            && (started is null || r.StartedUtc == started));

    /// <summary>Null when the journal recorded no usable start stamp; the caller then falls back rather than inventing one.</summary>
    private static DateTimeOffset? ParseStamp(string stamp) =>
        DateTime.TryParseExact(
            stamp.Length > 18 ? stamp[..18] : stamp, "yyyyMMdd-HHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
}
