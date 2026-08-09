using System.Windows;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Follows edits made to the OPEN repository outside the app. The dashboard's cards
/// already track the same debounced watcher signal, so without this the card and the
/// page it opens disagree about the same working tree for as long as the page stays up.
///
/// A watcher refresh is a read and never a mutation, but it still yields to the gates a
/// mutation holds: this page's busy flag and the repository lease that outlives it. A
/// read taken mid-swap describes refs that are half-written, and a signal refused here
/// is held rather than dropped — the edit that raised it is on disk whether or not the
/// operation covering it reports one.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Null outside the app host; the page then follows only its own operations.</summary>
    private readonly ProjectWatcherService? _watcher;

    /// <summary>
    /// Runs a callback on the UI thread. The watcher raises from a pool thread and the
    /// registry from whichever thread released the lease, while everything the refresh
    /// writes is bound state.
    /// </summary>
    private readonly Action<Action> _uiPost;

    /// <summary>A signal that named this repository while a gate was held, owed a refresh.</summary>
    private bool _watcherRefreshPending;

    /// <summary>
    /// The refresh the last signal started and did not await. Held so a caller can wait for
    /// the read itself; polling the lists it writes cannot tell a refresh that has not
    /// started yet from one that found nothing to change.
    /// </summary>
    internal Task WatcherRefresh { get; private set; } = Task.CompletedTask;

    private void SubscribeToRepoChanges()
    {
        if (_watcher is not null) _watcher.Changed += OnWatchedReposChanged;
        _busyRegistry.Changed += OnRepoLeaseChanged;
    }

    internal void OnWatchedReposChanged(IReadOnlyCollection<string> repoDirs) =>
        _uiPost(() => WatcherRefresh = HandleWatchedReposChangedAsync(repoDirs));

    private async Task HandleWatchedReposChangedAsync(IReadOnlyCollection<string> repoDirs)
    {
        if (!NamesOpenRepo(repoDirs)) return;
        await RefreshFromWatcherAsync();
    }

    /// <summary>
    /// Whether the signal covers the repository on screen. The empty set is the overflow
    /// signal: the watcher lost the events and can name nothing, so it covers every
    /// repository including this one.
    /// </summary>
    private bool NamesOpenRepo(IReadOnlyCollection<string> repoDirs)
    {
        if (Project is not { } project || project.DirectoryName.Length == 0) return false;
        return repoDirs.Count == 0 ||
               repoDirs.Any(dir => string.Equals(dir, project.DirectoryName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshFromWatcherAsync()
    {
        var repo = RepoPath;
        if (repo.Length == 0)
        {
            _watcherRefreshPending = false;
            return;
        }
        if (IsBusy || _busyRegistry.IsBusy(repo))
        {
            _watcherRefreshPending = true;
            return;
        }
        _watcherRefreshPending = false;
        var shown = DiffTarget;
        await SafeRefreshWorkingStateAsync();
        await FollowShownDiffAfterRefreshAsync(shown);
    }

    private void OnRepoLeaseChanged(string repoPath) => _uiPost(DrainWatcherRefresh);

    /// <summary>
    /// Both ends of a gate coming down. An operation releases the repository lease before
    /// it lowers this page's flag, so the registry's signal alone would still find a gate
    /// held; the flag's own transition is what closes that window.
    /// </summary>
    partial void OnIsBusyChanged(bool value)
    {
        if (!value) DrainWatcherRefresh();
    }

    /// <summary>
    /// Starts the held refresh, if one is held. Tested on the UI thread and never inside
    /// the task, so a drain that has nothing to do leaves the handle on the refresh that
    /// is actually running.
    /// </summary>
    private void DrainWatcherRefresh()
    {
        if (_watcherRefreshPending) WatcherRefresh = RefreshFromWatcherAsync();
    }

    /// <summary>
    /// Re-reads the working tree on demand. Refused rather than queued while an operation
    /// holds a gate: that operation refreshes the same surfaces when it finishes, and a
    /// button that appears to do nothing reads as broken.
    /// </summary>
    [RelayCommand]
    private async Task RefreshWorkingCopy()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        if (IsBusy || _busyRegistry.IsBusy(repo))
        {
            SyncStatusText = BusyNotice("Refresh");
            return;
        }
        var shown = DiffTarget;
        await SafeRefreshWorkingStateAsync();
        await FollowShownDiffAfterRefreshAsync(shown);
    }

    private static void PostToApplicationDispatcher(Action callback) =>
        _ = Application.Current?.Dispatcher.InvokeAsync(callback);
}
