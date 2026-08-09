using System.Diagnostics;
using System.Globalization;
using System.IO;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Whether the backup a record names can still be reached. <see cref="Unknown"/> is a backup
/// folder that could not be read, which is not the same fact as a bundle having been pruned.
/// </summary>
public enum RecordedBackupState
{
    None,
    Available,
    Pruned,
    Unknown
}

/// <summary>
/// One category the ledger actually holds, as a filter. Counts come from the records read, so a
/// chip is never offered for a category this repository has no record of.
/// </summary>
public sealed partial class OperationHistoryFilter : ObservableObject
{
    /// <summary>The literal used for the chip that clears the filter.</summary>
    public const string AllKey = "all";

    public required string Key { get; init; }

    public required string Label { get; init; }

    public required int Count { get; init; }

    [ObservableProperty] private bool _isActive;

    public string Chip => $"{Label} ({Count})";

    public string AccessibleName =>
        $"{Label}, {Count} operation(s){(IsActive ? ", selected" : "")}";

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(AccessibleName));
}

/// <summary>
/// One recorded operation as the overlay shows it. Every string is composed from the record and
/// nothing is inferred: a row states what was written, including that a backup it names is gone.
/// </summary>
public sealed class OperationHistoryRow
{
    public required OperationRecord Record { get; init; }

    public required string When { get; init; }

    public required string Label { get; init; }

    public required string Outcome { get; init; }

    public required RecordedBackupState BackupState { get; init; }

    /// <summary>Empty when the operation took no backup, so the row shows no backup line at all.</summary>
    public required string Backup { get; init; }

    /// <summary>Empty when the record is an ordinary operation rather than a recovering action.</summary>
    public required string Recovery { get; init; }

    public required string Detail { get; init; }

    public string Category => Record.Category.ToString();

    public bool HasDetail => Detail.Length > 0;

    public bool CanOpenBackup => BackupState == RecordedBackupState.Available;

    /// <summary>
    /// Composed here rather than in markup: each part carries its own separator, so an operation
    /// with no backup and no recovery is announced without trailing punctuation for absent values.
    /// </summary>
    public string AccessibleName =>
        $"{When}, {Label}, {Outcome}"
        + (Backup.Length > 0 ? $", {Backup}" : "")
        + (Recovery.Length > 0 ? $", {Recovery}" : "");
}

/// <summary>
/// The durable answer to "what did this app do to this repository, and what came of it".
///
/// A read surface only: nothing here mutates a repository. The one action it offers is opening a
/// surface that already carries its own gates — the Backups browser at the bundle a record names.
///
/// It reports the limits of what it holds rather than presenting a tail as a complete account:
/// operations run from a terminal were never recorded here, records rotate out, and the list is
/// capped. Each of those is stated on the surface.
/// </summary>
public partial class ProjectDetailViewModel
{
    private readonly OperationHistory _history;

    /// <summary>The record the most recent operation wrote, so an offered inverse or a retry can link back to it.</summary>
    private string _lastOperationRecordId = "";

    /// <summary>The record of the most recent failure, which the outcome line's affordance opens.</summary>
    private string _failedOperationRecordId = "";

    /// <summary>Rows as read, before the category filter; the filter never re-reads the ledger.</summary>
    private List<OperationHistoryRow> _operationHistoryAll = [];

    [ObservableProperty] private bool _operationHistoryVisible;

    partial void OnOperationHistoryVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SafetyOverlayHidden));
        OnPropertyChanged(nameof(MaintenanceOverlayHidden));
    }

    /// <summary>Offered beside a failed outcome so the reader can reach the record that explains it.</summary>
    [ObservableProperty] private bool _operationHistoryHintVisible;

    [ObservableProperty] private ObservableCollection<OperationHistoryRow> _operationHistoryRows = [];

    [ObservableProperty] private ObservableCollection<OperationHistoryFilter> _operationHistoryFilters = [];

    [ObservableProperty] private OperationHistoryRow? _selectedOperationHistoryRow;

    [ObservableProperty] private string _operationHistoryFilterKey = OperationHistoryFilter.AllKey;

    /// <summary>True once a read has finished and found nothing. The empty state must not show before that.</summary>
    [ObservableProperty] private bool _operationHistoryEmpty;

    [ObservableProperty] private string _operationHistoryStatusText = "";

    [ObservableProperty] private string _operationHistoryErrorText = "";

    /// <summary>What the list does not cover: where it begins, whether it is a tail, and what was never recorded.</summary>
    [ObservableProperty] private string _operationHistoryLimitsText = "";

    /// <summary>Copy for a repository with nothing on record, which is not the same as one nothing was done to.</summary>
    internal const string OperationHistoryEmptyNotice =
        "No operations recorded for this repository. This list holds what this app did — an operation run from a "
        + "terminal or another tool leaves no record here.";

    public string OperationHistoryEmptyText => OperationHistoryEmptyNotice;

    /// <summary>The read the overlay started and did not await, so a caller waits for the rows rather than polling.</summary>
    internal Task OperationHistoryRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private async Task OpenOperationHistory()
    {
        if (RepoPath.Length == 0 || RewriteWizardVisible) return;
        OperationHistoryErrorText = "";
        OperationHistoryStatusText = "";
        OperationHistoryFilterKey = OperationHistoryFilter.AllKey;
        SelectedOperationHistoryRow = null;
        OperationHistoryVisible = true;
        OperationHistoryRefresh = LoadOperationHistory();
        await OperationHistoryRefresh;
    }

    /// <summary>Opens the overlay on the record that explains the failure the outcome line reported.</summary>
    [RelayCommand]
    private async Task OpenOperationHistoryForFailure()
    {
        var id = _failedOperationRecordId;
        await OpenOperationHistory();
        if (id.Length == 0) return;
        if (OperationHistoryRows.FirstOrDefault(r => r.Record.Id == id) is { } match)
        {
            SelectedOperationHistoryRow = match;
            return;
        }
        OperationHistoryStatusText =
            "That operation is no longer in the retained records. The rest of this repository's history is below.";
    }

    /// <summary>Opens the overlay on the operation that took a backup, from the row that lists the bundle.</summary>
    [RelayCommand]
    private async Task OpenOperationHistoryForBackup(BackupEntry? entry)
    {
        var stamp = entry?.Handle.UtcStamp;
        await OpenOperationHistory();
        if (stamp is null) return;
        if (OperationHistoryRows.FirstOrDefault(r => r.Record.BackupStamp == stamp) is { } match)
        {
            SelectedOperationHistoryRow = match;
            return;
        }
        OperationHistoryStatusText =
            "No retained record names that backup. The sidecar beside the bundle still says what it was taken for.";
    }

    [RelayCommand]
    private void CloseOperationHistory()
    {
        OperationHistoryVisible = false;
        OperationHistoryRows = [];
        OperationHistoryFilters = [];
        _operationHistoryAll = [];
        SelectedOperationHistoryRow = null;
        OperationHistoryStatusText = "";
        OperationHistoryErrorText = "";
        OperationHistoryLimitsText = "";
        OperationHistoryEmpty = false;
    }

    /// <summary>Drops the overlay as the page leaves this repository; the records it lists are that repository's.</summary>
    private void CloseOperationHistoryOnProjectSwitch()
    {
        OperationHistoryHintVisible = false;
        _failedOperationRecordId = "";
        _lastOperationRecordId = "";
        if (!OperationHistoryVisible) return;
        CloseOperationHistory();
    }

    [RelayCommand]
    private async Task LoadOperationHistory()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;
        var keep = SelectedOperationHistoryRow?.Record.Id;

        OperationHistoryPage page;
        IReadOnlyCollection<string>? stamps;
        try
        {
            page = _history.Tail(repo);
            stamps = await ReadBackupStampsAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the operation history of {repo}", ex);
            if (IsCurrent(gen))
            {
                OperationHistoryErrorText = $"Could not read this repository's operation history: {ex.Message}";
                OperationHistoryEmpty = false;
            }
            return;
        }
        if (!IsCurrent(gen)) return;

        // A read that failed and a repository with nothing recorded produce the same empty list;
        // showing the empty state for the first claims a fact the read never established.
        OperationHistoryErrorText = page.ReadError is null
            ? ""
            : $"Part of this repository's operation history could not be read: {page.ReadError}";

        _operationHistoryAll = page.Records.Select(r => Describe(r, stamps)).ToList();
        OperationHistoryEmpty = page.ReadError is null && _operationHistoryAll.Count == 0;
        OperationHistoryLimitsText = DescribeLimits(page);
        RebuildOperationHistoryFilters();
        ApplyOperationHistoryFilter();
        SelectedOperationHistoryRow =
            OperationHistoryRows.FirstOrDefault(r => r.Record.Id == keep) ?? OperationHistoryRows.FirstOrDefault();
    }

    /// <summary>
    /// The bundles still on disk, so a record naming one that retention has since pruned says so
    /// rather than offering a link to nothing. Null when the folder could not be read or no backup
    /// service was configured; every row then reports its backup state as unknown, which is the
    /// honest answer and not the same one as pruned.
    /// </summary>
    private async Task<IReadOnlyCollection<string>?> ReadBackupStampsAsync(string repo)
    {
        if (_backups is null) return null;
        try
        {
            var handles = await _backups.ListBackupsAsync(repo);
            return handles.Select(h => h.UtcStamp).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list backups while reading the operation history of {repo}", ex);
            return null;
        }
    }

    [RelayCommand]
    private void SelectOperationHistoryFilter(string? key)
    {
        OperationHistoryFilterKey = string.IsNullOrEmpty(key) ? OperationHistoryFilter.AllKey : key;
        var keep = SelectedOperationHistoryRow?.Record.Id;
        ApplyOperationHistoryFilter();
        SelectedOperationHistoryRow =
            OperationHistoryRows.FirstOrDefault(r => r.Record.Id == keep) ?? OperationHistoryRows.FirstOrDefault();
    }

    private void RebuildOperationHistoryFilters()
    {
        var filters = new List<OperationHistoryFilter>
        {
            new() { Key = OperationHistoryFilter.AllKey, Label = "All", Count = _operationHistoryAll.Count }
        };
        filters.AddRange(_operationHistoryAll
            .GroupBy(r => r.Record.Category)
            .OrderBy(g => g.Key)
            .Select(g => new OperationHistoryFilter
            {
                Key = g.Key.ToString(),
                Label = CategoryLabel(g.Key),
                Count = g.Count()
            }));

        if (!filters.Any(f => f.Key == OperationHistoryFilterKey))
            OperationHistoryFilterKey = OperationHistoryFilter.AllKey;
        foreach (var filter in filters) filter.IsActive = filter.Key == OperationHistoryFilterKey;
        OperationHistoryFilters = new ObservableCollection<OperationHistoryFilter>(filters);
    }

    private void ApplyOperationHistoryFilter()
    {
        foreach (var filter in OperationHistoryFilters) filter.IsActive = filter.Key == OperationHistoryFilterKey;
        OperationHistoryRows = new ObservableCollection<OperationHistoryRow>(
            OperationHistoryFilterKey == OperationHistoryFilter.AllKey
                ? _operationHistoryAll
                : _operationHistoryAll.Where(r => r.Category == OperationHistoryFilterKey));
    }

    /// <summary>
    /// What the list does not cover. Stated whatever the outcome of the read: a tail presented
    /// without its limits reads as the whole account of a repository.
    /// </summary>
    internal static string DescribeLimits(OperationHistoryPage page)
    {
        if (page.Records.Count == 0 && page.ReadError is null) return "";

        var began = page.OldestRetainedUtc is { } oldest
            ? $"These records begin {oldest.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}. "
            : "";
        var truncated = page.Truncated
            ? $"Only the newest {OperationHistory.DefaultTailCount} are listed. "
            : "";
        var rotated = page.Rotated ? "Records older than that have been rotated out. " : "";
        return began + truncated + rotated
            + "Operations run from a terminal or another tool were never recorded here.";
    }

    private static OperationHistoryRow Describe(OperationRecord record, IReadOnlyCollection<string>? stamps)
    {
        var state = record.BackupStamp is not { Length: > 0 } stamp
            ? RecordedBackupState.None
            : stamps is null
                ? RecordedBackupState.Unknown
                : stamps.Contains(stamp)
                    ? RecordedBackupState.Available
                    : RecordedBackupState.Pruned;

        return new OperationHistoryRow
        {
            Record = record,
            When = record.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            Label = record.Label,
            Outcome = OutcomeLabel(record.Outcome),
            BackupState = state,
            Backup = BackupLabel(state),
            Recovery = record.Recovery is { } recovery ? RecoveryLabel(recovery.Kind) : "",
            Detail = record.Detail
        };
    }

    internal static string OutcomeLabel(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Succeeded => "Succeeded",
        OperationOutcome.Failed => "Failed",
        OperationOutcome.Refused => "Refused before it ran",
        OperationOutcome.Cancelled => "Cancelled",
        OperationOutcome.Interrupted => "Interrupted",
        _ => "Outcome unknown"
    };

    internal static string BackupLabel(RecordedBackupState state) => state switch
    {
        RecordedBackupState.Available => "Backup on disk",
        RecordedBackupState.Pruned => "Backup pruned",
        RecordedBackupState.Unknown => "Backup folder unreadable",
        _ => ""
    };

    internal static string RecoveryLabel(RecoveryKind kind) => kind switch
    {
        RecoveryKind.RestoreFromBackup => "Restored from a backup",
        RecoveryKind.UndoOffered => "Ran the offered inverse",
        RecoveryKind.StaleLockCleared => "Retried after a stale lock was cleared",
        _ => "Interrupted-operation marker discarded"
    };

    internal static string CategoryLabel(OperationCategory category) => category switch
    {
        OperationCategory.ForcePush => "Force push",
        OperationCategory.DeepClean => "Deep clean",
        OperationCategory.BackupRestore => "Backup restore",
        _ => category.ToString()
    };

    /// <summary>
    /// Opens the Backups browser at the bundle the selected record names. The browser carries the
    /// restore's own gates; nothing is restored from here.
    /// </summary>
    [RelayCommand]
    private async Task OpenBackupForRecord(OperationHistoryRow? row)
    {
        if (row is null || !row.CanOpenBackup) return;
        var stamp = row.Record.BackupStamp;
        CloseOperationHistory();
        await OpenBackups();
        if (stamp is null) return;
        if (BackupList.FirstOrDefault(e => e.Handle.UtcStamp == stamp) is { } match) SelectedBackup = match;
        else BackupsErrorText = "That backup is no longer on disk — retention may have pruned it since this list was read.";
    }

    /// <summary>
    /// Shows the diagnostic log in the shell. The log holds the swallowed failures a record's
    /// one-line detail cannot carry, and this is the only route to it from inside the app.
    /// </summary>
    [RelayCommand]
    private void RevealOperationLog()
    {
        var failure = RevealInShell(AppPaths.LogFile);
        OperationHistoryStatusText = failure is null
            ? $"Opened {AppPaths.LogFile} in the shell."
            : $"Could not open {AppPaths.LogFile}: {failure}";
    }

    /// <summary>
    /// Overridable so the command is exercisable without a shell. Returns null on success, else the
    /// failure to report — an uncaught Win32Exception here would reach the dispatcher.
    /// </summary>
    internal virtual string? RevealInShell(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not reveal {path}", ex);
            return ex.Message;
        }
    }
}
