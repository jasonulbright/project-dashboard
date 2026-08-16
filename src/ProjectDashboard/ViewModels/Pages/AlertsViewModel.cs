using System.Diagnostics;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// One GitHub repository's alert row — one row per owner/repo however many local clones and
/// worktrees carry it, so a slug is asked about once and answered once. Repositories with no
/// GitHub remote keep a row each, with their security cells explaining themselves.
/// </summary>
public sealed partial class AlertRow : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>owner/repo, or empty for a repository whose remote is not GitHub.</summary>
    public required string Slug { get; init; }

    /// <summary>The dashboard's last-scan counts, shown only until a refresh holds newer ones.</summary>
    public required int? ScannedIssues { get; init; }

    public required int? ScannedPrs { get; init; }

    [ObservableProperty] private string _issuesText = "—";
    [ObservableProperty] private string _prsText = "—";
    [ObservableProperty] private string _issuesDetail = "";
    [ObservableProperty] private string _dependabotText = "";
    [ObservableProperty] private string _codeScanningText = "";
    [ObservableProperty] private string _secretScanningText = "";
    [ObservableProperty] private string _dependabotDetail = "";
    [ObservableProperty] private string _codeScanningDetail = "";
    [ObservableProperty] private string _secretScanningDetail = "";
    [ObservableProperty] private string _asOfText = "";

    /// <summary>
    /// Every refused source's reason, visible on the row itself. A tooltip reaches only a mouse,
    /// and a reason only a mouse can read is a reason most readers never get.
    /// </summary>
    [ObservableProperty] private string _refusalSummary = "";

    /// <summary>Which sources hold a nonzero count. Rebuilt whole on every cache read.</summary>
    public HashSet<string> Firing { get; } = new(StringComparer.Ordinal);

    public string AccessibleName =>
        $"{Name}: {IssuesText} open issues, {PrsText} open pull requests, "
        + $"Dependabot {DependabotText}, code scanning {CodeScanningText}, secret scanning {SecretScanningText}"
        + (AsOfText.Length > 0 ? $", {AsOfText}" : "")
        + (RefusalSummary.Length > 0 ? $". {RefusalSummary}" : "");

    partial void OnIssuesTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
    partial void OnPrsTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
    partial void OnDependabotTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
    partial void OnCodeScanningTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
    partial void OnSecretScanningTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
    partial void OnAsOfTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
    partial void OnRefusalSummaryChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
}

/// <summary>
/// A portfolio view of what is open against every repository: issues, pull requests, and the
/// three GitHub security sources. It opens from the local cache — instantly, stamped with when
/// each answer was taken — and refreshes only when asked, conditionally, so an unchanged
/// repository costs a round-trip the rate limit never sees. A refresh is cancellable at any
/// point; a source that refuses is a labelled refusal on its row, never a zero; and an answer
/// that never arrived is reported as unanswered, never as confirmed.
/// </summary>
public partial class AlertsViewModel : ObservableObject
{
    /// <summary>Concurrent repositories one pass reads, matching the safety scan's fan-out shape.</summary>
    private const int RefreshConcurrency = 3;

    private readonly DashboardViewModel _dashboard;
    private readonly AlertsService _alerts;
    private readonly GitHubService _gitHub;

    public AlertsViewModel(DashboardViewModel dashboard, AlertsService alerts, GitHubService gitHub)
    {
        _dashboard = dashboard;
        _alerts = alerts;
        _gitHub = gitHub;
    }

    public ObservableCollection<AlertRow> Rows { get; } = [];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _onlyWithAlerts;
    [ObservableProperty] private string _sourceFilter = "Any source";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _refreshBusy;
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private string _emptyNotice = "";

    public IReadOnlyList<string> SourceFilterChoices { get; } =
        ["Any source", "Issues", "Pull requests", "Dependabot", "Code scanning", "Secret scanning"];

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnOnlyWithAlertsChanged(bool value) => ApplyFilter();
    partial void OnSourceFilterChanged(string value) => ApplyFilter();

    private List<AlertRow> _allRows = [];

    /// <summary>
    /// Rebuilds the rows from the dashboard's list and the cache. Nothing is asked of GitHub:
    /// opening the page costs no request and no wait, however many repositories there are —
    /// Refresh is the reader's own, cancellable, choice. A rebuild during a running pass shows
    /// the cache as the pass has written it so far; the pass rebuilds again when it ends.
    /// </summary>
    public void Open()
    {
        BuildRows();
        if (!RefreshBusy && StatusText.Length == 0)
            StatusText = "Opened from what was last read — each row says when. Refresh asks GitHub, conditionally.";
    }

    private void BuildRows()
    {
        var rows = new List<AlertRow>();
        var bySlug = new Dictionary<string, List<ProjectInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in _dashboard.Projects)
        {
            var slug = SlugOf(project);
            if (slug.Length == 0)
            {
                if (!project.IsRemoteOnly) rows.Add(Row("", [project]));
                continue;
            }
            if (!bySlug.TryGetValue(slug, out var group)) bySlug[slug] = group = [];
            group.Add(project);
        }
        rows.AddRange(bySlug.Select(pair => Row(pair.Key, pair.Value)));

        _allRows = [.. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        ApplyFilter();
    }

    private static string SlugOf(ProjectInfo project) =>
        project.IsRemoteOnly
            ? project.RemoteSlug
            : GitRemote.Parse(project.GitStatus.RemoteUrl) is { IsGitHub: true } remote
                ? $"{remote.Owner}/{remote.Repo}"
                : "";

    private AlertRow Row(string slug, List<ProjectInfo> projects)
    {
        var row = new AlertRow
        {
            // Every clone's display name, so two checkouts of one repository read as one
            // repository with two homes rather than as two repositories.
            Name = string.Join(", ", projects.Select(p => p.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
            Slug = slug,
            ScannedIssues = projects.Select(p => p.OpenIssueCount).FirstOrDefault(c => c is not null),
            ScannedPrs = projects.Select(p => p.OpenPrCount).FirstOrDefault(c => c is not null),
        };
        ApplyCache(row);
        return row;
    }

    /// <summary>Rereads every cell of one row from the cache, rebuilding its firing set whole.</summary>
    private void ApplyCache(AlertRow row)
    {
        row.Firing.Clear();

        if (row.Slug.Length == 0)
        {
            row.IssuesText = row.ScannedIssues?.ToString() ?? "—";
            row.PrsText = row.ScannedPrs?.ToString() ?? "—";
            row.IssuesDetail = "As of the last scan.";
            row.DependabotText = row.CodeScanningText = row.SecretScanningText = "—";
            var detail = "This repository's remote is not GitHub, so GitHub's sources have nothing to say about it.";
            row.DependabotDetail = row.CodeScanningDetail = row.SecretScanningDetail = detail;
            row.AsOfText = "";
            row.RefusalSummary = "";
            if ((row.ScannedIssues ?? 0) > 0) row.Firing.Add("Issues");
            if ((row.ScannedPrs ?? 0) > 0) row.Firing.Add("Pull requests");
            return;
        }

        var refusals = new List<string>();
        DateTimeOffset? oldest = null;
        void Stamp(AlertSourceState? state)
        {
            if (state is not null && (oldest is null || state.FetchedUtc < oldest)) oldest = state.FetchedUtc;
        }

        var issuesAndPrs = _alerts.Cached(row.Slug, AlertSource.IssuesAndPrs);
        var prs = _alerts.Cached(row.Slug, AlertSource.PullRequests);
        Stamp(issuesAndPrs);
        Stamp(prs);
        (row.IssuesText, row.PrsText, row.IssuesDetail) = DescribeCounts(row, issuesAndPrs, prs, refusals);

        foreach (var source in new[] { AlertSource.Dependabot, AlertSource.CodeScanning, AlertSource.SecretScanning })
        {
            var state = _alerts.Cached(row.Slug, source);
            Stamp(state);
            var (text, detail) = Describe(state, refusals);
            switch (source)
            {
                case AlertSource.Dependabot: row.DependabotText = text; row.DependabotDetail = detail; break;
                case AlertSource.CodeScanning: row.CodeScanningText = text; row.CodeScanningDetail = detail; break;
                default: row.SecretScanningText = text; row.SecretScanningDetail = detail; break;
            }
            if (state is { Count: > 0 }) row.Firing.Add(SourceName(source));
        }

        row.AsOfText = oldest is { } at ? $"as of {at.ToLocalTime():HH:mm}" : "not read yet";
        row.RefusalSummary = string.Join(" ", refusals);
    }

    /// <summary>
    /// Issue and pull request cells. GitHub's issues listing counts pull requests among issues,
    /// so the true issue count is the difference, floored at zero for the moment between the two
    /// reads. Refreshed answers outrank the scan's snapshot; the snapshot fills in only where no
    /// refresh has ever answered, and says so.
    /// </summary>
    private static (string Issues, string Prs, string Detail) DescribeCounts(
        AlertRow row, AlertSourceState? issuesAndPrs, AlertSourceState? prs, List<string> refusals)
    {
        if (issuesAndPrs is { Unreadable.Length: > 0 })
        {
            refusals.Add(issuesAndPrs.Unreadable + ".");
            return ("unreadable", prs is { Count: { } held } ? Fired(row, held) : row.ScannedPrs?.ToString() ?? "—",
                issuesAndPrs.Unreadable);
        }
        if (prs is { Unreadable.Length: > 0 }) refusals.Add(prs.Unreadable + ".");

        if (issuesAndPrs is { Count: { } combined } && prs is { Count: { } prCount })
        {
            var issues = Math.Max(0, combined - prCount);
            if (issues > 0) row.Firing.Add("Issues");
            return (issues.ToString(), Fired(row, prCount), "Refreshed from GitHub.");
        }

        if ((row.ScannedIssues ?? 0) > 0) row.Firing.Add("Issues");
        if ((row.ScannedPrs ?? 0) > 0) row.Firing.Add("Pull requests");
        return (row.ScannedIssues?.ToString() ?? "—", row.ScannedPrs?.ToString() ?? "—",
            "As of the last scan; Refresh asks GitHub.");
    }

    private static string Fired(AlertRow row, int prCount)
    {
        if (prCount > 0) row.Firing.Add("Pull requests");
        return prCount.ToString();
    }

    private static string SourceName(AlertSource source) => source switch
    {
        AlertSource.Dependabot => "Dependabot",
        AlertSource.CodeScanning => "Code scanning",
        _ => "Secret scanning",
    };

    private static (string Text, string Detail) Describe(AlertSourceState? state, List<string> refusals)
    {
        if (state is { Unreadable.Length: > 0 })
        {
            refusals.Add(state.Unreadable + ".");
            return ("unreadable", state.Unreadable);
        }
        return state is { Count: { } count }
            ? (count.ToString(), "")
            : ("—", "Not read yet. Refresh asks GitHub.");
    }

    private void ApplyFilter()
    {
        var wanted = _allRows.AsEnumerable();
        if (FilterText.Trim() is { Length: > 0 } text)
            wanted = wanted.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                                       || r.Slug.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (OnlyWithAlerts)
            wanted = SourceFilter == "Any source"
                ? wanted.Where(r => r.Firing.Count > 0)
                : wanted.Where(r => r.Firing.Contains(SourceFilter));
        else if (SourceFilter != "Any source")
            wanted = wanted.Where(r => r.Firing.Contains(SourceFilter));

        Rows.Clear();
        foreach (var row in wanted) Rows.Add(row);
        HasRows = Rows.Count > 0;
        EmptyNotice = _allRows.Count == 0
            ? "No repositories have been discovered yet."
            : HasRows ? "" : "No repository matches the current filter. The filter hides rows; it does not change what is held.";
    }

    private CancellationTokenSource? _refreshCts;

    /// <summary>The pass the Refresh button last started, held so a test can await the read itself.</summary>
    internal Task RefreshPass { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAll()
    {
        if (RefreshBusy) return Task.CompletedTask;
        return RefreshPass = RefreshAllAsync();
    }

    [RelayCommand]
    private void CancelRefresh() => _refreshCts?.Cancel();

    private async Task RefreshAllAsync()
    {
        var targets = _allRows.Where(r => r.Slug.Length > 0)
            .DistinctBy(r => r.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            StatusText = "Nothing to refresh — no discovered repository has a GitHub remote.";
            return;
        }

        RefreshBusy = true;
        using var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var outcome = AlertRefreshOutcome.Zero;
        var done = 0;
        var cancelled = false;
        try
        {
            // The identity decides what a read can see; answers held from another identity
            // describe that account's view and are dropped before this one's reads begin.
            _alerts.EnsureAccount(await ActiveIdentityAsync());

            using var throttle = new SemaphoreSlim(RefreshConcurrency);
            var work = targets.Select(async row =>
            {
                await throttle.WaitAsync(cts.Token);
                try
                {
                    var one = await _alerts.RefreshAsync(row.Slug, cts.Token);
                    lock (targets) outcome += one;
                    ApplyCache(row);
                    StatusText = $"Asking GitHub… {Interlocked.Increment(ref done)} of {targets.Count}";
                }
                finally
                {
                    throttle.Release();
                }
            });
            await Task.WhenAll(work);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("alert refresh pass failed", ex);
            StatusText = $"The refresh stopped early — {ex.Message}. Answers already taken are kept.";
            return;
        }
        finally
        {
            _refreshCts = null;
            RefreshBusy = false;
            // Rows are rebuilt from the cache whole: a page reopened mid-pass swapped the row
            // objects, and only a rebuild puts every answer the pass wrote onto the rows shown.
            BuildRows();
        }

        StatusText = DescribePass(targets.Count, done, outcome, cancelled);
    }

    /// <summary>
    /// The pass, in outcome classes that stay apart: an answer GitHub confirmed is not an answer
    /// that never arrived, and a reader told "confirmed" about a dead network would trust a
    /// number nothing vouched for.
    /// </summary>
    internal static string DescribePass(int targets, int reached, AlertRefreshOutcome outcome, bool cancelled)
    {
        var head = cancelled
            ? $"Refresh cancelled after {reached} of {targets} repositories; answers already taken are kept."
            : $"Refreshed {targets} {(targets == 1 ? "repository" : "repositories")} at {DateTime.Now:HH:mm}.";
        var parts = new List<string>();
        if (outcome.Changed > 0) parts.Add($"{outcome.Changed} answer{(outcome.Changed == 1 ? "" : "s")} changed");
        if (outcome.Unchanged > 0) parts.Add($"{outcome.Unchanged} confirmed unchanged");
        if (outcome.Refused > 0) parts.Add($"{outcome.Refused} refused — the reason is on the row");
        if (outcome.Unanswered > 0)
            parts.Add($"{outcome.Unanswered} unanswered (no reply arrived; held answers kept, unconfirmed)");
        return parts.Count == 0 ? head : $"{head} {string.Join("; ", parts)}.";
    }

    /// <summary>Every active gh account, as one identity string; "" when gh could not say.</summary>
    private async Task<string> ActiveIdentityAsync()
    {
        try
        {
            var state = await _gitHub.GetAuthStateAsync();
            if (state is null) return "";
            return string.Join(";", state.Accounts
                .Where(a => a.Active)
                .Select(a => $"{a.Login}@{a.Host}")
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the gh identity for the alerts cache", ex);
            return "";
        }
    }

    [RelayCommand]
    private void OpenSecurityPage(AlertRow? row)
    {
        if (row is null || row.Slug.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{row.Slug}/security") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the browser — {ex.Message}";
            Log.Warn($"open security page failed for {row.Slug}", ex);
        }
    }
}
