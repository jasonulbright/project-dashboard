using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>One worktree row, plus whether it is the checkout this page is describing.</summary>
/// <param name="IsCurrent">
/// The container of the repository path on screen. It is not a fact git records — the same listing
/// comes back from every worktree of a repository — so it is computed here rather than parsed.
/// </param>
public sealed record WorktreeRow(WorktreeEntry Entry, bool IsCurrent)
{
    public string Path => Entry.Path;
    public string BranchLabel => Entry.IsBare ? "bare"
        : Entry.IsDetached ? "detached"
        : Entry.Branch ?? "";

    /// <summary>Everything git flags about the entry, in one line; empty when it flags nothing.</summary>
    public string StateLabel
    {
        get
        {
            var parts = new List<string>();
            if (Entry.IsMain) parts.Add("main worktree");
            if (IsCurrent) parts.Add("this checkout");
            if (Entry.IsLocked) parts.Add("locked");
            if (Entry.IsPrunable) parts.Add(Entry.PrunableReason.Length > 0
                ? $"prunable — {Entry.PrunableReason}" : "prunable");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The Internals tab: the worktrees this repository has, the submodules it declares, and the
/// ignore rules at its root. Three things a checkout is made of that no other surface shows.
///
/// The worktree listing is the same from every worktree of a repository, so the row for the
/// checkout on screen is marked as such and the main worktree is marked as the main one — the app
/// itself runs from a linked worktree in development, and a listing that called that worktree the
/// repository would be describing something else.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Null when the host supplied none; the submodule surface then refuses instead of reporting none.</summary>
    private readonly SubmoduleService? _submoduleService;

    /// <summary>Real "loaded" flag — every list here is empty in the common case, so none can stand in.</summary>
    [ObservableProperty] private bool _internalsLoaded;

    /// <summary>The read the tab started and did not await, so a caller can wait for the lists rather than poll.</summary>
    internal Task InternalsRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private async Task LoadInternals()
    {
        InternalsRefresh = Task.WhenAll(LoadWorktrees(), LoadSubmodules(), LoadGitignore());
        await InternalsRefresh;
        InternalsLoaded = true;
    }

    private void ResetInternalsState()
    {
        InternalsLoaded = false;
        Worktrees = [];
        SelectedWorktree = null;
        NewWorktreePath = "";
        NewWorktreeBranch = "";
        WorktreesStatusText = "";
        WorktreesErrorText = "";
        Submodules = [];
        SelectedSubmodule = null;
        SubmodulesStatusText = "";
        SubmodulesErrorText = "";
        SubmoduleForce = false;
        SubmoduleConfirmDiscard = false;
        GitignoreText = "";
        GitignoreExists = false;
        GitignoreDirty = false;
        GitignoreStatusText = "";
        GitignoreErrorText = "";
        IgnoreProbePath = "";
        IgnoreProbeResult = "";
    }

    // ── Worktrees ───────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<WorktreeRow> _worktrees = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveWorktreeCommand))]
    private WorktreeRow? _selectedWorktree;

    [ObservableProperty] private string _newWorktreePath = "";
    [ObservableProperty] private string _newWorktreeBranch = "";
    [ObservableProperty] private string _worktreesStatusText = "";
    [ObservableProperty] private string _worktreesErrorText = "";

    [RelayCommand]
    private async Task LoadWorktrees()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;

        var keep = SelectedWorktree?.Path;
        List<WorktreeEntry> entries;
        try
        {
            entries = await _gitService.GetWorktreesAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the worktrees of {repo}", ex);
            if (IsCurrent(gen)) WorktreesErrorText = $"Could not read this repository's worktrees: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        WorktreesErrorText = "";
        Worktrees = new ObservableCollection<WorktreeRow>(
            entries.Select(e => new WorktreeRow(e, SamePath(e.Path, repo))));
        SelectedWorktree = Worktrees.FirstOrDefault(w => w.Path == keep)
            ?? Worktrees.FirstOrDefault(w => w.IsCurrent)
            ?? Worktrees.FirstOrDefault();
    }

    /// <summary>
    /// Whether two paths name the same directory. Git reports worktree paths with forward
    /// slashes on Windows while the project path carries backslashes, so a byte comparison
    /// would call every row a different directory.
    /// </summary>
    internal static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try
        {
            return string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task AddWorktree()
    {
        var path = NewWorktreePath.Trim();
        var branch = NewWorktreeBranch.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0 || IsBusy) return;

        if (path.Length == 0)
        {
            WorktreesErrorText = "Choose a directory for the new worktree. Git creates it and refuses a path that already exists.";
            return;
        }
        if (branch.Length > 0 && !await _gitService.IsValidBranchNameAsync(repo, branch))
        {
            if (IsCurrent(gen)) WorktreesErrorText = InvalidBranchNameMessage(branch);
            return;
        }
        if (!IsCurrent(gen)) return;
        if (branch.Length > 0 && Branches.Any(b => string.Equals(b.Name, branch, StringComparison.Ordinal)))
        {
            WorktreesErrorText = $"A branch called “{branch}” already exists here, and a worktree creates its branch. " +
                                 "Choose another name.";
            return;
        }

        WorktreesErrorText = "";
        var ok = await RunOp(r => _gitService.AddWorktreeAsync(r, path, branch.Length > 0 ? branch : null),
            "Add worktree", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            WorktreesErrorText = SyncStatusText;
            return;
        }
        WorktreesStatusText = branch.Length > 0
            ? $"Added a worktree at {path} on a new branch {branch}."
            : $"Added a worktree at {path}.";
        NewWorktreePath = "";
        NewWorktreeBranch = "";
        await LoadWorktrees();
        await LoadBranches();
    }

    /// <summary>Directory chosen by the reader, or null when the picker was cancelled.</summary>
    internal virtual string? PromptForDirectory(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Picks the PARENT directory and appends the branch name, because git refuses a worktree
    /// path that already exists and a folder picker can only return one that does.
    /// </summary>
    [RelayCommand]
    private void ChooseWorktreePath()
    {
        if (PromptForDirectory("Where should the new worktree be created?") is not { } parent) return;
        var leaf = NewWorktreeBranch.Trim();
        NewWorktreePath = leaf.Length > 0
            ? System.IO.Path.Combine(parent, leaf.Replace('/', '-'))
            : parent;
    }

    private bool CanRemoveWorktree() => SelectedWorktree is not null && !IsBusy && RepoPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveWorktree))]
    private async Task RemoveWorktree()
    {
        var row = SelectedWorktree;
        var repo = RepoPath;
        var gen = _generation;
        if (row is null || repo.Length == 0 || IsBusy) return;

        if (row.Entry.IsMain)
        {
            WorktreesErrorText = MainWorktreeRefusal;
            return;
        }

        var confirmed = await ConfirmPrompt("Remove this worktree?",
            $"Remove the worktree at {row.Path}?\n\n" +
            "The directory and everything uncommitted in it goes. The branch it had checked out stays in this " +
            "repository, and every commit already made on it stays with it.", "Remove worktree");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            WorktreesStatusText = ProjectSwitchedNotice("Worktree removal");
            return;
        }

        WorktreesErrorText = "";
        var ok = await RunOp(r => _gitService.RemoveWorktreeAsync(r, row.Path), "Remove worktree", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            WorktreesErrorText = SyncStatusText;
            return;
        }
        WorktreesStatusText = $"Removed the worktree at {row.Path}. Its branch is still here.";
        await LoadWorktrees();
    }

    /// <summary>
    /// The main worktree holds the repository itself; git refuses to remove it, and so does this
    /// before a confirmation is spent on it.
    /// </summary>
    internal const string MainWorktreeRefusal =
        "That is the main worktree — the repository lives there, and it cannot be removed this way. " +
        "Only the linked worktrees can go.";

    [RelayCommand]
    private async Task PruneWorktrees()
    {
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0 || IsBusy) return;

        var prunable = Worktrees.Count(w => w.Entry.IsPrunable);
        var confirmed = await ConfirmPrompt("Clear the stale worktree entries?",
            prunable == 0
                ? "Git reports no stale worktree entries here, so this will most likely clear nothing. Run it anyway?"
                : $"Clear {prunable} worktree {(prunable == 1 ? "entry" : "entries")} whose working tree is gone?\n\n" +
                  "Only the administrative record goes. A worktree still on disk is untouched, and no branch or " +
                  "commit is affected either way.",
            "Prune");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            WorktreesStatusText = ProjectSwitchedNotice("Worktree prune");
            return;
        }

        WorktreesErrorText = "";
        var ok = await RunOp(r => _gitService.PruneWorktreesAsync(r), "Prune worktrees", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            WorktreesErrorText = SyncStatusText;
            return;
        }
        var before = Worktrees.Count;
        await LoadWorktrees();
        if (!IsCurrent(gen)) return;
        var cleared = before - Worktrees.Count;
        WorktreesStatusText = cleared > 0
            ? $"Cleared {cleared} stale worktree {(cleared == 1 ? "entry" : "entries")}."
            : "Nothing was stale — every worktree entry still has its working tree.";
    }

    // ── Submodules ──────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<SubmoduleEntry> _submodules = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitSubmoduleCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSubmoduleCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncSubmoduleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeinitSubmoduleCommand))]
    private SubmoduleEntry? _selectedSubmodule;

    [ObservableProperty] private string _submodulesStatusText = "";
    [ObservableProperty] private string _submodulesErrorText = "";

    /// <summary>
    /// Adds --force to update and deinit. The service refuses it on update without the separate
    /// acknowledgement, so the two are separate switches here rather than one.
    /// </summary>
    [ObservableProperty] private bool _submoduleForce;

    /// <summary>Acknowledges that a forced update or a deinit discards work inside the submodule checkout.</summary>
    [ObservableProperty] private bool _submoduleConfirmDiscard;

    /// <summary>Shown instead of a silent return when the host wired no submodule service.</summary>
    internal const string SubmodulesUnavailableNotice =
        "Submodules are unavailable — the submodule service was not configured for this session.";

    [RelayCommand]
    private async Task LoadSubmodules()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        if (_submoduleService is null)
        {
            SubmodulesErrorText = SubmodulesUnavailableNotice;
            return;
        }
        var gen = _generation;

        var keep = SelectedSubmodule?.Path;
        List<SubmoduleEntry> entries;
        try
        {
            entries = await _submoduleService.GetSubmodulesAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the submodules of {repo}", ex);
            if (IsCurrent(gen)) SubmodulesErrorText = $"Could not read this repository's submodules: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        SubmodulesErrorText = "";
        Submodules = new ObservableCollection<SubmoduleEntry>(entries);
        SelectedSubmodule = Submodules.FirstOrDefault(s => s.Path == keep) ?? Submodules.FirstOrDefault();
    }

    private bool CanActOnSubmodule() =>
        SelectedSubmodule is not null && !IsBusy && RepoPath.Length > 0 && _submoduleService is not null;

    [RelayCommand(CanExecute = nameof(CanActOnSubmodule))]
    private async Task InitSubmodule()
    {
        var entry = SelectedSubmodule;
        var service = _submoduleService;
        var repo = RepoPath;
        var gen = _generation;
        if (entry is null || service is null || repo.Length == 0 || IsBusy) return;

        SubmodulesErrorText = "";
        var ok = await RunOp(r => service.InitAsync(r, entry.Path), $"Init {entry.Path}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            SubmodulesErrorText = SyncStatusText;
            return;
        }
        SubmodulesStatusText = $"Registered {entry.Path} in this repository's config. Nothing was cloned — " +
                               "that is what Update does.";
        await LoadSubmodules();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSubmodule))]
    private async Task UpdateSubmodule()
    {
        var entry = SelectedSubmodule;
        var service = _submoduleService;
        var repo = RepoPath;
        var gen = _generation;
        var force = SubmoduleForce;
        var acknowledged = SubmoduleConfirmDiscard;
        if (entry is null || service is null || repo.Length == 0 || IsBusy) return;

        // The service refuses --force without the acknowledgement; saying so here means the
        // refusal is not discovered as an opaque failure after a clone has already started.
        if (force && !acknowledged)
        {
            SubmodulesErrorText = ForceNeedsAcknowledgement;
            return;
        }
        if (force)
        {
            var confirmed = await ConfirmPrompt("Force this submodule back to the recorded commit?",
                $"Reset the checkout in {entry.Path} to the commit this repository records?\n\n" +
                "Commits made inside the submodule and not pushed anywhere, and any modification to its files, " +
                "are discarded. The superproject itself is not touched.", "Force update");
            if (!confirmed) return;
            if (!IsCurrent(gen))
            {
                SubmodulesStatusText = ProjectSwitchedNotice("Submodule update");
                return;
            }
        }

        SubmodulesErrorText = "";
        var request = new SubmoduleUpdateRequest
        {
            Path = entry.Path,
            Init = true,
            Force = force,
            ConfirmDiscard = acknowledged
        };
        var ok = await RunOp(r => service.UpdateAsync(r, request), $"Update {entry.Path}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            SubmodulesErrorText = SyncStatusText;
            return;
        }
        SubmodulesStatusText = $"{entry.Path} is at the commit this repository records.";
        await LoadSubmodules();
    }

    internal const string ForceNeedsAcknowledgement =
        "A forced update discards commits and changes inside the submodule checkout. Tick the acknowledgement " +
        "beside the switch before it will run.";

    [RelayCommand(CanExecute = nameof(CanActOnSubmodule))]
    private async Task SyncSubmodule()
    {
        var entry = SelectedSubmodule;
        var service = _submoduleService;
        var repo = RepoPath;
        var gen = _generation;
        if (entry is null || service is null || repo.Length == 0 || IsBusy) return;

        SubmodulesErrorText = "";
        var ok = await RunOp(r => service.SyncAsync(r, entry.Path), $"Sync {entry.Path}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            SubmodulesErrorText = SyncStatusText;
            return;
        }
        SubmodulesStatusText = $"{entry.Path} now uses the URL .gitmodules declares. Nothing was fetched.";
        await LoadSubmodules();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSubmodule))]
    private async Task DeinitSubmodule()
    {
        var entry = SelectedSubmodule;
        var service = _submoduleService;
        var repo = RepoPath;
        var gen = _generation;
        var force = SubmoduleForce;
        if (entry is null || service is null || repo.Length == 0 || IsBusy) return;

        var confirmed = await ConfirmPrompt("Empty this submodule's working tree?",
            $"Deinitialize {entry.Path}?\n\n" +
            "Its working tree is emptied and it is unregistered from this repository's config. The gitlink the " +
            "superproject records stays, so Update brings it back — but anything only in that checkout does not " +
            "come back with it.\n\n" +
            (force
                ? "Force is on, so git will proceed even though the checkout has local modifications."
                : "Without Force, git refuses while the checkout has local modifications."),
            "Deinitialize");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            SubmodulesStatusText = ProjectSwitchedNotice("Submodule deinit");
            return;
        }

        SubmodulesErrorText = "";
        // ConfirmDiscard carries the confirmation just given; the service refuses deinit without it.
        var request = new SubmoduleDeinitRequest { Path = entry.Path, Force = force, ConfirmDiscard = true };
        var ok = await RunOp(r => service.DeinitAsync(r, request), $"Deinit {entry.Path}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            SubmodulesErrorText = SyncStatusText;
            return;
        }
        SubmodulesStatusText = $"{entry.Path} is deinitialized. The superproject still records its commit.";
        await LoadSubmodules();
    }

    // ── Ignore rules ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _gitignoreText = "";

    /// <summary>Whether the repository has a root .gitignore at all; an absent file is not an empty one.</summary>
    [ObservableProperty] private bool _gitignoreExists;

    [ObservableProperty] private bool _gitignoreDirty;

    [ObservableProperty] private string _gitignoreStatusText = "";
    [ObservableProperty] private string _gitignoreErrorText = "";

    [ObservableProperty] private string _ignoreProbePath = "";
    [ObservableProperty] private string _ignoreProbeResult = "";

    /// <summary>Suppresses the dirty flag while the editor is being filled from disk.</summary>
    private bool _loadingGitignore;

    partial void OnGitignoreTextChanged(string value)
    {
        if (!_loadingGitignore) GitignoreDirty = true;
    }

    [RelayCommand]
    private async Task LoadGitignore()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;

        string? content;
        try
        {
            content = await _gitService.GetGitignoreAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the .gitignore of {repo}", ex);
            if (IsCurrent(gen)) GitignoreErrorText = $"Could not read .gitignore: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        GitignoreErrorText = "";
        _loadingGitignore = true;
        GitignoreText = content ?? "";
        _loadingGitignore = false;
        GitignoreExists = content is not null;
        GitignoreDirty = false;
        GitignoreStatusText = "";
    }

    /// <summary>
    /// Writes the editor's text to the repository's root .gitignore. A plain file write: the rules
    /// take effect for what git ignores from here on, and the file itself is left unstaged, so the
    /// change shows up on the Changes tab like any other edit.
    /// </summary>
    [RelayCommand]
    private async Task SaveGitignore()
    {
        var repo = RepoPath;
        var gen = _generation;
        var content = GitignoreText;
        if (repo.Length == 0) return;

        try
        {
            await _gitService.SaveGitignoreAsync(repo, content);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write the .gitignore of {repo}", ex);
            if (IsCurrent(gen)) GitignoreErrorText = $"Could not save .gitignore: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        GitignoreErrorText = "";
        GitignoreExists = true;
        GitignoreDirty = false;
        GitignoreStatusText = "Saved .gitignore. It is an ordinary edit — commit it like any other file. " +
                              "A file already tracked stays tracked no matter what the rules say.";
        await SafeRefreshWorkingStateAsync();
    }

    [RelayCommand]
    private async Task RevertGitignore()
    {
        await LoadGitignore();
        if (GitignoreErrorText.Length == 0) GitignoreStatusText = "Reloaded .gitignore from disk.";
    }

    /// <summary>
    /// Asks git whether a path is ignored, which is the only answer that accounts for every rule
    /// file involved — the repository's, any nested one, the global excludes, and .git/info/exclude
    /// — and for the negations among them.
    /// </summary>
    [RelayCommand]
    private async Task ProbeIgnorePath()
    {
        var repo = RepoPath;
        var path = IgnoreProbePath.Trim();
        var gen = _generation;
        if (repo.Length == 0) return;
        if (path.Length == 0)
        {
            IgnoreProbeResult = "Type a repository-relative path to test.";
            return;
        }
        if (GitignoreDirty)
        {
            IgnoreProbeResult = "Save the ignore rules first — git reads the file on disk, not the editor.";
            return;
        }

        bool ignored;
        try
        {
            ignored = await _gitService.CheckIgnoreAsync(repo, path);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not test {path} against the ignore rules of {repo}", ex);
            if (IsCurrent(gen)) IgnoreProbeResult = $"Could not test that path: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        IgnoreProbeResult = ignored
            ? $"{path} is ignored."
            : $"{path} is not ignored — no rule matches it, or a later rule un-ignores it.";
    }
}
