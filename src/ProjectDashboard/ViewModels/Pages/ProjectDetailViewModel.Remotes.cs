using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The Branches tab's second half: the remotes this repository is configured with, and the
/// operations that are about a branch's relationship to one — rename, upstream, comparison,
/// checking a remote branch out here, pruning refs the remote no longer has, and deleting the
/// branch on the remote.
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
    [NotifyCanExecuteChangedFor(nameof(PruneRemoteCommand))]
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

    /// <summary>
    /// Set only by a read that succeeded. An empty list is also what a failed read leaves behind,
    /// so the "no remotes" claim is made from this rather than from the count.
    /// </summary>
    [ObservableProperty] private bool _remotesEmpty;

    /// <summary>Remote-tracking refs, which are what an upstream can be set to and what can be deleted on a remote.</summary>
    [ObservableProperty] private ObservableCollection<string> _remoteBranches = [];

    /// <inheritdoc cref="RemotesEmpty"/>
    [ObservableProperty] private bool _remoteBranchesEmpty;

    private const string RemoteBranchesUnreadablePrefix = "Could not read this repository's remote branches: ";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRemoteBranchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckoutRemoteBranchCommand))]
    private string? _selectedRemoteBranch;

    /// <summary>Name the local branch would take; proposed from the selected ref and editable before the checkout.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckoutRemoteBranchCommand))]
    private string _remoteBranchLocalName = "";

    // A name proposed for the previously selected ref would create a branch tracking this one
    // under the other one's name.
    partial void OnSelectedRemoteBranchChanged(string? value) =>
        RemoteBranchLocalName = value is null ? "" : SplitTrackingRef(value).Branch;

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
        var gen = _generation;
        RemotesRefresh = LoadRemotes();
        await LoadBranches();
        await RemotesRefresh;
        if (IsCurrent(gen)) BranchesTabLoaded = true;
    }

    [RelayCommand]
    private async Task LoadRemotes()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;

        var keepRemote = SelectedRemote?.Name;
        var keepBranch = SelectedRemoteBranch;
        RemotesResult remotes;
        RemoteBranchesResult remoteBranches;
        try
        {
            remotes = await _gitService.GetRemotesAsync(repo);
            remoteBranches = await _gitService.GetRemoteBranchesAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the remotes of {repo}", ex);
            if (IsCurrent(gen))
            {
                RemotesErrorText = $"Could not read this repository's remotes: {ex.Message}";
                RemotesEmpty = false;
                RemoteBranchesEmpty = false;
            }
            return;
        }
        if (!IsCurrent(gen)) return;

        // A read that failed and a repository with nothing configured produce the same empty
        // list; showing the empty state for the first states a fact the read never established.
        if (remotes.HasError)
        {
            RemotesErrorText = $"Could not read this repository's remotes: {remotes.ErrorText}";
            RemotesEmpty = false;
            RemoteBranchesEmpty = false;
            return;
        }

        RemotesErrorText = "";
        Remotes = new ObservableCollection<RemoteEntry>(remotes.Remotes);
        RemotesEmpty = Remotes.Count == 0;
        RemoteBranches = new ObservableCollection<string>(remoteBranches.Branches);
        RemoteBranchesEmpty = !remoteBranches.HasError && RemoteBranches.Count == 0;
        SelectedRemote = Remotes.FirstOrDefault(r => r.Name == keepRemote) ?? Remotes.FirstOrDefault();
        SelectedRemoteBranch = keepBranch is not null && RemoteBranches.Contains(keepBranch) ? keepBranch : null;
        RefreshBranchExtraChoices();

        // The two reads answer different questions: a refused remote-branch read establishes
        // nothing about the configured remotes, so it is reported beside that list rather than in
        // place of it — and it belongs on the panel whose upstream and delete-on-remote pickers
        // are the lists it feeds. A read that answered clears this notice only.
        if (remoteBranches.HasError)
            BranchExtrasErrorText = RemoteBranchesUnreadablePrefix + remoteBranches.ErrorText;
        else if (BranchExtrasErrorText.StartsWith(RemoteBranchesUnreadablePrefix, StringComparison.Ordinal))
            BranchExtrasErrorText = "";
    }

    [RelayCommand]
    private async Task AddRemote()
    {
        var name = NewRemoteName.Trim();
        var url = NewRemoteUrl.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;
        if (IsBusy) { RemotesErrorText = BusyNotice("Add remote"); return; }

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

    // ── Pruning a remote ────────────────────────────────────────────────────────

    private bool CanPruneRemote() => SelectedRemote is not null && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Drops this repository's remote-tracking refs for branches the remote no longer has. The
    /// dry run that composes the confirmation is a read and takes no lease; only the prune is
    /// gated. A dry run that could not be performed refuses the offer: its empty list would
    /// otherwise be shown as "nothing is stale", which it never established.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPruneRemote))]
    private async Task PruneRemote()
    {
        var remote = SelectedRemote;
        var repo = RepoPath;
        var gen = _generation;
        if (remote is null || repo.Length == 0) return;
        if (IsBusy) { RemotesErrorText = BusyNotice("Prune"); return; }

        RemotesErrorText = "";
        RemotesStatusText = $"Reading what pruning {remote.Name} would drop…";
        PrunePreview preview;
        try
        {
            preview = await _gitService.PruneRemoteDryRunAsync(repo, remote.Name);
        }
        catch (Exception ex)
        {
            Log.Warn($"prune dry run failed for {repo}", ex);
            if (!IsCurrent(gen)) return;
            RemotesErrorText = PrunePreviewFailure(remote.Name, ex.Message);
            RemotesStatusText = "";
            return;
        }
        if (!IsCurrent(gen)) return;

        if (preview.HasError)
        {
            RemotesErrorText = PrunePreviewFailure(remote.Name, preview.ErrorText);
            RemotesStatusText = "";
            return;
        }
        if (preview.Refs.Count == 0)
        {
            RemotesStatusText =
                $"Nothing to prune — every remote-tracking branch under {remote.Name} is still on the remote.";
            return;
        }

        var confirmed = await ConfirmPrompt("Prune this remote?", PruneMessage(remote.Name, preview.Refs), "Prune");
        if (!confirmed)
        {
            RemotesStatusText = "";
            return;
        }
        if (!IsCurrent(gen))
        {
            RemotesStatusText = ProjectSwitchedNotice("Prune");
            return;
        }

        // The prune measures staleness again for itself, so the preview is what was offered and
        // not what runs. The count reported is the difference the list actually shows afterwards.
        var before = RemoteBranches.ToList();
        var ok = await RunOp(r => _gitService.PruneRemoteAsync(r, remote.Name), $"Prune {remote.Name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            RemotesStatusText = "";
            return;
        }
        await LoadRemotes();
        await LoadBranches();
        if (!IsCurrent(gen)) return;

        var dropped = before.Count(r => !RemoteBranches.Contains(r));
        RemotesStatusText =
            $"Pruned {dropped} remote-tracking branch{(dropped == 1 ? "" : "es")} under {remote.Name}. " +
            "Nothing on the remote changed, and no local branch was deleted.";
    }

    /// <summary>
    /// The confirmation for a prune, composed from the dry run so the refs at risk are named
    /// rather than counted. A long list is cut short with the remainder counted, so the dialog
    /// stays readable.
    /// </summary>
    internal static string PruneMessage(string remote, IReadOnlyList<string> refs)
    {
        const int shown = 12;
        var named = string.Join("\n", refs.Take(shown).Select(r => $"  {r}"));
        var rest = refs.Count > shown ? $"\n  …and {refs.Count - shown} more" : "";
        var one = refs.Count == 1;
        return $"Prune {remote}?\n\n" +
               $"{refs.Count} remote-tracking branch{(one ? "" : "es")} here {(one ? "is" : "are")} no longer on " +
               $"{remote}:\n\n{named}{rest}\n\n" +
               "Only this repository's copies go. Nothing on the remote changes, and no local branch is deleted — " +
               "one that tracked a pruned ref is left tracking something the remote no longer has.";
    }

    internal static string PrunePreviewFailure(string remote, string error) =>
        $"Could not read what pruning {remote} would drop, so nothing was pruned: {error}";

    // ── Checking out a branch from a remote ─────────────────────────────────────

    private bool CanCheckoutRemoteBranch() =>
        SelectedRemoteBranch is not null && RemoteBranchLocalName.Trim().Length > 0 && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Creates a local branch tracking the selected remote-tracking ref and switches to it.
    /// A name already taken here is left to git to refuse, which names the branch holding it;
    /// the sibling create-branch action pre-checks nothing either.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckoutRemoteBranch))]
    private async Task CheckoutRemoteBranch()
    {
        var trackingRef = SelectedRemoteBranch;
        var name = RemoteBranchLocalName.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (trackingRef is null || name.Length == 0 || repo.Length == 0) return;
        if (IsBusy) { RemotesErrorText = BusyNotice("Check out"); return; }

        if (!await _gitService.IsValidBranchNameAsync(repo, name))
        {
            if (IsCurrent(gen)) RemotesErrorText = InvalidBranchNameMessage(name);
            return;
        }
        if (!IsCurrent(gen)) return;

        RemotesErrorText = "";
        var ok = await RunOp(r => _gitService.CheckoutRemoteBranchAsync(r, trackingRef, name),
            $"Check out {name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            RemotesErrorText = SyncStatusText;
            RemotesStatusText = "";
            return;
        }
        RemotesStatusText =
            $"Created {name} here from {trackingRef} and switched to it. It tracks {trackingRef}; nothing was fetched.";
        await LoadBranches();
        await LoadRemotes();
        await ReloadCommitsAsync();
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
        if (branch is null || name.Length == 0 || repo.Length == 0) return;
        if (IsBusy) { BranchExtrasErrorText = BusyNotice("Branch rename"); return; }
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
        if (branch is null || upstream is null || repo.Length == 0) return;
        if (IsBusy) { BranchExtrasErrorText = BusyNotice("Upstream change"); return; }

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
        if (branch is null || repo.Length == 0) return;
        if (IsBusy) { BranchExtrasErrorText = BusyNotice("Upstream change"); return; }
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
    /// The comparison in words. A pair git could not measure — an unknown ref — is said to be
    /// unmeasured rather than shown as zero and zero. Two histories with no common commit are
    /// measured: each side's whole history is what the other lacks, and that is what is reported.
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
