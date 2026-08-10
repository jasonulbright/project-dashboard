using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The commit box's side of commit signing, on the same terms the history-editing surface
/// already runs on: a repository configured to sign is detected when it loads, the first commit
/// of the session asks which way to go, and nothing here turns signing off on its own — only a
/// <see cref="SigningChoice.ProceedUnsigned"/> the reader picked does, and only for the runs that
/// follow it.
///
/// The choice lives for as long as the repository is on screen and is written nowhere: a
/// persisted "never sign" would strip signatures the reader asked for in every later session
/// without saying so.
///
/// Detection is configuration, not verification. Nothing here probes the signing agent or claims
/// a commit will stall; what it states is that the repository signs, and that an uncached
/// passphrase can leave git waiting on a prompt this app cannot show.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Await seam for the load-time read, like <see cref="WorkingStateRefresh"/>.</summary>
    internal Task CommitSigningRefresh { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _commitSigningChipVisible;
    [ObservableProperty] private string _commitSigningChipText = "";
    [ObservableProperty] private string _commitSigningChipTooltip = "";
    [ObservableProperty] private bool _commitSigningOfferVisible;
    [ObservableProperty] private string _commitSigningOfferText = "";

    private bool _repoSignsCommits;
    private string _repoSigningFormat = "";
    private SigningChoice _commitSigning = SigningChoice.NotChosen;

    /// <summary>
    /// Cleared on a project switch and on nothing else. The choice is per repository, so a
    /// reload of the same one keeps the answer the reader already gave.
    /// </summary>
    private void ResetCommitSigningState()
    {
        _repoSignsCommits = false;
        _repoSigningFormat = "";
        _commitSigning = SigningChoice.NotChosen;
        CommitSigningChipVisible = false;
        CommitSigningChipText = "";
        CommitSigningChipTooltip = "";
        CommitSigningOfferVisible = false;
        CommitSigningOfferText = "";
    }

    private async Task SafeRefreshCommitSigningAsync()
    {
        try { await RefreshCommitSigningAsync(); }
        catch (Exception ex) { Log.Warn("commit-signing read failed", ex); }
    }

    /// <summary>
    /// One `git config` read per repository load, off the commit path: a read taken while the
    /// reader waits on a commit is a read that made the commit slower for nothing.
    /// </summary>
    private async Task RefreshCommitSigningAsync()
    {
        var gen = _generation;
        var repo = RepoPath;
        if (repo.Length == 0) return;

        var signs = await _gitService.SignsCommitsAsync(repo);
        var format = signs ? await _gitService.GetSigningFormatAsync(repo) : "";
        if (!IsCurrent(gen) || repo != RepoPath) return;

        _repoSignsCommits = signs;
        _repoSigningFormat = format;
        CommitSigningChipVisible = signs;
        if (!signs)
        {
            CommitSigningOfferVisible = false;
            return;
        }
        RefreshCommitSigningChip();
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
}
