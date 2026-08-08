using System.ComponentModel;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Paging the History list past its recent window (L-05).
///
/// INVARIANT the surgery commands depend on: <c>Commits</c> is a contiguous walk of
/// <c>git log HEAD</c> with index 0 at the tip and no filter applied. Depth is read
/// positionally — <c>Commits.IndexOf(SelectedCommit) + 1</c> becomes the <c>HEAD~depth</c> a
/// rebase is planned over — so a gap, an overlap, or a filtered subset would make a plan
/// rewrite commits the reader never saw.
///
/// A page is therefore appended only after the read is proved to continue the same walk: it
/// asks for one commit of overlap and refuses unless that commit is the one already at the end
/// of the list. A fetch, a pull, or a commit made in a terminal moves HEAD without this page
/// knowing, and a <c>--skip</c> taken from the new tip would land somewhere else entirely.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Commits the History list holds before any paging — what a page load and a refresh both read.</summary>
    internal const int HistoryRecentWindow = 50;

    /// <summary>Commits added per "load older" click.</summary>
    internal const int HistoryPageSize = 100;

    /// <summary>
    /// How many commits the list is currently walking back. Grows with each appended page and
    /// is what a post-operation reload re-reads, so paging is not undone by every commit.
    /// </summary>
    private int _historyWindowSize = HistoryRecentWindow;

    /// <summary>True while older commits may exist beyond the loaded window.</summary>
    [ObservableProperty] private bool _historyHasMore;

    [ObservableProperty] private string _historyPagingStatusText = "";

    /// <summary>Serializes the paging read against itself; two clicks must not append one page twice.</summary>
    [ObservableProperty] private bool _historyPaging;

    /// <summary>
    /// The append this command started and did not await. Held so a caller — and a headless
    /// test — can wait for the page instead of polling the properties it writes.
    /// </summary>
    internal Task HistoryPageLoad { get; private set; } = Task.CompletedTask;

    private bool CanLoadOlderCommits() => !HistoryPaging && HistoryHasMore && RepoPath.Length > 0;

    private void HandleHistoryDepthPropertyChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HistoryPaging):
            case nameof(HistoryHasMore):
            case nameof(Project):
                LoadOlderCommitsCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>Resets the window as the page leaves this repository; the depth belongs to the repository it was paged in.</summary>
    private void ResetHistoryWindow()
    {
        _historyWindowSize = HistoryRecentWindow;
        HistoryHasMore = false;
        HistoryPaging = false;
        HistoryPagingStatusText = "";
    }

    /// <summary>
    /// Whether a window of <paramref name="loaded"/> commits read at depth
    /// <paramref name="windowSize"/> may have older commits behind it. A full window cannot
    /// distinguish a branch of exactly that length from a longer one; the next click resolves
    /// it against git rather than guessing again.
    /// </summary>
    internal static bool WindowMayHaveMore(int loaded, int windowSize) => loaded >= windowSize;

    [RelayCommand(CanExecute = nameof(CanLoadOlderCommits))]
    private Task LoadOlderCommits()
    {
        HistoryPageLoad = LoadOlderCommitsAsync();
        return HistoryPageLoad;
    }

    private async Task LoadOlderCommitsAsync()
    {
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0 || HistoryPaging) return;

        // Nothing to anchor a continuation on: read the window from the tip instead.
        if (Commits.Count == 0)
        {
            await ReloadCommitsAsync();
            return;
        }

        var anchor = Commits[^1].Ref;
        HistoryPaging = true;
        HistoryPagingStatusText = "Reading older commits…";
        try
        {
            // One commit of overlap: the read starts at the list's last row so the reply
            // proves it continues the same walk before anything is appended.
            var page = await _gitService.GetCommitsPagedAsync(repo, Commits.Count - 1, HistoryPageSize + 1);
            if (!IsCurrent(gen)) return;

            if (page.Commits.Count == 0 ||
                !string.Equals(page.Commits[0].Ref, anchor, StringComparison.OrdinalIgnoreCase))
            {
                HistoryPagingStatusText =
                    "History moved since this list was read — it has been reloaded from the current tip.";
                await ReloadCommitsAsync();
                return;
            }

            for (var i = 1; i < page.Commits.Count; i++) Commits.Add(page.Commits[i]);
            _historyWindowSize = Commits.Count;
            HistoryHasMore = page.HasMore;
            // Appending in place keeps the scroll position, and raises no property change of
            // its own; the surgery gates read Commits.Count and would stay stale without this.
            OnPropertyChanged(nameof(Commits));
            if (Project is not null) Project.RecentCommits = [.. Commits];

            HistoryPagingStatusText = page.HasMore
                ? $"{Commits.Count} commits loaded."
                : $"{Commits.Count} commits loaded — that is the whole branch.";
        }
        catch (Exception ex)
        {
            Log.Warn($"history paging failed for {repo}", ex);
            if (IsCurrent(gen)) HistoryPagingStatusText = $"Could not read older commits: {ex.Message}";
        }
        finally
        {
            if (IsCurrent(gen)) HistoryPaging = false;
        }
    }
}
