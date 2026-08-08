using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The reflog viewer and the deep clean, which are two halves of one fact: after a rewrite the
/// replaced commits are still in the repository, and the reflog is where they are still reachable
/// from.
///
/// The viewer is read-only apart from one mutation — checking a recorded state out as a NEW branch,
/// which adds a ref and moves nothing. Every operation is bound to the entry's object id rather
/// than its <c>@{n}</c> selector: a selector is a position in a list that shifts as new entries
/// are recorded, so a click on row three could reach a different state than the row described.
///
/// The deep clean is the opposite kind of action and carries the opposite gates: it is behind the
/// danger-zone opt-in, takes a typed repository name, refuses while an interrupted operation is
/// recorded, and reports the reclaim it measured rather than the one it intended.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Null when the host supplied none; the deep clean then refuses instead of pretending it ran.</summary>
    private readonly DeepCleanService? _deepClean;

    // ── The viewer ──────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _reflogVisible;

    partial void OnReflogVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SafetyOverlayHidden));
        OnPropertyChanged(nameof(MaintenanceOverlayHidden));
    }

    /// <summary>The refs that have a reflog worth offering: HEAD, then every local branch.</summary>
    [ObservableProperty] private ObservableCollection<string> _reflogRefChoices = [];

    [ObservableProperty] private string? _selectedReflogRef;

    /// <summary>
    /// The read the ref picker started and did not await. Held so a caller — and a headless test —
    /// can wait for the list the selection asked for instead of polling the properties it writes.
    /// </summary>
    internal Task ReflogRefresh { get; private set; } = Task.CompletedTask;

    partial void OnSelectedReflogRefChanged(string? value)
    {
        if (ReflogVisible && value is not null) ReflogRefresh = LoadReflog();
    }

    [ObservableProperty] private ObservableCollection<ReflogEntry> _reflogEntries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckOutReflogEntryCommand))]
    private ReflogEntry? _selectedReflogEntry;

    /// <summary>True once a read has finished and found nothing. The empty state must not show before that.</summary>
    [ObservableProperty] private bool _reflogEmpty;

    [ObservableProperty] private string _reflogStatusText = "";
    [ObservableProperty] private string _reflogErrorText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckOutReflogEntryCommand))]
    private string _reflogBranchName = "";

    [RelayCommand]
    private async Task OpenReflog()
    {
        if (RepoPath.Length == 0 || ForcePushVisible) return;
        ReflogErrorText = "";
        ReflogStatusText = "";
        ReflogBranchName = "";
        SelectedReflogEntry = null;
        ReflogVisible = true;
        await LoadReflogRefs();
    }

    [RelayCommand]
    private void CloseReflog()
    {
        ReflogVisible = false;
        ReflogEntries = [];
        ReflogRefChoices = [];
        SelectedReflogRef = null;
        SelectedReflogEntry = null;
        ReflogBranchName = "";
        ReflogStatusText = "";
        ReflogErrorText = "";
        ReflogEmpty = false;
    }

    /// <summary>Drops the viewer as the page leaves this repository; the list it holds describes a repository the page no longer shows.</summary>
    private void CloseReflogOnProjectSwitch()
    {
        if (!ReflogVisible) return;
        CloseReflog();
    }

    /// <summary>
    /// Rebuilds the ref picker. HEAD is first and preselected: it is the only reflog that records
    /// every checkout, reset, and rewrite in one place, so it is where a reader looking for a lost
    /// state starts.
    /// </summary>
    [RelayCommand]
    private async Task LoadReflogRefs()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;

        var branches = await _gitService.GetBranchesAsync(repo);
        if (!IsCurrent(gen)) return;

        var previous = SelectedReflogRef;
        ReflogRefChoices = new ObservableCollection<string>(
            new[] { "HEAD" }.Concat(branches.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal)));
        var next = previous is not null && ReflogRefChoices.Contains(previous) ? previous : "HEAD";
        // Exactly one read either way: assigning a different value starts it through the change
        // handler, and assigning the same one raises no notification for it to start from.
        if (string.Equals(SelectedReflogRef, next, StringComparison.Ordinal)) ReflogRefresh = LoadReflog();
        else SelectedReflogRef = next;
        await ReflogRefresh;
    }

    private async Task LoadReflog()
    {
        var repo = RepoPath;
        var reference = SelectedReflogRef;
        if (repo.Length == 0 || reference is null) return;
        var gen = _generation;

        var keep = SelectedReflogEntry?.Sha;
        List<ReflogEntry> entries;
        try
        {
            entries = await _gitService.GetReflogAsync(repo, reference);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the reflog of {reference} in {repo}", ex);
            if (IsCurrent(gen)) ReflogErrorText = $"Could not read the reflog for {reference}: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        ReflogErrorText = "";
        ReflogEntries = new ObservableCollection<ReflogEntry>(entries);
        ReflogEmpty = entries.Count == 0;
        SelectedReflogEntry = ReflogEntries.FirstOrDefault(e => e.Sha == keep) ?? ReflogEntries.FirstOrDefault();
    }

    // ── The one mutation ────────────────────────────────────────────────────────

    private bool CanCheckOutReflogEntry() =>
        SelectedReflogEntry is not null && ReflogBranchName.Trim().Length > 0 && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Creates a branch at the selected entry's commit and switches to it. Nothing existing moves:
    /// this is the only way out of the viewer that changes the repository, and all it can do is
    /// add a ref.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckOutReflogEntry))]
    private async Task CheckOutReflogEntry()
    {
        var entry = SelectedReflogEntry;
        var name = ReflogBranchName.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (entry is null || name.Length == 0 || repo.Length == 0 || IsBusy) return;

        if (!await _gitService.IsValidBranchNameAsync(repo, name))
        {
            ReflogErrorText = $"“{name}” is not a valid branch name. Branch names cannot contain spaces, “..”, “~”, " +
                              "“^”, “:”, “?”, “*”, “[”, a leading dash, or a trailing “/” or “.lock”.";
            return;
        }
        if (Branches.Any(b => string.Equals(b.Name, name, StringComparison.Ordinal)))
        {
            ReflogErrorText = $"A branch called “{name}” already exists here. Choose another name.";
            return;
        }

        var confirmed = await ConfirmPrompt(
            "Check this state out as a new branch?",
            $"Create {name} at {entry.ShortSha} — recorded by “{entry.Description}” — and switch to it?\n\n" +
            "Nothing existing moves: this adds a branch and changes which one is checked out. Uncommitted changes " +
            "are carried onto the new branch, and git refuses the switch outright if they would be overwritten.",
            "Create branch");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            ReflogStatusText = ProjectSwitchedNotice("Branch creation");
            return;
        }

        ReflogErrorText = "";
        // Bound to the object id, not to the @{n} selector the row shows: the selector is a
        // position, and the reflog can gain entries between the read and this click.
        var ok = await RunOp(r => _gitService.CreateBranchAtAsync(r, name, entry.Sha),
            $"Create {name} at {entry.ShortSha}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            // The op reports into the sync pane; the click was made in this one, so its failure is
            // restated here or the click reads as having done nothing.
            ReflogErrorText = SyncStatusText;
            ReflogStatusText = "The branch was not created.";
            return;
        }

        ReflogStatusText = $"Created {name} at {entry.ShortSha} and switched to it. Nothing else moved.";
        ReflogBranchName = "";
        await ReloadCommitsAsync();
        await LoadBranches();
        await LoadReflogRefs();
    }

    // ── Deep clean ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _deepCleanStatusText = "";

    /// <summary>Shown instead of a silent return when the danger zone is switched off.</summary>
    internal const string DeepCleanDangerZoneOffNotice =
        "Deep clean is off. Turn on the danger zone in Settings first.";

    /// <summary>
    /// Expires every reflog and prunes the object store, so a completed rewrite's replaced commits
    /// stop being reachable and stop occupying space.
    ///
    /// Four independent gates: the danger-zone opt-in re-read here rather than trusted from the
    /// bound flag, a typed repository name, the generation guard for a switch landing while the
    /// prompt is open, and the service's own refusals — an interrupted operation on record, a
    /// stash stack, or a repository mid-rebase.
    /// </summary>
    [RelayCommand]
    private async Task DeepClean()
    {
        var service = _deepClean;
        var repo = RepoPath;
        if (repo.Length == 0) return;
        if (service is null)
        {
            DeepCleanStatusText = "Deep clean is unavailable — the maintenance service was not configured for this session.";
            return;
        }

        // Re-read, not the bound property: the panel's visibility is a rendering decision, and a
        // command reachable from the keyboard must enforce the gate itself.
        RefreshDangerZoneGate();
        if (!DangerZoneEnabled)
        {
            DeepCleanStatusText = DeepCleanDangerZoneOffNotice;
            return;
        }
        if (IsBusy)
        {
            DeepCleanStatusText = "Another operation is running on this repository — wait for it to finish.";
            return;
        }

        var gen = _generation;
        var name = RepoDisplayName();

        // Stated before the reader types: a refusal the service already knows about must not cost
        // a typed repository name to discover.
        if (await service.DescribeBlockerAsync(repo) is { } blocker)
        {
            if (IsCurrent(gen)) DeepCleanStatusText = blocker;
            return;
        }
        if (!IsCurrent(gen)) return;

        var typed = await PromptForTextAsync("Deep clean this repository?", DeepCleanMessage(name), "Deep clean");
        if (!RepoNameConfirmed(typed, name))
        {
            if (typed is not null) DeepCleanStatusText = $"Nothing was cleaned — that isn't {name}.";
            return;
        }
        if (!IsCurrent(gen))
        {
            DeepCleanStatusText = ProjectSwitchedNotice("Deep clean");
            return;
        }
        if (IsBusy)
        {
            DeepCleanStatusText = BusyGateNotice("Deep clean");
            return;
        }

        var holder = new object();
        IsBusy = true;
        _busyGateHolder = holder;
        DeepCleanStatusText = "Expiring the reflogs and pruning…";
        try
        {
            var result = await service.RunAsync(repo);
            if (!IsCurrent(gen)) return;
            DeepCleanStatusText = DescribeDeepClean(result);
            if (result.Success)
            {
                // The viewer, if it is open, is showing entries that no longer exist.
                if (ReflogVisible) ReflogRefresh = LoadReflog();
                await ReflogRefresh;
                await SafeRefreshWorkingStateAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"deep clean failed for {repo}", ex);
            if (IsCurrent(gen))
                DeepCleanStatusText =
                    "The deep clean failed before it could report where it stopped, so the reflogs may already be " +
                    $"expired even though nothing was pruned. {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_busyGateHolder, holder))
            {
                _busyGateHolder = null;
                if (IsCurrent(gen)) IsBusy = false;
            }
        }
    }

    /// <summary>
    /// What the reader is agreeing to. It states both sides of recoverability outright, because
    /// the whole point of the action is that some of what it removes cannot come back.
    /// </summary>
    internal static string DeepCleanMessage(string name) =>
        $"Expire every reflog in {name} and prune the object store?\n\n" +
        "A completed history rewrite leaves the commits it replaced in this repository — the reflog still points at " +
        "the old branch tips, which is what keeps them, and everything under them, reachable. This is what removes " +
        "them for good.\n\n" +
        "Still recoverable afterwards: whatever a backup bundle captured. Each bundle holds every ref as it stood " +
        "when it was taken, so the pre-rewrite history is in the bundle written before that rewrite, and Backups can " +
        "restore it.\n\n" +
        "Not recoverable afterwards: anything that only ever lived in a reflog — a commit you amended, a commit a " +
        "reset moved away from, any state reached after the last backup was taken. A bundle never held those.\n\n" +
        $"Type {name} to confirm.";

    /// <summary>
    /// The outcome in the terms the action was sold in. A reclaim is reported only where both
    /// measurements were taken, and a store that grew is said to have grown rather than dressed up
    /// as a reclaim of zero. Nothing here knows whether a backup bundle was ever taken, so the
    /// recoverability line is conditional in the same way the confirmation's is.
    /// </summary>
    internal static string DescribeDeepClean(DeepCleanResult result)
    {
        if (result.RefusalReason is { } refusal)
            return refusal;
        if (!result.Measured)
            return "Deep clean finished. The replaced history is no longer reachable in this repository. " +
                   "The object store could not be measured, so how much was reclaimed is unknown.";

        var before = result.Before!;
        var after = result.After!;
        var reclaimed = result.ReclaimedKiB;
        var size = reclaimed > 0
            ? $"reclaimed {DescribeKiB(reclaimed)}"
            : reclaimed == 0
                ? "reclaimed nothing measurable"
                : $"grew by {DescribeKiB(-reclaimed)}, because repacking cost more than the prune saved";
        return $"Deep clean finished and {size}: {DescribeKiB(before.TotalKiB)} before, " +
               $"{DescribeKiB(after.TotalKiB)} after, {before.TotalObjects:N0} objects down to {after.TotalObjects:N0}. " +
               "The commits the rewrite replaced are no longer reachable here; whatever a backup bundle captured is " +
               "still in that bundle, and Backups can restore it.";
    }

    private static string DescribeKiB(long kib) => kib switch
    {
        < 1024 => $"{kib:N0} KB",
        < 1024 * 1024 => $"{kib / 1024.0:N1} MB",
        _ => $"{kib / (1024.0 * 1024):N2} GB",
    };
}
