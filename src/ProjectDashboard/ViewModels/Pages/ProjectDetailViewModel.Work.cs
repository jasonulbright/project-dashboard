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

    /// <summary>
    /// Whatever raised <see cref="IsBusy"/>, or null while the gate is down. Only the holder
    /// that raised the gate releases it. A rewrite step outlives the page it started on, so a
    /// step whose session has left the live wizard still owns the gate it took — and must not
    /// lower the one an operation started on the page it left has since raised.
    /// </summary>
    private object? _busyGateHolder;

    // Stale index.lock recovery: shows a one-click "remove lock and retry" for the
    // op that failed on an orphaned lock (killed git never deletes its own lock).
    // The stashed op is repo-bound: it runs against the path passed in, and the
    // retry passes _staleLockRetryRepo — the path the op failed on — never the
    // live RepoPath, which a project switch can change while a retry is in flight.
    [ObservableProperty] private bool _staleLockRetryVisible;
    private Func<string, Task<ProcessResult>>? _staleLockRetryOp;
    private string _staleLockRetryRepo = "";
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
    [ObservableProperty] private string _newStashMessage = "";
    [ObservableProperty] private bool _stashIncludeUntracked;

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
    /// The app-wide record of repositories under a destructive operation. <see cref="IsBusy"/>
    /// serializes this page's ops against each other only; a rewrite runs under a lease on the
    /// repository, outlives this page, and is reachable from surfaces this flag knows nothing of.
    /// </summary>
    private readonly Services.Safety.RepoBusyRegistry _busyRegistry;

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

    /// <summary>
    /// How many times a different project has been applied. Read by tests that have to show a
    /// switch was NOT applied: the in-flight work of the project on screen is invalidated by the
    /// bump, so a switch skipped correctly is one that left this alone.
    /// </summary>
    internal int AppliedGeneration => _generation;

    /// <summary>
    /// Reload the working state and dependent UI (branch bar, banner, lists).
    ///
    /// Every caller joins the one read in flight and leaves a pass owed behind it. Two reads
    /// running side by side both write the file lists, and the one that started earlier can
    /// finish later — leaving the pane describing a working tree that has already moved on.
    /// The gates the mutating operations hold do not cover this: a watcher signal and a
    /// manual refresh are both ungated, and either can meet the read a project load starts.
    /// </summary>
    public Task RefreshWorkingStateAsync()
    {
        _workingStateReadOwed = true;
        return _workingStateRead.IsCompleted
            ? _workingStateRead = DrainWorkingStateReadsAsync()
            : _workingStateRead;
    }

    private Task _workingStateRead = Task.CompletedTask;
    private bool _workingStateReadOwed;

    /// <summary>
    /// Runs reads until none is owed. The flag is set before a caller joins, and it is only
    /// ever read on the UI thread between awaits, so a request arriving while a read is in
    /// flight is always answered by a pass that starts after it.
    /// </summary>
    private async Task DrainWorkingStateReadsAsync()
    {
        while (_workingStateReadOwed)
        {
            _workingStateReadOwed = false;
            await ReadWorkingStateAsync();
        }
    }

    private async Task ReadWorkingStateAsync()
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

        // Preserve the selection across the rebuild (new instances every parse), so a refresh
        // triggered by an unrelated op doesn't blank the diff pane and selection. Matched by
        // path, and the whole multi-selection, not only the focused row: a batch the reader
        // built by hand must survive the refresh that every operation on it triggers.
        var keepStaged = SelectedStagedFile?.Path;
        var keepUnstaged = SelectedUnstagedFile?.Path;
        var keepStagedSet = SelectedStagedFiles.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var keepUnstagedSet = SelectedUnstagedFiles.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        StagedFiles = Rebuild(state.Staged, StagedFiles);
        UnstagedFiles = Rebuild(state.Unstaged, UnstagedFiles);
        ConflictedFiles = Rebuild(state.Conflicted, ConflictedFiles);

        SelectedStagedFiles = StagedFiles.Where(f => keepStagedSet.Contains(f.Path)).ToList();
        SelectedUnstagedFiles = UnstagedFiles.Where(f => keepUnstagedSet.Contains(f.Path)).ToList();

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

    /// <summary>
    /// The rebuilt rows for one side, carrying forward the instance already on screen wherever
    /// the read describes the same file in the same state. A selection is held by reference, so
    /// a fresh instance for a file nothing moved re-fires the selection handlers — which drop
    /// the diff rows and the hunk row the reader is on. That row is what a confirmed hunk
    /// operation names, and a refresh nobody asked for must not take it away. A file that did
    /// move gets its new instance, and the reload that follows from it is the correct one.
    /// </summary>
    private static ObservableCollection<WorkingFile> Rebuild(
        IEnumerable<WorkingFile> read, IEnumerable<WorkingFile> shown)
    {
        var byPath = new Dictionary<string, WorkingFile>(StringComparer.Ordinal);
        foreach (var file in shown) byPath.TryAdd(file.Path, file);
        return new ObservableCollection<WorkingFile>(
            read.Select(file => byPath.TryGetValue(file.Path, out var existing) && existing.SameAs(file)
                ? existing
                : file));
    }

    // ── Stage / unstage / discard / diff ────────────────────────────────────

    /// <summary>
    /// The diff read a selection started and did not await. Held so a caller — and a headless
    /// test — can wait for the rows the selection asked for; polling the collection cannot tell
    /// the previous file's rows from the new file's.
    /// </summary>
    internal Task DiffRefresh { get; private set; } = Task.CompletedTask;

    // The hunk gates read the selected row, and the pane still holds the previous file's rows
    // until the read below returns; a row of those names a hunk of the file now selected.
    // The row is dropped as the selection moves, not once the replacement rows arrive.
    // Focus moving to one side clears the other side's selection outright, not just its
    // focused row: the actions on that side read its whole selection, and a selection left
    // highlighted beside a diff of the other side arms a button the reader is not looking at.
    partial void OnSelectedUnstagedFileChanged(WorkingFile? value)
    {
        if (value is not null)
        {
            SelectedStagedFile = null;
            SelectedStagedFiles = [];
            if (!SelectedUnstagedFiles.Contains(value)) SelectedUnstagedFiles = [value];
            SelectedDiffLine = null;
            DiffRefresh = ShowDiffAsync(value, staged: false);
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
            SelectedUnstagedFiles = [];
            if (!SelectedStagedFiles.Contains(value)) SelectedStagedFiles = [value];
            SelectedDiffLine = null;
            DiffRefresh = ShowDiffAsync(value, staged: true);
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
        DiffIsCombined = false;
        SelectedDiffLine = null;
        _diffFocus = null;
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
            DiffIsCombined = diff?.IsCombined ?? false;
            SelectedDiffLine = null;
            DiffLines = new ObservableCollection<DiffLine>(diff?.Lines ?? []);
            RestoreDiffFocus(file, staged);
        }
        catch (Exception ex)
        {
            Log.Warn($"diff load failed for {file.Path}", ex);
            if (IsCurrent(gen)) { DiffLines = []; _diffFocus = null; }
        }
    }

    [RelayCommand]
    private async Task StageAll()
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Stage all"); return; }
        var repo = RepoPath;
        var gen = _generation;
        // `git restore --staged .` clears the whole index, not the paths `git add -A` just added
        // to it. Content staged before this ran would go with them, so the offer stands only
        // where the index was empty and unstaging everything is the exact inverse.
        var indexWasEmpty = StagedFiles.Count == 0;
        if (await RunOp(r => _gitService.StageAllAsync(r), "Stage all", repo, gen) && IsCurrent(gen)
            && indexWasEmpty)
            OfferUndo("Unstage all", "Unstage all", repo, r => _gitService.UnstageAllAsync(r));
    }

    [RelayCommand]
    private async Task UnstageAll()
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Unstage all"); return; }
        var repo = RepoPath;
        var gen = _generation;
        // "Stage all again", not "undo": `git add -A` restages everything the worktree holds,
        // which is the previous index only when the index matched the worktree to begin with.
        if (await RunOp(r => _gitService.UnstageAllAsync(r), "Unstage all", repo, gen) && IsCurrent(gen))
            OfferUndo("Stage all again", "Stage all", repo, r => _gitService.StageAllAsync(r));
    }

    [RelayCommand]
    private async Task Commit()
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Commit"); return; }
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
        var result = await RunOp(repo => _gitService.CommitAsync(repo, CommitMessage.Trim(), AmendMode),
            AmendMode ? "Amend" : "Commit", RepoPath, gen);
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
        if (IsBusy) { SyncStatusText = BusyNotice("Fetch"); return; }
        await RunOp(repo => _gitService.FetchAsync(repo), "Fetch", RepoPath, _generation);
    }

    [RelayCommand]
    private async Task Pull()
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Pull"); return; }
        await RunOp(repo => _gitService.PullAsync(repo), "Pull", RepoPath, _generation);
    }

    [RelayCommand]
    private async Task Push()
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Push"); return; }
        var ok = await RunOp(repo => _gitService.PushAsync(repo), "Push", RepoPath, _generation);
        if (ok) await ReloadCommitsAsync();
    }

    /// <summary>
    /// Opens the repository in Windows Terminal. The launch is the whole operation, so a
    /// machine without wt.exe gets the failure in the status line — unhandled, the shell's
    /// Win32Exception would reach the dispatcher and take the app down.
    /// </summary>
    [RelayCommand]
    private void OpenRepoInTerminal()
    {
        if (RepoPath.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{RepoPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"terminal launch failed for {RepoPath}", ex);
            SyncStatusText = $"Could not open a terminal here: {ex.Message}";
        }
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
        if (name.Length == 0) { SyncStatusText = "Enter a branch name first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Create branch"); return; }
        var gen = _generation;
        var ok = await RunOp(repo => _gitService.CreateBranchAsync(repo, name), "Create branch",
            RepoPath, gen);
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
        if (branch is null) { SyncStatusText = "Select a branch first."; return; }
        if (branch.IsCurrent) { SyncStatusText = $"Already on {branch.Name}."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Switch branch"); return; }
        var ok = await RunOp(repo => _gitService.SwitchBranchAsync(repo, branch.Name), "Switch branch",
            RepoPath, _generation);
        if (ok)
        {
            await LoadBranches();
            await ReloadCommitsAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteBranch(BranchInfo? branch)
    {
        if (branch is null) { SyncStatusText = "Select a branch first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Delete branch"); return; }
        if (branch.IsCurrent)
        {
            SyncStatusText = "Can't delete the current branch — switch away first.";
            return;
        }
        // Two repos can hold the same branch name, so a rebind survives the delete's
        // own merged-only check and removes a ref the confirmation never named.
        var confirmedRepo = RepoPath;
        var gen = _generation;

        var confirmed = await ConfirmAsync("Delete branch?",
            $"Delete local branch {branch.Name}?\n\nOnly fully merged branches can be deleted this way.", "Delete");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            SyncStatusText = ProjectSwitchedNotice("Branch delete");
            return;
        }

        var ok = await RunOp(repo => _gitService.DeleteBranchAsync(repo, branch.Name), "Delete branch",
            confirmedRepo, gen);
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

    /// <summary>
    /// Whether a push would take anything. `git stash push` exits zero and saves nothing when the
    /// tree holds only files it was not asked to take, so a run reported from the exit code alone
    /// claims a snapshot that does not exist.
    /// </summary>
    private bool HasChangesToStash() =>
        WorkingState is { } state && state.Files.Any(f => StashIncludeUntracked || !f.IsUntracked);

    [RelayCommand]
    private async Task StashChanges()
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Stash changes"); return; }
        if (RepoPath.Length == 0) return;
        if (!HasChangesToStash())
        {
            SyncStatusText = WorkingState is { IsDirty: true }
                ? "Nothing to stash — only untracked files have changed; turn on \"Include untracked files\" to take them."
                : "Nothing to stash — the working tree is clean.";
            return;
        }

        var message = NewStashMessage.Trim();
        var includeUntracked = StashIncludeUntracked;
        var gen = _generation;
        var ok = await RunOp(
            repo => _gitService.StashPushAsync(repo, message.Length == 0 ? null : message, includeUntracked),
            "Stash changes", RepoPath, gen);
        // A stale success must not blank a message typed on the project switched to.
        if (ok && IsCurrent(gen))
        {
            NewStashMessage = "";
            await LoadStashes();
        }
    }

    [RelayCommand]
    private async Task StashApply(StashEntry? stash)
    {
        if (stash is null) { SyncStatusText = "Select a stash first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Apply stash"); return; }
        var ok = await RunOp(repo => _gitService.StashApplyAsync(repo, stash.Ref), "Apply stash",
            RepoPath, _generation);
        if (ok) await LoadStashes();
    }

    /// <summary>
    /// Pops a stash. A pop that fails — a conflict, most often — leaves the entry in place, and
    /// saying so is the whole recovery: the work is not lost and the list below still holds it.
    /// </summary>
    [RelayCommand]
    private async Task StashPop(StashEntry? stash)
    {
        if (stash is null) { SyncStatusText = "Select a stash first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Pop stash"); return; }
        var ok = await RunOp(repo => _gitService.StashPopAsync(repo, stash.Ref), "Pop stash",
            RepoPath, _generation,
            _ => $"{stash.Ref} was kept — resolve the working tree, then apply or drop it from this list.");
        if (ok) await LoadStashes();
    }

    [RelayCommand]
    private async Task StashDrop(StashEntry? stash)
    {
        if (stash is null) { SyncStatusText = "Select a stash first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Drop stash"); return; }
        // stash@{0} resolves in every repo that has a stash, so a rebind silently drops
        // a different repo's entry — unrecoverable once the reflog entry is gone.
        var confirmedRepo = RepoPath;
        var gen = _generation;

        var confirmed = await ConfirmAsync("Drop stash?",
            $"Drop {stash.Ref} ({stash.Subject})?\n\nThis cannot be undone.", "Drop");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            SyncStatusText = ProjectSwitchedNotice("Stash drop");
            return;
        }

        var ok = await RunOp(repo => _gitService.StashDropAsync(repo, stash.Ref), "Drop stash",
            confirmedRepo, gen);
        if (ok) await LoadStashes();
    }

    // ── Stash preview ───────────────────────────────────────────────────────

    /// <summary>Files the selected stash changed, and the rows of the one being read.</summary>
    [ObservableProperty] private ObservableCollection<FileDiff> _stashDiffFiles = [];
    [ObservableProperty] private FileDiff? _selectedStashDiffFile;
    [ObservableProperty] private ObservableCollection<DiffLine> _stashDiffLines = [];
    [ObservableProperty] private string _stashDiffError = "";

    private const string StashDiffReadFailed =
        "Couldn't read this stash — it is still in the list; nothing about it has changed.";

    /// <summary>
    /// The preview read a stash selection started and did not await. Held so a caller can wait
    /// for the rows that selection asked for; polling the collection cannot tell one stash's
    /// rows from the previous one's.
    /// </summary>
    internal Task StashDiffRefresh { get; private set; } = Task.CompletedTask;

    partial void OnSelectedStashChanged(StashEntry? value)
    {
        StashDiffFiles = [];
        SelectedStashDiffFile = null;
        StashDiffLines = [];
        StashDiffError = "";
        if (value is not null) StashDiffRefresh = LoadStashDiffAsync(value);
    }

    private async Task LoadStashDiffAsync(StashEntry stash)
    {
        var gen = _generation;
        var repo = RepoPath;
        if (repo.Length == 0) return;
        try
        {
            var files = await _gitService.GetStashDiffAsync(repo, stash.Ref);
            if (!IsCurrent(gen) || !ReferenceEquals(SelectedStash, stash)) return;
            if (files is null)
            {
                StashDiffError = StashDiffReadFailed;
                return;
            }
            StashDiffFiles = new ObservableCollection<FileDiff>(files);
            SelectedStashDiffFile = StashDiffFiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn($"stash diff failed for {stash.Ref}", ex);
            if (IsCurrent(gen) && ReferenceEquals(SelectedStash, stash)) StashDiffError = StashDiffReadFailed;
        }
    }

    partial void OnSelectedStashDiffFileChanged(FileDiff? value) =>
        StashDiffLines = new ObservableCollection<DiffLine>(value?.Lines ?? []);

    // ── History ─────────────────────────────────────────────────────────────

    partial void OnSelectedCommitChanged(GitCommit? value)
    {
        CommitFiles = [];
        CommitDiffLines = [];
        SelectedCommitFile = null;
        OnPropertyChanged(nameof(TagTargetLabel));
        if (value is not null)
            _ = LoadCommitFilesAsync(value);
    }

    private async Task LoadCommitFilesAsync(GitCommit commit)
    {
        var gen = _generation;
        try
        {
            var files = await _gitService.GetCommitFilesAsync(RepoPath, commit.Ref);
            if (IsCurrent(gen) && ReferenceEquals(SelectedCommit, commit))
                CommitFiles = new ObservableCollection<CommitFile>(files);
        }
        catch (Exception ex)
        {
            Log.Warn($"commit files failed for {commit.Ref}", ex);
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
            var diff = await _gitService.GetCommitFileDiffAsync(RepoPath, commit.Ref, file.Path);
            if (IsCurrent(gen) && ReferenceEquals(SelectedCommitFile, file))
                CommitDiffLines = new ObservableCollection<DiffLine>(diff?.Lines ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn($"commit diff failed for {commit.Ref} {file.Path}", ex);
        }
    }

    // ── Pull requests ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadPullRequests()
    {
        var gen = _generation;
        var slug = Project?.GitHubSlug ?? "";
        if (slug.Length == 0)
        {
            PullRequestsError = NoRemoteStatus;
            return;
        }
        var pullRequests = await FetchPullRequestsAsync(slug);
        // A stale write would also set PullRequestsLoaded, making the new project's
        // tab skip its own load and open the previous project's PR numbers.
        if (!IsCurrent(gen)) return;
        if (pullRequests is null)
        {
            // Marked loaded, the next visit would skip its own read and show an empty list
            // as though the repository had no open pull requests.
            PullRequestsError = PullRequestsFetchFailed;
            return;
        }
        PullRequestsError = "";
        PullRequests = new ObservableCollection<GitHubPullRequest>(pullRequests);
        PullRequestsLoaded = true;
    }

    [RelayCommand]
    private void OpenPullRequest(GitHubPullRequest? pr)
    {
        if (pr is null || Project is null || string.IsNullOrEmpty(Project.GitHubSlug)) return;
        OpenExternal($"https://github.com/{Project.GitHubSlug}/pull/{pr.Number}");
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Says that a sanctioned op was dropped because the project changed while its
    /// dialog was open. The caller writes it AFTER the generation guard and never
    /// inside the op: the switch that suppressed the op has already run ApplyProject,
    /// which clears both status lines, so a notice written any earlier is wiped.
    /// </summary>
    internal static string ProjectSwitchedNotice(string op) =>
        $"{op} cancelled — the project changed while the dialog was open.";

    /// <summary>
    /// Says that a sanctioned op never started because the repository was already under one.
    /// The gate is invisible, so a button that does nothing and says nothing reads as broken.
    /// </summary>
    internal static string BusyNotice(string op) =>
        $"{op} not started — another operation is running on this repository.";

    /// <summary>
    /// Runs a mutating git op with the busy guard, surfaces the outcome, refreshes state.
    /// The op receives the repo path the CALLER captured and must run against it —
    /// reading the live RepoPath here would rebind any op that awaited something first
    /// (a confirmation dialog, a stale-lock cleanup) to whatever repo a switch made
    /// current, so a confirmed discard/branch delete/stash drop would destroy work in a
    /// repository the confirmation never named.
    /// A run whose captured generation has moved is suppressed before the op starts, not
    /// merely denied its UI writes: the sanction belongs to a project no longer on screen.
    /// The busy gate is generation-owned: only the generation that acquired it may
    /// release it, so a stale release is impossible, not merely unlikely. A project
    /// switch resets IsBusy and bumps the generation; an old op's finally observing a
    /// different generation is a no-op. An unconditional release would reopen the gate
    /// while the new project's op is mid-flight, letting two mutating git ops overlap
    /// on one repository (index.lock / FETCH_HEAD.lock collisions). A stale op also
    /// returns false and writes no status, so caller continuations are skipped. The op
    /// records itself as the gate's holder for the other direction: a rewrite step that
    /// started on an earlier page returns under no generation of its own, and the holder
    /// is what tells it the gate it finds is not the gate it took.
    ///
    /// The repository lease is the gate that holds across pages: a rewrite's swap runs under
    /// one, and an op that consulted only this page's flag would run `git pull` into the middle
    /// of it, merging the un-rewritten remote history back over the rewrite. Held for the whole
    /// op in both directions, so a rewrite started while an op runs is refused rather than
    /// interleaved.
    /// </summary>
    /// <param name="advice">
    /// What the reader can still do about a failure, appended to the failure line. Consulted
    /// only for an op that ran and failed — never for one refused or suppressed, which changed
    /// nothing and has nothing to recover from.
    /// </param>
    private async Task<bool> RunOp(Func<string, Task<ProcessResult>> op, string label, string repo, int gen,
        Func<ProcessResult, string?>? advice = null)
    {
        if (!IsCurrent(gen) || IsBusy) return false;
        if (repo.Length == 0) return false;
        if (!_busyRegistry.TryAcquire(repo, out var lease))
        {
            SyncStatusText = $"{label} refused: another operation is running on this repository.";
            return false;
        }
        var holder = new object();
        IsBusy = true;
        _busyGateHolder = holder;
        SyncStatusText = $"{label}…";
        StaleLockRetryVisible = false;
        _staleLockRetryOp = null;
        // The previous operation's inverse belongs beside the outcome it undid. A new
        // operation replaces that outcome, so the offer goes with it.
        ClearUndoOffer();
        try
        {
            var result = await op(repo);
            if (!IsCurrent(gen))
            {
                // A stale op still mutated its bound repo on disk. When that repo
                // is the one back on screen (switched away and back), the lists
                // shown were loaded mid-op and predate the mutation — refresh
                // them; the refresh reads under the CURRENT generation, so it is
                // not a stale write. When the op's repo differs from the current
                // one, nothing on screen describes it, and a refresh here would
                // poke the unrelated current project's UI from a stale
                // continuation — it must not run.
                if (repo == RepoPath) await SafeRefreshWorkingStateAsync();
                return false;
            }
            SyncStatusText = result.Success ? $"{label} done." : $"{label} failed: {result.FirstError}";
            if (!result.Success && advice?.Invoke(result) is { Length: > 0 } recovery)
                SyncStatusText += $" {recovery}";
            if (GitService.IsIndexLockConflict(result))
            {
                _staleLockRetryOp = op;
                _staleLockRetryRepo = repo;
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
            lease.Dispose();
            if (ReferenceEquals(_busyGateHolder, holder))
            {
                _busyGateHolder = null;
                if (IsCurrent(gen)) IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task RemoveStaleLockAndRetry()
    {
        var op = _staleLockRetryOp;
        var repo = _staleLockRetryRepo;
        var label = _staleLockRetryLabel;
        _staleLockRetryOp = null;
        StaleLockRetryVisible = false;
        if (op is null || IsBusy) return;

        // One busy-gated unit: another op must not slip in between the lock
        // removal and the retry and recreate the contention being cleared.
        // Both halves are bound to the stashed repo path, never the live
        // RepoPath. The cleanup's age re-check delay leaves a window wide
        // enough for a project switch to land mid-flight, and the stashed op
        // replays a mutation — worst case a Discard — so a moved generation
        // abandons the retry before the op runs: the click that sanctioned the
        // replay was made on the project that has since left the screen.
        // Dispatcher continuations make the check-then-invoke atomic against
        // ApplyProject.
        var gen = _generation;
        await RunOp(async _ =>
        {
            var removed = await _gitService.TryCleanStaleLockAsync(repo);
            if (!IsCurrent(gen))
                return new ProcessResult(-1, "", "project switched during lock cleanup — retry abandoned", TimedOut: false);
            if (!removed)
                return new ProcessResult(-1, "", "no stale lock found — a git process may still be running", TimedOut: false);
            return await op(repo);
        }, label, repo, gen);
    }

    /// <summary>
    /// Re-reads the History list and re-selects the same commit by sha. Every entry is a fresh
    /// object, so the selection — which the surgery commands read to decide their range — would
    /// otherwise be dropped by every reload, including the one that follows a mutating operation.
    /// A commit the operation rewrote or removed has no sha in the new list and the selection
    /// clears, which is the honest outcome: it names history that is gone.
    /// A selection made while the read was in flight wins over the one captured before it: the
    /// later click is the user's current intent, and restoring the earlier sha would undo it.
    /// </summary>
    private async Task ReloadCommitsAsync()
    {
        var gen = _generation;
        // Re-read the window the list has been paged out to, not the default one: collapsing
        // back to the recent page would drop every page the reader loaded, and with it any
        // selection deeper than that page.
        var commits = await _gitService.GetRecentCommitsAsync(RepoPath, _historyWindowSize);
        if (!IsCurrent(gen)) return;
        var selectedSha = SelectedCommit?.Ref;
        Commits = new ObservableCollection<GitCommit>(commits);
        HistoryHasMore = WindowMayHaveMore(commits.Count, _historyWindowSize);
        if (Project is not null) Project.RecentCommits = commits;
        SelectedCommit = selectedSha is null
            ? null
            : commits.FirstOrDefault(c => string.Equals(c.Ref, selectedSha, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Overridable so the interleave a confirmed op has to survive — a project switch
    /// landing while the dialog is open — is reachable without a message pump.
    /// </summary>
    internal virtual async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel"
        };
        Helpers.DialogKeyGuard.Install(dialog);
        return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
