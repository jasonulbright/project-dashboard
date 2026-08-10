using ProjectDashboard.Models;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The backups block on the Settings page: how many captures each repository keeps, whether a
/// capture reaches past the refs, what every repository's backups occupy, and the one action that
/// applies a lowered count to repositories nothing is about to back up.
///
/// Both settings are read fresh by each capture, so neither needs a live-apply consumer — saving
/// is the whole of applying them. What does not follow from a save is retention: a lowered count
/// prunes on the next capture per repository, which is why the block states that and offers the
/// action rather than letting silence imply an effect the setting does not have.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Null when the host supplied none; the block then says it cannot read the folder.</summary>
    private readonly BackupService? _backups;

    [ObservableProperty] private int _backupRetentionCount = 10;

    [ObservableProperty] private bool _deepBackupCapture;

    /// <summary>What is on disk now. Read fresh on every load and after a prune, never cached.</summary>
    [ObservableProperty] private string _backupStorageSummary = "";

    /// <summary>What the last prune did, or why it did nothing.</summary>
    [ObservableProperty] private string _backupPruneStatus = "";

    /// <summary>
    /// The storage read this page last started. Held so a caller waits for the read rather than
    /// polling what it writes: an empty summary before the read and after it say different things.
    /// </summary>
    internal Task BackupStorageLoad { get; private set; } = Task.CompletedTask;

    internal const string BackupsUnavailable =
        "Backups are unavailable — the backup store was not configured for this session.";

    private void LoadBackupSettings(AppSettings settings)
    {
        BackupRetentionCount = settings.BackupRetentionCount;
        DeepBackupCapture = settings.DeepBackupCapture;
        RefreshBackupStorage();
    }

    /// <summary>
    /// Clamped on the way to disk to the floor the service itself applies, and read back into the
    /// field: a page that kept showing a zero the service silently treats as one would leave the
    /// reader believing a setting this app does not honour.
    /// </summary>
    private void SaveBackupSettings(AppSettings settings)
    {
        settings.BackupRetentionCount = BackupService.EffectiveRetention(BackupRetentionCount);
        BackupRetentionCount = settings.BackupRetentionCount;
        settings.DeepBackupCapture = DeepBackupCapture;
    }

    private void RefreshBackupStorage() => BackupStorageLoad = RefreshBackupStorageAsync();

    /// <summary>
    /// Off the dispatcher: one directory listing per repository over a folder that can hold
    /// hundreds of bundles, on a volume a PD_DATA_DIR override may put on a share.
    /// </summary>
    private async Task RefreshBackupStorageAsync()
    {
        if (_backups is { } backups)
        {
            var tally = await Task.Run(backups.MeasureStorage);
            BackupStorageSummary = DescribeStorage(tally);
            return;
        }
        BackupStorageSummary = BackupsUnavailable;
    }

    /// <summary>
    /// What the backup folder holds. A walk that did not finish is stated as a floor: the count
    /// and the size are then what was reached, and presenting them as the total would understate
    /// what a prune has to remove.
    /// </summary>
    internal static string DescribeStorage(BackupStorageTally tally)
    {
        if (tally.BackupCount == 0)
            return tally.Error is null
                ? "No backups are on disk."
                : $"No backups were counted, and the folder could not be read in full — {tally.Error}.";

        // Every form of this line opens with a digit, so the floor reads as one sentence.
        var held = $"{Backups(tally.BackupCount)} across {Repositories(tally.RepoCount)}, " +
                   $"{ByteSizeText.Describe(tally.Bytes)}.";
        return tally.Error is null
            ? held
            : $"At least {held} The folder could not be read in full — {tally.Error}.";
    }

    private static string Backups(int count) => count == 1 ? "1 backup" : $"{count} backups";

    private static string Repositories(int count) => count == 1 ? "1 repository" : $"{count} repositories";

    /// <summary>
    /// Applies the saved retention count to every repository at once. Retention is otherwise
    /// applied only by a repository's next capture, so a lowered count leaves an untouched
    /// repository over its limit indefinitely; this is what closes that.
    ///
    /// The count is read from disk, not from the field: an unsaved edit on screen is not what the
    /// captures use, and pruning to it would remove backups the saved setting says to keep.
    /// </summary>
    [RelayCommand]
    private async Task PruneBackupsNow()
    {
        if (_backups is not { } backups)
        {
            BackupPruneStatus = BackupsUnavailable;
            return;
        }

        var plan = await Task.Run(backups.PreviewPrune);
        if (plan.BackupCount == 0)
        {
            BackupPruneStatus = plan.Error is null
                ? "Nothing to prune — every repository is within the saved count of " +
                  $"{Backups(BackupService.EffectiveRetention(_settingsService.Load().BackupRetentionCount))}."
                : $"Nothing was pruned. The backup folder could not be read in full — {plan.Error}.";
            RefreshBackupStorage();
            return;
        }

        if (!await ConfirmAsync("Prune old backups?", PruneMessage(plan), "Prune")) return;

        var removed = await Task.Run(backups.PruneEveryRepository);
        BackupPruneStatus = DescribePrune(removed);
        RefreshBackupStorage();
    }

    /// <summary>
    /// What the prune would remove, stated before anything is deleted. The size is called an
    /// estimate because it is measured now and the delete happens after the reader answers: a
    /// capture landing in that window changes which backups are past the limit, and a figure
    /// presented as exact would be wrong in exactly the case the reader would notice.
    /// </summary>
    internal static string PruneMessage(BackupStorageTally plan) =>
        $"Prune {Backups(plan.BackupCount)} from {Repositories(plan.RepoCount)}?\n\n" +
        $"    About {ByteSizeText.Describe(plan.Bytes)} would be freed\n" +
        $"    Newest backups are kept; only those past the retention count go\n" +
        (plan.Error is null ? "" : $"    The folder could not be read in full, so more may be past the limit than this counts — {plan.Error}\n") +
        "\nThis removes bundles and their refs snapshots from this app's backup folder. Nothing in any " +
        "repository changes, and it cannot be undone. A backup an interrupted operation recorded goes " +
        "with the rest if it is past the count.";

    /// <summary>
    /// What the prune did, read from the files afterwards. A backup another process held open is
    /// still on disk and is not counted as reclaimed, and saying so is the difference between a
    /// reader checking their free space and a reader trusting a number.
    /// </summary>
    internal static string DescribePrune(BackupStorageTally removed)
    {
        if (removed.BackupCount == 0)
            return removed.Error is null
                ? "Nothing was pruned."
                : $"Nothing was pruned — {removed.Error}.";

        var did = $"Pruned {Backups(removed.BackupCount)} from {Repositories(removed.RepoCount)}, " +
                  $"freeing {ByteSizeText.Describe(removed.Bytes)}.";
        return removed.Error is null ? did : $"{did} Some were left — {removed.Error}.";
    }

    /// <summary>
    /// Overridable so the confirmed prune is reachable without a message pump, the same seam the
    /// project page's own confirmations use.
    /// </summary>
    internal virtual async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel"
        };
        Helpers.DialogKeyGuard.Install(dialog);
        return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
