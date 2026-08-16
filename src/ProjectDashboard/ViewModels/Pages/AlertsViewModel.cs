using System.Diagnostics;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>One repository's alert row: the held answers, worded as answers or as refusals.</summary>
public sealed partial class AlertRow : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>owner/repo, or empty for a repository whose remote is not GitHub.</summary>
    public required string Slug { get; init; }

    public required int? OpenIssues { get; init; }
    public required int? OpenPrs { get; init; }

    [ObservableProperty] private string _dependabotText = "";
    [ObservableProperty] private string _codeScanningText = "";
    [ObservableProperty] private string _secretScanningText = "";
    [ObservableProperty] private string _dependabotDetail = "";
    [ObservableProperty] private string _codeScanningDetail = "";
    [ObservableProperty] private string _secretScanningDetail = "";
    [ObservableProperty] private string _asOfText = "";

    /// <summary>Which sources hold a nonzero count, for the only-with-alerts filter.</summary>
    public HashSet<string> Firing { get; } = new(StringComparer.Ordinal);

    /// <summary>An unfetched count is a dash, never a zero the scan did not measure.</summary>
    public string IssuesText => OpenIssues?.ToString() ?? "—";

    public string PrsText => OpenPrs?.ToString() ?? "—";

    public string AccessibleName =>
        $"{Name}: {IssuesText} open issues, {PrsText} open pull requests, "
        + $"Dependabot {DependabotText}, code scanning {CodeScanningText}, secret scanning {SecretScanningText}"
        + (AsOfText.Length > 0 ? $", {AsOfText}" : "");
}

/// <summary>
/// A portfolio view of what is open against every repository: issues, pull requests, and the
/// three GitHub security sources. It opens from the local cache — instantly, stamped with when
/// each answer was taken — and refreshes conditionally, so an unchanged repository costs a
/// round-trip the rate limit never sees. A source that refuses is a labelled refusal on its
/// cell, never a zero.
/// </summary>
public partial class AlertsViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboard;
    private readonly AlertsService _alerts;

    public AlertsViewModel(DashboardViewModel dashboard, AlertsService alerts)
    {
        _dashboard = dashboard;
        _alerts = alerts;
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
    /// Rebuilds every row from the dashboard's list and the cache, then starts one conditional
    /// refresh pass. Called by the page on navigation: the cached picture is on screen before
    /// the first request leaves, and stays if none can.
    /// </summary>
    public async Task OpenAsync()
    {
        BuildRows();
        if (RefreshBusy) return;
        await RefreshAllAsync();
    }

    private void BuildRows()
    {
        _allRows = [.. _dashboard.Projects
            .Where(p => !p.IsRemoteOnly || p.RemoteSlug.Length > 0)
            .Select(Row)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        ApplyFilter();
    }

    private AlertRow Row(ProjectInfo project)
    {
        var slug = project.IsRemoteOnly
            ? project.RemoteSlug
            : GitRemote.Parse(project.GitStatus.RemoteUrl) is { IsGitHub: true } remote
                ? $"{remote.Owner}/{remote.Repo}"
                : "";
        var row = new AlertRow
        {
            Name = project.DisplayName,
            Slug = slug,
            OpenIssues = project.OpenIssueCount,
            OpenPrs = project.OpenPrCount,
        };
        if ((row.OpenIssues ?? 0) > 0) row.Firing.Add("Issues");
        if ((row.OpenPrs ?? 0) > 0) row.Firing.Add("Pull requests");
        ApplyCache(row);
        return row;
    }

    private void ApplyCache(AlertRow row)
    {
        if (row.Slug.Length == 0)
        {
            row.DependabotText = row.CodeScanningText = row.SecretScanningText = "—";
            var detail = "This repository's remote is not GitHub, so GitHub's security sources have nothing to say about it.";
            row.DependabotDetail = row.CodeScanningDetail = row.SecretScanningDetail = detail;
            return;
        }

        DateTimeOffset? oldest = null;
        foreach (var source in AlertsService.Sources)
        {
            var state = _alerts.Cached(row.Slug, source);
            var (text, detail) = Describe(state);
            switch (source)
            {
                case AlertSource.Dependabot: row.DependabotText = text; row.DependabotDetail = detail; break;
                case AlertSource.CodeScanning: row.CodeScanningText = text; row.CodeScanningDetail = detail; break;
                default: row.SecretScanningText = text; row.SecretScanningDetail = detail; break;
            }
            if (state is { Count: > 0 }) row.Firing.Add(SourceName(source));
            if (state is not null && (oldest is null || state.FetchedUtc < oldest)) oldest = state.FetchedUtc;
        }
        row.AsOfText = oldest is { } at ? $"as of {at.ToLocalTime():HH:mm}" : "not read yet";
    }

    private static string SourceName(AlertSource source) => source switch
    {
        AlertSource.Dependabot => "Dependabot",
        AlertSource.CodeScanning => "Code scanning",
        _ => "Secret scanning",
    };

    private static (string Text, string Detail) Describe(AlertSourceState? state) => state switch
    {
        null => ("—", "Not read yet. Refresh asks GitHub."),
        { Unreadable.Length: > 0 } => ("?", state.Unreadable),
        { Count: { } count } => (count.ToString(), ""),
        _ => ("—", "Not read yet. Refresh asks GitHub."),
    };

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

    /// <summary>
    /// One pass over every GitHub-slugged row, sequentially — a trickle, not a burst. Rows keep
    /// their held answers until each one's own refresh lands.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAll()
    {
        if (RefreshBusy) return;
        await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        var targets = _allRows.Where(r => r.Slug.Length > 0).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Nothing to refresh — no discovered repository has a GitHub remote.";
            return;
        }

        RefreshBusy = true;
        var changed = 0;
        var unchanged = 0;
        var refused = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                StatusText = $"Asking GitHub… {i + 1} of {targets.Count} ({targets[i].Name})";
                var outcome = await RefreshRepoAsync(targets[i]);
                changed += outcome.Changed;
                unchanged += outcome.Unchanged;
                refused += outcome.Refused;
            }
        }
        finally
        {
            RefreshBusy = false;
        }
        ApplyFilter();
        StatusText =
            $"Refreshed {targets.Count} {(targets.Count == 1 ? "repository" : "repositories")} at {DateTime.Now:HH:mm}: "
            + $"{changed} source {(changed == 1 ? "answer" : "answers")} changed, {unchanged} confirmed unchanged"
            + (refused > 0 ? $", {refused} refused — the reason is on the cell." : ".");
    }

    private async Task<AlertRefreshOutcome> RefreshRepoAsync(AlertRow row)
    {
        try
        {
            var outcome = await _alerts.RefreshAsync(row.Slug);
            ApplyCache(row);
            return outcome;
        }
        catch (Exception ex)
        {
            Log.Warn($"alert refresh failed for {row.Slug}", ex);
            return new AlertRefreshOutcome(0, 0, 0);
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
