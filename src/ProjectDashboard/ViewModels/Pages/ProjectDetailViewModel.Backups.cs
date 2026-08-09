using System.Globalization;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// One backup as the browser shows it: when it was taken, what it was taken for, and how much
/// it can put back. <see cref="Restorable"/> is false for a backup whose sidecar could not be
/// read, which is the same condition a restore refuses on — so the row says what the button
/// would say, before it is pressed.
/// </summary>
public sealed class BackupEntry
{
    public required BackupHandle Handle { get; init; }

    public required string Taken { get; init; }

    public required string Operation { get; init; }

    public required string Detail { get; init; }

    public required bool Restorable { get; init; }
}

/// <summary>
/// The Backups browser and the crash-recovery banner: the two surfaces that reach a backup from
/// outside the wizard session that took it.
///
/// The wizard's Undo is bound to one live session; close it and the only in-app route to the
/// bundle is gone. This surface is the durable one, and it is also the answer when the crash
/// journal is empty but an operation was interrupted anyway — an unreadable journal reports
/// nothing pending, so the backups on disk are the record that survives it.
///
/// A restore replaces every ref and ends in `git reset --hard`, so it carries the same gates as
/// the rewrite that took the backup: a clean working tree, the repository name typed out, and
/// the repository lease held for the whole operation.
/// </summary>
public partial class ProjectDetailViewModel
{
    private readonly BackupService? _backups;
    private readonly RewriteRecoveryService? _recovery;

    // ── The browser ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _backupsVisible;

    partial void OnBackupsVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));

    [ObservableProperty] private ObservableCollection<BackupEntry> _backupList = [];

    /// <summary>
    /// Whether there is anything to restore. Read from the list itself, so the restore gate and
    /// the confirmation field are absent — not merely disabled — before the first load returns.
    /// </summary>
    public bool BackupsHasEntries => BackupList.Count > 0;

    partial void OnBackupListChanged(ObservableCollection<BackupEntry> value) =>
        OnPropertyChanged(nameof(BackupsHasEntries));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedBackupCommand))]
    private BackupEntry? _selectedBackup;

    [ObservableProperty] private string _backupsStatusText = "";
    [ObservableProperty] private string _backupsErrorText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedBackupCommand))]
    private bool _backupsBusy;

    /// <summary>True when the browser has finished a load and found nothing — the empty state must not show before that.</summary>
    [ObservableProperty] private bool _backupsEmpty;

    /// <summary>
    /// Set while this repository has an interrupted operation recorded, so the browser states
    /// which condition the reader is in rather than leaving the two indistinguishable.
    /// </summary>
    [ObservableProperty] private string _backupsJournalNote = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedBackupCommand))]
    private string _backupsConfirmInput = "";

    /// <summary>The exact text the reader must type: the repository folder name, never a generic word.</summary>
    [ObservableProperty] private string _backupsConfirmPhrase = "";

    /// <summary>
    /// True when no full-page overlay is up. Bound to the IsEnabled of every page surface they
    /// cover, and read by each pane's open command: a scrim stops the mouse but no keystroke, so
    /// two panes must never be up at once.
    /// </summary>
    public bool SafetyOverlayHidden =>
        !RewriteWizardVisible && !BackupsVisible && !ForcePushVisible && !ReflogVisible
        && !TagsVisible && !FileHistoryVisible && !CommitGraphVisible && !OperationHistoryVisible;

    /// <summary>
    /// True when neither maintenance pane is up. The force-push pane opens from the rewrite
    /// wizard's own result screen and draws over it, so the wizard keeps the session holding the
    /// only one-click undo — and this disables the wizard's controls underneath, because a scrim
    /// stops the mouse and no keystroke.
    /// </summary>
    /// <remarks>
    /// The operation-history overlay is counted here because it opens over the Backups browser as
    /// a cross-link: a scrim stops the mouse and no keystroke, and the restore behind it is gated
    /// by a typed repository name that stays typeable otherwise.
    /// </remarks>
    public bool MaintenanceOverlayHidden =>
        !ForcePushVisible && !ReflogVisible && !TagsVisible && !OperationHistoryVisible;

    [RelayCommand]
    private async Task OpenBackups()
    {
        if (RepoPath.Length == 0 || RewriteWizardVisible) return;
        BackupsConfirmPhrase = RepoDisplayName();
        BackupsConfirmInput = "";
        BackupsErrorText = "";
        BackupsStatusText = "";
        SelectedBackup = null;
        BackupsVisible = true;
        await LoadBackups();
    }

    [RelayCommand]
    private void CloseBackups()
    {
        // The restore holds the repository lease and the browser is the only report of how it
        // ended; closing over a running one would hide that.
        if (BackupsBusy)
        {
            BackupsStatusText = "The restore is still running — wait for it to finish.";
            return;
        }
        BackupsVisible = false;
        BackupList = [];
        SelectedBackup = null;
        BackupsConfirmInput = "";
        BackupsStatusText = "";
        BackupsErrorText = "";
    }

    /// <summary>
    /// Drops the browser as the page leaves this repository. A restore in flight holds the
    /// repository lease and its own generation guard, so the overlay closing does not end it —
    /// what closes is a list describing a repository the page no longer shows.
    /// </summary>
    private void CloseBackupsOnProjectSwitch()
    {
        // Lowered whatever the overlay was showing: the running restore's own clear is
        // generation-guarded, so a flag carried into the next repository is never lowered again
        // and every later browser opens refusing to close.
        BackupsBusy = false;
        if (!BackupsVisible) return;
        BackupsVisible = false;
        BackupList = [];
        SelectedBackup = null;
        BackupsConfirmInput = "";
        BackupsStatusText = "";
        BackupsErrorText = "";
        BackupsJournalNote = "";
        BackupsEmpty = false;
    }

    [RelayCommand]
    private async Task LoadBackups()
    {
        var service = _backups;
        var repo = RepoPath;
        if (service is null)
        {
            BackupsErrorText = "Backups are unavailable — the backup service was not configured for this session.";
            return;
        }
        if (repo.Length == 0) return;

        var gen = _generation;
        var selected = SelectedBackup?.Handle.UtcStamp;
        List<BackupHandle> handles;
        try
        {
            handles = await service.ListBackupsAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list backups for {repo}", ex);
            if (IsCurrent(gen)) BackupsErrorText = $"Could not read the backup folder: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        BackupList = new ObservableCollection<BackupEntry>(handles.Select(h => Describe(service, h)));
        BackupsEmpty = BackupList.Count == 0;
        SelectedBackup = BackupList.FirstOrDefault(e => e.Handle.UtcStamp == selected) ?? BackupList.FirstOrDefault();
        BackupsJournalNote = DescribeJournalState(repo);
    }

    /// <summary>One row from the sidecar. An unreadable sidecar is reported as such, never rendered as an empty backup.</summary>
    private static BackupEntry Describe(BackupService service, BackupHandle handle)
    {
        var details = service.ReadDetails(handle);
        if (details is null)
            return new BackupEntry
            {
                Handle = handle,
                Taken = DescribeStamp(handle.UtcStamp),
                Operation = "Unreadable",
                Detail = "The refs snapshot beside this bundle is missing or unreadable, so nothing can be restored from it.",
                Restorable = false,
            };

        var operation = details.Operation.Length > 0 ? details.Operation : "Operation not recorded";
        var head = details.HeadRef.Length > 0
            ? details.HeadRef
            : details.HeadObjectId.Length > 0
                ? $"detached at {details.HeadObjectId[..Math.Min(8, details.HeadObjectId.Length)]}"
                : "no HEAD recorded";
        return new BackupEntry
        {
            Handle = handle,
            Taken = DescribeStamp(handle.UtcStamp),
            Operation = operation,
            Detail = $"{details.RefCount} ref(s) · HEAD {head} · {DescribeBytes(details.BundleBytes)}",
            Restorable = true,
        };
    }

    /// <summary>
    /// The capture stamp as local time. Falls back to the raw stamp rather than inventing a date
    /// for a filename this build did not write.
    /// </summary>
    internal static string DescribeStamp(string utcStamp)
    {
        var core = utcStamp.Length > 18 ? utcStamp[..18] : utcStamp;
        return DateTime.TryParseExact(core, "yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : utcStamp;
    }

    private static string DescribeBytes(long bytes) => bytes switch
    {
        <= 0 => "size unknown",
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
    };

    private string DescribeJournalState(string repo) =>
        _recovery?.PendingFor(repo) is { } entry
            ? $"An interrupted {DescribeInterrupted(entry)} is recorded for this repository. " +
              "Restoring the backup it names returns this repository to its state before that operation."
            : "";

    // ── Restore ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The typed phrase is required here for the same reason the rewrite requires it: a restore
    /// replaces every ref in the repository and resets the working tree onto them.
    /// </summary>
    internal bool BackupsConfirmSatisfied =>
        BackupsConfirmPhrase.Length > 0
        && string.Equals(BackupsConfirmInput.Trim(), BackupsConfirmPhrase, StringComparison.Ordinal);

    private bool CanRestoreSelectedBackup() =>
        SelectedBackup is { Restorable: true } && BackupsConfirmSatisfied && !BackupsBusy;

    [RelayCommand(CanExecute = nameof(CanRestoreSelectedBackup))]
    private async Task RestoreSelectedBackup()
    {
        // Re-checked rather than trusted from the affordance: the enabled state is what a reader
        // sees, this is the guard that holds.
        if (!CanRestoreSelectedBackup()) return;
        var service = _backups;
        var entry = SelectedBackup;
        if (service is null || entry is null) return;

        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;
        if (IsBusy)
        {
            BackupsStatusText = "Another operation is running on this repository — wait for it to finish.";
            return;
        }

        // Clean-tree gate, before the lease and before any git write: the restore ends in
        // `git reset --hard`, and the bundle holds committed history only, so an uncommitted
        // change discarded here exists in no backup at all.
        var state = await _gitService.GetWorkingStateAsync(repo);
        if (!IsCurrent(gen)) return;
        if (state is null)
        {
            BackupsErrorText = $"'{repo}' could not be read by git — refusing the restore.";
            return;
        }
        if (state.IsDirty)
        {
            BackupsErrorText =
                $"This working tree has {state.Files.Count} uncommitted change(s). The restore ends in a hard reset " +
                "that would discard them, and the backup holds committed history only — commit or stash them first.";
            return;
        }

        if (!_busyRegistry.TryAcquire(repo, out var lease))
        {
            BackupsErrorText = $"Repository is busy with another operation: {repo}";
            return;
        }

        var holder = new object();
        IsBusy = true;
        _busyGateHolder = holder;
        BackupsBusy = true;
        BackupsErrorText = "";
        BackupsStatusText = "Restoring…";
        try
        {
            // The browser refuses a dirty tree above and offers no confirmed discard, so the
            // service re-check stays a refusal for a tree dirtied since that read.
            var restore = await service.RestoreAsync(entry.Handle, allowDirty: false);
            var landed = restore.Success || restore.RefsRestored;

            // The marker exists to say an operation was interrupted; the repository is now back
            // at the state that operation started from, so it no longer is. Cleared for the
            // captured repository ahead of the generation check, which governs writes to a page
            // that has moved on — a switch during the restore must not leave the marker set for
            // a repository that was just restored.
            if (landed && _recovery is not null) await _recovery.ClearAsync(repo);
            if (!IsCurrent(gen)) return;

            BackupsStatusText = DescribeRestore(restore);
            if (landed)
            {
                RefreshRecoveryBanner();
                await ReloadCommitsAsync();
                await SafeRefreshWorkingStateAsync();
            }
            // A restore spends the typed confirmation: the next one is its own decision.
            BackupsConfirmInput = "";
        }
        catch (Exception ex)
        {
            Log.Warn($"Restore from backup failed for {repo}", ex);
            if (IsCurrent(gen))
                BackupsErrorText =
                    "The restore failed before it could report where it stopped, so this repository's refs may be " +
                    $"pre-restore, restored, or partly restored. Check them before running anything else against it. {ex.Message}";
        }
        finally
        {
            lease.Dispose();
            // A restore started after a project switch raises this flag on a newer generation;
            // lowering it from here would let that one's overlay close mid-restore.
            if (IsCurrent(gen)) BackupsBusy = false;
            if (ReferenceEquals(_busyGateHolder, holder))
            {
                _busyGateHolder = null;
                if (IsCurrent(gen)) IsBusy = false;
            }
        }
    }

    // ── Crash recovery ──────────────────────────────────────────────────────────

    /// <summary>The interrupted operation this page is currently reporting, or null when there is none.</summary>
    private RewriteJournalEntry? _recoveryEntry;

    /// <summary>Repositories whose banner the reader chose to hide for this session, keyed by <see cref="RepoKey"/>.</summary>
    private readonly HashSet<string> _recoveryBannerHidden = new(StringComparer.Ordinal);

    [ObservableProperty] private bool _recoveryBannerVisible;
    [ObservableProperty] private string _recoveryBannerText = "";

    /// <summary>True while the discard choice is open. Discarding abandons the only marker that an operation was interrupted, so it is typed out.</summary>
    [ObservableProperty] private bool _recoveryDiscardVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscardRecoveryRecordCommand))]
    private string _recoveryDiscardInput = "";

    [ObservableProperty] private string _recoveryDiscardPhrase = "";

    /// <summary>
    /// Puts this repository's interrupted operation on screen, or takes it off. Read from the
    /// startup detection rather than the journal: detection ran before any window existed, and
    /// re-reading here would report an operation this session is itself running.
    /// </summary>
    private void RefreshRecoveryBanner()
    {
        var repo = RepoPath;
        _recoveryEntry = repo.Length > 0 ? _recovery?.PendingFor(repo) : null;
        RecoveryDiscardVisible = false;
        RecoveryDiscardInput = "";
        RecoveryDiscardPhrase = RepoDisplayName();

        if (_recoveryEntry is null || (repo.Length > 0 && _recoveryBannerHidden.Contains(RepoKey.For(repo))))
        {
            RecoveryBannerVisible = false;
            RecoveryBannerText = "";
            return;
        }

        RecoveryBannerText =
            $"An interrupted {DescribeInterrupted(_recoveryEntry)} was found for this repository. " +
            "Nothing has been restored — this app never restores on its own. Its backup is still on disk; " +
            "open Backups to inspect it and restore if you want it back.";
        RecoveryBannerVisible = true;
    }

    /// <summary>Names the interrupted operation from what the journal recorded, without inventing what it did not.</summary>
    internal static string DescribeInterrupted(RewriteJournalEntry entry)
    {
        var phase = entry.Phase.Length > 0 ? entry.Phase : "unrecorded phase";
        var when = entry.UtcStamp.Length > 0 ? $", started {DescribeStamp(entry.UtcStamp)}" : "";
        return $"history operation (phase '{phase}'{when})";
    }

    /// <summary>Opens the browser on the backup the interrupted operation recorded, so the reader restores that one and not a neighbour.</summary>
    [RelayCommand]
    private async Task OpenBackupsForRecovery()
    {
        var stamp = _recoveryEntry?.BackupHandle?.UtcStamp;
        await OpenBackups();
        if (stamp is null) return;
        if (BackupList.FirstOrDefault(e => e.Handle.UtcStamp == stamp) is { } match)
        {
            SelectedBackup = match;
            return;
        }
        BackupsErrorText =
            "The backup that interrupted operation recorded is no longer on disk — it may have been pruned by the " +
            "retention setting or deleted. Any other backup below can still be restored.";
    }

    /// <summary>Hides the banner for this session and keeps the marker, so the next launch offers it again.</summary>
    [RelayCommand]
    private void KeepRecoveryRecord()
    {
        var repo = RepoPath;
        if (repo.Length > 0) _recoveryBannerHidden.Add(RepoKey.For(repo));
        RecoveryBannerVisible = false;
        RecoveryDiscardVisible = false;
        SyncStatusText = "Interrupted-operation record kept. It will be offered again at the next launch.";
    }

    [RelayCommand]
    private void BeginDiscardRecoveryRecord()
    {
        if (_recoveryEntry is null) return;
        RecoveryDiscardPhrase = RepoDisplayName();
        RecoveryDiscardInput = "";
        RecoveryDiscardVisible = true;
    }

    [RelayCommand]
    private void CancelDiscardRecoveryRecord()
    {
        RecoveryDiscardVisible = false;
        RecoveryDiscardInput = "";
    }

    internal bool RecoveryDiscardConfirmSatisfied =>
        RecoveryDiscardPhrase.Length > 0
        && string.Equals(RecoveryDiscardInput.Trim(), RecoveryDiscardPhrase, StringComparison.Ordinal);

    private bool CanDiscardRecoveryRecord() => _recoveryEntry is not null && RecoveryDiscardConfirmSatisfied;

    /// <summary>
    /// Drops the marker that an operation was interrupted. Typed out because it is the only
    /// record that this repository may be mid-operation; the backup it names is left on disk and
    /// stays reachable from the Backups browser.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDiscardRecoveryRecord))]
    private async Task DiscardRecoveryRecord()
    {
        if (!CanDiscardRecoveryRecord()) return;
        var repo = RepoPath;
        if (repo.Length == 0 || _recovery is null) return;

        await _recovery.ClearAsync(repo);
        RefreshRecoveryBanner();
        SyncStatusText =
            "Interrupted-operation record discarded. The backup it named is still on disk under Backups.";
        if (BackupsVisible) BackupsJournalNote = DescribeJournalState(repo);
    }
}
