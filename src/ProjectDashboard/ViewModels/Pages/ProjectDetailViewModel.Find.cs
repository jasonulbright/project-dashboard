using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Find in this repository: the same fan-out the palette runs, narrowed to one working tree.
/// Read-only — it lists files and lines, and the one thing a row does is show that file in
/// Explorer, which is the only jump that works for an ignored file as well as a tracked one.
///
/// The scope switches are the palette's, and so is the rule about them: the pane opens on tracked
/// content every time. The widest scope reads build output, and a scope carried across opens would
/// spend that on a search nobody widened.
/// </summary>
public partial class ProjectDetailViewModel
{
    private RepoSearchService? _searchService;

    private RepoSearchService SearchService =>
        _searchService ??= NewSearchService(_gitService, _busyRegistry);

    /// <summary>
    /// The fan-out this pane runs, built once. Virtual so a test that asserts what a search found
    /// can hand it a budget the shipped clock cannot cut short — the pane's own reads are four git
    /// processes, and on a loaded machine four spawns alone can outlast the widest scope's budget.
    /// The registry is passed through rather than rebuilt: a search that consulted a private one
    /// would read a repository another operation holds.
    /// </summary>
    internal virtual RepoSearchService NewSearchService(GitService git, RepoBusyRegistry busy) =>
        new(git, busy);

    private readonly SearchScopeSelection _findScope = new();

    private CancellationTokenSource? _findCts;

    [ObservableProperty] private bool _findVisible;

    partial void OnFindVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));

    [ObservableProperty] private string _findTerm = "";

    [ObservableProperty] private ObservableCollection<RepoSearchHit> _findHits = [];

    [ObservableProperty] private string _findStatusText = "";

    /// <summary>Said beside the widest switch only; the other two cost what a reader expects.</summary>
    [ObservableProperty] private string _findScopeNoticeText = "";

    [ObservableProperty] private bool _findRunning;

    /// <summary>True once a search has finished and found nothing; the empty state must not show before that.</summary>
    [ObservableProperty] private bool _findEmpty;

    /// <summary>The scope the pane is searching under, as the three switches read it.</summary>
    public SearchContentScope FindScope => _findScope.Current;

    public bool FindScopeIsTracked => FindScope == SearchContentScope.Tracked;
    public bool FindScopeIsWithUntracked => FindScope == SearchContentScope.WithUntracked;
    public bool FindScopeIsEverything => FindScope == SearchContentScope.Everything;

    /// <summary>The search the pane started and did not await, so a caller waits for the rows rather than polling.</summary>
    internal Task FindRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Opens the pane. Refuses while any full-page pane is up: those cover this one, and a scrim
    /// stops the mouse but no keystroke.
    /// </summary>
    [RelayCommand]
    private void OpenFind()
    {
        if (RepoPath.Length == 0 || !SafetyOverlayHidden) return;

        _findScope.Reset();
        PublishFindScope();
        FindTerm = "";
        FindHits = [];
        FindEmpty = false;
        FindStatusText = "";
        FindVisible = true;
    }

    [RelayCommand]
    private void CloseFind()
    {
        CancelFind();
        FindVisible = false;
        FindTerm = "";
        FindHits = [];
        FindEmpty = false;
        FindRunning = false;
        FindStatusText = "";
    }

    /// <summary>Drops the pane as the page leaves this repository; its rows name files of the repository the page no longer shows.</summary>
    private void CloseFindOnProjectSwitch()
    {
        if (FindVisible) CloseFind();
    }

    [RelayCommand]
    private Task SetFindScope(string? scope)
    {
        if (!Enum.TryParse<SearchContentScope>(scope, out var parsed)) return Task.CompletedTask;
        if (!_findScope.Select(parsed)) return Task.CompletedTask;

        PublishFindScope();
        // The rows on screen were read under the previous scope; a scope change re-reads rather
        // than relabelling, so nothing is left standing under a heading it did not come from.
        return RunFindAsync();
    }

    private void PublishFindScope()
    {
        FindScopeNoticeText =
            _findScope.Current == SearchContentScope.Everything ? SearchScopeCopy.EverythingNotice : "";
        OnPropertyChanged(nameof(FindScope));
        OnPropertyChanged(nameof(FindScopeIsTracked));
        OnPropertyChanged(nameof(FindScopeIsWithUntracked));
        OnPropertyChanged(nameof(FindScopeIsEverything));
    }

    [RelayCommand]
    private Task RunFind() => RunFindAsync();

    private Task RunFindAsync()
    {
        FindRefresh = SearchThisRepoAsync();
        return FindRefresh;
    }

    private void CancelFind()
    {
        _findCts?.Cancel();
        _findCts?.Dispose();
        _findCts = null;
    }

    private async Task SearchThisRepoAsync()
    {
        var repo = RepoPath;
        var gen = _generation;
        var term = FindTerm.Trim();
        var scope = new SearchScope(_findScope.Current, SearchBreadth.CurrentRepo);

        FindHits = [];
        FindEmpty = false;

        if (repo.Length == 0) return;
        if (term.Length < RepoSearchService.MinTermLength)
        {
            FindStatusText = $"Type at least {RepoSearchService.MinTermLength} characters to search.";
            return;
        }

        CancelFind();
        var cts = new CancellationTokenSource();
        _findCts = cts;
        FindRunning = true;
        FindStatusText = $"Searching {SearchScopeCopy.Name(scope.Content)}…";
        try
        {
            var result = await SearchService.SearchAsync(
                term, [new RepoSearchTarget(ProjectName, repo)], scope, cts.Token);
            if (!IsCurrent(gen) || !ReferenceEquals(_findCts, cts)) return;

            FindHits = [.. result.Hits];
            FindEmpty = result.Hits.Count == 0;
            FindStatusText = result.ReposSkipped > 0
                ? "This repository could not be read — another operation may be holding it."
                : SearchScopeCopy.Summary(result, scope);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"find failed in {repo}", ex);
            if (IsCurrent(gen)) FindStatusText = $"The search failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_findCts, cts))
            {
                _findCts = null;
                FindRunning = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Shows one hit's file in Explorer. The jump that works at every scope: an ignored file has
    /// no history to open and an untracked one has no blame, and a row whose action only worked
    /// for tracked files would be dead on most of what the widest scope returns.
    /// </summary>
    [RelayCommand]
    private void RevealFindHit(RepoSearchHit? hit)
    {
        if (hit is null || RepoPath.Length == 0) return;

        var full = System.IO.Path.Combine(RepoPath, hit.FilePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var failure = RevealInShell(full);
        FindStatusText = failure ?? $"{hit.FilePath} is shown in Explorer.";
    }

    /// <summary>The display name the fan-out labels this repository's rows with.</summary>
    private string ProjectName =>
        Project?.DisplayName is { Length: > 0 } name ? name : System.IO.Path.GetFileName(RepoPath);
}
