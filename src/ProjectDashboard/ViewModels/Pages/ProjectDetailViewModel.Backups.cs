using System.Globalization;
using System.IO;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// One backup as the browser shows it: when it was taken, what it was taken for, and how much
/// it can put back. <see cref="Restorable"/> is false for a backup whose sidecar could not be
/// read, which is the same condition a restore refuses on — so the row says what the button
/// would say, before it is pressed.
///
/// <see cref="Verification"/> is null until a verify is asked for. It is never inferred from
/// <see cref="Restorable"/>: a readable sidecar says nothing about whether the bundle beside it
/// still unpacks, and only running `git bundle verify` answers that.
/// </summary>
public sealed partial class BackupEntry : ObservableObject
{
    public required BackupHandle Handle { get; init; }

    public required string Taken { get; init; }

    public required string Operation { get; init; }

    public required string Detail { get; init; }

    public required bool Restorable { get; init; }

    /// <summary>Whether the sidecar recorded this capture as a deep one. False for one it could not read.</summary>
    public required bool Deep { get; init; }

    /// <summary>
    /// What this backup holds and what it does not, in the words its tier warrants. Empty for a
    /// backup whose sidecar could not be read: nothing restores from it, so naming a scope for it
    /// would describe a capture this app cannot read the tier of.
    /// </summary>
    public string ScopeText =>
        !Restorable ? ""
        : Deep
            ? "This backup also holds the objects no ref reaches — commits a reflog alone held, and every stash " +
              "entry — so they survive here by object id. A restore puts those objects back; it does not rebuild " +
              "the reflog or the stash stack."
            : "This backup holds the refs it recorded and the newest stash entry only. Commits reachable from a " +
              "reflog alone, and stash entries below the newest, were never in it and no restore brings them back.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerificationText))]
    [NotifyPropertyChangedFor(nameof(VerificationSuffix))]
    private BundleVerifyState? _verification;

    public string VerificationText => Verification switch
    {
        BundleVerifyState.Verified => "Verified: the bundle reads back.",
        BundleVerifyState.Failed => "Verification failed: this bundle cannot be restored.",
        BundleVerifyState.Unknown => "Verification could not be run, so this bundle's state is unknown.",
        _ => ""
    };

    /// <summary>
    /// The verification clause a composed row name appends, separator included, so an unverified
    /// row's name ends after its detail instead of on a dangling comma.
    /// </summary>
    public string VerificationSuffix => VerificationText.Length == 0 ? "" : ", " + VerificationText;
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
    [NotifyCanExecuteChangedFor(nameof(VerifySelectedBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedBackupCommand))]
    private BackupEntry? _selectedBackup;

    [ObservableProperty] private string _backupsStatusText = "";
    [ObservableProperty] private string _backupsErrorText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackupNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifySelectedBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportBackupCommand))]
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
    /// Which tier the next manual backup takes. Seeded from the saved preference each time the
    /// browser opens, so the box shows what the coordinators' own backups are doing, and settable
    /// here for one capture without changing what the setting says.
    /// </summary>
    [ObservableProperty] private bool _deepBackupRequested;

    /// <summary>
    /// True when no full-page overlay is up. Bound to the IsEnabled of every page surface they
    /// cover, and read by each pane's open command: a scrim stops the mouse but no keystroke, so
    /// two panes must never be up at once.
    /// </summary>
    public bool SafetyOverlayHidden =>
        !RewriteWizardVisible && !BackupsVisible && !ForcePushVisible && !ReflogVisible
        && !TagsVisible && !FileHistoryVisible && !CommitGraphVisible && !OperationHistoryVisible
        && !WorkflowLogVisible && !FindVisible;

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
        DeepBackupRequested = _settingsService?.Load().DeepBackupCapture ?? false;
        BackupsVisible = true;
        await LoadBackups();
    }

    [RelayCommand]
    private void CloseBackups()
    {
        // The browser is the only report of how its own operation ended — and a restore also holds
        // the repository lease while it runs; closing over either would hide that.
        if (BackupsBusy)
        {
            BackupsStatusText = "This browser's operation is still running — wait for it to finish.";
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
            BackupsErrorText = BackupsUnavailableRefusal;
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
                Deep = false,
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
            Detail = $"{details.RefCount} ref(s) · HEAD {head} · {DescribeBytes(details.BundleBytes)} · {DescribeTier(details)}",
            Restorable = true,
            Deep = details.DeepCapture,
        };
    }

    /// <summary>
    /// Which tier a capture was, named on the row itself. Invisible after the fact otherwise: the
    /// bundle does not say, and two backups of the same repository can be of different tiers.
    /// </summary>
    internal static string DescribeTier(BackupDetails details) =>
        !details.DeepCapture
            ? "standard capture"
            : details.DeepObjectCount == 1
                ? "deep capture, 1 object beyond the refs"
                : $"deep capture, {details.DeepObjectCount} objects beyond the refs";

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

    private static string DescribeBytes(long bytes) => ByteSizeText.Describe(bytes);

    private string DescribeJournalState(string repo) =>
        _recovery?.PendingFor(repo) is { } entry
            ? $"An interrupted {DescribeInterrupted(entry)} is recorded for this repository. " +
              "Restoring the backup it names returns this repository to its state before that operation."
            : "";

    // ── Create, verify, delete ──────────────────────────────────────────────────

    internal const string BackupsUnavailableRefusal =
        "Backups are unavailable — the backup service was not configured for this session.";

    private bool CanBackupNow() => !BackupsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Takes a backup of the open repository on demand, outside any destructive operation.
    ///
    /// Unlike the verify and delete beside it, this reads the repository rather than only the
    /// files under the app's backup folder, so it holds the repository lease for its whole run:
    /// `git bundle create` walks refs and objects, and an operation writing them underneath it
    /// would leave a bundle recording a state the repository never held as a whole. Acquiring the
    /// lease is also what decides the refusal — a prior "is anything running?" read would be
    /// answered before the operation it is asking about could start.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private async Task BackupNow()
    {
        if (BackupsBusy) return;
        var service = _backups;
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;
        if (service is null)
        {
            BackupsErrorText = BackupsUnavailableRefusal;
            return;
        }

        var started = DateTimeOffset.UtcNow;
        if (IsBusy)
        {
            BackupsErrorText = BusyNotice("Back up now");
            RecordBackupOp(repo, OperationCategory.BackupCreate, "Back up now",
                OperationOutcome.Refused, BackupsErrorText, started);
            return;
        }
        if (!_busyRegistry.TryAcquire(repo, out var lease))
        {
            BackupsErrorText = $"Repository is busy with another operation: {repo}";
            RecordBackupOp(repo, OperationCategory.BackupCreate, "Back up now",
                OperationOutcome.Refused, BackupsErrorText, started);
            return;
        }

        var deep = DeepBackupRequested;
        var holder = new object();
        IsBusy = true;
        _busyGateHolder = holder;
        BackupsBusy = true;
        BackupsErrorText = "";
        BackupsStatusText = deep ? "Backing up, deep capture…" : "Backing up…";
        BackupHandle? handle = null;
        try
        {
            handle = await service.CreateBackupAsync(repo, ManualBackupOperation, deep);
            RecordBackupOp(repo, OperationCategory.BackupCreate, "Back up now",
                OperationOutcome.Succeeded, "", started, handle.UtcStamp);
        }
        catch (BackupException ex)
        {
            RecordBackupOp(repo, OperationCategory.BackupCreate, "Back up now",
                OperationOutcome.Failed, ex.Message, started);
            if (IsCurrent(gen))
            {
                BackupsStatusText = "";
                BackupsErrorText = ex.Message;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"manual backup failed for {repo}", ex);
            RecordBackupOp(repo, OperationCategory.BackupCreate, "Back up now",
                OperationOutcome.Failed, ex.Message, started);
            if (IsCurrent(gen))
            {
                BackupsStatusText = "";
                BackupsErrorText = $"The backup could not be taken: {ex.Message}";
            }
        }
        finally
        {
            lease.Dispose();
            // A backup started before a project switch raises these on an older generation;
            // lowering them from here would open the gate under the newer page's own operation.
            if (IsCurrent(gen)) BackupsBusy = false;
            if (ReferenceEquals(_busyGateHolder, holder))
            {
                _busyGateHolder = null;
                if (IsCurrent(gen)) IsBusy = false;
            }
        }

        if (handle is null || !IsCurrent(gen)) return;
        await LoadBackups();
        if (!IsCurrent(gen)) return;
        var fresh = BackupList.FirstOrDefault(e => e.Handle.UtcStamp == handle.UtcStamp);
        if (fresh is not null)
        {
            SelectedBackup = fresh;
            // Creation runs `git bundle verify` on what it wrote and deletes a bundle that fails,
            // so a handle coming back is a bundle that verified at that moment — which is what the
            // row claims, rather than that it still verifies now.
            fresh.Verification = BundleVerifyState.Verified;
        }
        BackupsStatusText = (
            $"Backed up at {DescribeStamp(handle.UtcStamp)}. The bundle was verified as it was written. " +
            (fresh?.ScopeText ?? "")).TrimEnd();
    }

    /// <summary>What a manual backup's sidecar records it was taken for.</summary>
    internal const string ManualBackupOperation = "Manual backup";

    private bool CanVerifySelectedBackup() => SelectedBackup is not null && !BackupsBusy;

    /// <summary>
    /// Runs the restore's own precondition against the selected bundle and reports it, without
    /// restoring. Read-only, so no confirmation and no repository lease.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanVerifySelectedBackup))]
    private async Task VerifySelectedBackup()
    {
        var service = _backups;
        var entry = SelectedBackup;
        var gen = _generation;
        if (entry is null || BackupsBusy) return;
        if (service is null)
        {
            BackupsErrorText = BackupsUnavailableRefusal;
            return;
        }

        BackupsBusy = true;
        BackupsErrorText = "";
        BackupsStatusText = "Verifying…";
        BundleVerifyResult result;
        try
        {
            result = await service.VerifyBackupAsync(entry.Handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"verify of backup {entry.Handle.UtcStamp} failed", ex);
            result = new BundleVerifyResult(BundleVerifyState.Unknown, ex.Message);
        }
        finally
        {
            if (IsCurrent(gen)) BackupsBusy = false;
        }
        if (!IsCurrent(gen)) return;

        entry.Verification = result.State;
        BackupsStatusText = result.Verified
            ? $"The bundle taken {entry.Taken} reads back — this backup is restorable."
            : "";
        BackupsErrorText = result.State switch
        {
            BundleVerifyState.Failed =>
                $"The bundle taken {entry.Taken} cannot be restored — it failed verification: {result.Detail}",
            BundleVerifyState.Unknown =>
                $"Verification of the bundle taken {entry.Taken} could not be run, so whether it is restorable " +
                $"is unknown — it was neither confirmed good nor found bad: {result.Detail}",
            _ => ""
        };
    }

    private bool CanDeleteSelectedBackup() => SelectedBackup is not null && !BackupsBusy;

    /// <summary>
    /// Removes one backup's bundle and refs sidecar from disk.
    ///
    /// Plainly confirmed rather than typed: nothing in the repository changes, only the files
    /// under the app's backup folder. The repository lease is not taken for the same reason.
    /// Retention is unaffected — the next backup prunes from whatever is left, so removing one
    /// here does not cost a second.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedBackup))]
    private async Task DeleteSelectedBackup()
    {
        var service = _backups;
        var entry = SelectedBackup;
        var repo = RepoPath;
        var gen = _generation;
        if (entry is null || BackupsBusy || repo.Length == 0) return;
        if (service is null)
        {
            BackupsErrorText = BackupsUnavailableRefusal;
            return;
        }

        var stamp = entry.Handle.UtcStamp;
        var message = DeleteBackupMessage(entry, service.MeasureBackupBytes(entry.Handle));
        if (!await ConfirmAsync("Delete this backup?", message, "Delete")) return;
        if (!IsCurrent(gen))
        {
            BackupsStatusText = ProjectSwitchedNotice("Backup delete");
            return;
        }

        var started = DateTimeOffset.UtcNow;
        BackupsBusy = true;
        BackupsErrorText = "";
        BackupsStatusText = "Deleting…";
        var failure = "";
        try
        {
            // The delete is best effort and never throws, so what it reports is read from the
            // files themselves afterwards, not from the call having returned.
            var state = await service.DeleteBackupAsync(entry.Handle);
            failure = DeleteFailureNotice(state);
            RecordBackupOp(repo, OperationCategory.BackupDelete, $"Delete backup {stamp}",
                state == BackupDeleteState.Deleted ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                failure, started, stamp);
        }
        catch (Exception ex)
        {
            Log.Warn($"delete of backup {stamp} failed", ex);
            failure = $"The backup could not be deleted: {ex.Message}";
            RecordBackupOp(repo, OperationCategory.BackupDelete, $"Delete backup {stamp}",
                OperationOutcome.Failed, ex.Message, started, stamp);
        }
        finally
        {
            if (IsCurrent(gen)) BackupsBusy = false;
        }

        // Reloaded whichever way it went: a delete that failed part way leaves a list that no
        // longer describes what is on disk.
        if (!IsCurrent(gen)) return;
        await LoadBackups();
        if (!IsCurrent(gen)) return;

        if (failure.Length > 0)
        {
            BackupsStatusText = "";
            BackupsErrorText = failure;
            return;
        }

        BackupsStatusText =
            $"Deleted the backup taken {entry.Taken}. Nothing in this repository changed.";
        // An interrupted operation naming the bundle just deleted is now pointing at nothing, and
        // the reader is in the state the browser already has words for.
        if (_recovery?.PendingFor(repo)?.BackupHandle?.UtcStamp == stamp)
            BackupsErrorText = RecordedBackupGoneNotice;
    }

    /// <summary>
    /// What a partial delete left, in the words that case actually warrants. The two are not
    /// interchangeable: one leaves the backup restorable and one means it is gone, and a single
    /// message covering both would be false in whichever case it was not written for.
    /// </summary>
    internal static string DeleteFailureNotice(BackupDeleteState state) => state switch
    {
        BackupDeleteState.BundleRemains => BundleStillOnDiskFailure,
        BackupDeleteState.SnapshotRemains => SnapshotStillOnDiskFailure,
        _ => ""
    };

    internal const string BundleStillOnDiskFailure =
        "This backup's bundle is still on disk after the delete — another process may hold it open. Its refs " +
        "snapshot was left alone, so the backup is intact and still restorable; nothing was removed.";

    internal const string SnapshotStillOnDiskFailure =
        "This backup's bundle was removed but its refs snapshot could not be — another process may hold it " +
        "open. The backup is gone and cannot be restored. What is left is not a backup on its own, and the " +
        "next read of this repository's backups that can remove it will.";

    /// <summary>
    /// What the confirmation says: the same detail the row carries — the refs the sidecar recorded
    /// and where HEAD was — plus what the two files occupy and what is known about whether the
    /// bundle still reads back, which is nothing at all until a verify is run.
    /// </summary>
    internal static string DeleteBackupMessage(BackupEntry entry, long? bytes) =>
        $"Delete the backup taken {entry.Taken}?\n\n" +
        $"    {entry.Operation}\n" +
        $"    {entry.Detail}\n" +
        $"    On disk: {(bytes is null ? "size unknown — the files could not be read" : DescribeBytes(bytes.Value))}\n" +
        $"    {(entry.VerificationText.Length > 0 ? entry.VerificationText : "Not verified in this session.")}\n\n" +
        "This removes the bundle and its refs snapshot from this app's backup folder. Nothing in the " +
        "repository changes, and no other backup is affected. It cannot be undone.";

    // ── Export, import ──────────────────────────────────────────────────────────

    private bool CanExportSelectedBackup() => SelectedBackup is not null && !BackupsBusy;

    /// <summary>
    /// Copies the selected backup's files to a folder the reader picks. Read-only toward the
    /// backup store and the repository, so no confirmation and no repository lease.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportSelectedBackup))]
    private async Task ExportSelectedBackup()
    {
        var service = _backups;
        var entry = SelectedBackup;
        var repo = RepoPath;
        var gen = _generation;
        if (entry is null || BackupsBusy || repo.Length == 0) return;
        if (service is null)
        {
            BackupsErrorText = BackupsUnavailableRefusal;
            return;
        }

        var destination = PromptForDirectory("Export this backup to…");
        if (destination is null) return;
        if (!IsCurrent(gen))
        {
            BackupsStatusText = ProjectSwitchedNotice("Backup export");
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var stamp = entry.Handle.UtcStamp;
        BackupsBusy = true;
        BackupsErrorText = "";
        BackupsStatusText = "Exporting…";
        BackupExportResult result;
        try
        {
            result = await service.ExportBackupAsync(entry.Handle, destination);
        }
        catch (Exception ex)
        {
            Log.Warn($"export of backup {stamp} failed", ex);
            result = new BackupExportResult(false, $"The backup could not be exported: {ex.Message}");
        }
        finally
        {
            if (IsCurrent(gen)) BackupsBusy = false;
        }
        RecordBackupOp(repo, OperationCategory.BackupExport, $"Export backup {stamp}",
            result.Success ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            result.Success ? "" : result.Message, started, stamp);
        if (!IsCurrent(gen)) return;

        BackupsStatusText = result.Success ? result.Message : "";
        BackupsErrorText = result.Success ? "" : result.Message;
    }

    private bool CanImportBackup() => !BackupsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Brings an exported bundle/sidecar pair into this repository's backup folder. Additive:
    /// nothing in the repository or in any existing backup changes, and a stamp collision stores
    /// the import under a disambiguated name rather than touching what holds the stamp.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportBackup))]
    private async Task ImportBackup()
    {
        var service = _backups;
        var repo = RepoPath;
        var gen = _generation;
        if (BackupsBusy || repo.Length == 0) return;
        if (service is null)
        {
            BackupsErrorText = BackupsUnavailableRefusal;
            return;
        }

        var bundlePath = PromptForBundleFile();
        if (bundlePath is null) return;
        if (!IsCurrent(gen))
        {
            BackupsStatusText = ProjectSwitchedNotice("Backup import");
            return;
        }

        var started = DateTimeOffset.UtcNow;
        BackupsBusy = true;
        BackupsErrorText = "";
        BackupsStatusText = "Importing…";
        BackupImportResult result;
        try
        {
            result = await service.ImportBackupAsync(repo, bundlePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"import of bundle {bundlePath} failed", ex);
            result = new BackupImportResult(false, $"The backup could not be imported: {ex.Message}");
        }
        finally
        {
            if (IsCurrent(gen)) BackupsBusy = false;
        }
        RecordBackupOp(repo, OperationCategory.BackupImport,
            $"Import backup {Path.GetFileNameWithoutExtension(bundlePath)}",
            result.Success ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            result.Success ? "" : result.Message, started, result.Handle?.UtcStamp);
        if (!IsCurrent(gen)) return;

        if (!result.Success)
        {
            BackupsStatusText = "";
            BackupsErrorText = result.Message;
            return;
        }

        await LoadBackups();
        if (!IsCurrent(gen)) return;
        if (result.Handle is { } imported)
            SelectedBackup = BackupList.FirstOrDefault(e => e.Handle.UtcStamp == imported.UtcStamp);
        BackupsStatusText = result.Message;
    }

    /// <summary>Bundle chosen by the reader, or null when the picker was cancelled.</summary>
    internal virtual string? PromptForBundleFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a backup bundle",
            Filter = "Git bundle (*.bundle)|*.bundle",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// One record for a backup this page created or deleted. The backups a coordinator takes are
    /// already named by that operation's own record, so nothing here duplicates them.
    /// </summary>
    private void RecordBackupOp(string repo, OperationCategory category, string label,
        OperationOutcome outcome, string detail, DateTimeOffset started, string? stamp = null) =>
        _history.Append(OperationRecord.For(repo, category, label, outcome, detail, started, backupStamp: stamp));

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

            BackupsStatusText = DescribeRestoredScope(DescribeRestore(restore), entry, landed);
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

    /// <summary>
    /// The restore outcome with what the backup actually held appended. A standard capture never
    /// received the reflog-only commits or the stash entries below the newest, and an outcome
    /// reading "history restored" alone would be taken as having put those back too. Appended only
    /// once refs landed: a restore that changed nothing restored no scope to describe.
    /// </summary>
    internal static string DescribeRestoredScope(string outcome, BackupEntry entry, bool landed) =>
        landed && entry.ScopeText.Length > 0 ? $"{outcome} {entry.ScopeText}" : outcome;

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
        BackupsErrorText = RecordedBackupGoneNotice;
    }

    /// <summary>
    /// Said whenever the bundle an interrupted operation named is absent, whether retention pruned
    /// it or the reader deleted it here — the two leave the reader in the same position, so they
    /// are told the same thing.
    /// </summary>
    internal const string RecordedBackupGoneNotice =
        "The backup that interrupted operation recorded is no longer on disk — it may have been pruned by the " +
        "retention setting or deleted. Any other backup below can still be restored.";

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
