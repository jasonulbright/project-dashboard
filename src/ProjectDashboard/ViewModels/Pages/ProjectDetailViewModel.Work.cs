using System.Diagnostics;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Work-area state for the detail page tabs: Changes, History, Branches,
/// Issues, Pull Requests, Stashes. Loads lazily per surface; every mutating
/// command refreshes the working state it invalidated.
/// </summary>
public partial class ProjectDetailViewModel
{
    // ── Working state (Changes tab + branch bar + state banner) ─────────────

    [ObservableProperty] private WorkingState? _workingState;
    [ObservableProperty] private ObservableCollection<WorkingFile> _stagedFiles = [];
    [ObservableProperty] private ObservableCollection<WorkingFile> _unstagedFiles = [];
    [ObservableProperty] private ObservableCollection<WorkingFile> _conflictedFiles = [];
    [ObservableProperty] private WorkingFile? _selectedStagedFile;
    [ObservableProperty] private WorkingFile? _selectedUnstagedFile;
    [ObservableProperty] private ObservableCollection<DiffLine> _diffLines = [];
    [ObservableProperty] private string _diffTitle = "";
    [ObservableProperty] private bool _diffIsBinary;
    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private bool _amendMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _syncStatusText = "";

    // State banner
    [ObservableProperty] private bool _stateBannerVisible;
    [ObservableProperty] private string _stateBannerText = "";

    // Branch bar
    [ObservableProperty] private string _branchLabel = "";
    [ObservableProperty] private string _aheadBehindLabel = "";

    // ── Branches tab ─────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<BranchInfo> _branches = [];
    [ObservableProperty] private BranchInfo? _selectedBranch;
    [ObservableProperty] private string _newBranchName = "";

    // ── Stashes tab ──────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<StashEntry> _stashes = [];
    [ObservableProperty] private StashEntry? _selectedStash;

    // ── History tab ──────────────────────────────────────────────────────────
    [ObservableProperty] private GitCommit? _selectedCommit;
    [ObservableProperty] private ObservableCollection<CommitFile> _commitFiles = [];
    [ObservableProperty] private CommitFile? _selectedCommitFile;
    [ObservableProperty] private ObservableCollection<DiffLine> _commitDiffLines = [];

    // ── Pull requests tab ────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<GitHubPullRequest> _pullRequests = [];
    [ObservableProperty] private bool _pullRequestsLoaded;

    private string RepoPath => Project?.FullPath ?? "";

    /// <summary>
    /// Bumped every time a different project is applied. Async continuations capture it
    /// and bail if it changed while they awaited — a slow op on project A must never write
    /// to (or mutate a file in) project B after the user switched. Guarding on RepoPath
    /// alone is not enough: two repos can share a path, and stale file lists could stage
    /// the wrong file.
    /// </summary>
    private int _generation;
    internal void BumpGeneration() => _generation++;
    private bool IsCurrent(int gen) => gen == _generation;

    /// <summary>Reload the working state and dependent UI (branch bar, banner, lists).</summary>
    public async Task RefreshWorkingStateAsync()
    {
        var gen = _generation;
        var repo = RepoPath;
        if (repo.Length == 0) return;

        var state = await _gitService.GetWorkingStateAsync(repo);
        if (!IsCurrent(gen)) return; // switched projects mid-await — drop this result

        WorkingState = state;
        if (state is null)
        {
            BranchLabel = "";
            AheadBehindLabel = "";
            StateBannerVisible = false;
            StagedFiles = [];
            UnstagedFiles = [];
            ConflictedFiles = [];
            return;
        }

        // Preserve the selected file across the rebuild (new instances every parse), so a
        // refresh triggered by an unrelated op doesn't blank the diff pane and selection.
        var keepStaged = SelectedStagedFile?.Path;
        var keepUnstaged = SelectedUnstagedFile?.Path;

        StagedFiles = new ObservableCollection<WorkingFile>(state.Staged);
        UnstagedFiles = new ObservableCollection<WorkingFile>(state.Unstaged);
        ConflictedFiles = new ObservableCollection<WorkingFile>(state.Conflicted);

        if (keepStaged is not null)
            SelectedStagedFile = StagedFiles.FirstOrDefault(f => f.Path == keepStaged);
        if (keepUnstaged is not null && SelectedStagedFile is null)
            SelectedUnstagedFile = UnstagedFiles.FirstOrDefault(f => f.Path == keepUnstaged);

        BranchLabel = state.Detached ? "detached HEAD" : state.Branch;
        AheadBehindLabel = !state.HasUpstream ? "no upstream"
            : (state.Ahead, state.Behind) switch
            {
                (0, 0) => "up to date",
                (var a, 0) => $"↑{a}",
                (0, var b) => $"↓{b}",
                var (a, b) => $"↑{a} ↓{b}"
            };

        (StateBannerVisible, StateBannerText) = state.Activity switch
        {
            RepoActivity.Merging => (true, state.HasConflicts
                ? "Merge in progress with conflicts — resolve them in a terminal, then commit."
                : "Merge in progress — commit or abort it in a terminal."),
            RepoActivity.Rebasing => (true, "Rebase in progress — continue or abort it in a terminal."),
            RepoActivity.CherryPicking => (true, "Cherry-pick in progress — continue or abort it in a terminal."),
            RepoActivity.Reverting => (true, "Revert in progress — continue or abort it in a terminal."),
            RepoActivity.Bisecting => (true, "Bisect in progress — finish it in a terminal."),
            _ when state.Detached => (true, "Detached HEAD — you're not on a branch; switch or create one before committing."),
            _ when state.HasConflicts => (true, "Unresolved conflicts — fix them in a terminal, then stage and commit."),
            _ => (false, "")
        };
    }

    // ── Stage / unstage / discard / diff ────────────────────────────────────

    partial void OnSelectedUnstagedFileChanged(WorkingFile? value)
    {
        if (value is not null)
        {
            SelectedStagedFile = null;
            _ = ShowDiffAsync(value, staged: false);
        }
        else if (SelectedStagedFile is null)
        {
            ClearDiff();
        }
    }

    partial void OnSelectedStagedFileChanged(WorkingFile? value)
    {
        if (value is not null)
        {
            SelectedUnstagedFile = null;
            _ = ShowDiffAsync(value, staged: true);
        }
        else if (SelectedUnstagedFile is null)
        {
            ClearDiff();
        }
    }

    /// <summary>List rebuilds null both selections; a diff for a file no longer listed must not linger.</summary>
    private void ClearDiff()
    {
        DiffLines = [];
        DiffTitle = "";
        DiffIsBinary = false;
    }

    private async Task ShowDiffAsync(WorkingFile file, bool staged)
    {
        var gen = _generation;
        var repo = RepoPath;
        try
        {
            DiffTitle = file.OrigPath is null ? file.Path : $"{file.OrigPath} → {file.Path}";
            var diff = await _gitService.GetFileDiffAsync(repo, file, staged);
            if (!IsCurrent(gen) || !ReferenceEquals(staged ? SelectedStagedFile : SelectedUnstagedFile, file))
                return; // selection or project changed mid-await
            DiffIsBinary = diff?.IsBinary ?? false;
            DiffLines = new ObservableCollection<DiffLine>(diff?.Lines ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn($"diff load failed for {file.Path}", ex);
            if (IsCurrent(gen)) DiffLines = [];
        }
    }

    [RelayCommand]
    private async Task StageFile(WorkingFile? file)
    {
        if (file is null || IsBusy) return;
        await RunOp(() => _gitService.StageAsync(RepoPath, file.Path), "Stage");
    }

    [RelayCommand]
    private async Task UnstageFile(WorkingFile? file)
    {
        if (file is null || IsBusy) return;
        await RunOp(() => _gitService.UnstageAsync(RepoPath, file.Path), "Unstage");
    }

    [RelayCommand]
    private async Task StageAll()
    {
        if (IsBusy) return;
        await RunOp(() => _gitService.StageAllAsync(RepoPath), "Stage all");
    }

    [RelayCommand]
    private async Task UnstageAll()
    {
        if (IsBusy) return;
        await RunOp(() => _gitService.UnstageAllAsync(RepoPath), "Unstage all");
    }

    [RelayCommand]
    private async Task DiscardFile(WorkingFile? file)
    {
        if (file is null || IsBusy) return;

        var verb = file.IsUntracked ? "Delete untracked file" : "Discard changes to";
        var confirmed = await ConfirmAsync("Discard changes?",
            $"{verb} {file.Path}?\n\nThis cannot be undone.", "Discard");
        if (!confirmed) return;

        await RunOp(() => _gitService.DiscardAsync(RepoPath, file), "Discard");
    }

    [RelayCommand]
    private async Task Commit()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            SyncStatusText = "Enter a commit message first.";
            return;
        }
        if (!AmendMode && StagedFiles.Count == 0)
        {
            SyncStatusText = "Nothing staged — stage files first.";
            return;
        }

        var result = await RunOp(() => _gitService.CommitAsync(RepoPath, CommitMessage.Trim(), AmendMode),
            AmendMode ? "Amend" : "Commit");
        if (result)
        {
            CommitMessage = "";
            AmendMode = false;
            await ReloadCommitsAsync();
        }
    }

    partial void OnAmendModeChanged(bool value)
    {
        // Prefill the last message when turning amend on into an empty box.
        if (value && string.IsNullOrWhiteSpace(CommitMessage))
            _ = PrefillAmendMessageAsync();
    }

    private async Task PrefillAmendMessageAsync()
    {
        var gen = _generation;
        var msg = await _gitService.GetLastCommitMessageAsync(RepoPath);
        if (IsCurrent(gen) && AmendMode && string.IsNullOrWhiteSpace(CommitMessage))
            CommitMessage = msg;
    }

    // ── Sync ops ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Fetch()
    {
        if (IsBusy) return;
        await RunOp(() => _gitService.FetchAsync(RepoPath), "Fetch");
    }

    [RelayCommand]
    private async Task Pull()
    {
        if (IsBusy) return;
        await RunOp(() => _gitService.PullAsync(RepoPath), "Pull");
    }

    [RelayCommand]
    private async Task Push()
    {
        if (IsBusy) return;
        var ok = await RunOp(() => _gitService.PushAsync(RepoPath), "Push");
        if (ok) await ReloadCommitsAsync();
    }

    [RelayCommand]
    private void OpenRepoInTerminal()
    {
        if (RepoPath.Length == 0) return;
        Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{RepoPath}\"") { UseShellExecute = true });
    }

    // ── Branches ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadBranches()
    {
        var gen = _generation;
        if (RepoPath.Length == 0) return;
        var branches = await _gitService.GetBranchesAsync(RepoPath);
        if (IsCurrent(gen))
            Branches = new ObservableCollection<BranchInfo>(branches);
    }

    [RelayCommand]
    private async Task CreateBranch()
    {
        var name = NewBranchName.Trim();
        if (name.Length == 0 || IsBusy) return;
        var ok = await RunOp(() => _gitService.CreateBranchAsync(RepoPath, name), "Create branch");
        if (ok)
        {
            NewBranchName = "";
            await LoadBranches();
        }
    }

    [RelayCommand]
    private async Task SwitchBranch(BranchInfo? branch)
    {
        if (branch is null || branch.IsCurrent || IsBusy) return;
        var ok = await RunOp(() => _gitService.SwitchBranchAsync(RepoPath, branch.Name), "Switch branch");
        if (ok)
        {
            await LoadBranches();
            await ReloadCommitsAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteBranch(BranchInfo? branch)
    {
        if (branch is null || IsBusy) return;
        if (branch.IsCurrent)
        {
            SyncStatusText = "Can't delete the current branch — switch away first.";
            return;
        }

        var confirmed = await ConfirmAsync("Delete branch?",
            $"Delete local branch {branch.Name}?\n\nOnly fully merged branches can be deleted this way.", "Delete");
        if (!confirmed) return;

        var ok = await RunOp(() => _gitService.DeleteBranchAsync(RepoPath, branch.Name), "Delete branch");
        if (ok) await LoadBranches();
    }

    // ── Stashes ─────────────────────────────────────────────────────────────

    /// <summary>Real "loaded" flag — Stashes.Count==0 is the common case, so it can't stand in.</summary>
    [ObservableProperty] private bool _stashesLoaded;

    [RelayCommand]
    private async Task LoadStashes()
    {
        var gen = _generation;
        if (RepoPath.Length == 0) return;
        var stashes = await _gitService.GetStashesAsync(RepoPath);
        if (IsCurrent(gen))
        {
            Stashes = new ObservableCollection<StashEntry>(stashes);
            StashesLoaded = true;
        }
    }

    [RelayCommand]
    private async Task StashApply(StashEntry? stash)
    {
        if (stash is null || IsBusy) return;
        var ok = await RunOp(() => _gitService.StashApplyAsync(RepoPath, stash.Ref), "Apply stash");
        if (ok) await LoadStashes();
    }

    [RelayCommand]
    private async Task StashPop(StashEntry? stash)
    {
        if (stash is null || IsBusy) return;
        var ok = await RunOp(() => _gitService.StashPopAsync(RepoPath, stash.Ref), "Pop stash");
        if (ok) await LoadStashes();
    }

    [RelayCommand]
    private async Task StashDrop(StashEntry? stash)
    {
        if (stash is null || IsBusy) return;
        var confirmed = await ConfirmAsync("Drop stash?",
            $"Drop {stash.Ref} ({stash.Subject})?\n\nThis cannot be undone.", "Drop");
        if (!confirmed) return;

        var ok = await RunOp(() => _gitService.StashDropAsync(RepoPath, stash.Ref), "Drop stash");
        if (ok) await LoadStashes();
    }

    // ── History ─────────────────────────────────────────────────────────────

    partial void OnSelectedCommitChanged(GitCommit? value)
    {
        CommitFiles = [];
        CommitDiffLines = [];
        SelectedCommitFile = null;
        if (value is not null)
            _ = LoadCommitFilesAsync(value);
    }

    private async Task LoadCommitFilesAsync(GitCommit commit)
    {
        var gen = _generation;
        try
        {
            var files = await _gitService.GetCommitFilesAsync(RepoPath, commit.ShortHash);
            if (IsCurrent(gen) && ReferenceEquals(SelectedCommit, commit))
                CommitFiles = new ObservableCollection<CommitFile>(files);
        }
        catch (Exception ex)
        {
            Log.Warn($"commit files failed for {commit.ShortHash}", ex);
        }
    }

    partial void OnSelectedCommitFileChanged(CommitFile? value)
    {
        CommitDiffLines = [];
        if (value is not null && SelectedCommit is not null)
            _ = LoadCommitDiffAsync(SelectedCommit, value);
    }

    private async Task LoadCommitDiffAsync(GitCommit commit, CommitFile file)
    {
        try
        {
            var diff = await _gitService.GetCommitFileDiffAsync(RepoPath, commit.ShortHash, file.Path);
            if (ReferenceEquals(SelectedCommitFile, file))
                CommitDiffLines = new ObservableCollection<DiffLine>(diff?.Lines ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn($"commit diff failed for {commit.ShortHash} {file.Path}", ex);
        }
    }

    // ── Pull requests ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadPullRequests()
    {
        if (Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        PullRequests = new ObservableCollection<GitHubPullRequest>(
            await _gitHubService.GetPullRequestsAsync(Project.GitHubSlug));
        PullRequestsLoaded = true;
    }

    [RelayCommand]
    private void OpenPullRequest(GitHubPullRequest? pr)
    {
        if (pr is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        Process.Start(new ProcessStartInfo($"https://github.com/{Project.GitHubSlug}/pull/{pr.Number}")
            { UseShellExecute = true });
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

    /// <summary>Runs a mutating git op with the busy guard, surfaces the outcome, refreshes state.</summary>
    private async Task<bool> RunOp(Func<Task<ProcessResult>> op, string label)
    {
        IsBusy = true;
        SyncStatusText = $"{label}…";
        try
        {
            var result = await op();
            SyncStatusText = result.Success ? $"{label} done." : $"{label} failed: {result.FirstError}";
            await RefreshWorkingStateAsync();
            return result.Success;
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} failed for {RepoPath}", ex);
            SyncStatusText = $"{label} failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCommitsAsync()
    {
        var commits = await _gitService.GetRecentCommitsAsync(RepoPath, 50);
        Commits = new ObservableCollection<GitCommit>(commits);
        if (Project is not null) Project.RecentCommits = commits;
    }

    private static async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = await new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel"
        }.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
