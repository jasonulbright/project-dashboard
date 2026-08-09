using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The tag viewer, opened from History because every tag names a commit in that list and the
/// one it creates lands on whichever commit is selected there.
///
/// Creating, deleting, and checking a tag out touch refs in this repository only. A push is the
/// one outward action, and it only adds: it sends a tag to the chosen remote and removes nothing
/// there. No path deletes a tag on a remote, so a delete is reported for what it is — the local
/// ref is gone and a remote's copy of it is not — rather than as the tag having been removed.
/// </summary>
public partial class ProjectDetailViewModel
{
    [ObservableProperty] private bool _tagsVisible;

    partial void OnTagsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SafetyOverlayHidden));
        OnPropertyChanged(nameof(MaintenanceOverlayHidden));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushAllTagsCommand))]
    private ObservableCollection<TagInfo> _tags = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTagCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckOutTagAsBranchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushTagCommand))]
    private TagInfo? _selectedTag;

    /// <summary>True once a read has finished and found none. The empty state must not show before that.</summary>
    [ObservableProperty] private bool _tagsEmpty;

    [ObservableProperty] private string _tagsStatusText = "";
    [ObservableProperty] private string _tagsErrorText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateTagCommand))]
    private string _newTagName = "";

    /// <summary>Non-empty makes the new tag annotated; empty makes it lightweight.</summary>
    [ObservableProperty] private string _newTagMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckOutTagAsBranchCommand))]
    private string _tagBranchName = "";

    /// <summary>Names of this repository's remotes, read when the viewer opens; empty when it has none.</summary>
    [ObservableProperty] private ObservableCollection<string> _tagRemoteNames = [];

    /// <summary>Where a push sends tags. Null when no remote is configured or the remote read failed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushTagCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushAllTagsCommand))]
    private string? _selectedTagRemote;

    /// <summary>The remote a clone gets by default, preferred as the push target when it is configured.</summary>
    private const string ConventionalRemoteName = "origin";

    private const string RemotesUnreadablePrefix = "Could not read this repository's remotes: ";

    /// <summary>The read the viewer started and did not await, so a caller can wait for the list rather than poll.</summary>
    internal Task TagsRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The commit a new tag would land on: whichever the History list has selected, or the
    /// checked-out commit when it has none. Stated on the surface, because a tag created against
    /// the wrong commit looks identical to one created against the right one.
    /// </summary>
    public string TagTargetLabel =>
        SelectedCommit is { } commit
            ? $"{commit.ShortHash} — {commit.Message}"
            : "the checked-out commit (HEAD)";

    /// <summary>The revision a new tag is created at; null means HEAD, which is what git tags by default.</summary>
    private string? NewTagTarget => SelectedCommit?.Ref;

    [RelayCommand]
    private async Task OpenTags()
    {
        if (RepoPath.Length == 0 || ForcePushVisible || ReflogVisible) return;
        TagsErrorText = "";
        TagsStatusText = "";
        NewTagName = "";
        NewTagMessage = "";
        TagBranchName = "";
        SelectedTag = null;
        TagsVisible = true;
        OnPropertyChanged(nameof(TagTargetLabel));
        TagsRefresh = LoadTags();
        await TagsRefresh;
    }

    [RelayCommand]
    private void CloseTags()
    {
        TagsVisible = false;
        Tags = [];
        TagRemoteNames = [];
        SelectedTag = null;
        SelectedTagRemote = null;
        NewTagName = "";
        NewTagMessage = "";
        TagBranchName = "";
        TagsStatusText = "";
        TagsErrorText = "";
        TagsEmpty = false;
    }

    /// <summary>Drops the viewer as the page leaves this repository; the tags it lists are that repository's.</summary>
    private void CloseTagsOnProjectSwitch()
    {
        if (!TagsVisible) return;
        CloseTags();
    }

    [RelayCommand]
    private async Task LoadTags()
    {
        var repo = RepoPath;
        if (repo.Length == 0) return;
        var gen = _generation;

        var keep = SelectedTag?.Name;
        var keepRemote = SelectedTagRemote;
        TagsResult tags;
        RemotesResult remotes;
        try
        {
            tags = await _gitService.GetTagsAsync(repo);
            remotes = await _gitService.GetRemotesAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the tags of {repo}", ex);
            if (IsCurrent(gen))
            {
                TagsErrorText = $"Could not read this repository's tags: {ex.Message}";
                TagsEmpty = false;
            }
            return;
        }
        if (!IsCurrent(gen)) return;

        // "No tags" is a fact about the repository; a ref read that failed establishes no such
        // fact, and the empty state would claim one.
        if (tags.HasError)
        {
            TagsErrorText = $"Could not read this repository's tags: {tags.ErrorText}";
            TagsEmpty = false;
            return;
        }

        TagsErrorText = "";
        Tags = new ObservableCollection<TagInfo>(tags.Tags.OrderByDescending(t => t.DisplayDate ?? DateTimeOffset.MinValue)
                                                          .ThenBy(t => t.Name, StringComparer.Ordinal));
        TagsEmpty = Tags.Count == 0;
        TagRemoteNames = new ObservableCollection<string>(remotes.Remotes.Select(r => r.Name));
        SelectedTag = Tags.FirstOrDefault(t => t.Name == keep) ?? Tags.FirstOrDefault();
        SelectedTagRemote =
            keepRemote is not null && TagRemoteNames.Contains(keepRemote) ? keepRemote
            : TagRemoteNames.Contains(ConventionalRemoteName) ? ConventionalRemoteName
            : TagRemoteNames.FirstOrDefault();

        // The two reads answer different questions: a refused remote read leaves the push targets
        // unknown and establishes nothing about the tags it never touched, so it is reported
        // beside the list rather than in place of it — it is what explains an empty push
        // dropdown. A read that answered clears this notice only, leaving the status an
        // operation left in place.
        if (remotes.HasError) TagsStatusText = RemotesUnreadablePrefix + remotes.ErrorText;
        else if (TagsStatusText.StartsWith(RemotesUnreadablePrefix, StringComparison.Ordinal)) TagsStatusText = "";
    }

    // ── Create ──────────────────────────────────────────────────────────────────

    private bool CanCreateTag() => NewTagName.Trim().Length > 0 && !IsBusy && RepoPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreateTag))]
    private async Task CreateTag()
    {
        var name = NewTagName.Trim();
        var message = NewTagMessage.Trim();
        var repo = RepoPath;
        var gen = _generation;
        var target = NewTagTarget;
        var targetLabel = TagTargetLabel;
        if (name.Length == 0 || repo.Length == 0 || IsBusy) return;

        if (!await _gitService.IsValidTagNameAsync(repo, name))
        {
            if (IsCurrent(gen)) TagsErrorText = InvalidTagNameMessage(name);
            return;
        }
        if (!IsCurrent(gen)) return;
        if (Tags.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)))
        {
            TagsErrorText = $"A tag called “{name}” already exists here. Delete it first or choose another name.";
            return;
        }

        TagsErrorText = "";
        var ok = await RunOp(r => _gitService.CreateTagAsync(r, name, message.Length > 0 ? message : null, target),
            $"Create tag {name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            TagsErrorText = SyncStatusText;
            TagsStatusText = "The tag was not created.";
            return;
        }

        var kind = message.Length > 0 ? "Annotated tag" : "Lightweight tag";
        TagsStatusText = $"{kind} {name} created at {targetLabel}. It exists here only — nothing was pushed.";
        NewTagName = "";
        NewTagMessage = "";
        await LoadTags();
    }

    /// <summary>What git will not accept, said in the terms a reader can act on.</summary>
    internal static string InvalidTagNameMessage(string name) =>
        $"“{name}” is not a valid tag name. Tag names cannot contain spaces, “..”, “~”, “^”, “:”, “?”, “*”, " +
        "“[”, a leading dash, or a trailing “/”, “.” or “.lock”.";

    // ── Delete ──────────────────────────────────────────────────────────────────

    private bool CanDeleteTag() => SelectedTag is not null && !IsBusy && RepoPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteTag))]
    private async Task DeleteTag()
    {
        var tag = SelectedTag;
        var repo = RepoPath;
        var gen = _generation;
        if (tag is null || repo.Length == 0 || IsBusy) return;

        var remoteNote = RemoteTagNotice(TagRemoteNames);
        var confirmed = await ConfirmPrompt("Delete this tag?",
            $"Delete the tag {tag.Name}, which points at {tag.TargetSubject}?\n\n{remoteNote}", "Delete tag");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            TagsStatusText = ProjectSwitchedNotice("Tag delete");
            return;
        }

        TagsErrorText = "";
        var ok = await RunOp(r => _gitService.DeleteTagAsync(r, tag.Name), $"Delete tag {tag.Name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            TagsErrorText = SyncStatusText;
            TagsStatusText = "The tag was not deleted.";
            return;
        }

        TagsStatusText = $"Deleted {tag.Name} here. {remoteNote}";
        await LoadTags();
    }

    /// <summary>
    /// What a local delete leaves standing. A tag on a remote is a separate ref that only a push
    /// of a deletion can remove, and no surface here performs one — so the reader is told which
    /// remotes could still be carrying the tag rather than left to assume it is gone everywhere.
    /// </summary>
    internal static string RemoteTagNotice(IReadOnlyCollection<string> remoteNames) =>
        remoteNames.Count == 0
            ? "This repository has no remotes, so nothing here knows of another copy."
            : $"The delete is local. If {string.Join(", ", remoteNames)} also carries this tag, it still will — " +
              "removing a tag from a remote takes a push that deletes it there, and this surface only sends tags.";

    // ── Push ────────────────────────────────────────────────────────────────────

    private bool CanPushTag() =>
        SelectedTag is not null && SelectedTagRemote is not null && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Sends the selected tag to the chosen remote. Additive and unconfirmed, the same risk class
    /// as pushing commits: it creates the ref there and moves nothing here. A remote that refuses
    /// the ref leaves both sides as they were.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPushTag))]
    private async Task PushTag()
    {
        var tag = SelectedTag;
        var remote = SelectedTagRemote;
        var repo = RepoPath;
        var gen = _generation;
        if (tag is null || remote is null || repo.Length == 0) return;
        if (IsBusy) { TagsErrorText = BusyNotice("Push tag"); return; }

        TagsErrorText = "";
        var ok = await RunOp(r => _gitService.PushTagAsync(r, remote, tag.Name),
            $"Push {tag.Name} to {remote}", repo, gen, TagPushRefusalAdvice);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            TagsErrorText = SyncStatusText;
            TagsStatusText = $"{tag.Name} was not pushed. It is unchanged here.";
            return;
        }
        TagsStatusText = $"Pushed {tag.Name} to {remote}. Both carry it now, and the tag here did not move.";
    }

    private bool CanPushAllTags() =>
        SelectedTagRemote is not null && Tags.Count > 0 && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Sends every tag this repository holds to the chosen remote. Refs are not pushed as one
    /// unit, so a run the remote partly refused is reported as partial rather than as a failure
    /// that changed nothing there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPushAllTags))]
    private async Task PushAllTags()
    {
        var remote = SelectedTagRemote;
        var repo = RepoPath;
        var gen = _generation;
        if (remote is null || repo.Length == 0) return;
        if (IsBusy) { TagsErrorText = BusyNotice("Push tags"); return; }

        TagsErrorText = "";
        var count = Tags.Count;
        var ok = await RunOp(r => _gitService.PushAllTagsAsync(r, remote), $"Push all tags to {remote}",
            repo, gen, TagPushRefusalAdvice);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            TagsErrorText = SyncStatusText;
            TagsStatusText =
                $"Not every tag reached {remote}. Any the remote accepted are there; the rest are unchanged, " +
                "here and on the remote.";
            return;
        }
        // A tag the remote already carried needed no transfer; what the run establishes is that
        // every tag here is now there, which is what is claimed.
        TagsStatusText =
            $"All {count} tag{(count == 1 ? "" : "s")} here {(count == 1 ? "is" : "are")} now on {remote}. " +
            $"A tag only on {remote} is untouched — this sends tags, it does not fetch or remove any.";
    }

    /// <summary>
    /// The sentence a rejected tag push needs beyond git's own. git reports the rejection and the
    /// remote's reason for it; neither says the tag cannot be pushed from here at all, which is
    /// the one failure retrying or renaming will not fix.
    /// </summary>
    internal static string? TagPushRefusalAdvice(ProcessResult result) =>
        IsProtectedRefRefusal(result.StdErr) || IsProtectedRefRefusal(result.StdOut)
            ? "This tag is protected on the remote and cannot be pushed from here — the protection has to be " +
              "lifted on the remote first."
            : null;

    /// <summary>
    /// Wordings a remote uses when its own protection or ruleset refused a ref, matched against
    /// the remote's echoed lines.
    /// </summary>
    private static readonly string[] ProtectionRefusalMarkers =
        ["protected tag", "protected ref", "protected branch", "GH006", "GH013", "rule violations", "being restricted"];

    /// <summary>
    /// Whether a push a remote rejected was rejected for a protection rule. The rejection line
    /// alone does not establish it — a tag already on the remote at another commit is rejected
    /// too, and that one is answered here rather than on the remote — so the reason has to name
    /// the protection as well.
    /// </summary>
    internal static bool IsProtectedRefRefusal(string output) =>
        output.Contains("[remote rejected]", StringComparison.Ordinal) &&
        ProtectionRefusalMarkers.Any(m => output.Contains(m, StringComparison.OrdinalIgnoreCase));

    // ── Check out as a branch ───────────────────────────────────────────────────

    private bool CanCheckOutTagAsBranch() =>
        SelectedTag is not null && TagBranchName.Trim().Length > 0 && !IsBusy && RepoPath.Length > 0;

    /// <summary>
    /// Creates a branch at the selected tag's commit and switches to it. Nothing existing moves,
    /// and the tag is left where it is — this is the way onto a tagged state that does not leave
    /// the checkout detached.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckOutTagAsBranch))]
    private async Task CheckOutTagAsBranch()
    {
        var tag = SelectedTag;
        var name = TagBranchName.Trim();
        var repo = RepoPath;
        var gen = _generation;
        if (tag is null || name.Length == 0 || repo.Length == 0 || IsBusy) return;

        if (!await _gitService.IsValidBranchNameAsync(repo, name))
        {
            if (IsCurrent(gen)) TagsErrorText = InvalidBranchNameMessage(name);
            return;
        }
        if (!IsCurrent(gen)) return;
        if (Branches.Any(b => string.Equals(b.Name, name, StringComparison.Ordinal)))
        {
            TagsErrorText = $"A branch called “{name}” already exists here. Choose another name.";
            return;
        }

        TagsErrorText = "";
        // Bound to the tag's commit rather than its name: a tag can be moved between the read
        // and this click, and the row named a commit.
        var ok = await RunOp(r => _gitService.CreateBranchAtAsync(r, name, tag.TargetSha),
            $"Create {name} at {tag.Name}", repo, gen);
        if (!IsCurrent(gen)) return;

        if (!ok)
        {
            TagsErrorText = SyncStatusText;
            TagsStatusText = "The branch was not created.";
            return;
        }

        TagsStatusText = $"Created {name} at {tag.Name} and switched to it. The tag itself did not move.";
        TagBranchName = "";
        await ReloadCommitsAsync();
        await LoadBranches();
    }

    internal static string InvalidBranchNameMessage(string name) =>
        $"“{name}” is not a valid branch name. Branch names cannot contain spaces, “..”, “~”, “^”, “:”, “?”, " +
        "“*”, “[”, a leading dash, or a trailing “/” or “.lock”.";
}
