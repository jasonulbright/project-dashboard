using System.Globalization;
using ProjectDashboard.Services;
using ProjectDashboard.Models;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Searching history by message, author, path, and date range. The results are their OWN list:
/// the History tab's <c>Commits</c> is a contiguous unfiltered walk that the surgery commands
/// read positionally, and filtering it in place would hand a rebase a depth measured against
/// commits the reader never saw. A result row selects its commit in the main list when the
/// loaded window holds it, and says so when it does not.
/// </summary>
public partial class ProjectDetailViewModel
{
    internal const int HistorySearchPageSize = 100;

    [ObservableProperty] private string _historySearchMessage = "";
    [ObservableProperty] private string _historySearchAuthor = "";
    [ObservableProperty] private string _historySearchPath = "";
    [ObservableProperty] private string _historySearchSince = "";
    [ObservableProperty] private string _historySearchUntil = "";
    [ObservableProperty] private string _historySearchStatus = "";
    [ObservableProperty] private bool _historySearchBusy;
    [ObservableProperty] private bool _historySearchHasMore;

    public ObservableCollection<GitCommit> HistorySearchResults { get; } = [];

    public bool HistorySearchHasResults => HistorySearchResults.Count > 0;

    /// <summary>The search this command started and did not await, for a caller to await instead of polling.</summary>
    internal Task HistorySearchLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The filter the fields currently describe, or null with a refusal when a date does not
    /// parse — a search silently run without the date the reader typed would answer a different
    /// question than the one asked.
    /// </summary>
    internal (CommitFilter? Filter, string Refusal) BuildHistoryFilter()
    {
        DateTimeOffset? since = null;
        DateTimeOffset? until = null;
        if (HistorySearchSince.Trim() is { Length: > 0 } sinceText)
        {
            if (!DateTimeOffset.TryParse(sinceText, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal, out var parsed))
                return (null, $"\"{sinceText}\" is not a date this search can read — try a form like 2026-08-01.");
            since = parsed;
        }
        if (HistorySearchUntil.Trim() is { Length: > 0 } untilText)
        {
            if (!DateTimeOffset.TryParse(untilText, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal, out var parsed))
                return (null, $"\"{untilText}\" is not a date this search can read — try a form like 2026-08-15.");
            // A bare date means the whole day: midnight would exclude every commit made on it.
            until = untilText.Contains(':') ? parsed : parsed.AddDays(1).AddTicks(-1);
        }

        var filter = new CommitFilter
        {
            MessageGrep = HistorySearchMessage.Trim() is { Length: > 0 } message ? message : null,
            Author = HistorySearchAuthor.Trim() is { Length: > 0 } author ? author : null,
            Path = HistorySearchPath.Trim() is { Length: > 0 } path ? path : null,
            Since = since,
            Until = until,
        };
        return filter is { MessageGrep: null, Author: null, Path: null, Since: null, Until: null }
            ? (null, "Type at least one filter first — an empty search is the History list above.")
            : (filter, "");
    }

    [RelayCommand]
    private Task SearchHistory()
    {
        HistorySearchLoad = SearchHistoryAsync(append: false);
        return HistorySearchLoad;
    }

    [RelayCommand]
    private Task LoadMoreHistorySearchResults()
    {
        HistorySearchLoad = SearchHistoryAsync(append: true);
        return HistorySearchLoad;
    }

    private async Task SearchHistoryAsync(bool append)
    {
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0 || HistorySearchBusy) return;

        var (filter, refusal) = BuildHistoryFilter();
        if (filter is null)
        {
            HistorySearchStatus = refusal;
            return;
        }

        HistorySearchBusy = true;
        HistorySearchStatus = "Searching…";
        try
        {
            var skip = append ? HistorySearchResults.Count : 0;
            var page = await _gitService.GetCommitsPagedAsync(repo, skip, HistorySearchPageSize, filter);
            if (!IsCurrent(gen)) return;

            if (!append) HistorySearchResults.Clear();
            foreach (var commit in page.Commits) HistorySearchResults.Add(commit);
            HistorySearchHasMore = page.HasMore;
            OnPropertyChanged(nameof(HistorySearchHasResults));

            HistorySearchStatus = HistorySearchResults.Count == 0
                ? "No commit matches these filters in this repository's history."
                : page.HasMore
                    ? $"{HistorySearchResults.Count} matching commits loaded — more exist."
                    : $"{HistorySearchResults.Count} matching {(HistorySearchResults.Count == 1 ? "commit" : "commits")} — that is every match.";
        }
        catch (Exception ex)
        {
            Log.Warn($"history search failed for {repo}", ex);
            if (IsCurrent(gen)) HistorySearchStatus = $"The search failed: {ex.Message}";
        }
        finally
        {
            if (IsCurrent(gen)) HistorySearchBusy = false;
        }
    }

    [RelayCommand]
    private void ClearHistorySearch()
    {
        HistorySearchMessage = "";
        HistorySearchAuthor = "";
        HistorySearchPath = "";
        HistorySearchSince = "";
        HistorySearchUntil = "";
        HistorySearchResults.Clear();
        HistorySearchHasMore = false;
        HistorySearchStatus = "";
        OnPropertyChanged(nameof(HistorySearchHasResults));
    }

    /// <summary>
    /// Puts a result's commit under the main list's selection when the loaded window holds it,
    /// so the surgery and detail affordances aim at it. A match deeper than the window is named
    /// as such rather than silently ignored — selecting it would first mean paging to it.
    /// </summary>
    [RelayCommand]
    private void SelectHistorySearchResult(GitCommit? commit)
    {
        if (commit is null) return;
        var loaded = Commits.FirstOrDefault(
            c => string.Equals(c.Ref, commit.Ref, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            SelectedCommit = loaded;
            HistorySearchStatus = $"Selected {commit.ShortHash} in the list above.";
            return;
        }
        HistorySearchStatus =
            $"{commit.ShortHash} is older than the {Commits.Count} commits the list above has loaded — " +
            "use \"Load older commits\" to page down to it.";
    }

    /// <summary>The search belongs to the repository it was typed against.</summary>
    private void ResetHistorySearch()
    {
        ClearHistorySearch();
        HistorySearchBusy = false;
    }
}
