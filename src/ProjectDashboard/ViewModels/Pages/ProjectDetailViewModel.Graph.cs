using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The commit graph pane: lane-assigned pages of the DAG over every local branch plus
/// HEAD, drawn as columns.
///
/// Lanes are stable across pages because the service re-walks from the ref tips for every page,
/// so an appended page's columns line up with the ones already drawn. Each appended page brings
/// its own <see cref="CommitGraphPage.IncomingLanes"/>, which is exactly the lane state its
/// first row inherits from the row above it.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Pixels per lane column. The pane reserves this per lane the loaded pages have used.</summary>
    internal const double GraphLaneWidth = 16;

    internal const int GraphPageSize = 200;

    /// <summary>
    /// Stateless over the same <see cref="GitService"/> this page already holds — it owns no
    /// lease and caches nothing — so it is built here rather than threaded through the host.
    /// </summary>
    private CommitGraphService? _commitGraph;
    private CommitGraphService Graph => _commitGraph ??= new CommitGraphService(_gitService);

    [ObservableProperty] private bool _commitGraphVisible;

    partial void OnCommitGraphVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));

    [ObservableProperty] private ObservableCollection<CommitGraphRow> _graphRows = [];

    [ObservableProperty] private CommitGraphRow? _selectedGraphRow;

    /// <summary>Widest lane count any loaded page demanded; every row reserves the same width.</summary>
    [ObservableProperty] private int _graphLaneCount;

    public double GraphLaneColumnWidth => Math.Max(1, GraphLaneCount) * GraphLaneWidth;

    partial void OnGraphLaneCountChanged(int value) => OnPropertyChanged(nameof(GraphLaneColumnWidth));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreGraphCommand))]
    private bool _graphLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreGraphCommand))]
    private bool _graphHasMore;

    /// <summary>True once a read has finished and found nothing; the empty state must not show before that.</summary>
    [ObservableProperty] private bool _graphEmpty;

    [ObservableProperty] private string _graphStatusText = "";
    [ObservableProperty] private string _graphErrorText = "";

    private int _graphSkip;

    /// <summary>
    /// The page read the pane started and did not await. Held so a caller — and a headless test —
    /// can wait for it instead of polling the properties it writes.
    /// </summary>
    internal Task GraphRefresh { get; private set; } = Task.CompletedTask;

    private bool CanLoadMoreGraph() => !GraphLoading && GraphHasMore && RepoPath.Length > 0;

    [RelayCommand]
    private async Task OpenCommitGraph()
    {
        if (RepoPath.Length == 0 || !SafetyOverlayHidden) return;

        GraphRows = [];
        SelectedGraphRow = null;
        GraphLaneCount = 0;
        GraphSkipReset();
        GraphEmpty = false;
        GraphHasMore = false;
        GraphStatusText = "";
        GraphErrorText = "";
        CommitGraphVisible = true;

        GraphRefresh = LoadGraphPageAsync(append: false);
        await GraphRefresh;
    }

    [RelayCommand]
    private void CloseCommitGraph()
    {
        CommitGraphVisible = false;
        GraphRows = [];
        SelectedGraphRow = null;
        GraphLaneCount = 0;
        GraphSkipReset();
        GraphEmpty = false;
        GraphHasMore = false;
        GraphLoading = false;
        GraphStatusText = "";
        GraphErrorText = "";
    }

    /// <summary>Drops the pane as the page leaves this repository; the graph it holds is that repository's.</summary>
    private void CloseCommitGraphOnProjectSwitch()
    {
        if (CommitGraphVisible) CloseCommitGraph();
    }

    private void GraphSkipReset() => _graphSkip = 0;

    [RelayCommand(CanExecute = nameof(CanLoadMoreGraph))]
    private Task LoadMoreGraph()
    {
        GraphRefresh = LoadGraphPageAsync(append: true);
        return GraphRefresh;
    }

    private async Task LoadGraphPageAsync(bool append)
    {
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0 || GraphLoading) return;

        GraphLoading = true;
        GraphStatusText = append ? "Reading older commits…" : "Reading the graph…";
        try
        {
            var page = await Graph.GetGraphAsync(repo,
                new CommitGraphRequest { Skip = append ? _graphSkip : 0, Take = GraphPageSize });
            if (!IsCurrent(gen) || !CommitGraphVisible) return;

            if (page.HasError)
            {
                // An empty page and a failed walk are indistinguishable to a reader; the
                // service separates them, so the pane must too.
                GraphErrorText =
                    "The commit graph could not be read. A ref may point at a missing object, or git may be unavailable.";
                GraphStatusText = "";
                GraphHasMore = false;
                if (!append) GraphEmpty = false;
                return;
            }

            GraphErrorText = "";
            var rows = CommitGraphRow.ForPage(page);
            if (append)
            {
                foreach (var row in rows) GraphRows.Add(row);
                OnPropertyChanged(nameof(GraphRows));
            }
            else
            {
                GraphRows = new ObservableCollection<CommitGraphRow>(rows);
            }

            _graphSkip = page.Skip + page.Commits.Count;
            GraphLaneCount = Math.Max(GraphLaneCount, page.LaneCount);
            GraphHasMore = page.HasMore;
            GraphEmpty = GraphRows.Count == 0;
            GraphStatusText = GraphEmpty
                ? ""
                : GraphHasMore
                    ? $"{GraphRows.Count} commits drawn across {GraphLaneCount} lane(s)."
                    : $"{GraphRows.Count} commits drawn across {GraphLaneCount} lane(s) — that is every one reachable from the local refs.";

            if (!append) PreselectGraphRowFromHistory();
        }
        catch (Exception ex)
        {
            Log.Warn($"commit graph failed for {repo}", ex);
            if (IsCurrent(gen))
            {
                GraphErrorText = $"The commit graph could not be read: {ex.Message}";
                GraphStatusText = "";
            }
        }
        finally
        {
            if (IsCurrent(gen)) GraphLoading = false;
        }
    }

    /// <summary>Opens the pane on the commit the History list has selected, when that commit is drawn.</summary>
    private void PreselectGraphRowFromHistory()
    {
        if (SelectedCommit?.Ref is not { Length: > 0 } sha) return;
        SelectedGraphRow = GraphRows.FirstOrDefault(
            r => string.Equals(r.Sha, sha, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mirrors the graph selection onto the History list. The graph walks every local branch
    /// while the list walks HEAD, so a drawn commit can legitimately have no row there — said
    /// plainly rather than left as a selection that silently did not move.
    /// </summary>
    partial void OnSelectedGraphRowChanged(CommitGraphRow? value)
    {
        if (value is null) return;
        var match = Commits.FirstOrDefault(
            c => string.Equals(c.Ref, value.Sha, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            GraphStatusText = HistoryHasMore
                ? $"{value.ShortSha} is not in the loaded History window — it is on another branch, or behind it."
                : $"{value.ShortSha} is not on the branch the History list is showing.";
            return;
        }
        SelectedCommit = match;
    }
}
