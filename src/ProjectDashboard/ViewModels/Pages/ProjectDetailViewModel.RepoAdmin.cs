using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The Repo tab's administration of the repository itself: renaming it, archiving and
/// unarchiving it, and syncing a fork from its parent. None of the three sit behind the
/// danger-zone opt-in, which gates repository delete alone; each carries its own confirmation,
/// typed where the change is outward-facing or discards local commits.
///
/// Two of them write this machine as well as GitHub — the remote-URL update a rename offers,
/// and the local fast-forward or reset `gh repo sync` performs — so those hold the repository
/// lease for the whole operation rather than the page's own busy flag.
/// </summary>
public partial class ProjectDetailViewModel
{
    // ── Repo tab: state-derived facts ───────────────────────────────────────────

    /// <summary>False until the settings load; an unread repository is not a known-archived one.</summary>
    public bool RepoIsArchived => RepoSettings?.IsArchived ?? false;

    /// <summary>
    /// Whether the tab's editors are live. GitHub refuses every write to an archived repository,
    /// so an enabled editor over one offers a save that cannot land.
    /// </summary>
    public bool RepoEditsEnabled => !RepoIsArchived;

    public bool RepoIsFork => RepoSettings?.IsFork ?? false;

    public string RepoParentSlug => RepoSettings?.ParentSlug ?? "";

    /// <summary>One button, both directions: the label is the action available in the current state.</summary>
    public string RepoArchiveActionLabel => RepoIsArchived ? "Unarchive repository" : "Archive repository";

    // ── Repo tab: rename ────────────────────────────────────────────────────────

    [ObservableProperty] private string _repoRenameDraft = "";

    /// <summary>
    /// What became of the local remote URL after a rename. Held apart from the status line
    /// because it outlives the operation: it is the standing answer to which name this clone
    /// addresses the repository by.
    /// </summary>
    [ObservableProperty] private string _repoRenameNotice = "";

    // ── Repo tab: fork ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForkSyncOfferable))]
    private ForkDivergence? _forkDivergence;

    [ObservableProperty] private bool _forkDivergenceLoading;
    [ObservableProperty] private string _forkDivergenceText = "";

    /// <summary>
    /// The branch `gh repo sync` will move: the parent's default branch, which is the name gh
    /// resolves for both sides. "" until a comparison has resolved it.
    /// </summary>
    private string _forkSyncBranch = "";

    private int _forkDivergenceFetch;

    /// <summary>
    /// True only when a comparison answered. A read that failed leaves the sync unoffered rather
    /// than falling through to the fast-forward path, which would claim nothing local is at risk.
    /// </summary>
    public bool ForkSyncOfferable => ForkDivergence is not null;

    /// <summary>Resets the repo-administration fields so nothing leaks across a project switch.</summary>
    private void ResetRepoAdminState()
    {
        RepoRenameDraft = "";
        RepoRenameNotice = "";
        ForkDivergence = null;
        ForkDivergenceLoading = false;
        ForkDivergenceText = "";
        _forkSyncBranch = "";
    }

    // ── Remote mutations (overridable so the surfaces are drivable without gh) ───

    internal virtual Task<ProcessResult> RenameRepoRemoteAsync(string slug, string newName)
        => _gitHubService.RenameRepoAsync(slug, newName);

    internal virtual Task<ProcessResult> ArchiveRepoRemoteAsync(string slug)
        => _gitHubService.ArchiveRepoAsync(slug);

    internal virtual Task<ProcessResult> UnarchiveRepoRemoteAsync(string slug)
        => _gitHubService.UnarchiveRepoAsync(slug);

    /// <summary>Runs in the clone: this is the call that moves a local branch and the working tree.</summary>
    internal virtual Task<ProcessResult> SyncForkRemoteAsync(string repoPath, bool force)
        => _gitHubService.SyncForkAsync(repoPath, force);

    internal virtual Task<ForkDivergence?> FetchForkDivergenceAsync(string parentSlug, string parentOwner,
        string forkOwner, string branch)
        => _gitHubService.GetForkDivergenceAsync(parentSlug, parentOwner, forkOwner, branch);

    // ── Rename ──────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RenameRepo()
    {
        if (IsBusy) return;
        var slug = Slug;
        var loaded = RepoSettings;
        var repo = RepoPath;
        if (!HasGitHubRemote(slug)) return;
        if (!HasRepoSettings(loaded)) return;
        if (!RepoWritable(loaded, "Rename")) return;

        var newName = RepoRenameDraft.Trim();
        if (newName.Length == 0)
        {
            GitHubStatusText = "Enter the new repository name.";
            return;
        }
        // gh refuses a name carrying one, and the refusal is worth making here: a reader who
        // typed owner/name is asking for a transfer, which is a different operation entirely.
        if (newName.Contains('/', StringComparison.Ordinal))
        {
            GitHubStatusText =
                "A repository name cannot contain '/'. Renaming never changes the owner — transferring " +
                "a repository to another account is done on github.com.";
            return;
        }
        if (string.Equals(newName, loaded.Name, StringComparison.Ordinal))
        {
            GitHubStatusText = $"{slug} is already named {newName}.";
            return;
        }

        var gen = _generation;
        RepoRenameNotice = "";
        var typed = await PromptForTextAsync("Rename this repository?",
            RepoRenameMessage(slug, newName), "Rename repository");
        if (!RepoNameConfirmed(typed, slug))
        {
            if (typed is not null) GitHubStatusText = $"Repository not renamed — that isn't {slug}.";
            return;
        }
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Repository rename");
            return;
        }
        if (IsBusy)
        {
            GitHubStatusText = BusyGateNotice("Repository rename");
            return;
        }

        var label = $"Rename {slug} to {newName}";
        // Captured with the path, for the same reason: the offer awaits a dialog, and a project
        // applied while it is open moves what Project names out from under the continuation.
        var owner = Project;
        // A project with no clone has no local state to serialize against, and the lease is keyed
        // on a path it does not have; the rename is then a remote-only call on the light gate.
        var result = repo.Length == 0
            ? await RunGitHubOpResult(() => RenameRepoRemoteAsync(slug, newName), label)
            : await RunGitHubRepoOpResult(async () =>
            {
                var renamed = await RenameRepoRemoteAsync(slug, newName);
                // Offered under the same lease as the rename: no local operation may move this
                // clone's origin between the name changing on GitHub and the URL here matching it.
                if (renamed.Success) await OfferLocalRemoteUpdateAsync(owner, repo, slug, newName, gen);
                return renamed;
            }, label, repo);

        if (result is null || !IsCurrent(gen)) return;
        if (!result.Success)
        {
            GitHubStatusText = RepoRenameFailureMessage(newName, result.FirstError);
            return;
        }
        RepoRenameDraft = "";
        if (repo.Length == 0) RepoRenameNotice = RenameNoCloneNotice;
        await LoadRepoSettings();
    }

    /// <summary>
    /// Offers this clone's origin the new URL and reports what came of it. Declining is a complete
    /// outcome, not a half-done rename: GitHub redirects the old address, so fetch and push keep
    /// working — what the reader is told is which name this page goes on showing until the URL changes.
    ///
    /// Every parameter is the value the rename was confirmed against, never the live one. The
    /// offer awaits a dialog, and a project applied while it is open swaps what
    /// <see cref="ProjectDetailViewModel.Project"/> names; the git write still belongs to
    /// <paramref name="repo"/>, and its record of itself still belongs to <paramref name="owner"/>.
    /// </summary>
    private async Task OfferLocalRemoteUpdateAsync(ProjectInfo? owner, string repo, string oldSlug,
        string newName, int gen)
    {
        RemotesResult remotes;
        try
        {
            remotes = await _gitService.GetRemotesAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the remotes of {repo} after a rename", ex);
            SetRenameNotice(RenameRemoteUnreadableNotice(ex.Message), gen);
            return;
        }
        if (remotes.HasError)
        {
            SetRenameNotice(RenameRemoteUnreadableNotice(remotes.ErrorText), gen);
            return;
        }

        var origin = remotes.Remotes.FirstOrDefault(r => r.Name == OriginRemote);
        if (origin is null || RenamedRemoteUrl(origin.FetchUrl, oldSlug, newName) is not { } url)
        {
            SetRenameNotice(RenameRemoteNotMatchedNotice(oldSlug), gen);
            return;
        }
        if (!await ConfirmAsync("Update this clone's remote URL?",
                RenameRemoteOfferMessage(origin.FetchUrl, url), "Update origin"))
        {
            SetRenameNotice(RenameRemoteDeclinedNotice, gen);
            return;
        }

        // Runs whatever the reader moved to: the confirmation named this clone, the lease is held
        // over it, and the write is keyed by the captured path rather than by anything on screen.
        var set = await _gitService.SetRemoteUrlAsync(repo, OriginRemote, url);
        if (!set.Success)
        {
            SetRenameNotice(RenameRemoteFailedNotice(set.FirstError), gen);
            return;
        }
        // The slug every gh call addresses is derived from origin's URL, so the model carrying it
        // is corrected — on the project that owns the URL, not on whichever one is now open.
        // Writing the live Project would hand the next project's page a slug for this repository,
        // and every action on that page would silently address this one.
        if (owner is not null) owner.GitStatus.RemoteUrl = url;
        if (!IsCurrent(gen)) return;
        // Bindings and the notice describe the page, so they follow the page's own project.
        OnPropertyChanged(nameof(Project));
        RepoRenameNotice = RenameRemoteUpdatedNotice(url);
        if (BranchesTabLoaded) await LoadRemotes();
    }

    /// <summary>
    /// Writes the rename notice only while the project it describes is still the one on screen.
    /// The notice is card state, not a status line: it sits inside the card that names the loaded
    /// repository, beside that repository's rename box, and a project switch clears it for exactly
    /// that reason. A sentence about the repository that left would stand there as a claim about
    /// the one now open. The rename's own record under that repository outlives the switch, and
    /// the clone's URL is readable from its remotes.
    /// </summary>
    private void SetRenameNotice(string notice, int gen)
    {
        if (IsCurrent(gen)) RepoRenameNotice = notice;
    }

    /// <summary>The remote a clone's slug is read from, and the only one a rename offers to rewrite.</summary>
    private const string OriginRemote = "origin";

    /// <summary>
    /// The origin URL with its repository segment renamed, or null when the URL does not name
    /// <paramref name="oldSlug"/> on GitHub. The scheme, host, credentials, owner and any .git
    /// suffix are kept byte for byte: a rebuilt URL could change the transport the clone
    /// authenticates over.
    /// </summary>
    internal static string? RenamedRemoteUrl(string url, string oldSlug, string newName)
    {
        if (newName.Length == 0 || newName.Contains('/', StringComparison.Ordinal)) return null;
        if (GitRemote.Parse(url) is not { IsGitHub: true } parsed) return null;
        if (!string.Equals($"{parsed.Owner}/{parsed.Repo}", oldSlug, StringComparison.OrdinalIgnoreCase))
            return null;

        var trimmed = url.Trim();
        var dotGit = trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
        var core = (dotGit ? trimmed[..^4] : trimmed).TrimEnd('/');
        var cut = core.LastIndexOfAny(['/', ':']);
        return cut < 0 ? null : core[..(cut + 1)] + newName + (dotGit ? ".git" : "");
    }

    internal static string RepoRenameMessage(string slug, string newName) =>
        $"Rename {slug} to {OwnerOf(slug)}/{newName}?\n\n" +
        "GitHub redirects the old address, so existing clones and links keep resolving. Anything pointed at " +
        "the literal old URL does not follow that redirect: CI configuration, webhooks, package references " +
        "and scripts that name the repository by hand.\n\n" +
        "This clone's origin URL is left as it is; updating it is offered separately once the rename lands.\n\n" +
        $"Type {slug} to confirm.";

    /// <summary>
    /// The collision gets its own sentence: gh passes GitHub's field error straight through, and
    /// on its own it reads as an outage rather than as a name the reader can simply change.
    /// </summary>
    internal static string RepoRenameFailureMessage(string newName, string error) =>
        GitHubService.IsRepoNameTaken(error)
            ? $"Rename failed: this account already has a repository named {newName}. Choose another name."
            : $"Rename failed: {error}";

    internal static string RenameRemoteOfferMessage(string from, string to) =>
        $"Point this clone's origin at the renamed repository?\n\n    {from}\n    becomes\n    {to}\n\n" +
        "Declining changes nothing here: GitHub redirects the old URL, so fetch and push keep working.";

    internal const string RenameRemoteDeclinedNotice =
        "Renamed on GitHub. This clone's origin still names the old repository; GitHub redirects it, so fetch " +
        "and push keep working, and this tab goes on showing the old name until the URL is changed.";

    internal const string RenameNoCloneNotice =
        "Renamed on GitHub. There is no clone of this project on this machine, so no remote URL needed updating.";

    internal static string RenameRemoteUpdatedNotice(string url) =>
        $"Renamed on GitHub, and this clone's origin now points at {url}.";

    internal static string RenameRemoteFailedNotice(string error) =>
        $"Renamed on GitHub, but this clone's origin could not be changed: {error} The old URL still resolves " +
        "through GitHub's redirect; it can be changed on the Branches tab.";

    internal static string RenameRemoteUnreadableNotice(string error) =>
        $"Renamed on GitHub, but this clone's remotes could not be read: {error} Whether origin still names the " +
        "old repository is unknown; check it on the Branches tab.";

    internal static string RenameRemoteNotMatchedNotice(string oldSlug) =>
        $"Renamed on GitHub. No origin remote here names {oldSlug}, so there was no local URL to update.";

    /// <summary>Owner half of an owner/name slug; the whole string when it carries no separator.</summary>
    internal static string OwnerOf(string slug)
    {
        var slash = slug.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? slug : slug[..slash];
    }

    // ── Archive / unarchive ─────────────────────────────────────────────────────

    /// <summary>
    /// Whether the loaded settings allow a write. GitHub refuses every write to an archived
    /// repository; a disabled editor is a rendering decision, and a command reachable from the
    /// keyboard enforces the state itself.
    /// </summary>
    private bool RepoWritable(RepoSettings loaded, string what)
    {
        if (!loaded.IsArchived) return true;
        GitHubStatusText =
            $"{what} refused — {Slug} is archived and read-only on GitHub. Unarchive it before changing it.";
        return false;
    }

    [RelayCommand]
    private async Task ToggleRepoArchive()
    {
        if (IsBusy) return;
        var slug = Slug;
        var loaded = RepoSettings;
        if (!HasGitHubRemote(slug)) return;
        if (!HasRepoSettings(loaded)) return;

        var archiving = !loaded.IsArchived;
        var what = archiving ? "Archive" : "Unarchive";
        var gen = _generation;
        if (!await ConfirmAsync($"{what} this repository?", RepoArchiveMessage(slug, archiving), what)) return;
        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice($"{what} repository");
            return;
        }
        if (IsBusy)
        {
            GitHubStatusText = BusyGateNotice($"{what} repository");
            return;
        }

        var ok = await RunGitHubOpResult(
            () => archiving ? ArchiveRepoRemoteAsync(slug) : UnarchiveRepoRemoteAsync(slug),
            $"{what} {slug}", RepoPath) is { Success: true };
        // The card re-renders from what the remote reports and never from an optimistic flip: a
        // failed archive that had already moved the flag would grey out every editor on a
        // repository that is still live.
        if (ok && IsCurrent(gen)) await LoadRepoSettings();
    }

    internal static string RepoArchiveMessage(string slug, bool archiving) =>
        archiving
            ? $"Archive {slug}?\n\n" +
              "GitHub makes an archived repository read-only. Pushes are refused, and issues, pull requests, " +
              "releases and settings can be read but not changed — including every editor on this tab, which " +
              "greys out while it stays archived. The code, history and issues stay visible to anyone who " +
              "could already see them.\n\nUnarchiving reverses it."
            : $"Unarchive {slug}?\n\n" +
              "Writes are accepted again: pushes, issues, pull requests, and the editors on this tab.";

    // ── Fork divergence and sync ────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadForkDivergence()
    {
        var loaded = RepoSettings;
        var slug = Slug;
        var fetch = ++_forkDivergenceFetch;
        ForkDivergence = null;
        _forkSyncBranch = "";
        ForkDivergenceText = "";
        if (loaded is null || !loaded.IsFork || slug.Length == 0) return;

        var parentSlug = loaded.ParentSlug;
        var gen = _generation;
        ForkDivergenceLoading = true;
        try
        {
            // The parent's default branch is the one gh resolves for both sides of a sync, so it
            // is also the only branch a comparison shown beside that button may describe.
            var parent = await FetchRepoSettingsAsync(parentSlug);
            var branch = parent?.DefaultBranch ?? "";
            var divergence = branch.Length == 0
                ? null
                : await FetchForkDivergenceAsync(parentSlug, OwnerOf(parentSlug), OwnerOf(slug), branch);
            if (!IsCurrent(gen) || _forkDivergenceFetch != fetch) return;
            ForkDivergence = divergence;
            _forkSyncBranch = divergence is null ? "" : branch;
            ForkDivergenceText = DescribeForkDivergence(parentSlug, branch, divergence);
        }
        finally
        {
            if (IsCurrent(gen) && _forkDivergenceFetch == fetch) ForkDivergenceLoading = false;
        }
    }

    internal static string DescribeForkDivergence(string parentSlug, string branch, ForkDivergence? divergence) =>
        divergence is null
            ? $"Couldn't compare this fork with {parentSlug}. How far apart they are is unknown, and a sync is " +
              "not offered on a comparison that never answered."
            : divergence.InSync
                ? $"{branch} matches {parentSlug}. There is nothing to sync."
                : $"{branch} is {CommitTally(divergence.Behind)} behind and {CommitTally(divergence.Ahead)} ahead of {parentSlug}.";

    private static string CommitTally(int count) => count == 1 ? "1 commit" : $"{count} commits";

    [RelayCommand]
    private async Task SyncFork()
    {
        if (IsBusy) return;
        var slug = Slug;
        var loaded = RepoSettings;
        var repo = RepoPath;
        if (!HasGitHubRemote(slug)) return;
        if (!HasRepoSettings(loaded)) return;
        if (!loaded.IsFork)
        {
            GitHubStatusText = $"{slug} is not a fork — there is no parent repository to sync from.";
            return;
        }
        if (repo.Length == 0)
        {
            GitHubStatusText = "Syncing a fork updates a clone on this machine, and this project has none here.";
            return;
        }
        // Tri-state: a comparison that failed is not a fork that is up to date, and the
        // fast-forward path would claim nothing local is at risk on the strength of it.
        if (ForkDivergence is not { } divergence || _forkSyncBranch.Length == 0)
        {
            GitHubStatusText = ForkSyncUnreadNotice;
            return;
        }

        var parentSlug = loaded.ParentSlug;
        var branch = _forkSyncBranch;
        if (divergence.Behind == 0)
        {
            GitHubStatusText = $"{branch} already carries everything {parentSlug} has — nothing to sync.";
            return;
        }

        var gen = _generation;
        // Ahead of the parent means gh cannot fast-forward: the sync becomes a hard reset that
        // drops those commits, which is outward of "catch up" and takes the typed confirmation.
        var discarding = divergence.Ahead > 0;
        if (discarding)
        {
            var typed = await PromptForTextAsync("Sync this fork over its own commits?",
                ForkSyncDiscardMessage(slug, parentSlug, branch, divergence), "Sync and discard");
            if (!RepoNameConfirmed(typed, slug))
            {
                if (typed is not null) GitHubStatusText = $"Fork not synced — that isn't {slug}.";
                return;
            }
        }
        else if (!await ConfirmAsync("Sync this fork?",
                     ForkSyncMessage(parentSlug, branch, divergence), "Sync fork"))
        {
            return;
        }

        if (!IsCurrent(gen))
        {
            GitHubStatusText = ProjectSwitchedNotice("Sync fork");
            return;
        }
        if (IsBusy)
        {
            GitHubStatusText = BusyGateNotice("Sync fork");
            return;
        }

        var result = await RunGitHubRepoOpResult(() => SyncForkRemoteAsync(repo, discarding),
            $"Sync {branch} from {parentSlug}", repo);
        if (result is null || !IsCurrent(gen)) return;
        if (!result.Success)
        {
            GitHubStatusText = ForkSyncFailureMessage(result.FirstError);
            return;
        }
        await SafeRefreshWorkingStateAsync();
        await LoadForkDivergence();
    }

    internal const string ForkSyncUnreadNotice =
        "Sync fork not offered — how far this fork stands from its parent could not be read, and syncing on an " +
        "unread comparison would name a count nothing measured. Refresh the tab and try again.";

    internal static string ForkSyncMessage(string parentSlug, string branch, ForkDivergence divergence) =>
        $"Bring {branch} in this clone up to {parentSlug}?\n\n" +
        $"It moves forward by {CommitTally(divergence.Behind)}. Nothing local is discarded, and the branch is only " +
        "fast-forwarded — a working tree with uncommitted changes on it stops the sync rather than being reset.";

    internal static string ForkSyncDiscardMessage(string slug, string parentSlug, string branch,
        ForkDivergence divergence) =>
        $"Reset {branch} in this clone onto {parentSlug}?\n\n" +
        $"{branch} is {CommitTally(divergence.Ahead)} ahead of {parentSlug}. Those commits are not on the parent, " +
        $"and this sync discards them: the branch is hard-reset to the parent's, gaining {CommitTally(divergence.Behind)}.\n\n" +
        "Commits only reachable from this branch are recoverable from the reflog until git expires them; anything " +
        "uncommitted in the working tree is not.\n\n" +
        $"Type {slug} to confirm.";

    /// <summary>
    /// Two refusals gh states plainly and a caller cannot tell apart from a network failure by
    /// the exit code alone: a branch that has diverged, and a working tree with changes on it.
    /// Both are worked around by the reader, not retried.
    /// </summary>
    internal static string ForkSyncFailureMessage(string error) =>
        GitHubService.IsForkSyncDiverged(error)
            ? "Sync fork failed: this clone's branch has commits the parent does not. Refresh the tab — syncing " +
              "over them takes the typed confirmation that names how many would be discarded."
            : GitHubService.IsForkSyncDirtyWorkingTree(error)
                ? "Sync fork failed: the working tree has uncommitted or untracked changes on the branch being " +
                  "synced. Commit or stash them, then sync."
                : $"Sync fork failed: {error}";
}
