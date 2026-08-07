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

    // Stale index.lock recovery: shows a one-click "remove lock and retry" for the
    // op that failed on an orphaned lock (killed git never deletes its own lock).
    [ObservableProperty] private bool _staleLockRetryVisible;
    private Func<Task<ProcessResult>>? _staleLockRetryOp;
    private string _staleLockRetryLabel = "";

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

        var gen = _generation;
        var result = await RunOp(() => _gitService.CommitAsync(RepoPath, CommitMessage.Trim(), AmendMode),
            AmendMode ? "Amend" : "Commit");
        // A stale success must not clear a draft typed on the project switched to.
        if (result && IsCurrent(gen))
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
        var gen = _generation;
        var ok = await RunOp(() => _gitService.CreateBranchAsync(RepoPath, name), "Create branch");
        // A stale success must not blank a branch name typed on the project switched to.
        if (ok && IsCurrent(gen))
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
        var gen = _generation;
        try
        {
            var diff = await _gitService.GetCommitFileDiffAsync(RepoPath, commit.ShortHash, file.Path);
            if (IsCurrent(gen) && ReferenceEquals(SelectedCommitFile, file))
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
        var gen = _generation;
        if (Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        var pullRequests = await _gitHubService.GetPullRequestsAsync(Project.GitHubSlug);
        // A stale write would also set PullRequestsLoaded, making the new project's
        // tab skip its own load and open the previous project's PR numbers.
        if (IsCurrent(gen))
        {
            PullRequests = new ObservableCollection<GitHubPullRequest>(pullRequests);
            PullRequestsLoaded = true;
        }
    }

    [RelayCommand]
    private void OpenPullRequest(GitHubPullRequest? pr)
    {
        if (pr is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        Process.Start(new ProcessStartInfo($"https://github.com/{Project.GitHubSlug}/pull/{pr.Number}")
            { UseShellExecute = true });
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs a mutating git op with the busy guard, surfaces the outcome, refreshes state.
    /// The busy gate is generation-owned: only the generation that acquired it may
    /// release it, so a stale release is impossible, not merely unlikely. A project
    /// switch resets IsBusy and bumps the generation; an old op's finally observing a
    /// different generation is a no-op. An unconditional release would reopen the gate
    /// while the new project's op is mid-flight, letting two mutating git ops overlap
    /// on one repository (index.lock / FETCH_HEAD.lock collisions). A stale op also
    /// returns false and writes no status, so caller continuations are skipped.
    /// </summary>
    private async Task<bool> RunOp(Func<Task<ProcessResult>> op, string label)
    {
        if (IsBusy) return false;
        var gen = _generation;
        var repo = RepoPath;
        IsBusy = true;
        SyncStatusText = $"{label}…";
        StaleLockRetryVisible = false;
        _staleLockRetryOp = null;
        try
        {
            var result = await op();
            if (!IsCurrent(gen)) return false;
            SyncStatusText = result.Success ? $"{label} done." : $"{label} failed: {result.FirstError}";
            if (GitService.IsIndexLockConflict(result))
            {
                _staleLockRetryOp = op;
                _staleLockRetryLabel = label;
                StaleLockRetryVisible = true;
            }
            await RefreshWorkingStateAsync();
            return result.Success;
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} failed for {repo}", ex);
            if (IsCurrent(gen)) SyncStatusText = $"{label} failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (IsCurrent(gen)) IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveStaleLockAndRetry()
    {
        var op = _staleLockRetryOp;
        var label = _staleLockRetryLabel;
        _staleLockRetryOp = null;
        StaleLockRetryVisible = false;
        if (op is null || IsBusy) return;

        // One busy-gated unit: another op must not slip in between the lock
        // removal and the retry and recreate the contention being cleared.
        await RunOp(async () =>
        {
            var removed = await _gitService.TryCleanStaleLockAsync(RepoPath);
            if (!removed)
                return new ProcessResult(-1, "", "no stale lock found — a git process may still be running", TimedOut: false);
            return await op();
        }, label);
    }

    private async Task ReloadCommitsAsync()
    {
        var gen = _generation;
        var commits = await _gitService.GetRecentCommitsAsync(RepoPath, 50);
        if (!IsCurrent(gen)) return;
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
