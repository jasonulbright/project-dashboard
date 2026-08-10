using System.Windows;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// One line of the rollup: a group's own heading, or a finding under it. Both shapes share a row
/// type so the list is one flat, virtualized collection — a findings list over a large portfolio
/// has the same growth as the card grid, and nesting an items control per group would defeat that.
/// </summary>
public sealed class SafetyRow
{
    public required bool IsGroup { get; init; }

    public required string Title { get; init; }

    /// <summary>A group's state line, or a finding's headline.</summary>
    public required string Line { get; init; }

    public string Detail { get; init; } = "";

    /// <summary>Empty when the row offers nothing, which is what keeps a dead button off the list.</summary>
    public string ActionLabel { get; init; } = "";

    public SafetyAction Action { get; init; } = SafetyAction.None;

    public string RepoPath { get; init; } = "";

    public SafetySeverity Severity { get; init; } = SafetySeverity.Informational;

    public bool HasAction => ActionLabel.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    public bool IsFinding => !IsGroup;

    /// <summary>Empty for a group heading and for the lowest severity, which needs no adjective.</summary>
    public string SeverityLabel => IsGroup ? "" : Severity switch
    {
        SafetySeverity.NeedsAttention => "Needs attention",
        SafetySeverity.WorthALook => "Worth a look",
        _ => "",
    };

    /// <summary>
    /// Composed here rather than in markup: each part carries its own separator, so a finding with
    /// no repository name and no severity adjective is announced without punctuation around the
    /// values it does not have.
    /// </summary>
    public string AccessibleName =>
        (IsGroup ? Title : Title.Length > 0 ? $"{Title}, {Line}" : Line)
        + (IsGroup ? $", {Line}" : "")
        + (SeverityLabel.Length > 0 ? $", {SeverityLabel}" : "")
        + (Detail.Length > 0 ? $", {Detail}" : "");
}

/// <summary>
/// The portfolio-wide safety rollup: what this app can tell a reader about every repository it
/// knows, grouped by signal, with the cost of each answer stated rather than hidden.
///
/// It performs no destructive or recovering operation. Every row's action opens a surface that
/// carries its own gates — the Backups browser, the reflog viewer, a work-area tab — or runs one
/// read-only check.
///
/// Three tiers, and the page never blurs them. The free tier is computed from the project list the
/// dashboard already holds and spawns no git process. The cheap tier is one ref read and one
/// directory listing per repository, on an explicit ask. The expensive tier verifies bundles and
/// walks the object store, per repository, on an explicit ask, and never runs on a timer or as
/// part of any scan.
///
/// No expensive answer is cached. Every candidate key is an approximation of the thing it stands
/// for and each misses changes this app itself makes: an object-store reading does not move when a
/// reset abandons a commit, and a backup listing does not move when a bundle already on disk is
/// altered. Both checks are behind a press that states its cost, so a reader asking for one is
/// asking for the repository as it is now — and serving a stale answer to that ask is the one
/// failure this page exists to prevent. Each result carries the moment it was taken, which is what
/// keeps a result still on screen from a previous run honest.
///
/// Absence of a finding is never a clean bill of health: the header states which tiers have run,
/// and a repository the expensive tier has not reached reads as not checked.
/// </summary>
public partial class SafetyViewModel : ObservableObject
{
    /// <summary>Concurrent repositories a portfolio check reads, matching the bulk-sync fan-out.</summary>
    private const int ScanConcurrency = 4;

    private readonly DashboardViewModel _dashboard;
    private readonly RepoBusyRegistry _busy;
    private readonly SafetyScanner _scanner;
    private readonly SettingsService _settings;
    private readonly OperationHistory _history;
    private readonly ProjectDiscoveryService? _discovery;
    private readonly Action<Action> _uiPost;

    /// <summary>Null when the host supplied none; the page then reports the journal as unread rather than empty.</summary>
    private readonly RewriteRecoveryService? _recovery;

    private sealed record CheapEntry(SafetyCheapScan Scan, DateTimeOffset At);

    /// <summary>
    /// One repository's expensive answers, each carrying when it was taken. Nothing here is a
    /// cache: an expensive check runs every time it is asked for, and the stamp is what keeps a
    /// result left on screen from reading as current.
    /// </summary>
    private sealed record VerifyEntry(SafetyBackupVerification Result, DateTimeOffset At);

    private sealed record ReflogOnlyEntry(SafetyReflogOnlyScan Result, DateTimeOffset At);

    private readonly Dictionary<string, CheapEntry> _cheap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VerifyEntry> _verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReflogOnlyEntry> _reflogOnly = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _checkCts;
    private int _cheapSkipped;

    /// <summary>
    /// Raised to open a project's detail view at one work-area tab. The rollup routes through its
    /// own events rather than the dashboard's navigation so that reaching a surface from here never
    /// writes the shell's pending-project state on this page's behalf: the shell sets it as it
    /// navigates, and a second writer makes the two disagree about which project is being opened.
    /// </summary>
    public event Action<ProjectInfo, DetailTab>? NavigateToProjectTabRequested;

    /// <summary>
    /// Raised to open a project's detail view with one full-page pane already up. Separate from the
    /// tab handoff because a pane draws over the work area rather than selecting inside it.
    /// </summary>
    public event Action<ProjectInfo, DetailOverlay>? NavigateToProjectOverlayRequested;

    [ObservableProperty] private ObservableCollection<SafetyRow> _rows = [];

    [ObservableProperty] private string _rollupText = "";

    [ObservableProperty] private string _tierText = "";

    [ObservableProperty] private string _statusText = "";

    /// <summary>The running "n of m" of a portfolio check. Shown, not announced — one line per repository would bury the result.</summary>
    [ObservableProperty] private string _progressText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckBranchesAndBackupsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCheckCommand))]
    private bool _checkRunning;

    /// <summary>
    /// <paramref name="uiPost"/> runs a callback on the UI thread; null takes the running
    /// application's dispatcher. The recovery service raises from whichever thread cleared a
    /// marker, and the rows it changes belong to the UI thread.
    /// </summary>
    public SafetyViewModel(
        DashboardViewModel dashboard,
        RepoBusyRegistry busyRegistry,
        SettingsService settingsService,
        GitService gitService,
        BackupService? backups = null,
        RewriteRecoveryService? recovery = null,
        OperationHistory? history = null,
        ProjectDiscoveryService? discovery = null,
        Action<Action>? uiPost = null)
    {
        _dashboard = dashboard;
        _busy = busyRegistry;
        _settings = settingsService;
        _history = history ?? new OperationHistory();
        _scanner = new SafetyScanner(gitService, backups, _history);
        _recovery = recovery;
        _discovery = discovery;
        _uiPost = uiPost ?? PostToApplicationDispatcher;

        // The project list is the free tier's whole input, and every refresh replaces the
        // collection, so the property notification is what a recompute follows. Polling it would
        // be the background work this page exists without.
        _dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.Projects)) _uiPost(Rebuild);
        };
        if (_recovery is not null) _recovery.PendingChanged += () => _uiPost(Rebuild);

        Rebuild();
    }

    private static void PostToApplicationDispatcher(Action callback) =>
        _ = Application.Current?.Dispatcher.InvokeAsync(callback);

    /// <summary>Whether the cheap tier has an answer for every repository it was asked about.</summary>
    private SafetyTierState CheapState =>
        CheckRunning && _cheap.Count == 0 ? SafetyTierState.Running
        : _cheap.Count > 0 ? SafetyTierState.Ran
        : SafetyTierState.NotRun;

    // ── Free tier ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes every free-tier answer and re-renders. Costs no git process: that is asserted,
    /// because a check quietly promoted into this path would be paid on every dashboard refresh
    /// across the whole portfolio.
    /// </summary>
    public void Rebuild()
    {
        var projects = _dashboard.Projects.ToList();
        var checkable = SafetySurvey.Checkable(projects);
        var settings = _settings.Load();

        var interrupted = SafetySurvey.Interrupted(ReadInterrupted());
        var unreadable = SafetySurvey.StatusUnreadable(projects);
        var diverged = DivergedFindings(checkable);
        var noRemote = SafetySurvey.NoRemote(projects);
        var dirty = SafetySurvey.UncommittedWork(projects);
        var stale = SafetySurvey.StaleProjectData(
            _discovery?.LastDiscoveryAt, SettingsDelta.EffectiveRefreshSeconds(settings), DateTimeOffset.Now);

        var rows = new List<SafetyRow>();
        rows.AddRange(InterruptedGroup(interrupted));
        rows.AddRange(Group("Repositories git could not read", UnreadableState(unreadable), unreadable));
        rows.AddRange(BackupGroup(checkable));
        rows.AddRange(ReflogOnlyGroup(checkable));
        rows.AddRange(Group("Diverged branches", DivergedState(checkable), diverged));
        rows.AddRange(Group("Repositories with no remote", PlainState(noRemote.Count, "No repository is without a remote."), noRemote));
        rows.AddRange(Group("Project data age", PlainState(stale.Count, "The project list is within its refresh interval."), stale));
        rows.AddRange(Group("Uncommitted work", DirtyState(dirty.Count), dirty));

        Rows = new ObservableCollection<SafetyRow>(rows);

        var findings = interrupted.Concat(unreadable).Concat(diverged).Concat(noRemote)
            .Concat(dirty).Concat(stale).Concat(BackupFindings(checkable)).Concat(ReflogOnlyFindings(checkable))
            .ToList();
        RollupText = ComposeRollup(checkable, findings);
        TierText = SafetyCopy.TiersRun(CheapState, _verified.Count, _reflogOnly.Count, checkable.Count)
            + SafetyCopy.Skipped(_cheapSkipped);
    }

    /// <summary>
    /// The severity line. A repository is counted at its worst finding, and the last number counts
    /// the repositories that produced none at all — of any severity — so it never absorbs one whose
    /// only finding was informational.
    /// </summary>
    internal static string ComposeRollup(
        IReadOnlyList<ProjectInfo> checkable, IReadOnlyList<SafetyFinding> findings)
    {
        var worst = new Dictionary<string, SafetySeverity>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings)
        {
            if (finding.RepoPath.Length == 0) continue;
            if (!worst.TryGetValue(finding.RepoPath, out var current) || finding.Severity < current)
                worst[finding.RepoPath] = finding.Severity;
        }

        var attention = worst.Count(kv => kv.Value == SafetySeverity.NeedsAttention);
        var look = worst.Count(kv => kv.Value == SafetySeverity.WorthALook);
        var quiet = checkable.Count(p => !worst.ContainsKey(p.FullPath));
        return SafetyCopy.Rollup(attention, look, quiet);
    }

    /// <summary>
    /// The pending interruptions, enriched from the ledger where it holds a record for one. The
    /// mark beside the ledger is the link that survives the record scrolling out of the tail.
    /// </summary>
    private IReadOnlyList<InterruptedOperation> ReadInterrupted()
    {
        var pending = _recovery?.Pending ?? [];
        var operations = new List<InterruptedOperation>(pending.Count);
        foreach (var entry in pending)
        {
            var label = "";
            try
            {
                if (_history.ReadInterruptedMark(entry.RepoPath) is { } mark)
                    label = _history.Tail(entry.RepoPath).Records
                        .FirstOrDefault(r => r.Id == mark.RecordId)?.Label ?? "";
            }
            catch (Exception ex)
            {
                Log.Warn($"could not read the recorded interruption for {entry.RepoPath}", ex);
            }
            operations.Add(new InterruptedOperation(
                entry.RepoPath, entry.Phase, entry.UtcStamp, entry.BackupHandle?.UtcStamp, label));
        }
        return operations;
    }

    /// <summary>
    /// Divergence at whichever depth has been read for each repository: every local branch where
    /// the cheap tier has an answer, the current branch alone where it does not.
    /// </summary>
    private List<SafetyFinding> DivergedFindings(IReadOnlyList<ProjectInfo> checkable)
    {
        var findings = new List<SafetyFinding>();
        foreach (var project in checkable)
        {
            if (project.GitStatus.HasError) continue;
            if (_cheap.TryGetValue(project.FullPath, out var entry) && entry.Scan.Error is null)
                findings.AddRange(SafetySurvey.DivergedBranches(project, entry.Scan.Branches));
            else
                findings.AddRange(SafetySurvey.DivergedCurrentBranch([project]));
        }
        return findings;
    }

    // ── Group rendering ─────────────────────────────────────────────────────────

    private static IEnumerable<SafetyRow> Group(
        string title, string state, IReadOnlyList<SafetyFinding> findings)
    {
        yield return new SafetyRow { IsGroup = true, Title = title, Line = state };
        foreach (var finding in findings) yield return FindingRow(finding);
    }

    private static SafetyRow FindingRow(SafetyFinding finding) => new()
    {
        IsGroup = false,
        Title = finding.RepoName,
        Line = finding.Headline,
        Detail = finding.Detail,
        ActionLabel = finding.ActionLabel,
        Action = finding.Action,
        RepoPath = finding.RepoPath,
        Severity = finding.Severity,
    };

    /// <summary>
    /// A group's state line when nothing was found. Stated rather than left blank: an empty group
    /// is a claim, and a reader cannot tell a claim from an omission.
    /// </summary>
    private static string PlainState(int count, string clear) =>
        count == 0 ? clear : $"{count} finding(s).";

    private static string UnreadableState(IReadOnlyList<SafetyFinding> findings) =>
        findings.Count == 0
            ? "git read every repository on the list."
            : $"{findings.Count} repositor(y/ies) git could not read. Nothing below describes them.";

    private static string DirtyState(int count) =>
        count == 0
            ? "No repository has uncommitted work."
            : $"{count} repositor(y/ies) with uncommitted work — the same count as the dashboard's Dirty chip.";

    private IEnumerable<SafetyRow> InterruptedGroup(IReadOnlyList<SafetyFinding> findings)
    {
        var state = _recovery is null
            ? "The recovery journal was not read by this session, so whether anything was interrupted is unknown."
            : findings.Count == 0
                ? "No repository has an interrupted operation on record. " + SafetyCopy.InterruptedCaveat
                : $"{findings.Count} repositor(y/ies) with an interrupted operation. " + SafetyCopy.InterruptedCaveat;
        return Group("Interrupted operations", state, findings);
    }

    private string DivergedState(IReadOnlyList<ProjectInfo> checkable)
    {
        var read = checkable.Count(p => _cheap.ContainsKey(p.FullPath));
        return read == 0
            ? "Current branch only. Run the branch and backup check to include every local branch."
            : $"Every local branch read on {read} of {checkable.Count} repositor(y/ies); the rest are current-branch only."
              + SafetyCopy.Skipped(_cheapSkipped);
    }

    /// <summary>
    /// Backups, at whichever depth each repository has been read. Four conditions are distinguished
    /// per repository — no backup, on disk and unverified, verified, verification failed — and a
    /// repository the cheap tier has not listed is none of them.
    /// </summary>
    private IEnumerable<SafetyRow> BackupGroup(IReadOnlyList<ProjectInfo> checkable)
    {
        var state = _cheap.Count == 0
            ? SafetyCopy.NotChecked + " Run the branch and backup check to list what is on disk."
            : $"Listed on {_cheap.Count} of {checkable.Count} repositor(y/ies); verified on {_verified.Count}. "
              + SafetyCopy.BackupCheckLimit + SafetyCopy.Skipped(_cheapSkipped);

        yield return new SafetyRow { IsGroup = true, Title = "Backups", Line = state };

        foreach (var project in checkable)
        {
            if (!_cheap.TryGetValue(project.FullPath, out var cheap)) continue;
            var verified = _verified.TryGetValue(project.FullPath, out var v) ? v : null;
            yield return new SafetyRow
            {
                IsGroup = false,
                Title = project.DirectoryName,
                Line = SafetyCopy.BackupState(
                    cheap.Scan.BackupCount, verified?.Result.Failed ?? 0, verified?.Result.Unknown ?? 0, verified?.At),
                Detail = BackupDetail(cheap, verified),
                ActionLabel = cheap.Scan.BackupCount == 0 ? "Open Backups" : "Verify",
                Action = cheap.Scan.BackupCount == 0 ? SafetyAction.OpenBackups : SafetyAction.VerifyBackups,
                RepoPath = project.FullPath,
                Severity = BackupSeverity(cheap, verified),
            };
        }
    }

    /// <summary>
    /// A bundle found bad needs attention; one the verifier never answered for is worth a look and
    /// is never ranked as a defect it was not shown to have.
    /// </summary>
    private static SafetySeverity BackupSeverity(CheapEntry cheap, VerifyEntry? verified) =>
        (verified?.Result.Failed ?? 0) > 0 ? SafetySeverity.NeedsAttention
        : cheap.Scan.BackupCount == 0 ? SafetySeverity.WorthALook
        : verified is not null && (verified.Result.Unknown > 0 || verified.Result.Error is not null)
            ? SafetySeverity.WorthALook
            : SafetySeverity.Informational;

    private static string BackupDetail(CheapEntry cheap, VerifyEntry? verified)
    {
        if (verified?.Result.Error is { } error) return error;

        if (verified is not null && (verified.Result.Failed > 0 || verified.Result.Unknown > 0))
        {
            var parts = new List<string>();
            if (verified.Result.Failed > 0)
                parts.Add($"Failed verification: {string.Join(", ", verified.Result.FailedStamps)}. "
                    + "A bundle a restore would refuse is a backup that is not there.");
            if (verified.Result.Unknown > 0)
                parts.Add($"Could not be verified: {string.Join(", ", verified.Result.UnknownStamps)}. "
                    + "Neither confirmed good nor found bad.");
            return string.Join(" ", parts) + " " + SafetyCopy.BackupCheckLimit;
        }

        if (cheap.Scan.BackupCount == 0)
            return "Nothing here can be restored from this app; a destructive operation would take one first.";
        return SafetyCopy.BackupCheckLimit;
    }

    /// <summary>
    /// Reflog-only commits, per repository and never in bulk by default. A repository nobody has
    /// asked about renders as not checked — a blank row here would read as a repository with none.
    /// </summary>
    private IEnumerable<SafetyRow> ReflogOnlyGroup(IReadOnlyList<ProjectInfo> checkable)
    {
        var state = checkable.Count == 0
            ? "No local repository to check."
            : $"Checked on {_reflogOnly.Count} of {checkable.Count} repositor(y/ies). "
              + "This walk reads the object store, so it runs only when asked.";

        yield return new SafetyRow { IsGroup = true, Title = "Reflog-only commits", Line = state };

        foreach (var project in checkable)
        {
            var entry = _reflogOnly.TryGetValue(project.FullPath, out var e) ? e : null;
            yield return new SafetyRow
            {
                IsGroup = false,
                Title = project.DirectoryName,
                Line = entry is null ? SafetyCopy.NotChecked
                    : entry.Result.Error is not null ? "Could not be measured"
                    : entry.Result.Count == 0 ? $"No reflog-only commit as of {SafetyCopy.Stamp(entry.At)}"
                    : $"{entry.Result.Count} reflog-only commit(s) as of {SafetyCopy.Stamp(entry.At)}",
                Detail = entry?.Result.Error
                    ?? (entry is null
                        ? ""
                        : "A backup bundle holds refs, so a commit reachable only from a reflog is in no bundle."),
                ActionLabel = entry is null ? "Check" : entry.Result.Count > 0 ? "Open Reflog" : "Re-check",
                Action = entry is not null && entry.Result.Count > 0
                    ? SafetyAction.OpenReflog
                    : SafetyAction.CheckReflogOnly,
                RepoPath = project.FullPath,
                Severity = entry is null ? SafetySeverity.Informational
                    : entry.Result.Count > 0 ? SafetySeverity.WorthALook
                    : SafetySeverity.Informational,
            };
        }
    }

    /// <summary>
    /// What the rollup counts a repository's backups as. A bundle the verifier never answered for
    /// is its own finding at its own severity rather than being counted as a failure or as clear.
    /// </summary>
    private List<SafetyFinding> BackupFindings(IReadOnlyList<ProjectInfo> checkable) =>
        checkable
            .Where(p => _verified.ContainsKey(p.FullPath))
            .Select(p => (Project: p, Result: _verified[p.FullPath].Result))
            .Where(x => x.Result.Failed > 0 || x.Result.Unknown > 0)
            .Select(x => new SafetyFinding(
                SafetySignal.UnverifiedBackup,
                x.Result.Failed > 0 ? SafetySeverity.NeedsAttention : SafetySeverity.WorthALook,
                x.Project.FullPath, x.Project.DirectoryName,
                x.Result.Failed > 0
                    ? "A backup bundle failed verification"
                    : "A backup bundle could not be verified",
                "", SafetyAction.OpenBackups, "Open Backups"))
            .ToList();

    private List<SafetyFinding> ReflogOnlyFindings(IReadOnlyList<ProjectInfo> checkable) =>
        checkable
            .Where(p => _reflogOnly.TryGetValue(p.FullPath, out var r) && r.Result.Error is null && r.Result.Count > 0)
            .Select(p => new SafetyFinding(
                SafetySignal.ReflogOnlyCommits, SafetySeverity.WorthALook, p.FullPath, p.DirectoryName,
                "Commits live only in a reflog", "", SafetyAction.OpenReflog, "Open Reflog"))
            .ToList();

    // ── Cheap and expensive tiers ───────────────────────────────────────────────

    /// <summary>
    /// Cheap tier across the portfolio. Leaseless by construction: every read is read-only, and a
    /// repository another operation holds is skipped and counted rather than read mid-write.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartCheck))]
    private async Task CheckBranchesAndBackups()
    {
        var targets = SafetySurvey.Checkable(_dashboard.Projects);
        if (!BeginCheck(targets.Count, "Checking branches and backups")) return;

        try
        {
            var skipped = 0;
            var done = 0;
            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, CheapEntry>(
                StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(ScanConcurrency);
            var ct = _checkCts!.Token;

            await Task.WhenAll(targets.Select(async project =>
            {
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    if (_busy.IsBusy(project.FullPath))
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                    var scan = await _scanner.ScanAsync(project.FullPath, ct);
                    results[project.FullPath] = new CheapEntry(scan, DateTimeOffset.Now);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Warn($"safety branch and backup check failed for {project.FullPath}", ex);
                    results[project.FullPath] = new CheapEntry(
                        new SafetyCheapScan([], 0, ex.Message), DateTimeOffset.Now);
                }
                finally
                {
                    ProgressText = $"{Interlocked.Increment(ref done)}/{targets.Count}";
                    gate.Release();
                }
            }));

            foreach (var (path, entry) in results) _cheap[path] = entry;
            _cheapSkipped = skipped;
            StatusText = $"Branches and backups checked on {results.Count} of {targets.Count} repositories."
                + SafetyCopy.Skipped(skipped);
        }
        finally
        {
            EndCheck();
        }
    }

    /// <summary>
    /// Expensive tier across the portfolio. Named with its cost because it is the one button here
    /// that can run for minutes, and it reports how far it has got rather than spinning over an
    /// unknown duration.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartCheck))]
    private async Task CheckAll()
    {
        var targets = SafetySurvey.Checkable(_dashboard.Projects);
        if (!BeginCheck(targets.Count, "Verifying backups and walking reflogs")) return;

        try
        {
            var skipped = 0;
            var done = 0;
            using var gate = new SemaphoreSlim(ScanConcurrency);
            var ct = _checkCts!.Token;
            var verified = new System.Collections.Concurrent.ConcurrentDictionary<string, VerifyEntry>(
                StringComparer.OrdinalIgnoreCase);
            var walked = new System.Collections.Concurrent.ConcurrentDictionary<string, ReflogOnlyEntry>(
                StringComparer.OrdinalIgnoreCase);

            await Task.WhenAll(targets.Select(async project =>
            {
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    if (_busy.IsBusy(project.FullPath))
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                    verified[project.FullPath] = new VerifyEntry(
                        await _scanner.VerifyBackupsAsync(project.FullPath, ct), DateTimeOffset.Now);
                    walked[project.FullPath] = new ReflogOnlyEntry(
                        await _scanner.CountReflogOnlyAsync(project.FullPath, ct), DateTimeOffset.Now);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Warn($"safety deep check failed for {project.FullPath}", ex);
                }
                finally
                {
                    ProgressText = $"{Interlocked.Increment(ref done)}/{targets.Count}";
                    gate.Release();
                }
            }));

            foreach (var (path, entry) in verified) _verified[path] = entry;
            foreach (var (path, entry) in walked) _reflogOnly[path] = entry;
            StatusText = $"Deep checks finished on {targets.Count - skipped} of {targets.Count} repositories."
                + SafetyCopy.Skipped(skipped);
        }
        finally
        {
            EndCheck();
        }
    }

    private bool CanStartCheck() => !CheckRunning;

    /// <summary>
    /// Claims the page's one check slot, or refuses and says why. A repository under a destructive
    /// operation is skipped per repository rather than gating the whole run: every read here is
    /// read-only and leaseless, so the skip is what keeps one out of a swap, and a whole-portfolio
    /// gate would only hide the other repositories' answers behind one busy repository.
    /// </summary>
    private bool BeginCheck(int targetCount, string what)
    {
        StatusText = "";
        if (CheckRunning) return false;
        if (targetCount == 0)
        {
            StatusText = "No local repository to check.";
            return false;
        }

        _checkCts = new CancellationTokenSource();
        CheckRunning = true;
        ProgressText = $"0/{targetCount}";
        StatusText = $"{what} across {targetCount} repositories…";
        Rebuild();
        return true;
    }

    private void EndCheck()
    {
        _checkCts?.Dispose();
        _checkCts = null;
        CheckRunning = false;
        ProgressText = "";
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(CheckRunning))]
    private void CancelCheck()
    {
        _checkCts?.Cancel();
        StatusText = "Cancelling — the repositories already read keep their results.";
    }

    // ── Row actions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one row's single offer. Every navigation target carries its own gates; the two checks
    /// are read-only and take no lease, and both refuse while that repository is busy.
    /// </summary>
    [RelayCommand]
    private async Task RunRowAction(SafetyRow? row)
    {
        if (row is null || row.Action == SafetyAction.None) return;

        if (row.Action == SafetyAction.Rescan)
        {
            _dashboard.ForceRefreshCommand.Execute(null);
            StatusText = "Rescanning the projects root.";
            return;
        }

        if (row.RepoPath.Length == 0) return;

        if (row.Action is SafetyAction.VerifyBackups or SafetyAction.CheckReflogOnly)
        {
            await RunRepoCheckAsync(row);
            return;
        }

        var project = _dashboard.FindByPath(row.RepoPath);
        if (project is null)
        {
            StatusText = "That repository is no longer on the dashboard's list — rescan and try again.";
            return;
        }

        switch (row.Action)
        {
            case SafetyAction.OpenChanges:
                NavigateToProjectTabRequested?.Invoke(project, DetailTab.Changes);
                break;
            case SafetyAction.OpenBranches:
                NavigateToProjectTabRequested?.Invoke(project, DetailTab.Branches);
                break;
            case SafetyAction.OpenRemotes:
                NavigateToProjectTabRequested?.Invoke(project, DetailTab.Internals);
                break;
            case SafetyAction.OpenBackups:
                NavigateToProjectOverlayRequested?.Invoke(project, DetailOverlay.Backups);
                break;
            case SafetyAction.OpenRecoveryBackups:
                NavigateToProjectOverlayRequested?.Invoke(project, DetailOverlay.RecoveryBackups);
                break;
            case SafetyAction.OpenReflog:
                NavigateToProjectOverlayRequested?.Invoke(project, DetailOverlay.Reflog);
                break;
        }
    }

    private async Task RunRepoCheckAsync(SafetyRow row)
    {
        if (CheckRunning)
        {
            StatusText = $"{row.Title}: {SafetyCopy.CheckAlreadyRunningRefusal}";
            return;
        }
        if (_busy.IsBusy(row.RepoPath))
        {
            StatusText = $"{row.Title}: {SafetyCopy.RepoBusyRefusal}";
            return;
        }

        _checkCts = new CancellationTokenSource();
        CheckRunning = true;
        StatusText = row.Action == SafetyAction.VerifyBackups
            ? $"Verifying {row.Title}'s backup bundles…"
            : $"Walking {row.Title}'s reflogs…";
        try
        {
            var ct = _checkCts.Token;
            if (row.Action == SafetyAction.VerifyBackups)
            {
                var result = await _scanner.VerifyBackupsAsync(row.RepoPath, ct);
                _verified[row.RepoPath] = new VerifyEntry(result, DateTimeOffset.Now);
                StatusText = $"{row.Title}: "
                    + SafetyCopy.BackupState(result.OnDisk, result.Failed, result.Unknown, DateTimeOffset.Now);
            }
            else
            {
                var result = await _scanner.CountReflogOnlyAsync(row.RepoPath, ct);
                _reflogOnly[row.RepoPath] = new ReflogOnlyEntry(result, DateTimeOffset.Now);
                StatusText = result.Error is not null
                    ? $"{row.Title}: the reflog walk could not be completed — {result.Error}"
                    : $"{row.Title}: {result.Count} reflog-only commit(s).";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{row.Title}: the check was cancelled, so nothing was measured.";
        }
        catch (Exception ex)
        {
            Log.Warn($"safety check failed for {row.RepoPath}", ex);
            StatusText = $"{row.Title}: the check failed — {ex.Message}";
        }
        finally
        {
            _checkCts?.Dispose();
            _checkCts = null;
            CheckRunning = false;
            Rebuild();
        }
    }
}
