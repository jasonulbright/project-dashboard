using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The per-file viewer: one path's commit history, followed across renames, beside a
/// blame of its current content. Read-only — nothing here changes a ref, an index, or a file.
///
/// Blame is read on a worker thread. The porcelain output of a large file is megabytes and its
/// parse is proportional to the line count, so running it on the dispatcher stalls the window
/// for as long as the file is long.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Commits read for one path. A file older than this is truncated, and the pane says so.</summary>
    internal const int FileHistoryLimit = 200;

    [ObservableProperty] private bool _fileHistoryVisible;

    partial void OnFileHistoryVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));

    /// <summary>Repository-relative path the viewer is showing.</summary>
    [ObservableProperty] private string _fileHistoryPath = "";

    [ObservableProperty] private ObservableCollection<GitCommit> _fileHistoryCommits = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectFileHistoryCommitInListCommand))]
    private GitCommit? _selectedFileHistoryCommit;

    [ObservableProperty] private ObservableCollection<BlameLine> _blameLines = [];

    [ObservableProperty] private BlameLine? _selectedBlameLine;

    [ObservableProperty] private bool _fileHistoryLoading;
    [ObservableProperty] private bool _blameLoading;

    /// <summary>True once a read has finished and found nothing; the empty state must not show before that.</summary>
    [ObservableProperty] private bool _fileHistoryEmpty;
    [ObservableProperty] private bool _blameEmpty;
    [ObservableProperty] private bool _blameTruncated;

    [ObservableProperty] private string _fileHistoryStatusText = "";
    [ObservableProperty] private string _fileHistoryErrorText = "";

    /// <summary>
    /// The reads the viewer started and did not await. Held so a caller — and a headless test —
    /// can wait for them instead of polling the properties they write.
    /// </summary>
    internal Task FileHistoryRefresh { get; private set; } = Task.CompletedTask;
    internal Task BlameRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Opens the viewer on one path. Refuses while any full-page pane is up: those cover this
    /// one, and a scrim stops the mouse but no keystroke.
    /// </summary>
    [RelayCommand]
    private async Task OpenFileHistory(string? path)
    {
        var target = (path ?? "").Trim();
        if (target.Length == 0 || RepoPath.Length == 0 || !SafetyOverlayHidden) return;

        FileHistoryPath = target;
        FileHistoryCommits = [];
        BlameLines = [];
        SelectedFileHistoryCommit = null;
        SelectedBlameLine = null;
        FileHistoryEmpty = false;
        BlameEmpty = false;
        BlameTruncated = false;
        FileHistoryStatusText = "";
        FileHistoryErrorText = "";
        FileHistoryVisible = true;

        FileHistoryRefresh = LoadFileHistoryAsync(target);
        BlameRefresh = LoadBlameAsync(target);
        await Task.WhenAll(FileHistoryRefresh, BlameRefresh);
    }

    [RelayCommand]
    private void CloseFileHistory()
    {
        FileHistoryVisible = false;
        FileHistoryPath = "";
        FileHistoryCommits = [];
        BlameLines = [];
        SelectedFileHistoryCommit = null;
        SelectedBlameLine = null;
        FileHistoryEmpty = false;
        BlameEmpty = false;
        BlameTruncated = false;
        FileHistoryLoading = false;
        BlameLoading = false;
        FileHistoryStatusText = "";
        FileHistoryErrorText = "";
    }

    /// <summary>Drops the viewer as the page leaves this repository; it describes a file of the repository the page no longer shows.</summary>
    private void CloseFileHistoryOnProjectSwitch()
    {
        if (FileHistoryVisible) CloseFileHistory();
    }

    private async Task LoadFileHistoryAsync(string path)
    {
        var repo = RepoPath;
        var gen = _generation;
        FileHistoryLoading = true;
        try
        {
            var history = await _gitService.GetFileHistoryAsync(repo, path, FileHistoryLimit);
            if (!IsCurrent(gen) || !string.Equals(FileHistoryPath, path, StringComparison.Ordinal)) return;

            if (history.HasError)
            {
                // A read that could not run and a path nothing ever touched look the same to a
                // reader; the service separates them, so the pane must too.
                FileHistoryErrorText = $"Could not read the history of {path}: {history.ErrorText}";
                FileHistoryEmpty = false;
                return;
            }

            var commits = history.Commits;
            FileHistoryCommits = new ObservableCollection<GitCommit>(commits);
            FileHistoryEmpty = commits.Count == 0;
            if (commits.Count >= FileHistoryLimit)
                FileHistoryStatusText =
                    $"Showing the {FileHistoryLimit} most recent commits that touched this path; it has more behind them.";
        }
        catch (Exception ex)
        {
            Log.Warn($"file history failed for {path} in {repo}", ex);
            if (IsCurrent(gen)) FileHistoryErrorText = $"Could not read the history of {path}: {ex.Message}";
        }
        finally
        {
            if (IsCurrent(gen)) FileHistoryLoading = false;
        }
    }

    private async Task LoadBlameAsync(string path)
    {
        var repo = RepoPath;
        var gen = _generation;
        BlameLoading = true;
        try
        {
            // Off the dispatcher: the porcelain parse is proportional to the file's line count.
            var blame = await Task.Run(() => _gitService.GetBlameAsync(repo, path));
            if (!IsCurrent(gen) || !string.Equals(FileHistoryPath, path, StringComparison.Ordinal)) return;

            if (blame.HasError)
            {
                FileHistoryErrorText = $"Could not blame {path}: {blame.ErrorText}";
                BlameEmpty = false;
                return;
            }

            BlameLines = new ObservableCollection<BlameLine>(blame.Lines);
            BlameEmpty = blame.Lines.Count == 0;
            BlameTruncated = blame.Truncated;
        }
        catch (Exception ex)
        {
            Log.Warn($"blame failed for {path} in {repo}", ex);
            if (IsCurrent(gen)) FileHistoryErrorText = $"Could not blame {path}: {ex.Message}";
        }
        finally
        {
            if (IsCurrent(gen)) BlameLoading = false;
        }
    }

    /// <summary>
    /// A blame row names the commit that last touched that line; selecting it selects that
    /// commit in the file's history beside it. A commit older than the loaded history has no
    /// row to select, and that is said rather than left as a click that does nothing.
    /// </summary>
    partial void OnSelectedBlameLineChanged(BlameLine? value)
    {
        if (value is null) return;
        var match = FileHistoryCommits.FirstOrDefault(
            c => string.Equals(c.Ref, value.Sha, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            FileHistoryStatusText =
                $"{Abbreviate(value.Sha)} is older than the loaded history of this path, so it has no row here.";
            return;
        }
        SelectedFileHistoryCommit = match;
        FileHistoryStatusText = "";
    }

    private bool CanSelectFileHistoryCommitInList() => SelectedFileHistoryCommit is not null;

    /// <summary>
    /// Closes the viewer on the selected commit's row in the page's own History list. The list
    /// is a window over the branch: a commit outside it — or one not on the current branch at
    /// all — has no row, and the viewer stays open saying so instead of closing onto nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectFileHistoryCommitInList))]
    private void SelectFileHistoryCommitInList()
    {
        if (SelectedFileHistoryCommit is not { } commit) return;
        var match = Commits.FirstOrDefault(
            c => string.Equals(c.Ref, commit.Ref, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            FileHistoryStatusText = HistoryHasMore
                ? $"{commit.ShortHash} is not in the loaded History window — load older commits there first."
                : $"{commit.ShortHash} is not on the branch the History list is showing.";
            return;
        }
        SelectedCommit = match;
        CloseFileHistory();
    }

    private static string Abbreviate(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
