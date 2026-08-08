using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The Branches tab's second half: the remotes this repository is configured with, and the
/// operations on a local branch that are about its relationship to one — rename, upstream,
/// comparison, and deleting the branch on the remote.
///
/// Everything but the last is local and reversible. Deleting a branch on a remote is the only
/// outward-facing action on the page that is not a push of this repository's own work: it removes
/// a ref other people fetch, so it takes a typed confirmation naming the exact ref rather than a
/// yes/no dialog.
/// </summary>
public partial class ProjectDetailViewModel
{
    // ── Remotes ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<RemoteEntry> _remotes = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetRemoteUrlCommand))]
    private RemoteEntry? _selectedRemote;

    /// <summary>Real "loaded" flag — a repository with no remotes is the common case, so an empty list cannot stand in.</summary>
    [ObservableProperty] private bool _branchesTabLoaded;

    [ObservableProperty] private string _newRemoteName = "";
    [ObservableProperty] private string _newRemoteUrl = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameRemoteCommand))]
    private string _remoteRenameTo = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetRemoteUrlCommand))]
    private string _remoteUrlEdit = "";

    [ObservableProperty] private string _remotesStatusText = "";
    [ObservableProperty] private string _remotesErrorText = "";

    /// <summary>Remote-tracking refs, which are what an upstream can be set to and what can be deleted on a remote.</summary>
    [ObservableProperty] private ObservableCollection<string> _remoteBranches = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRemoteBranchCommand))]
    private string? _selectedRemoteBranch;

    partial void OnSelectedRemoteChanged(RemoteEntry? value)
    {
        // The edit boxes describe the selected remote; carrying the previous one's text over
        // would arm a rename or a URL change against a remote the reader never typed for.
        RemoteRenameTo = value?.Name ?? "";
        RemoteUrlEdit = value?.FetchUrl ?? "";
    }

    /// <summary>The read the tab started and did not await, so a caller can wait for the lists rather than poll.</summary>
    internal Task RemotesRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Everything the Branches tab shows. One command rather than two so the branch list and the
    /// remote lists it is read against are never a refresh apart.
    /// </summary>
    [RelayCommand]
    private async Task LoadBranchesTab()
    {
        RemotesRefresh = LoadRemotes();
        await LoadBranches();
        await RemotesRefresh;
        BranchesTabLoaded = true;
    }

    [RelayCommand]
    private async Task LoadRemotes()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;

        var keepRemote = SelectedRemote?.Name;
        var keepBranch = SelectedRemoteBranch;
        List<RemoteEntry> remotes;
        List<string> remoteBranches;
        try
        {
            remotes = await _gitService.GetRemotesAsync(repo);
            remoteBranches = await _gitService.GetRemoteBranchesAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the remotes of {repo}", ex);
            if (IsCurrent(gen)) RemotesErrorText = $"Could not read this repository's remotes: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        RemotesErrorText = "";
        Remotes = new ObservableCollection<RemoteEntry>(remotes);
        RemoteBranches = new ObservableCollection<string>(remoteBranches);
        SelectedRemote = Remotes.FirstOrDefault(r => r.Name == keepRemote) ?? Remotes.FirstOrDefault();
        SelectedRemoteBranch = keepBranch is not null && RemoteBranches.Contains(keepBranch) ? keepBranch : null;
        RefreshBranchExtraChoices();
    }

    [RelayCommand]
    private async Task AddRemote()
    {
        var name = NewRemoteName.Trim();
        var url = NewRemoteUrl.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0 || IsBusy) return;

        if (!GitService.IsPlausibleRemoteUrl(url))
        {
            RemotesErrorText = "A remote URL cannot be empty, start with a dash, or contain spaces or control characters.";
            return;
        }
        if (!await _gitService.IsValidRemoteNameAsync(repo, name))
        {
            if (IsCurrent(gen)) RemotesErrorText = InvalidRemoteNameMessage(name);
            return;
        }
        if (!IsCurrent(gen)) return;
        if (Remotes.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
        {
            RemotesErrorText = $"A remote called “{name}” is already configured here. Change its URL instead.";
            return;
        }

        RemotesErrorText = "";
        var ok = await RunOp(r => _gitService.AddRemoteAsync(r, name, url), $"Add remote {name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            return;
        }
        RemotesStatusText = $"Added {name}. Nothing was fetched — the remote is configured, not contacted.";
        NewRemoteName = "";
        NewRemoteUrl = "";
        await LoadRemotes();
    }

    internal static string InvalidRemoteNameMessage(string name) =>
        $"“{name}” is not a valid remote name. Remote names cannot be empty, contain a slash, a space, “..”, " +
        "“~”, “^”, “:”, “?”, “*”, “[”, or start with a dash.";

    private bool CanRemoveRemote() => SelectedRemote is not null && !IsBusy && RepoPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveRemote))]
    private async Task RemoveRemote()
    {
        var remote = SelectedRemote;
        var repo = RepoPath;
        var gen = _generation;
        if (remote is null || repo.Length == 0 || IsBusy) return;

        var confirmed = await ConfirmPrompt("Remove this remote?",
            $"Remove {remote.Name} ({remote.FetchUrl}) from this repository?\n\n" +
            "The remote-tracking branches under it go with it, and any local branch that tracked one is left " +
            "with no upstream. Nothing on the remote itself changes.", "Remove remote");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            RemotesStatusText = ProjectSwitchedNotice("Remote removal");
            return;
        }

        RemotesErrorText = "";
        var ok = await RunOp(r => _gitService.RemoveRemoteAsync(r, remote.Name), $"Remove remote {remote.Name}",
            repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            return;
        }
        RemotesStatusText = $"Removed {remote.Name} here. The repository it pointed at is untouched.";
        await LoadRemotes();
        await LoadBranches();
    }

    private bool CanRenameRemote() =>
        SelectedRemote is not null && RemoteRenameTo.Trim().Length > 0 && !IsBusy && RepoPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRenameRemote))]
    private async Task RenameRemote()
    {
        var remote = SelectedRemote;
        var name = RemoteRenameTo.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (remote is null || name.Length == 0 || repo.Length == 0 || IsBusy) return;
        if (string.Equals(name, remote.Name, StringComparison.Ordinal)) return;

        if (!await _gitService.IsValidRemoteNameAsync(repo, name))
        {
            if (IsCurrent(gen)) RemotesErrorText = InvalidRemoteNameMessage(name);
            return;
        }
        if (!IsCurrent(gen)) return;
        if (Remotes.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
        {
            RemotesErrorText = $"A remote called “{name}” is already configured here. Choose another name.";
            return;
        }

        RemotesErrorText = "";
        var ok = await RunOp(r => _gitService.RenameRemoteAsync(r, remote.Name, name),
            $"Rename remote {remote.Name} to {name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            return;
        }
        RemotesStatusText = $"Renamed {remote.Name} to {name}. Its remote-tracking branches moved with it.";
        await LoadRemotes();
        await LoadBranches();
    }

    private bool CanSetRemoteUrl() =>
        SelectedRemote is not null && RemoteUrlEdit.Trim().Length > 0 && !IsBusy && RepoPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSetRemoteUrl))]
    private async Task SetRemoteUrl()
    {
        var remote = SelectedRemote;
        var url = RemoteUrlEdit.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (remote is null || repo.Length == 0 || IsBusy) return;
        if (string.Equals(url, remote.FetchUrl, StringComparison.Ordinal)) return;

        if (!GitService.IsPlausibleRemoteUrl(url))
        {
            RemotesErrorText = "A remote URL cannot be empty, start with a dash, or contain spaces or control characters.";
            return;
        }

        RemotesErrorText = "";
        var ok = await RunOp(r => _gitService.SetRemoteUrlAsync(r, remote.Name, url),
            $"Change {remote.Name} URL", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            return;
        }
        // set-url writes the fetch URL; a separately configured push URL is left as it was.
        RemotesStatusText = $"{remote.Name} now fetches from {url}. Any separate push URL was left alone.";
        await LoadRemotes();
    }

    // ── Deleting a branch on a remote ───────────────────────────────────────────

    private bool CanDeleteRemoteBranch() => SelectedRemoteBranch is not null && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Deletes a branch on the remote. The only action on this page that removes a ref other
    /// people fetch, so the exact ref has to be typed: a yes/no dialog on a list row is one
    /// mis-click away from deleting the branch next to the one meant.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRemoteBranch))]
    private async Task DeleteRemoteBranch()
    {
        var trackingRef = SelectedRemoteBranch;
        var repo = RepoPath;
        var gen = _generation;
        if (trackingRef is null || repo.Length == 0 || IsBusy) return;

        var (remote, branch) = SplitTrackingRef(trackingRef);
        if (branch.Length == 0)
        {
            RemotesErrorText = $"“{trackingRef}” does not name a remote and a branch, so there is nothing to delete.";
            return;
        }

        var typed = await PromptForTextAsync("Delete this branch on the remote?",
            RemoteBranchDeleteMessage(trackingRef, remote, branch), "Delete on remote");
        if (!TrackingRefConfirmed(typed, trackingRef))
        {
            if (typed is not null)
                RemotesStatusText = $"Nothing was deleted — that isn't {trackingRef}.";
            return;
        }
        if (!IsCurrent(gen))
        {
            RemotesStatusText = ProjectSwitchedNotice("Remote branch delete");
            return;
        }

        RemotesErrorText = "";
        var ok = await RunOp(r => _gitService.DeleteRemoteBranchAsync(r, remote, branch),
            $"Delete {branch} on {remote}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            return;
        }
        RemotesStatusText = $"Deleted {branch} on {remote}. Any local branch of that name is still here.";
        await LoadRemotes();
        await LoadBranches();
    }

    /// <summary>The remote name and the branch name inside a remote-tracking ref, split at the first slash.</summary>
    internal static (string Remote, string Branch) SplitTrackingRef(string trackingRef)
    {
        var slash = trackingRef.IndexOf('/');
        return slash <= 0 ? (trackingRef, "") : (trackingRef[..slash], trackingRef[(slash + 1)..]);
    }

    /// <summary>
    /// Whether the typed text is the exact remote-tracking ref. Ordinal and untrimmed of case:
    /// refs are byte-exact, and two branches differing only in case are two branches.
    /// </summary>
    internal static bool TrackingRefConfirmed(string? typed, string trackingRef) =>
        typed is not null && trackingRef.Length > 0 && string.Equals(typed.Trim(), trackingRef, StringComparison.Ordinal);

    internal static string RemoteBranchDeleteMessage(string trackingRef, string remote, string branch) =>
        $"Delete {branch} on {remote}?\n\n" +
        "This one is outward-facing: it removes the branch from the remote repository, for everyone who fetches " +
        "it. Whatever was only on that branch is reachable afterwards only from a clone that already has it.\n\n" +
        $"The local branch of the same name, if there is one, stays here.\n\nType {trackingRef} to confirm.";

    // ── Branch extras ───────────────────────────────────────────────────────────

    [ObservableProperty] private string _branchRenameTo = "";

    /// <summary>Remote-tracking refs a branch's upstream can be set to.</summary>
    [ObservableProperty] private ObservableCollection<string> _upstreamChoices = [];

    [ObservableProperty] private string? _selectedUpstreamChoice;

    /// <summary>Every other ref the selected branch can be measured against: local branches and remote-tracking refs.</summary>
    [ObservableProperty] private ObservableCollection<string> _compareBaseChoices = [];

    [ObservableProperty] private string? _selectedCompareBase;

    [ObservableProperty] private string _branchCompareText = "";

    [ObservableProperty] private string _branchExtrasStatusText = "";
    [ObservableProperty] private string _branchExtrasErrorText = "";

    partial void OnSelectedBranchChanged(BranchInfo? value)
    {
        BranchRenameTo = value?.Name ?? "";
        // A count measured against the previous branch would read as this one's.
        BranchCompareText = "";
        RefreshBranchExtraChoices();
    }

    /// <summary>
    /// Rebuilds the pickers from the lists already read. The compare list excludes the selected
    /// branch itself, which would always measure zero, and the upstream list is remote-tracking
    /// refs only, which is what git accepts.
    /// </summary>
    private void RefreshBranchExtraChoices()
    {
        var keepUpstream = SelectedUpstreamChoice;
        var keepBase = SelectedCompareBase;
        var selected = SelectedBranch?.Name;

        UpstreamChoices = new ObservableCollection<string>(RemoteBranches);
        CompareBaseChoices = new ObservableCollection<string>(
            Branches.Select(b => b.Name).Where(n => !string.Equals(n, selected, StringComparison.Ordinal))
                    .Concat(RemoteBranches));

        SelectedUpstreamChoice =
            keepUpstream is not null && UpstreamChoices.Contains(keepUpstream) ? keepUpstream
            : SelectedBranch is { Upstream.Length: > 0 } b && UpstreamChoices.Contains(b.Upstream) ? b.Upstream
            : UpstreamChoices.FirstOrDefault();
        SelectedCompareBase = keepBase is not null && CompareBaseChoices.Contains(keepBase)
            ? keepBase
            : CompareBaseChoices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RenameSelectedBranch()
    {
        var branch = SelectedBranch;
        var name = BranchRenameTo.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (branch is null || name.Length == 0 || repo.Length == 0 || IsBusy) return;
        if (string.Equals(name, branch.Name, StringComparison.Ordinal)) return;

        if (!await _gitService.IsValidBranchNameAsync(repo, name))
        {
            if (IsCurrent(gen)) BranchExtrasErrorText = InvalidBranchNameMessage(name);
            return;
        }
        if (!IsCurrent(gen)) return;
        if (Branches.Any(b => string.Equals(b.Name, name, StringComparison.Ordinal)))
        {
            BranchExtrasErrorText = $"A branch called “{name}” already exists here. Choose another name.";
            return;
        }

        BranchExtrasErrorText = "";
        var ok = await RunOp(r => _gitService.RenameBranchAsync(r, branch.Name, name),
            $"Rename {branch.Name} to {name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            BranchExtrasErrorText = SyncStatusText;
            return;
        }
        BranchExtrasStatusText = $"Renamed {branch.Name} to {name} here. The branch on any remote keeps its old name.";
        await LoadBranches();
    }

    [RelayCommand]
    private async Task SetBranchUpstream()
    {
        var branch = SelectedBranch;
        var upstream = SelectedUpstreamChoice;
        var repo = RepoPath;
        var gen = _generation;
        if (branch is null || upstream is null || repo.Length == 0 || IsBusy) return;

        BranchExtrasErrorText = "";
        var ok = await RunOp(r => _gitService.SetUpstreamAsync(r, branch.Name, upstream),
            $"Track {upstream} from {branch.Name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            BranchExtrasErrorText = SyncStatusText;
            return;
        }
        BranchExtrasStatusText = $"{branch.Name} now tracks {upstream}. Nothing was fetched or pushed.";
        await LoadBranches();
        RefreshBranchExtraChoices();
    }

    [RelayCommand]
    private async Task UnsetBranchUpstream()
    {
        var branch = SelectedBranch;
        var repo = RepoPath;
        var gen = _generation;
        if (branch is null || repo.Length == 0 || IsBusy) return;
        if (branch.Upstream.Length == 0)
        {
            BranchExtrasStatusText = $"{branch.Name} has no upstream to clear.";
            return;
        }

        BranchExtrasErrorText = "";
        var ok = await RunOp(r => _gitService.UnsetUpstreamAsync(r, branch.Name),
            $"Clear the upstream of {branch.Name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            BranchExtrasErrorText = SyncStatusText;
            return;
        }
        BranchExtrasStatusText =
            $"{branch.Name} no longer tracks anything. The remote-tracking ref itself is still here.";
        await LoadBranches();
        RefreshBranchExtraChoices();
    }

    /// <summary>
    /// Counts the commits between the selected branch and another ref. A read, not a mutation, so
    /// it runs outside the busy gate — nothing it does can collide with an operation in flight.
    /// </summary>
    [RelayCommand]
    private async Task CompareSelectedBranch()
    {
        var branch = SelectedBranch;
        var baseRef = SelectedCompareBase;
        var repo = RepoPath;
        var gen = _generation;
        if (branch is null || baseRef is null || repo.Length == 0) return;

        BranchExtrasErrorText = "";
        var comparison = await _gitService.CompareRefsAsync(repo, branch.Name, baseRef);
        if (!IsCurrent(gen)) return;

        BranchCompareText = DescribeComparison(branch.Name, baseRef, comparison);
    }

    /// <summary>
    /// The comparison in words. A pair git could not measure — an unknown ref, or two histories
    /// with no common commit — is said to be unmeasured rather than shown as zero and zero.
    /// </summary>
    internal static string DescribeComparison(string reference, string baseRef, RefComparison? comparison) =>
        comparison is null
            ? $"{reference} and {baseRef} could not be compared — one of them is unknown here."
            : comparison switch
            {
                (0, 0) => $"{reference} and {baseRef} are at the same commit.",
                (var a, 0) => $"{reference} is {a} commit{Plural(a)} ahead of {baseRef}.",
                (0, var b) => $"{reference} is {b} commit{Plural(b)} behind {baseRef}.",
                var (a, b) => $"{reference} is {a} commit{Plural(a)} ahead of and {b} commit{Plural(b)} behind {baseRef}.",
            };

    private static string Plural(int count) => count == 1 ? "" : "s";
}
