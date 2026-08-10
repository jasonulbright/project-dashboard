using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Object signing on the everyday surfaces — the commit box and the tag creator — on the same
/// terms the history-editing surface already runs on: a repository configured to sign is detected
/// when it loads, the first write of the session asks which way to go, and nothing here turns
/// signing off on its own. Only a <see cref="SigningChoice.ProceedUnsigned"/> the reader picked
/// does, and only for the runs that follow it.
///
/// Commits and tags carry SEPARATE answers. git's two settings are independent, and a reader who
/// declined signing for a working commit has not thereby declined it for a release tag — carrying
/// one answer onto the other would drop a signature nobody was asked about, which is the whole
/// failure this surface exists to prevent.
///
/// Each choice lives for as long as the repository is on screen and is written nowhere: a
/// persisted "never sign" would strip signatures the reader asked for in every later session
/// without saying so.
///
/// Detection is configuration, not verification. Nothing here probes the signing agent or claims
/// a write will stall; what it states is that the repository signs, and that an uncached
/// passphrase can leave git waiting on a prompt this app cannot show.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Await seam for the load-time read, like <see cref="WorkingStateRefresh"/>.</summary>
    internal Task SigningRefresh { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _commitSigningChipVisible;
    [ObservableProperty] private string _commitSigningChipText = "";
    [ObservableProperty] private string _commitSigningChipTooltip = "";
    [ObservableProperty] private bool _commitSigningOfferVisible;
    [ObservableProperty] private string _commitSigningOfferText = "";

    [ObservableProperty] private bool _tagSigningChipVisible;
    [ObservableProperty] private string _tagSigningChipText = "";
    [ObservableProperty] private string _tagSigningChipTooltip = "";
    [ObservableProperty] private bool _tagSigningOfferVisible;
    [ObservableProperty] private string _tagSigningOfferText = "";

    private bool _repoSignsCommits;
    private bool _repoSignsTags;
    private string _repoSigningFormat = "";
    private SigningChoice _commitSigning = SigningChoice.NotChosen;
    private SigningChoice _tagSigning = SigningChoice.NotChosen;

    /// <summary>
    /// Cleared on a project switch and on nothing else. The answers are per repository, so a
    /// reload of the same one keeps what the reader already gave.
    /// </summary>
    private void ResetSigningState()
    {
        _repoSignsCommits = false;
        _repoSignsTags = false;
        _repoSigningFormat = "";
        _commitSigning = SigningChoice.NotChosen;
        _tagSigning = SigningChoice.NotChosen;
        _tagSigningRetry = null;
        _tagSigningRetryAnnotated = false;
        CommitSigningChipVisible = false;
        CommitSigningChipText = "";
        CommitSigningChipTooltip = "";
        CommitSigningOfferVisible = false;
        CommitSigningOfferText = "";
        TagSigningChipVisible = false;
        TagSigningChipText = "";
        TagSigningChipTooltip = "";
        TagSigningOfferVisible = false;
        TagSigningOfferText = "";
    }

    private async Task SafeRefreshSigningAsync()
    {
        try { await RefreshSigningAsync(); }
        catch (Exception ex) { Log.Warn("signing configuration read failed", ex); }
    }

    /// <summary>
    /// The configuration reads, per repository load and off every write path: a read taken while
    /// the reader waits on a commit is a read that made the commit slower for nothing. The format
    /// is read once and covers both kinds — git's gpg.format is not per object kind.
    /// </summary>
    private async Task RefreshSigningAsync()
    {
        var gen = _generation;
        var repo = RepoPath;
        if (repo.Length == 0) return;

        var commits = await _gitService.SignsCommitsAsync(repo);
        var tags = await _gitService.SignsTagsAsync(repo);
        var format = commits || tags ? await _gitService.GetSigningFormatAsync(repo) : "";
        if (!IsCurrent(gen) || repo != RepoPath) return;

        _repoSignsCommits = commits;
        _repoSignsTags = tags;
        _repoSigningFormat = format;
        CommitSigningChipVisible = commits;
        TagSigningChipVisible = tags;
        if (commits) RefreshCommitSigningChip(); else CommitSigningOfferVisible = false;
        if (tags) RefreshTagSigningChip(); else TagSigningOfferVisible = false;
    }

    private void RefreshCommitSigningChip()
    {
        var unsigned = _commitSigning == SigningChoice.ProceedUnsigned;
        CommitSigningChipText = unsigned ? "Signs commits — this session: unsigned" : "Signs commits";
        CommitSigningChipTooltip =
            (_repoSigningFormat.Length > 0
                ? $"commit.gpgsign is on and gpg.format is {_repoSigningFormat}. "
                : "commit.gpgsign is on. ")
            + "This is what the repository is configured to do, not a check that any commit was signed. "
            + (unsigned
                ? "Commits from this box are running with signing turned off until this project is left."
                : "The first commit of this session asks whether to sign.");
    }

    private bool CommitSigningChoicePending => _repoSignsCommits && _commitSigning == SigningChoice.NotChosen;

    private void ShowCommitSigningOffer(string label)
    {
        CommitSigningOfferText =
            $"{label} needs a decision first: this repository signs commits (commit.gpgsign is on). " +
            "If the signing key's passphrase is not cached, git waits on a prompt this app cannot show and the " +
            "attempt is killed at its timeout. Sign as configured, or commit without signing.";
        CommitSigningOfferVisible = true;
    }

    /// <summary>
    /// Puts the choice back after a signed attempt failed on the signing. The failure line
    /// beside it carries what happened; this says only what the two buttons now do, since the
    /// answer already given is the one that just failed.
    /// </summary>
    private void ReofferCommitSigningAfterFailure(string label)
    {
        CommitSigningOfferText =
            $"The signed {label.ToLowerInvariant()} wrote nothing. Sign as configured to try it again, " +
            "or commit without signing.";
        CommitSigningOfferVisible = true;
    }

    /// <summary>
    /// What a failed signing commit leaves the reader able to do, appended to the failure line.
    /// Read only where signing is configured on and the run was asked to sign, so a failure that
    /// had nothing to do with the key is not narrated as though it did.
    /// </summary>
    private string? CommitSigningAdvice(ProcessResult result, SigningChoice signing)
    {
        if (!_repoSignsCommits || signing != SigningChoice.KeepSigning) return null;
        if (result.TimedOut)
            return "This repository signs commits, and the attempt ran out its budget without git reporting anything — "
                + "the shape of a signing key whose passphrase is not cached, waiting on a prompt (pinentry) this app "
                + "cannot show. Nothing was committed. Cache the passphrase in a terminal, or commit without signing.";
        return GitService.IsSigningFailure(result)
            ? "The signing key could not sign this commit, so git wrote none. Fix the signing setup, or commit without signing."
            : null;
    }

    /// <summary>
    /// True when a failed commit is the signing question re-opening rather than a plain failure:
    /// the reader picked signing, signing is what failed, and the other answer is still available.
    /// </summary>
    private bool CommitSigningTroubled(ProcessResult result, SigningChoice signing) =>
        CommitSigningAdvice(result, signing) is not null;

    /// <summary>
    /// Commits signing exactly as the repository is configured to, accepting the wait the offer
    /// on screen names.
    /// </summary>
    [RelayCommand]
    private Task CommitSigned() => ChooseCommitSigningAsync(SigningChoice.KeepSigning);

    /// <summary>
    /// Commits with `commit.gpgsign=false` for this run and the ones after it in this project.
    /// Confirmed separately from the Commit click: the commit comes out unsigned and nothing in
    /// this app re-signs it.
    /// </summary>
    [RelayCommand]
    private async Task CommitUnsigned()
    {
        var repo = RepoPath;
        if (!await ConfirmAsync(
                "Commit without signing?",
                "This repository is configured to sign its commits. Committing without signing leaves this commit — "
                + "and the ones after it, until you leave this project — unsigned.\n\n"
                + "Nothing in this app re-signs them; re-signing afterwards is a manual git job. The repository's "
                + "configuration is not changed.",
                "Commit unsigned"))
            return;
        // The dialog is a window the reader can hold open across a project switch; the answer
        // then authorises a commit in a repository the question never named.
        if (repo != RepoPath) return;
        await ChooseCommitSigningAsync(SigningChoice.ProceedUnsigned);
    }

    private async Task ChooseCommitSigningAsync(SigningChoice choice)
    {
        if (IsBusy) { SyncStatusText = BusyNotice(AmendMode ? "Amend" : "Commit"); return; }
        if (!_repoSignsCommits) return;
        _commitSigning = choice;
        CommitSigningOfferVisible = false;
        RefreshCommitSigningChip();
        await RunCommitAsync();
    }

    // ── Tags ────────────────────────────────────────────────────────────────

    private void RefreshTagSigningChip()
    {
        var unsigned = _tagSigning == SigningChoice.ProceedUnsigned;
        TagSigningChipText = unsigned ? "Signs tags — this session: unsigned" : "Signs tags";
        TagSigningChipTooltip =
            (_repoSigningFormat.Length > 0
                ? $"tag.gpgsign is on and gpg.format is {_repoSigningFormat}. "
                : "tag.gpgsign is on. ")
            + "With it on git signs every tag it creates here, lightweight requests included. "
            + "This is what the repository is configured to do, not a check that any tag was signed. "
            + (unsigned
                ? "Tags created here are running with signing turned off until this project is left."
                : "The first tag of this session asks whether to sign.");
    }

    private bool TagSigningChoicePending => _repoSignsTags && _tagSigning == SigningChoice.NotChosen;

    /// <summary>
    /// The creation the offer is about, held rather than re-read from the boxes: the name gate ran
    /// against these values, the offer names them, and the repository is the one the question was
    /// asked about. Null whenever no offer is outstanding.
    /// </summary>
    private Func<Task>? _tagSigningRetry;

    /// <summary>True when the held creation carries a message, which decides what the offer says.</summary>
    private bool _tagSigningRetryAnnotated;

    private void ClearTagSigningOffer()
    {
        _tagSigningRetry = null;
        _tagSigningRetryAnnotated = false;
        TagSigningOfferVisible = false;
        TagSigningOfferText = "";
    }

    private void HoldTagSigningOffer(
        string name, string message, string repo, int gen, string? target, string targetLabel)
    {
        _tagSigningRetry = () => RunCreateTagAsync(name, message, repo, gen, target, targetLabel);
        _tagSigningRetryAnnotated = message.Length > 0;
        ShowTagSigningOffer(_tagSigningRetryAnnotated);
    }

    /// <summary>
    /// The offer names what `tag.gpgsign` does to the shape the reader asked for. A lightweight
    /// request is not exempt: git turns a bare `git tag` into a signed one, which needs a message
    /// it has not been given, so signing as configured cannot produce the tag that was asked for.
    /// </summary>
    private void ShowTagSigningOffer(bool annotated)
    {
        TagSigningOfferText = annotated
            ? "This repository signs tags (tag.gpgsign is on), and this tag carries a message, so git signs it. "
            + "If the signing key's passphrase is not cached, git waits on a prompt this app cannot show and the "
            + "attempt is killed at its timeout. Sign as configured, or create the tag without signing."
            : "This repository signs tags (tag.gpgsign is on), and with it on git will not create a lightweight "
            + "tag — it makes a signed one, which needs a message this tag has not been given. Add a message and "
            + "sign as configured, or create the lightweight tag without signing.";
        TagSigningOfferVisible = true;
    }

    private void ReofferTagSigningAfterFailure(
        string name, string message, string repo, int gen, string? target, string targetLabel)
    {
        _tagSigningRetry = () => RunCreateTagAsync(name, message, repo, gen, target, targetLabel);
        _tagSigningRetryAnnotated = message.Length > 0;
        TagSigningOfferText =
            "The signed tag was not created. Sign as configured to try it again, or create it without signing.";
        TagSigningOfferVisible = true;
    }

    /// <summary>
    /// What a failed signing tag leaves the reader able to do, appended to the failure line. Read
    /// only where tag signing is configured on and the run was asked to sign, so a failure that
    /// had nothing to do with the key is not narrated as though it did.
    /// </summary>
    private string? TagSigningAdvice(ProcessResult result, SigningChoice signing)
    {
        if (!_repoSignsTags || signing != SigningChoice.KeepSigning) return null;
        if (result.TimedOut)
            return "This repository signs tags, and the attempt ran out its budget without git reporting anything — "
                + "the shape of a signing key whose passphrase is not cached, waiting on a prompt (pinentry) this app "
                + "cannot show. No tag was created. Cache the passphrase in a terminal, or create it without signing.";
        return GitService.IsSigningFailure(result)
            ? "The signing key could not sign this tag, so git created none. Fix the signing setup, or create it without signing."
            : null;
    }

    private bool TagSigningTroubled(ProcessResult result, SigningChoice signing) =>
        TagSigningAdvice(result, signing) is not null;

    /// <summary>
    /// Creates the tag signing exactly as the repository is configured to, accepting the wait the
    /// offer on screen names. A lightweight request is refused here rather than spawned: git would
    /// make a signed tag out of it and fail for the message it has not been given, and the reason
    /// is known before anything runs. The offer is left standing, so the unsigned answer — which
    /// does produce the lightweight tag that was asked for — stays one click away.
    /// </summary>
    [RelayCommand]
    private async Task CreateTagSigned()
    {
        // The held creation is what runs, so a message typed into the box after the offer went up
        // reaches this only by pressing Create tag again.
        if (!_tagSigningRetryAnnotated)
        {
            TagsErrorText =
                "A signed tag needs a message, and tag.gpgsign is on for this repository. Add one and press "
                + "Create tag again, or create the lightweight tag without signing.";
            return;
        }
        await ChooseTagSigningAsync(SigningChoice.KeepSigning);
    }

    /// <summary>
    /// Creates the tag with `tag.gpgsign=false` for this run and the ones after it in this
    /// project. Confirmed separately from the Create click: the tag comes out unsigned and nothing
    /// in this app re-signs it.
    /// </summary>
    [RelayCommand]
    private async Task CreateTagUnsigned()
    {
        var repo = RepoPath;
        var annotated = _tagSigningRetryAnnotated;
        if (!await ConfirmAsync(
                "Create the tag without signing?",
                "This repository is configured to sign its tags. Creating this one without signing leaves it — and "
                + (annotated
                    ? "the tags after it, until you leave this project — unsigned.\n\n"
                    : "the tags after it, until you leave this project — unsigned. Without a message it comes out "
                    + "lightweight, which is a ref and no tag object at all.\n\n")
                + "Nothing in this app re-signs them; re-signing afterwards is a manual git job. The repository's "
                + "configuration is not changed.",
                "Create unsigned"))
            return;
        // The dialog is a window the reader can hold open across a project switch; the answer then
        // authorises a tag in a repository the question never named.
        if (repo != RepoPath) return;
        await ChooseTagSigningAsync(SigningChoice.ProceedUnsigned);
    }

    private async Task ChooseTagSigningAsync(SigningChoice choice)
    {
        if (IsBusy) { TagsErrorText = BusyNotice("Create tag"); return; }
        var create = _tagSigningRetry;
        if (create is null || !_repoSignsTags) return;
        _tagSigningRetry = null;
        _tagSigning = choice;
        TagSigningOfferVisible = false;
        RefreshTagSigningChip();
        await create();
    }
}
