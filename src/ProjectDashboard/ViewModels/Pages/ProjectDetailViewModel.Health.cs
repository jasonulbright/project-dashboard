using ProjectDashboard.Services;
using ProjectDashboard.Services.Health;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>The one thing a health row offers. Every value runs a read or hands off to a gated surface.</summary>
public enum HealthRowAction
{
    None,

    /// <summary>Runs this row's deep check, or runs it again.</summary>
    Run,

    /// <summary>Opens the rewrite wizard with this object's path already in the purge field.</summary>
    Purge,
}

/// <summary>
/// One line of the health page: a check, or one of the objects a check listed. Both shapes share a
/// row type so the page is one flat list — the object rows belong under the check that produced
/// them, and a nested items control would put a second selection model on the surface.
/// </summary>
public sealed class HealthRow
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required HealthState State { get; init; }

    public required string Summary { get; init; }

    public string Detail { get; init; } = "";

    public HealthRowAction Action { get; init; } = HealthRowAction.None;

    public string ActionLabel { get; init; } = "";

    /// <summary>Set only on an object row whose path a ref still names; empty rows offer no hand-off.</summary>
    public string PurgePath { get; init; } = "";

    /// <summary>True for a row the deep tier owns, which is what the page marks as costing something.</summary>
    public bool IsDeep { get; init; }

    /// <summary>True for one of the objects a check listed, which is indented under it.</summary>
    public bool IsObject { get; init; }

    public bool HasAction => ActionLabel.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// The state in words. An object row carries none: it is a listing, not a verdict, and a
    /// state word beside it would read as one.
    /// </summary>
    public string StateLabel => IsObject ? "" : State switch
    {
        HealthState.Ok => "OK",
        HealthState.Warn => "Worth a look",
        HealthState.Bad => "Needs attention",
        HealthState.Unknown => "Could not be measured",
        HealthState.NotApplicable => "Not applicable",
        _ => "Not run",
    };

    /// <summary>
    /// Composed here rather than in markup: each part carries its own separator, so a row with no
    /// state word and no detail is announced without punctuation around the values it lacks.
    /// </summary>
    public string AccessibleName =>
        Title
        + (StateLabel.Length > 0 ? $", {StateLabel}" : "")
        + (Summary.Length > 0 ? $", {Summary}" : "")
        + (Detail.Length > 0 ? $", {Detail}" : "");
}

/// <summary>
/// The Health tab: what this application can tell a reader about one repository, with the cost of
/// each answer stated rather than hidden.
///
/// Two tiers, and the tab never blurs them. The quick tier is local reads that run once on tab
/// activation and on an explicit refresh. The deep tier reads every object, reaches a network, or
/// verifies a bundle, and runs only when its own button is pressed — never on a timer, never on a
/// scan, and never by escalation from a cheap answer. A connectivity pass that comes back clean
/// says so and does not go on to read object contents.
///
/// Every read here is read-only and leaseless. The page refuses to start a check against a
/// repository another operation holds, and it watches the busy registry for the duration of one:
/// a repository that became busy mid-check had its object store written under the reading, so the
/// result is reported as unmeasured rather than as a verdict about a store in motion.
///
/// Nothing on this tab writes to the repository. The one place it acts is the hand-off from a
/// listed object into the rewrite wizard's purge field, which carries every gate that wizard has.
/// </summary>
public partial class ProjectDetailViewModel
{
    private RepoHealthScanner? _healthScanner;

    private RepoHealthScanner HealthScanner =>
        _healthScanner ??= new RepoHealthScanner(_gitService, _backups, _history);

    private readonly Dictionary<string, (HealthCheck Check, DateTimeOffset At)> _healthDeep = new(StringComparer.Ordinal);
    private IReadOnlyList<HealthCheck> _healthQuick = [];
    private IReadOnlyList<LargeObject> _healthLargeObjects = [];
    private DateTimeOffset? _healthQuickAt;

    /// <summary>
    /// The two runs carry separate tokens. One field would leave a quick refresh reading under the
    /// token a deep check owns, so cancelling either would stop both — and the tier split is the
    /// one thing this page must not blur.
    /// </summary>
    private CancellationTokenSource? _healthCts;

    private CancellationTokenSource? _healthQuickCts;

    /// <summary>
    /// Real "loaded" flag rather than a non-empty list: the quick tier produces rows even when
    /// every read failed, so the list cannot stand in for whether it ran.
    /// </summary>
    [ObservableProperty] private bool _healthLoaded;

    [ObservableProperty] private ObservableCollection<HealthRow> _healthRows = [];

    [ObservableProperty] private string _healthHeaderText = HealthCopy.NeverChecked;

    [ObservableProperty] private string _healthTierText = HealthCopy.QuickTierScope;

    [ObservableProperty] private string _healthStatusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelHealthCheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshHealthCommand))]
    private bool _healthCheckRunning;

    /// <summary>The read the tab started and did not await, so a caller waits for the rows rather than polling.</summary>
    internal Task HealthRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The quick tier, on tab activation. Guarded by <see cref="HealthLoaded"/> so a revisit is
    /// inert, matching every other lazy surface on this page.
    /// </summary>
    [RelayCommand]
    private async Task LoadHealth()
    {
        if (HealthLoaded) return;
        await RunQuickHealthChecksAsync();
    }

    private bool CanRefreshHealth() => !HealthCheckRunning;

    [RelayCommand(CanExecute = nameof(CanRefreshHealth))]
    private Task RefreshHealth() => RunQuickHealthChecksAsync();

    private Task RunQuickHealthChecksAsync()
    {
        HealthRefresh = QuickHealthChecksAsync();
        return HealthRefresh;
    }

    private async Task QuickHealthChecksAsync()
    {
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;

        if (_busyRegistry.IsBusy(repo))
        {
            HealthStatusText = SafetyCopy.RepoBusyRefusal;
            RebuildHealthRows();
            return;
        }

        HealthStatusText = "Running the local checks…";
        _healthQuickCts?.Dispose();
        _healthQuickCts = new CancellationTokenSource();
        IReadOnlyList<HealthCheck> checks;
        try
        {
            checks = await HealthScanner.QuickAsync(repo, _healthQuickCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"the health quick tier failed for {repo}", ex);
            if (IsCurrent(gen)) HealthStatusText = $"The local checks could not be run: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        _healthQuick = checks;
        _healthQuickAt = DateTimeOffset.Now;
        HealthLoaded = true;
        HealthStatusText = "";
        RebuildHealthRows();
    }

    /// <summary>
    /// Rebuilds every row from the answers on hand. A deep check nobody ran renders as its own
    /// row in <see cref="HealthState.NotRun"/>: a page that showed only what it measured would
    /// read as though the rest had nothing to report.
    /// </summary>
    private void RebuildHealthRows()
    {
        var rows = new List<HealthRow>();
        foreach (var check in _healthQuick) rows.Add(CheckRow(check, at: null));

        foreach (var placeholder in RepoHealthScanner.DeepNotRun())
        {
            var known = _healthDeep.TryGetValue(placeholder.Id, out var entry);
            rows.Add(CheckRow(known ? entry.Check : placeholder, known ? entry.At : null));
            if (placeholder.Id == HealthCheckId.LargeObjects && known)
                rows.AddRange(_healthLargeObjects.Select(ObjectRow));
        }

        HealthRows = new ObservableCollection<HealthRow>(rows);
        HealthHeaderText = HealthCopy.LastChecked(_healthQuickAt);
    }

    /// <summary>
    /// One check as a row. A deep answer carries the moment it was taken: nothing on this page
    /// re-runs an expensive check on its own, so a result left on screen from an earlier press
    /// reads as current unless it says when it was taken.
    /// </summary>
    private static HealthRow CheckRow(HealthCheck check, DateTimeOffset? at)
    {
        var deep = check.Tier == HealthTier.Deep;
        return new HealthRow
        {
            Id = check.Id,
            Title = check.Title,
            State = check.State,
            Summary = check.Summary + (at is null ? "" : $" As of {SafetyCopy.Stamp(at.Value)}."),
            Detail = check.Detail,
            IsDeep = deep,
            Action = deep && check.State != HealthState.NotApplicable ? HealthRowAction.Run : HealthRowAction.None,
            ActionLabel = !deep || check.State == HealthState.NotApplicable ? ""
                : check.State == HealthState.NotRun ? "Run"
                : "Run again",
        };
    }

    private static HealthRow ObjectRow(LargeObject entry) => new()
    {
        Id = HealthCheckId.LargeObjects,
        Title = entry.Path.Length > 0 ? entry.Path : entry.Sha[..Math.Min(12, entry.Sha.Length)],
        State = HealthState.Ok,
        Summary = HealthCopy.Bytes(entry.Bytes)
            + (entry.Path.Length > 0 ? "" : " — no ref names this object, so it has no path."),
        Detail = entry.Sha,
        IsObject = true,
        PurgePath = entry.Path,
        Action = entry.Path.Length > 0 ? HealthRowAction.Purge : HealthRowAction.None,
        ActionLabel = entry.Path.Length > 0 ? "Purge…" : "",
    };

    // ── Deep tier ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RunHealthRowAction(HealthRow? row)
    {
        if (row is null || row.Action == HealthRowAction.None) return;

        if (row.Action == HealthRowAction.Purge)
        {
            HandOffToPurge(row);
            return;
        }

        await RunDeepHealthCheckAsync(row.Id);
    }

    /// <summary>
    /// Runs one deep check, or says why it will not. The page runs one at a time, refuses against
    /// a repository another operation holds, and takes no lease of its own — every read here is
    /// read-only, and a twenty-minute lease on a read would block the reader's own work.
    /// </summary>
    private async Task RunDeepHealthCheckAsync(string id)
    {
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;

        if (HealthCheckRunning)
        {
            HealthStatusText = SafetyCopy.CheckAlreadyRunningRefusal;
            return;
        }
        if (_busyRegistry.IsBusy(repo))
        {
            HealthStatusText = SafetyCopy.RepoBusyRefusal;
            return;
        }

        // A repository that becomes busy mid-check has its object store written under the reading,
        // so the answer describes a store in motion. Watched rather than re-tested at the end: an
        // operation that started and finished inside the window would leave no trace to test for.
        var disturbed = 0;
        void OnBusyChanged(string path)
        {
            if (RepoHealthScanner.IsInside(path, repo) || RepoHealthScanner.IsInside(repo, path))
                Interlocked.Exchange(ref disturbed, 1);
        }

        _healthCts = new CancellationTokenSource();
        HealthCheckRunning = true;
        HealthStatusText = $"{TitleFor(id)}… this runs until it finishes or you cancel it.";
        _busyRegistry.Changed += OnBusyChanged;
        try
        {
            var ct = _healthCts.Token;
            var at = DateTimeOffset.Now;
            var check = await RunOneDeepCheckAsync(id, repo, ct);
            if (!IsCurrent(gen)) return;

            if (Volatile.Read(ref disturbed) == 1)
                check = check with
                {
                    State = HealthState.Unknown,
                    Summary = "Another operation ran against this repository while the check was in progress, "
                        + "so this reading describes an object store that was being written and establishes nothing.",
                };

            _healthDeep[id] = (check, at);
            HealthStatusText = $"{check.Title}: {check.Summary}";
        }
        catch (OperationCanceledException)
        {
            // The result is dropped rather than kept: a cancelled check measured part of a store
            // and the row stays as it was, which for a first run is Not run.
            if (IsCurrent(gen))
                HealthStatusText = $"{TitleFor(id)} was cancelled, so nothing was measured.";
        }
        catch (Exception ex)
        {
            Log.Warn($"the health check '{id}' failed for {repo}", ex);
            if (IsCurrent(gen)) HealthStatusText = $"{TitleFor(id)} failed: {ex.Message}";
        }
        finally
        {
            _busyRegistry.Changed -= OnBusyChanged;
            _healthCts?.Dispose();
            _healthCts = null;
            HealthCheckRunning = false;
            if (IsCurrent(gen)) RebuildHealthRows();
        }
    }

    private async Task<HealthCheck> RunOneDeepCheckAsync(string id, string repo, CancellationToken ct)
    {
        switch (id)
        {
            case HealthCheckId.Connectivity:
                return await HealthScanner.CheckConnectivityAsync(repo, ct);
            case HealthCheckId.Strict:
                return await HealthScanner.CheckStrictAsync(repo, ct);
            case HealthCheckId.Reachability:
                return await HealthScanner.CheckReachabilityAsync(repo, ct);
            case HealthCheckId.BackupVerify:
            {
                var (check, result) = await HealthScanner.CheckBackupsAsync(repo, ct);
                ApplyVerificationToBackupRows(result);
                return check;
            }
            case HealthCheckId.LargeObjects:
            {
                var (check, scan) = await HealthScanner.CheckLargeObjectsAsync(repo, ct);
                _healthLargeObjects = scan.Objects;
                return check;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "no deep check answers to this row");
        }
    }

    private static string TitleFor(string id) =>
        RepoHealthScanner.DeepNotRun().FirstOrDefault(c => c.Id == id)?.Title ?? "The check";

    private bool CanCancelHealthCheck() => HealthCheckRunning;

    [RelayCommand(CanExecute = nameof(CanCancelHealthCheck))]
    private void CancelHealthCheck()
    {
        // Written before the cancel, not after: the cancelled run reports what it left behind, and
        // a line written after the token trips can land on top of that report or under it.
        HealthStatusText = "Cancelling — a check that is stopped measures nothing.";
        _healthCts?.Cancel();
    }

    /// <summary>
    /// Stops a running check when the page leaves the visual tree. A deep read outliving the page
    /// that asked for it would go on holding a git process for a repository nobody is looking at.
    /// </summary>
    internal void CancelHealthChecksOnLeave()
    {
        _healthCts?.Cancel();
        _healthQuickCts?.Cancel();
    }

    /// <summary>
    /// Writes this verification back into the Backups browser's rows, so one bundle is not worded
    /// two ways across two surfaces. A pass cut short leaves the bundles it never reached alone:
    /// only a complete pass can call the bundles it did not name verified.
    /// </summary>
    private void ApplyVerificationToBackupRows(SafetyBackupVerification result)
    {
        var complete = result.Error is null && result.Checked == result.OnDisk;
        foreach (var entry in BackupList)
        {
            var stamp = entry.Handle.UtcStamp;
            if (result.FailedStamps.Contains(stamp)) entry.Verification = BundleVerifyState.Failed;
            else if (result.UnknownStamps.Contains(stamp)) entry.Verification = BundleVerifyState.Unknown;
            else if (complete) entry.Verification = BundleVerifyState.Verified;
        }
    }

    /// <summary>
    /// The one place this tab acts, and it acts by handing off: the object's path goes into the
    /// rewrite wizard's purge field and the wizard carries every gate it already has — the dry
    /// run, the typed confirmation, the backup. Nothing is removed here.
    /// </summary>
    private void HandOffToPurge(HealthRow row)
    {
        if (row.PurgePath.Length == 0) return;
        if (IsBusy)
        {
            HealthStatusText = BusyNotice("Purge");
            return;
        }

        OpenRewriteWizardCommand.Execute(null);
        if (!RewriteWizardVisible)
        {
            HealthStatusText = "The rewrite wizard could not be opened for this repository.";
            return;
        }

        RewriteOperationIsPurgePath = true;
        RewritePurgePathsText = row.PurgePath;
        HealthStatusText = $"{row.PurgePath} is in the wizard's purge field. Nothing has been removed.";
    }

    /// <summary>
    /// Drops every health answer and stops a check in flight. Called on a project switch: a result
    /// left standing would describe the repository the page just left.
    /// </summary>
    private void ResetHealthState()
    {
        _healthCts?.Cancel();
        _healthCts?.Dispose();
        _healthCts = null;
        _healthQuickCts?.Cancel();
        _healthQuickCts?.Dispose();
        _healthQuickCts = null;
        HealthCheckRunning = false;
        HealthLoaded = false;
        _healthQuick = [];
        _healthDeep.Clear();
        _healthLargeObjects = [];
        _healthQuickAt = null;
        HealthRows = [];
        HealthHeaderText = HealthCopy.NeverChecked;
        HealthStatusText = "";
    }
}
