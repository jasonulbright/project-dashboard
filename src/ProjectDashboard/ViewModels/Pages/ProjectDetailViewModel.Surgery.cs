using System.ComponentModel;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// One commit inside a planned history edit. The list is oldest first — the order a rebase todo
/// uses. The three marks are mutually exclusive: a dropped commit's message is never written,
/// and a folded one's is discarded by the fold, so carrying a reword alongside either would
/// describe a commit the replay does not produce.
/// </summary>
public sealed partial class PlannedCommit : ObservableObject
{
    [ObservableProperty] private bool _drop;
    [ObservableProperty] private bool _squashIntoPrevious;
    [ObservableProperty] private string? _newMessage;

    public required string Sha { get; init; }

    public required string Subject { get; init; }

    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;

    /// <summary>The subject this commit carries after the plan is applied.</summary>
    public string EffectiveSubject => SurgeryText.FirstLine(NewMessage) ?? Subject;

    public string MarkLabel =>
        Drop ? "drop" : SquashIntoPrevious ? "squash" : NewMessage is not null ? "reword" : "pick";

    partial void OnDropChanged(bool value)
    {
        if (value)
        {
            SquashIntoPrevious = false;
            NewMessage = null;
        }
        OnPropertyChanged(nameof(MarkLabel));
    }

    partial void OnSquashIntoPreviousChanged(bool value)
    {
        if (value)
        {
            Drop = false;
            NewMessage = null;
        }
        OnPropertyChanged(nameof(MarkLabel));
    }

    partial void OnNewMessageChanged(string? value)
    {
        if (value is not null)
        {
            Drop = false;
            SquashIntoPrevious = false;
        }
        OnPropertyChanged(nameof(MarkLabel));
        OnPropertyChanged(nameof(EffectiveSubject));
    }
}

/// <summary>How much of the range one plan changes, in the terms a confirmation states.</summary>
public sealed record HistoryPlanScope(int Dropped, int Squashed, int Reworded, bool Reordered)
{
    public bool HasChange => Dropped > 0 || Squashed > 0 || Reworded > 0 || Reordered;

    /// <summary>Every kind of change the plan carries, or "no change" — never a partial account of it.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Dropped > 0) parts.Add($"{Dropped} commit(s) dropped");
            if (Squashed > 0) parts.Add($"{Squashed} commit(s) squashed");
            if (Reworded > 0) parts.Add($"{Reworded} commit(s) reworded");
            if (Reordered) parts.Add("order changed");
            return parts.Count == 0 ? "no change" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// What a plan resolves to: the combined todo one gated apply runs, the history that apply
/// produces, and how much of the range it changes. <see cref="Preview"/> is compiled from the
/// same walk that built <see cref="Todo"/>, so it cannot describe a history the apply would not.
/// </summary>
public sealed record HistoryPlanResolution(
    RebaseTodo? Todo, IReadOnlyList<string> Preview, HistoryPlanScope Scope, string? Refusal)
{
    public bool IsValid => Refusal is null && Todo is not null;

    public static HistoryPlanResolution Refused(string reason) =>
        new(null, [], new HistoryPlanScope(0, 0, 0, false), reason);
}

/// <summary>
/// The pure part of history planning: moves and marks in, one combined rebase todo out.
///
/// A single apply carries every kind of change at once — reordered rows, drops, folds and
/// rewords — because they are positions and actions in one todo rather than separate
/// operations. Whatever a replay cannot express is refused by the compiler before an apply
/// exists, and the preview is that compiler's own account of the result.
/// </summary>
public static class HistoryPlan
{
    /// <summary>Swaps an entry with the one before it. False when the index is out of range or already first.</summary>
    public static bool MoveUp(IList<PlannedCommit> commits, int index)
    {
        if (index < 1 || index >= commits.Count) return false;
        (commits[index - 1], commits[index]) = (commits[index], commits[index - 1]);
        return true;
    }

    /// <summary>Swaps an entry with the one after it. False when the index is out of range or already last.</summary>
    public static bool MoveDown(IList<PlannedCommit> commits, int index)
    {
        if (index < 0 || index >= commits.Count - 1) return false;
        (commits[index], commits[index + 1]) = (commits[index + 1], commits[index]);
        return true;
    }

    /// <summary>The combined todo the plan describes: every row in its planned position, with its mark.</summary>
    public static RebaseTodo BuildTodo(IReadOnlyList<PlannedCommit> planned) =>
        new()
        {
            Steps = planned.Select(p => new RebaseStep(
                p.Sha,
                p.Drop ? RebaseStepAction.Drop
                    : p.SquashIntoPrevious ? RebaseStepAction.Fixup
                    : RebaseStepAction.Pick,
                p.Drop || p.SquashIntoPrevious ? null : p.NewMessage)).ToList()
        };

    /// <summary>
    /// The history the plan produces, oldest first, as display lines — the compiler's own
    /// account of the replay, not a second reading of the marks. Empty for a plan no replay can
    /// express, because there is then no history to show.
    /// </summary>
    public static IReadOnlyList<string> Preview(IReadOnlyList<PlannedCommit> planned) =>
        Compile(planned).Result.Select(c => c.Line).ToList();

    /// <summary>
    /// Resolves marks and moves against the range they were planned on. <paramref name="originalOrder"/>
    /// is the scope's own order, so a plan that lost or gained a commit is refused rather than
    /// handed to a driver that would reject a partial permutation with a less specific message.
    ///
    /// The two refusals stated here are the ones this surface can advise on — the range it was
    /// built from, and the reset that replaces an emptied branch. Everything else is the
    /// compiler's own refusal, so the dialog and the service name a contradiction identically.
    /// </summary>
    public static HistoryPlanResolution Resolve(IReadOnlyList<PlannedCommit> planned, IReadOnlyList<string> originalOrder)
    {
        if (planned.Count != originalOrder.Count ||
            !planned.Select(p => p.Sha).OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(originalOrder.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            return HistoryPlanResolution.Refused(
                "The plan no longer matches the commits it was built from — reload the history and plan again.");

        if (planned.Count == 0)
            return HistoryPlanResolution.Refused(NothingToApply);

        if (planned[0].SquashIntoPrevious)
            return HistoryPlanResolution.Refused(
                "The first commit in the plan has nothing before it to squash into.");

        var dropped = planned.Count(p => p.Drop);
        if (dropped > 0 && dropped == planned.Count)
            return HistoryPlanResolution.Refused(
                "Dropping every commit in the range would empty the branch — use a reset to the commit " +
                "before the range instead.");

        var compiled = Compile(planned);
        if (!compiled.IsValid) return HistoryPlanResolution.Refused(compiled.Refusal!);

        var scope = new HistoryPlanScope(
            dropped,
            planned.Count(p => p.SquashIntoPrevious),
            planned.Count(p => !p.Drop && !p.SquashIntoPrevious && p.NewMessage is not null),
            !planned.Select(p => p.Sha).SequenceEqual(originalOrder, StringComparer.OrdinalIgnoreCase));

        var preview = compiled.Result.Select(c => c.Line).ToList();
        if (!scope.HasChange)
            return new HistoryPlanResolution(null, preview, scope, NothingToApply);

        return new HistoryPlanResolution(BuildTodo(planned), preview, scope, null);
    }

    private const string NothingToApply = "Nothing to apply — move, drop, squash, or reword a commit first.";

    private static RebaseTodoCompilation Compile(IReadOnlyList<PlannedCommit> planned) =>
        RebaseTodoCompiler.Compile(
            BuildTodo(planned),
            planned.Select(p => new RebaseCommit(p.Sha, p.Subject)).ToList());
}

/// <summary>What one surgery asks the user to confirm.</summary>
public sealed record SurgeryConfirmation(string Title, string Message, string ConfirmLabel);

/// <summary>
/// Commit surgery on the History tab: per-commit reword, squash, drop, reset, revert,
/// cherry-pick and staged-change injection, plus combined multi-commit planning.
///
/// Everything destructive goes through <see cref="SurgeryCoordinator"/>, which owns the busy
/// lease, the tree gate, the backup and the journal. Preconditions this layer can see are
/// disabled state with a reason, not a failure; the ones only the service can see arrive as
/// its own message and are shown as it wrote them, because they name the offending files and
/// the conflicting commit. Conflicts are never resolved in-app: the default policy aborts, and
/// the retry offer hands the repository to a terminal instead.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>
    /// Gated entry point for commit surgery, supplied by composition. Null leaves every
    /// surgery command disabled with a reason rather than throwing at click time.
    /// </summary>
    public SurgeryCoordinator? Surgery
    {
        get => _surgery;
        set
        {
            _surgery = value;
            RaiseSurgeryCanExecuteChanged();
        }
    }

    private SurgeryCoordinator? _surgery;

    [ObservableProperty] private string _surgeryStatusText = "";
    [ObservableProperty] private string _surgeryFailureText = "";
    [ObservableProperty] private bool _surgeryUndoVisible;
    [ObservableProperty] private string _surgeryUndoLabel = "";
    [ObservableProperty] private bool _surgeryLeaveStoppedOfferVisible;
    [ObservableProperty] private bool _surgeryStashOfferVisible;

    // The undo and the conflict retry are bound to the repository they were produced for, never
    // to the live RepoPath: a project switch while the offer is on screen must not replay a
    // restore, or a rebase, against a repository the click never described.
    private UndoHandle? _surgeryUndo;
    private string _surgeryUndoRepo = "";
    private string _surgeryUndoOperation = "";
    private Func<RebaseConflictPolicy, Task<SurgeryResult>>? _surgeryRetry;
    private string _surgeryRetryLabel = "";
    private string _surgeryRetryRepo = "";

    /// <summary>Confirm seam: replaced where no window can be shown.</summary>
    internal Func<SurgeryConfirmation, Task<bool>> ConfirmSurgeryAsync { get; set; }

    /// <summary>Message-entry seam: title, prompt, initial text → the text, or null when cancelled.</summary>
    internal Func<string, string, string, Task<string?>> PromptForCommitMessageAsync { get; set; } =
        Views.Windows.CommitMessagePromptWindow.ShowAsync;

    /// <summary>History-planning seam: the range to plan on → the accepted plan, or null when cancelled.</summary>
    internal Func<IReadOnlyList<PlannedCommit>, Task<IReadOnlyList<PlannedCommit>?>> ShowHistoryPlanAsync { get; set; } =
        Views.Windows.HistoryPlanWindow.ShowAsync;

    // ── Preconditions ───────────────────────────────────────────────────────

    private bool RepoIdle =>
        Surgery is not null && !IsBusy && RepoPath.Length > 0 && WorkingState is { Activity: RepoActivity.None };

    private bool TreeIsClean => WorkingState is { IsDirty: false };

    private bool HasStagedChanges => WorkingState is not null && WorkingState.Staged.Any();

    private bool HasUnstagedChanges => WorkingState is not null && WorkingState.Unstaged.Any();

    private int SelectedCommitIndex => SelectedCommit is null ? -1 : Commits.IndexOf(SelectedCommit);

    /// <summary>
    /// How many commits back from HEAD the selection sits, counted so the range includes it.
    /// Zero when the selection is not in the loaded list, which no editable range can cover.
    /// </summary>
    private int DepthOfSelected => SelectedCommitIndex + 1;

    private bool CanEditPastCommits() => RepoIdle && DepthOfSelected > 0 && TreeIsClean;

    private bool CanSquashSelectedIntoPrevious() =>
        CanEditPastCommits() && SelectedCommitIndex + 1 < Commits.Count;

    private bool CanResetToSelectedCommit() => RepoIdle && DepthOfSelected > 0;

    private bool CanResetHardToSelectedCommit() => CanResetToSelectedCommit() && TreeIsClean;

    private bool CanAmendStagedIntoSelectedCommit() =>
        RepoIdle && DepthOfSelected > 0 && HasStagedChanges && !HasUnstagedChanges;

    private bool CanUndoLastSurgery() => _surgeryUndo is not null && !IsBusy && _surgeryUndoRepo == RepoPath;

    private bool CanStashBeforeSurgery() => !IsBusy && RepoPath.Length > 0 && WorkingState is { IsDirty: true };

    /// <summary>
    /// What blocks any commit operation, whatever the working tree looks like, or null.
    /// Null rather than "" so a bound tooltip is absent instead of an empty popup.
    /// </summary>
    public string? ResetBlockedReason =>
        Surgery is null ? "Commit surgery is unavailable — the service was not supplied."
        : RepoPath.Length == 0 ? "No project is open."
        : IsBusy ? "Another git operation is running."
        : WorkingState is null ? "Reading the repository…"
        : WorkingState.Activity != RepoActivity.None
            ? "The repository is in the middle of another operation — finish or abort it in a terminal first."
        : DepthOfSelected < 1 ? "Select a commit in the list first."
        : null;

    /// <summary>Why the commands that rebase are unavailable, or null when they are not.</summary>
    public string? SurgeryBlockedReason =>
        ResetBlockedReason ?? (TreeIsClean
            ? null
            : $"{WorkingState!.Files.Count} uncommitted change(s) block a rebase: {SurgeryDirtyFileList}. " +
              "Stash or commit them first.");

    /// <summary>
    /// Why squashing the selection into the commit before it is unavailable, or null. The list
    /// is a truncated read of the branch, so its last row has no loaded predecessor to fold into
    /// even where the branch carries more history behind it.
    /// </summary>
    public string? SquashIntoPreviousBlockedReason =>
        SurgeryBlockedReason
        ?? (SelectedCommitIndex + 1 >= Commits.Count
            ? "Only the loaded history can be squashed — this is the oldest commit shown."
            : null);

    /// <summary>Why folding the staged changes into a commit is unavailable, or null when it is not.</summary>
    public string? AmendIntoCommitBlockedReason =>
        ResetBlockedReason
        ?? (!HasStagedChanges ? "Nothing is staged — stage the fix first." : null)
        ?? (HasUnstagedChanges
            ? $"{WorkingState!.Unstaged.Count()} unstaged change(s) would make git refuse the rebase — stage or stash them first."
            : null);

    private string SurgeryDirtyFileList
    {
        get
        {
            var files = WorkingState?.Files ?? [];
            var named = files.Take(10).Select(f => f.Path).ToList();
            var listed = string.Join(", ", named);
            if (files.Count > named.Count) listed += $", … (+{files.Count - named.Count} more)";
            return listed;
        }
    }

    /// <summary>
    /// Command availability and the disabled-state reasons depend on state owned by the other
    /// partials, which cannot carry this partial's notification attributes. Dispatched from the
    /// class's single OnPropertyChanged override.
    /// </summary>
    private void HandleSurgeryPropertyChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Project):
                ResetSurgeryState();
                RaiseSurgeryCanExecuteChanged();
                break;
            case nameof(IsBusy):
            case nameof(SelectedCommit):
            case nameof(WorkingState):
            case nameof(Commits):
                RaiseSurgeryCanExecuteChanged();
                break;
        }
    }

    private void RaiseSurgeryCanExecuteChanged()
    {
        RewordSelectedCommitCommand.NotifyCanExecuteChanged();
        SquashSelectedIntoPreviousCommand.NotifyCanExecuteChanged();
        DropSelectedCommitCommand.NotifyCanExecuteChanged();
        ResetSoftToSelectedCommitCommand.NotifyCanExecuteChanged();
        ResetMixedToSelectedCommitCommand.NotifyCanExecuteChanged();
        ResetHardToSelectedCommitCommand.NotifyCanExecuteChanged();
        RevertSelectedCommitCommand.NotifyCanExecuteChanged();
        CherryPickSelectedCommitCommand.NotifyCanExecuteChanged();
        AmendStagedIntoSelectedCommitCommand.NotifyCanExecuteChanged();
        PlanHistoryEditCommand.NotifyCanExecuteChanged();
        UndoLastSurgeryCommand.NotifyCanExecuteChanged();
        StashBeforeSurgeryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ResetBlockedReason));
        OnPropertyChanged(nameof(SurgeryBlockedReason));
        OnPropertyChanged(nameof(SquashIntoPreviousBlockedReason));
        OnPropertyChanged(nameof(AmendIntoCommitBlockedReason));
    }

    /// <summary>
    /// Clears every offer that belongs to the previous project. An undo left on screen across a
    /// switch would restore a repository the reader is no longer looking at.
    /// </summary>
    private void ResetSurgeryState()
    {
        SurgeryStatusText = "";
        SurgeryFailureText = "";
        SurgeryUndoVisible = false;
        SurgeryUndoLabel = "";
        SurgeryLeaveStoppedOfferVisible = false;
        SurgeryStashOfferVisible = false;
        _surgeryUndo = null;
        _surgeryUndoRepo = "";
        _surgeryUndoOperation = "";
        _surgeryRetry = null;
        _surgeryRetryLabel = "";
        _surgeryRetryRepo = "";
    }

    // ── Per-commit operations ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanEditPastCommits))]
    private async Task RewordSelectedCommit()
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var depth = DepthOfSelected;
        var repo = RepoPath;
        var gen = _generation;
        if (surgery is null || commit is null || depth < 1) return;

        var message = await PromptForCommitMessageAsync(
            "Reword commit", $"New message for {commit.ShortHash}", commit.Message);
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Reword this commit?",
                $"Replace the message of {commit.ShortHash} — “{commit.Message}” — with “{FirstLine(message)}”?\n\n" +
                $"{commit.ShortHash} and the {depth - 1} commit(s) after it are rewritten with new ids.",
                "Reword")))
            return;

        await RunSurgeryAsync($"Reword {commit.ShortHash}", repo, gen,
            policy => surgery.RewordAsync(repo, depth, commit.ShortHash, message, policy),
            RebaseConflictPolicy.AbortAndReport, retryable: true);
    }

    [RelayCommand(CanExecute = nameof(CanSquashSelectedIntoPrevious))]
    private async Task SquashSelectedIntoPrevious()
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var index = SelectedCommitIndex;
        var repo = RepoPath;
        var gen = _generation;
        if (surgery is null || commit is null || index < 0 || index + 1 >= Commits.Count) return;

        var previous = Commits[index + 1];
        var depth = index + 2;

        var message = await PromptForCommitMessageAsync(
            "Squash into previous commit",
            $"Message for the combined commit ({previous.ShortHash} + {commit.ShortHash})",
            $"{previous.Message}\n\n{commit.Message}");
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Squash into the previous commit?",
                $"Fold {commit.ShortHash} — “{commit.Message}” — into {previous.ShortHash} — “{previous.Message}”?\n\n" +
                $"The two become one commit and the {index} commit(s) after them are rewritten with new ids.",
                "Squash")))
            return;

        await RunSurgeryAsync($"Squash {commit.ShortHash} into {previous.ShortHash}", repo, gen,
            policy => surgery.SquashAsync(repo, depth, [previous.ShortHash, commit.ShortHash], message, policy),
            RebaseConflictPolicy.AbortAndReport, retryable: true);
    }

    [RelayCommand(CanExecute = nameof(CanEditPastCommits))]
    private async Task DropSelectedCommit()
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var depth = DepthOfSelected;
        var repo = RepoPath;
        var gen = _generation;
        if (surgery is null || commit is null || depth < 1) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Drop this commit?",
                $"Remove {commit.ShortHash} — “{commit.Message}” — from {BranchDescription}?\n\n" +
                $"Its changes leave the branch and the {depth - 1} commit(s) after it are rewritten with new ids. " +
                "A backup is taken first, and Undo restores it.",
                "Drop")))
            return;

        await RunSurgeryAsync($"Drop {commit.ShortHash}", repo, gen,
            policy => surgery.DropAsync(repo, depth, [commit.ShortHash], policy),
            RebaseConflictPolicy.AbortAndReport, retryable: true);
    }

    [RelayCommand(CanExecute = nameof(CanResetToSelectedCommit))]
    private Task ResetSoftToSelectedCommit() => ResetToSelectedCommitAsync(ResetMode.Soft);

    [RelayCommand(CanExecute = nameof(CanResetToSelectedCommit))]
    private Task ResetMixedToSelectedCommit() => ResetToSelectedCommitAsync(ResetMode.Mixed);

    [RelayCommand(CanExecute = nameof(CanResetHardToSelectedCommit))]
    private Task ResetHardToSelectedCommit() => ResetToSelectedCommitAsync(ResetMode.Hard);

    internal async Task ResetToSelectedCommitAsync(ResetMode mode)
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var index = SelectedCommitIndex;
        var repo = RepoPath;
        var gen = _generation;
        if (surgery is null || commit is null || index < 0) return;

        var effect = mode switch
        {
            ResetMode.Soft => "Their changes stay staged, ready to be recommitted.",
            ResetMode.Mixed => "Their changes stay in the working tree, unstaged.",
            _ => "Their changes are DELETED from the working tree as well."
        };
        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                $"{mode} reset to this commit?",
                $"Move {BranchDescription} back to {commit.ShortHash} — “{commit.Message}”?\n\n" +
                $"The {index} commit(s) after it leave the branch. {effect}",
                $"{mode} reset")))
            return;

        await RunSurgeryAsync($"{mode} reset to {commit.ShortHash}", repo, gen,
            _ => surgery.ResetAsync(repo, commit.ShortHash, mode),
            RebaseConflictPolicy.AbortAndReport, retryable: false);
    }

    [RelayCommand(CanExecute = nameof(CanEditPastCommits))]
    private async Task RevertSelectedCommit()
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var repo = RepoPath;
        var gen = _generation;
        if (surgery is null || commit is null) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Revert this commit?",
                $"Add a new commit on {BranchDescription} that undoes {commit.ShortHash} — “{commit.Message}”?\n\n" +
                "Existing history is not rewritten. A conflicting revert stops and is left for a terminal.",
                "Revert")))
            return;

        await RunSurgeryAsync($"Revert {commit.ShortHash}", repo, gen,
            _ => surgery.RevertAsync(repo, commit.ShortHash),
            RebaseConflictPolicy.AbortAndReport, retryable: false);
    }

    [RelayCommand(CanExecute = nameof(CanEditPastCommits))]
    private async Task CherryPickSelectedCommit()
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var repo = RepoPath;
        var gen = _generation;
        if (surgery is null || commit is null) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Cherry-pick this commit?",
                $"Replay {commit.ShortHash} — “{commit.Message}” — onto {BranchDescription} as a new commit?\n\n" +
                "Existing history is not rewritten. A conflicting pick stops and is left for a terminal.",
                "Cherry-pick")))
            return;

        await RunSurgeryAsync($"Cherry-pick {commit.ShortHash}", repo, gen,
            _ => surgery.CherryPickAsync(repo, [commit.ShortHash]),
            RebaseConflictPolicy.AbortAndReport, retryable: false);
    }

    [RelayCommand(CanExecute = nameof(CanAmendStagedIntoSelectedCommit))]
    private async Task AmendStagedIntoSelectedCommit()
    {
        var surgery = Surgery;
        var commit = SelectedCommit;
        var index = SelectedCommitIndex;
        var repo = RepoPath;
        var gen = _generation;
        var staged = WorkingState?.Staged.Count() ?? 0;
        if (surgery is null || commit is null || index < 0) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Fold the staged changes into this commit?",
                $"Add the {staged} staged change(s) to {commit.ShortHash} — “{commit.Message}”?\n\n" +
                $"That commit and the {index} commit(s) after it are rewritten with new ids.",
                "Fold in")))
            return;

        await RunSurgeryAsync($"Fold staged changes into {commit.ShortHash}", repo, gen,
            policy => surgery.InjectStagedIntoAsync(repo, commit.ShortHash, policy),
            RebaseConflictPolicy.AbortAndReport, retryable: true);
    }

    // ── Multi-commit planning ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanEditPastCommits))]
    private async Task PlanHistoryEdit()
    {
        var surgery = Surgery;
        var depth = DepthOfSelected;
        var repo = RepoPath;
        if (surgery is null || depth < 1) return;

        var gen = _generation;
        // The failure text and the two offers that explain it are one message; clearing the text
        // alone would leave a Retry or a Stash button standing with nothing on screen naming it.
        SurgeryFailureText = "";
        SurgeryLeaveStoppedOfferVisible = false;
        SurgeryStashOfferVisible = false;
        SurgeryStatusText = "Reading the editable range…";

        RebaseScope scope;
        try
        {
            scope = await surgery.LoadScopeAsync(repo, depth);
        }
        catch (Exception ex)
        {
            // A merge inside the range throws here; the message names the commit and says to narrow it.
            if (!IsCurrent(gen)) return;
            SurgeryStatusText = "The range is not editable.";
            SurgeryFailureText = ex.Message;
            return;
        }
        if (!IsCurrent(gen)) return;
        SurgeryStatusText = "";

        var planned = scope.Commits.Select(c => new PlannedCommit { Sha = c.Sha, Subject = c.Subject }).ToList();
        var originalOrder = planned.Select(p => p.Sha).ToList();

        var accepted = await ShowHistoryPlanAsync(planned);
        if (accepted is null || !IsCurrent(gen)) return;

        var resolution = HistoryPlan.Resolve(accepted, originalOrder);
        if (!resolution.IsValid)
        {
            SurgeryStatusText = "Nothing applied.";
            SurgeryFailureText = resolution.Refusal ?? "";
            return;
        }

        var summary = resolution.Scope.Summary;
        var preview = string.Join("\n", resolution.Preview);
        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Apply this plan?",
                $"Rewrite the last {depth} commit(s) of {BranchDescription} — {summary} — to this, oldest first:\n\n{preview}\n\n" +
                "This is what git is asked to replay, not a promise: a conflict can still stop the rebase, " +
                "and it is aborted by default.",
                "Apply")))
            return;

        var todo = resolution.Todo!;
        await RunSurgeryAsync($"Apply plan ({summary})", repo, gen,
            policy => surgery.RunPlanAsync(repo, depth, todo, policy),
            RebaseConflictPolicy.AbortAndReport, retryable: true);
    }

    // ── Conflict handling, undo, and the stash offer ────────────────────────

    [RelayCommand]
    private async Task RetrySurgeryLeavingItStopped()
    {
        var operate = _surgeryRetry;
        var label = _surgeryRetryLabel;
        var repo = _surgeryRetryRepo;
        var gen = _generation;
        if (operate is null || IsBusy || repo.Length == 0 || repo != RepoPath) return;

        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Retry and stop at the conflict?",
                $"Run “{label}” again and leave the rebase stopped at the first conflict?\n\n" +
                "The repository stays mid-rebase. There is no conflict editor here: finish it with " +
                "Open in Terminal, or abort it there. Undo restores the backup either way.",
                "Retry and stop")))
            return;
        if (!IsCurrent(gen)) return;

        _surgeryRetry = null;
        SurgeryLeaveStoppedOfferVisible = false;
        await RunSurgeryAsync($"{label}, stopping at the conflict", repo, gen, operate,
            RebaseConflictPolicy.LeaveStopped, retryable: false);
    }

    [RelayCommand(CanExecute = nameof(CanUndoLastSurgery))]
    private async Task UndoLastSurgery()
    {
        var undo = _surgeryUndo;
        var repo = _surgeryUndoRepo;
        var operation = _surgeryUndoOperation;
        var gen = _generation;
        if (undo is null || IsBusy || repo.Length == 0 || repo != RepoPath) return;

        var dirty = WorkingState is { IsDirty: true } ? WorkingState.Files.Count : 0;
        // The count is what the tree holds now; the restore reports its own, which also covers
        // what the restored history no longer matches.
        var warning = dirty > 0
            ? $"The working tree holds {dirty} uncommitted change(s) right now. Restoring ends in a hard reset and discards them."
            : "Restoring ends in a hard reset, so any uncommitted change is discarded.";
        if (!await ConfirmSurgeryAsync(new SurgeryConfirmation(
                "Undo the last history edit?",
                $"Restore this repository to the backup taken before “{operation}”?\n\n{warning}",
                "Restore")))
            return;
        // The dirty count above describes the repository this handle restores. A switch while the
        // confirm was open means that count is on screen for nothing, so the restore is dropped.
        if (!IsCurrent(gen)) return;

        IsBusy = true;
        SurgeryStatusText = "Restoring the backup…";
        try
        {
            var result = await undo.RestoreAsync();
            if (!IsCurrent(gen)) return;

            if (result.Success)
            {
                // The restore undid the operation the recovery marker describes, so the marker goes
                // with it: left behind it reports an interrupted rewrite at the next launch. The
                // marker write is not part of the restore's outcome — a throw here costs one stale
                // recovery prompt, while reporting it as a failed restore would deny a repository
                // that is already back at its backup and leave its spent undo offer standing.
                if (Surgery is not null)
                {
                    try
                    {
                        await Surgery.ConcludeUndoAsync(repo);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"undo of '{operation}' restored {repo} but could not clear the recovery marker", ex);
                    }
                }
                SurgeryStatusText = result.WorktreeWasDirty
                    ? $"Restored — {result.DiscardedChangeCount} uncommitted change(s) were discarded."
                    : "Restored.";
                SurgeryFailureText = "";
                SurgeryLeaveStoppedOfferVisible = false;
                SurgeryStashOfferVisible = false;
                _surgeryUndo = null;
                _surgeryRetry = null;
                SurgeryUndoVisible = false;
            }
            else
            {
                SurgeryStatusText = "Restore failed.";
                SurgeryFailureText = result.Message;
            }

            await RefreshWorkingStateAsync();
            await ReloadCommitsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"undo of '{operation}' failed for {repo}", ex);
            if (IsCurrent(gen))
            {
                SurgeryStatusText = "Restore failed.";
                SurgeryFailureText = ex.Message;
            }
        }
        finally
        {
            if (IsCurrent(gen)) IsBusy = false;
        }
    }

    [RelayCommand]
    private void DismissSurgeryUndo()
    {
        _surgeryUndo = null;
        SurgeryUndoVisible = false;
        UndoLastSurgeryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStashBeforeSurgery))]
    private async Task StashBeforeSurgery()
    {
        if (IsBusy || RepoPath.Length == 0) return;
        var gen = _generation;
        var repo = RepoPath;
        var ok = await RunOp(
            r => _gitService.StashPushAsync(r, "before history edit", includeUntracked: true),
            "Stash changes", repo, gen);
        if (!IsCurrent(gen)) return;
        if (!ok)
        {
            // The stash reports into the sync pane; the offer that asked for it stands in this
            // one, so its failure is restated here or the click reads as having done nothing.
            SurgeryStatusText = "Stash failed.";
            SurgeryFailureText = SyncStatusText;
            return;
        }
        SurgeryStashOfferVisible = false;
        SurgeryFailureText = "";
        await LoadStashes();
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs one gated surgery through the generation-owned busy gate the other tabs use, then
    /// republishes the outcome: the service's own failure text, the undo it hands back on
    /// success and failure alike, and the retry offer when a stopped rebase was aborted.
    ///
    /// <paramref name="repo"/> and <paramref name="gen"/> are the caller's, read before the
    /// prompt and the confirm — neither is re-read here. A confirm does not block input, so
    /// reading either afterwards would attribute this run, its undo and its retry to whichever
    /// project the user switched to while the dialog was open. A generation that moved during
    /// the confirm means the surface that asked for this is gone, so nothing runs.
    /// </summary>
    private async Task<bool> RunSurgeryAsync(
        string label,
        string repo,
        int gen,
        Func<RebaseConflictPolicy, Task<SurgeryResult>> operate,
        RebaseConflictPolicy policy,
        bool retryable)
    {
        if (IsBusy || !IsCurrent(gen) || repo.Length == 0) return false;
        IsBusy = true;
        SurgeryStatusText = $"{label}…";
        SurgeryFailureText = "";
        SurgeryLeaveStoppedOfferVisible = false;
        SurgeryStashOfferVisible = false;
        try
        {
            var result = await operate(policy);
            if (!IsCurrent(gen))
            {
                // The op still ran against the repo it was bound to; refresh only when that
                // repo is the one back on screen, exactly as the git ops do.
                if (repo == RepoPath) await SafeRefreshWorkingStateAsync();
                return false;
            }

            PublishSurgeryResult(label, repo, result, operate, retryable);
            await RefreshWorkingStateAsync();
            await ReloadCommitsAsync();

            // Only the clean-tree gate is fixed by stashing. The unstaged-changes gate guards an
            // operation whose input is the staged changes, and the stash below takes those with
            // it, so the offer there would remove what the operation runs on. Decided against the
            // freshly read state, because the state the gate refused may be newer than the one
            // the command was enabled on.
            SurgeryStashOfferVisible =
                result.Refusal == SurgeryRefusal.UncommittedChanges && WorkingState is { IsDirty: true };
            return result.Success;
        }
        catch (Exception ex)
        {
            Log.Warn($"{label} failed for {repo}", ex);
            if (IsCurrent(gen))
            {
                SurgeryStatusText = $"{label} failed.";
                SurgeryFailureText = ex.Message;
            }
            return false;
        }
        finally
        {
            if (IsCurrent(gen)) IsBusy = false;
        }
    }

    private void PublishSurgeryResult(
        string label,
        string repo,
        SurgeryResult result,
        Func<RebaseConflictPolicy, Task<SurgeryResult>> operate,
        bool retryable)
    {
        // Every gate refusal returns before the backup step, so a refusal carries no undo at all.
        // Where one is handed back, restoring it ends in a hard reset, so offering it for an
        // outcome that proves nothing moved can only discard uncommitted work. The service's own
        // claim is the discriminator: a failure it could not classify carries neither git-level
        // result and is exactly the case the undo exists for.
        if (result.Undo is not null && !result.RepositoryUntouched)
        {
            _surgeryUndo = result.Undo;
            _surgeryUndoRepo = repo;
            _surgeryUndoOperation = label;
            SurgeryUndoLabel = $"Undo “{label}”";
            SurgeryUndoVisible = true;
        }
        UndoLastSurgeryCommand.NotifyCanExecuteChanged();

        if (result.Success)
        {
            SurgeryStatusText = result.Advisory is null ? $"{label} done." : $"{label} done — {result.Advisory}";
            SurgeryFailureText = "";
            return;
        }

        SurgeryStatusText = $"{label} failed.";
        SurgeryFailureText = result.FailureReason ?? "The operation failed without reporting a reason.";

        // Only a rebase that stopped and was aborted has a conflict left to stop at on a retry.
        if (retryable && result.Rebase is { Aborted: true })
        {
            _surgeryRetry = operate;
            _surgeryRetryLabel = label;
            _surgeryRetryRepo = repo;
            SurgeryLeaveStoppedOfferVisible = true;
        }
    }

    private string BranchDescription =>
        WorkingState is { Detached: false, Branch.Length: > 0 } state ? state.Branch : "this branch";

    private static string FirstLine(string text)
    {
        var line = SurgeryText.FirstLine(text) ?? text;
        return line.Length > 100 ? line[..100] + "…" : line;
    }
}
