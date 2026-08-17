using System.Diagnostics;
using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The conflict panel: a driver for the sequencer a repository is stopped in, not a merge editor.
///
/// What it does is list the unmerged paths with the shape git recorded for each, render the
/// stages read-only through the same diff renderer the rest of the page uses, record one side of
/// a conflict as the resolution, and continue or abort the merge, rebase, cherry-pick or revert.
/// Nothing here lets a merged buffer be typed into, and no path resolves a conflict on its own
/// initiative.
///
/// Every refusal names its reason and leaves Open in Terminal reachable, and Abort stays one
/// click away from every state this panel can produce — including a continue that failed. That is
/// what makes an in-app path safe to offer: from anywhere it can leave the reader, the terminal
/// route is unchanged and abandoning the sequence is immediate.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Null outside the app host; the panel then reads as unavailable rather than half-working.</summary>
    internal ConflictResolver? Conflicts { get; init; }

    /// <summary>Null outside the app host; a stopped rebase then reads as started elsewhere, which is abort-only.</summary>
    internal RebaseDriver? Rebase { get; init; }

    [ObservableProperty] private bool _conflictsVisible;

    partial void OnConflictsVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));

    [ObservableProperty] private ObservableCollection<ConflictFile> _conflictRows = [];

    [ObservableProperty] private ConflictFile? _selectedConflictRow;

    [ObservableProperty] private ObservableCollection<DiffLine> _conflictDiffLines = [];
    [ObservableProperty] private ObservableCollection<SideBySideRow> _conflictDiffRows = [];

    /// <summary>Why the preview pane is showing no diff, or empty when it is.</summary>
    [ObservableProperty] private string _conflictPreviewNote = "";

    [ObservableProperty] private ConflictComparison _conflictComparison = ConflictComparison.BaseToOurs;

    /// <summary>The message the commit a continue writes will carry. Seeded from what git prepared.</summary>
    [ObservableProperty] private string _conflictMessage = "";

    /// <summary>What git prepared, held so an untouched box continues the sequencer's own way.</summary>
    private string _conflictMessageAsPrepared = "";

    [ObservableProperty] private string _conflictStatusText = "";
    [ObservableProperty] private string _conflictErrorText = "";

    /// <summary>The activity this panel is driving, re-read after every operation.</summary>
    [ObservableProperty] private RepoActivity _conflictActivity = RepoActivity.None;

    /// <summary>Why Continue is not offered, or empty when it is.</summary>
    [ObservableProperty] private string _conflictContinueRefusal = "";

    /// <summary>Why Abort is not offered, or empty when it is.</summary>
    [ObservableProperty] private string _conflictAbortRefusal = "";

    /// <summary>Set once a read has finished and found no unmerged paths; the empty state must not show before that.</summary>
    [ObservableProperty] private bool _conflictRowsEmpty;

    /// <summary>The panel's reads, held so a caller can await them rather than poll what they write.</summary>
    internal Task ConflictsRefresh { get; private set; } = Task.CompletedTask;
    internal Task ConflictPreviewRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Where the rebase a repository is stopped in came from; re-read with the working state.</summary>
    private RebaseDriver.StoppedRebaseOrigin _rebaseOrigin = RebaseDriver.StoppedRebaseOrigin.NotStopped;

    /// <summary>
    /// Overridable so a test can put the panel in each rebase-origin state without a driver: the
    /// classification is filesystem-shaped, and how this surface behaves under it is the subject.
    /// Without a driver a stopped rebase reads as started elsewhere — the abort-only answer, which
    /// is the safe one to be wrong in.
    /// </summary>
    internal virtual RebaseDriver.StoppedRebaseOrigin InspectStoppedRebase(string repoPath) =>
        Rebase?.InspectStoppedRebase(repoPath) ?? RebaseDriver.StoppedRebaseOrigin.StartedElsewhere;

    // ── Refusals, one place ─────────────────────────────────────────────────

    internal const string GitlinkRefusal =
        "This is a submodule conflict — resolve it in a terminal.";

    internal const string NoContentRefusal =
        "Neither side has content to take — resolve in a terminal.";

    internal const string BisectRefusal =
        "Bisect is finished in a terminal.";

    internal const string ForeignRebaseRefusal =
        "This rebase was started outside this app, so continuing it needs a terminal. Abort is available here.";

    internal const string ReclaimedRebaseRefusal =
        "The commit messages this stopped rebase refers to have been cleaned up, so continuing would fail on a " +
        "missing file. Abort it here, or finish it in a terminal.";

    internal const string NoSequenceRefusal =
        "There is no merge, rebase, cherry-pick or revert in progress — stage the resolved files and commit.";

    internal const string BinaryPreviewNote =
        "Binary file — no preview. Take ours and take theirs still apply.";

    internal const string ResolverUnavailable =
        "Conflict resolution is unavailable in this window — use a terminal.";

    internal static string MarkerRefusal(string path, string marker) =>
        $"{path} still contains conflict markers ({marker}) — nothing was staged. " +
        "Finish resolving it, or open it in an editor.";

    internal static string UnresolvedRefusal(int count) =>
        $"{count} file(s) are still unresolved — resolve every one of them first.";

    // ── Opening and closing ─────────────────────────────────────────────────

    /// <summary>
    /// True where the panel has something to drive: an activity it knows, or unmerged paths with
    /// no activity at all. Bisect is neither, and a clean tree is neither.
    /// </summary>
    public bool ConflictPanelOffered =>
        Conflicts is not null && WorkingState is { } state &&
        (ConflictResolver.ContinueVerb(state.Activity) is not null || state.HasConflicts);

    /// <summary>
    /// Every read of the working state decides afresh whether the panel has anything to drive,
    /// including the read that finds the repository unreadable and writes null.
    /// </summary>
    partial void OnWorkingStateChanged(WorkingState? value) => OnPropertyChanged(nameof(ConflictPanelOffered));

    [RelayCommand]
    private async Task OpenConflicts()
    {
        if (RepoPath.Length == 0 || !SafetyOverlayHidden) return;
        if (Conflicts is null)
        {
            SyncStatusText = ResolverUnavailable;
            return;
        }

        ConflictStatusText = "";
        ConflictErrorText = "";
        SelectedConflictRow = null;
        ConflictsVisible = true;
        ConflictsRefresh = LoadConflictsAsync(seedMessage: true);
        await ConflictsRefresh;
    }

    [RelayCommand]
    private void CloseConflicts()
    {
        ConflictsVisible = false;
        ConflictRows = [];
        ConflictRowsEmpty = false;
        SelectedConflictRow = null;
        ClearConflictPreview();
        ConflictMessage = "";
        _conflictMessageAsPrepared = "";
        ConflictStatusText = "";
        ConflictErrorText = "";
        ConflictActivity = RepoActivity.None;
        ConflictContinueRefusal = "";
        ConflictAbortRefusal = "";
    }

    /// <summary>Drops the panel as the page leaves this repository; every path it lists is that repository's.</summary>
    private void CloseConflictsOnProjectSwitch()
    {
        if (ConflictsVisible) CloseConflicts();
        _rebaseOrigin = RebaseDriver.StoppedRebaseOrigin.NotStopped;
    }

    private void ClearConflictPreview()
    {
        ConflictDiffLines = [];
        ConflictPreviewNote = "";
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task RefreshConflicts() => ConflictsRefresh = LoadConflictsAsync(seedMessage: false);

    /// <summary>
    /// Rebuilds the list from the index and the working state together: the unmerged stages say
    /// what each path can be resolved with, and the porcelain code says what git calls the shape.
    /// The selection is carried across by path, so resolving one file does not throw the reader
    /// off the next.
    /// </summary>
    private async Task LoadConflictsAsync(bool seedMessage)
    {
        var gen = _generation;
        var repo = RepoPath;
        if (Conflicts is null || repo.Length == 0) return;

        await RefreshWorkingStateAsync();
        if (!IsCurrent(gen)) return;

        var activity = WorkingState?.Activity ?? RepoActivity.None;
        var read = await Conflicts.ReadUnmergedAsync(repo);
        if (!IsCurrent(gen)) return;

        if (read.Error is { Length: > 0 } error)
        {
            // The rows already on screen describe the repository as the last good read found it;
            // dropping them over a failed read would report conflicts as resolved that are not.
            ConflictErrorText = $"Could not read the unmerged files: {error}";
            return;
        }

        ConflictActivity = activity;
        var held = SelectedConflictRow?.Path;
        var rows = BuildConflictRows(WorkingState, read.ByPath);
        ConflictRows = new ObservableCollection<ConflictFile>(rows);
        ConflictRowsEmpty = rows.Count == 0;
        SelectedConflictRow = held is null
            ? rows.FirstOrDefault()
            : rows.FirstOrDefault(r => string.Equals(r.Path, held, StringComparison.Ordinal)) ?? rows.FirstOrDefault();

        if (seedMessage)
        {
            _conflictMessageAsPrepared = await Conflicts.ReadPreparedMessageAsync(repo, activity);
            if (!IsCurrent(gen)) return;
            ConflictMessage = _conflictMessageAsPrepared;
        }

        RefreshConflictGates();
    }

    /// <summary>
    /// One row per unmerged path, with the refusals that apply to it decided here rather than at
    /// the click: a button that runs and then declines reads as a broken button.
    /// </summary>
    internal static List<ConflictFile> BuildConflictRows(
        WorkingState? state, IReadOnlyDictionary<string, ConflictStages> stages)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in state?.Conflicted ?? [])
            codes[file.Path] = $"{file.IndexStatus}{file.WorktreeStatus}";

        var rows = new List<ConflictFile>();
        foreach (var (path, stage) in stages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var refusal = stage.IsGitlink ? GitlinkRefusal
                : !stage.HasOurs && !stage.HasTheirs ? NoContentRefusal
                : "";
            rows.Add(new ConflictFile
            {
                Path = path,
                Code = codes.TryGetValue(path, out var code) ? code : "",
                HasBase = stage.HasBase,
                HasOurs = stage.HasOurs,
                HasTheirs = stage.HasTheirs,
                IsGitlink = stage.IsGitlink,
                Refusal = refusal
            });
        }
        return rows;
    }

    /// <summary>
    /// Recomputes what the two sequencer buttons may do. Continue writes a commit and needs every
    /// path merged; abort needs only an activity to abandon, which is what keeps it reachable from
    /// the state a failed continue leaves.
    /// </summary>
    private void RefreshConflictGates()
    {
        var activity = ConflictActivity;
        var unresolved = ConflictRows.Count;
        var drivable = ConflictResolver.ContinueVerb(activity) is not null;

        ConflictAbortRefusal = drivable ? ""
            : activity == RepoActivity.Bisecting ? BisectRefusal
            : "There is nothing in progress to abort.";

        ConflictContinueRefusal =
            activity == RepoActivity.Bisecting ? BisectRefusal
            : !drivable ? NoSequenceRefusal
            : unresolved > 0 ? UnresolvedRefusal(unresolved)
            : activity != RepoActivity.Rebasing ? ""
            : _rebaseOrigin switch
            {
                RebaseDriver.StoppedRebaseOrigin.MessagesReclaimed => ReclaimedRebaseRefusal,
                RebaseDriver.StoppedRebaseOrigin.StartedElsewhere => ForeignRebaseRefusal,
                _ => ""
            };

        OnPropertyChanged(nameof(ConflictContinueOffered));
        OnPropertyChanged(nameof(ConflictAbortOffered));
    }

    public bool ConflictContinueOffered => ConflictContinueRefusal.Length == 0;
    public bool ConflictAbortOffered => ConflictAbortRefusal.Length == 0;

    public string ConflictActivityLabel => ConflictActivity switch
    {
        RepoActivity.Merging => "Merge in progress",
        RepoActivity.Rebasing => "Rebase in progress",
        RepoActivity.CherryPicking => "Cherry-pick in progress",
        RepoActivity.Reverting => "Revert in progress",
        RepoActivity.Bisecting => "Bisect in progress",
        _ => "Unresolved conflicts"
    };

    /// <summary>
    /// What "ours" and "theirs" name in the sequence running. During a rebase the two are the
    /// reverse of what a reader expects: ours is the history being replayed ONTO, and theirs is
    /// the commit being replayed.
    /// </summary>
    public string ConflictSidesNote => ConflictActivity switch
    {
        RepoActivity.Rebasing =>
            "During a rebase, ours is the branch being replayed onto and theirs is the commit being replayed.",
        RepoActivity.CherryPicking =>
            "During a cherry-pick, ours is the current branch and theirs is the commit being applied.",
        RepoActivity.Reverting =>
            "During a revert, ours is the current branch and theirs is the change being undone.",
        _ => "Ours is this branch; theirs is the incoming side."
    };

    partial void OnConflictActivityChanged(RepoActivity value)
    {
        OnPropertyChanged(nameof(ConflictActivityLabel));
        OnPropertyChanged(nameof(ConflictSidesNote));
    }

    // ── Preview ─────────────────────────────────────────────────────────────

    partial void OnSelectedConflictRowChanged(ConflictFile? value)
    {
        ClearConflictPreview();
        if (value is not null) ConflictPreviewRefresh = LoadConflictPreviewAsync(value, ConflictComparison);
    }

    partial void OnConflictComparisonChanged(ConflictComparison value)
    {
        if (SelectedConflictRow is not { } row) return;
        ClearConflictPreview();
        ConflictPreviewRefresh = LoadConflictPreviewAsync(row, value);
    }

    partial void OnConflictDiffLinesChanged(ObservableCollection<DiffLine> value) => RebuildConflictDiffRows();

    /// <summary>Built only for the mode that renders them, like every other diff pane on the page.</summary>
    internal void RebuildConflictDiffRows() =>
        ConflictDiffRows = DiffSideBySide
            ? new ObservableCollection<SideBySideRow>(SideBySideDiff.Build(ConflictDiffLines))
            : [];

    /// <summary>
    /// Renders one pair of stages, or says why it cannot. A side the index holds no stage for is
    /// not a failed read: the file is absent on that side, and the other side's content is what
    /// there is to show.
    /// </summary>
    private async Task LoadConflictPreviewAsync(ConflictFile file, ConflictComparison comparison)
    {
        var gen = _generation;
        var repo = RepoPath;
        if (Conflicts is not { } resolver || repo.Length == 0) return;

        if (file.IsGitlink)
        {
            ConflictPreviewNote = GitlinkRefusal;
            return;
        }

        var (left, right) = SidesOf(comparison);
        var hasLeft = Holds(file, left);
        var hasRight = Holds(file, right);

        FileDiff? diff;
        var note = "";
        if (hasLeft && hasRight)
            diff = await resolver.ReadStageDiffAsync(repo, file.Path, left, right);
        else if (hasLeft || hasRight)
        {
            var side = hasLeft ? left : right;
            note = $"Only the {Name(side)} side has content here; it is shown whole.";
            diff = await resolver.ReadStageContentAsync(repo, file.Path, side);
        }
        else
        {
            ConflictPreviewNote = $"Neither the {Name(left)} nor the {Name(right)} side has content for this path.";
            return;
        }

        if (!IsCurrent(gen) || !ReferenceEquals(SelectedConflictRow, file) || ConflictComparison != comparison)
            return;

        if (diff is null)
        {
            ConflictPreviewNote = "Could not read these two stages.";
            return;
        }
        if (diff.IsBinary)
        {
            ConflictPreviewNote = BinaryPreviewNote;
            return;
        }

        ConflictDiffLines = new ObservableCollection<DiffLine>(diff.Lines);
        ConflictPreviewNote =
            diff.Lines.Count == 0 ? "These two sides are identical."
            : diff.Truncated ? $"{note} This preview was cut short by the read budget.".TrimStart()
            : note;
    }

    private static (ConflictSide Left, ConflictSide Right) SidesOf(ConflictComparison comparison) =>
        comparison switch
        {
            ConflictComparison.BaseToOurs => (ConflictSide.Base, ConflictSide.Ours),
            ConflictComparison.BaseToTheirs => (ConflictSide.Base, ConflictSide.Theirs),
            _ => (ConflictSide.Ours, ConflictSide.Theirs)
        };

    private static bool Holds(ConflictFile file, ConflictSide side) => side switch
    {
        ConflictSide.Base => file.HasBase,
        ConflictSide.Ours => file.HasOurs,
        _ => file.HasTheirs
    };

    private static string Name(ConflictSide side) => side switch
    {
        ConflictSide.Base => "base",
        ConflictSide.Ours => "ours",
        _ => "theirs"
    };

    // ── Per-file resolutions ────────────────────────────────────────────────

    [RelayCommand]
    private Task TakeOurs(ConflictFile? file) => TakeSideAsync(file ?? SelectedConflictRow, ConflictSide.Ours);

    [RelayCommand]
    private Task TakeTheirs(ConflictFile? file) => TakeSideAsync(file ?? SelectedConflictRow, ConflictSide.Theirs);

    /// <summary>
    /// Records one side as the resolution of one path, under one confirmation. The row is captured
    /// before the dialog: the list rebuilds on every refresh, and a confirmed resolution must land
    /// on the path the question named.
    /// </summary>
    private async Task TakeSideAsync(ConflictFile? file, ConflictSide side)
    {
        if (Conflicts is not { } resolver || file is null) return;
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;

        if (file.IsRefused)
        {
            ConflictErrorText = file.Refusal;
            return;
        }
        if (!(side == ConflictSide.Ours ? file.CanTakeOurs : file.CanTakeTheirs))
        {
            ConflictErrorText = NoContentRefusal;
            return;
        }
        if (IsBusy)
        {
            ConflictErrorText = BusyNotice("Resolve");
            return;
        }

        var hasContent = side == ConflictSide.Ours ? file.HasOurs : file.HasTheirs;
        var label = $"Take {Name(side)} for {file.Path}";
        var confirmed = await ConfirmAsync(
            hasContent ? $"Take the {Name(side)} side?" : $"Take the {Name(side)} side, which deleted this file?",
            hasContent
                ? $"{file.Path} is replaced by the {Name(side)} side and staged as resolved.\n\n" +
                  "Anything edited into this file in the working tree is overwritten."
                : $"The {Name(side)} side deleted {file.Path}. Taking it removes the file and stages the removal " +
                  "as the resolution.",
            hasContent ? $"Take {Name(side)}" : "Delete and resolve");
        if (!confirmed) return;
        if (!IsCurrent(gen) || repo != RepoPath)
        {
            ConflictStatusText = ProjectSwitchedNotice(label);
            return;
        }

        ConflictErrorText = "";
        var ok = await RunOp(r => resolver.TakeSideAsync(r, file.Path, side, hasContent), label, repo, gen);
        await AfterConflictOpAsync(ok, gen, label, $"{file.Path} resolved with the {Name(side)} side.");
    }

    /// <summary>
    /// Stages what the reader resolved outside this app, refusing outright while the file still
    /// carries a conflict marker. Staged with one, it reaches the commit the continue writes and
    /// from there the history — the worst outcome this surface can produce — so the refusal has no
    /// override, and a file that moved between the scan and the stage is unstaged again rather
    /// than trusted.
    /// </summary>
    [RelayCommand]
    private async Task StageResolved(ConflictFile? file)
    {
        var target = file ?? SelectedConflictRow;
        if (Conflicts is not { } resolver || target is null) return;
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;

        if (target.IsGitlink)
        {
            ConflictErrorText = GitlinkRefusal;
            return;
        }
        if (IsBusy)
        {
            ConflictErrorText = BusyNotice("Stage resolved");
            return;
        }

        ConflictErrorText = "";
        ConflictResolver.StageResolvedResult? outcome = null;
        var label = $"Stage resolved {target.Path}";
        var ok = await RunOp(async r =>
        {
            outcome = await resolver.StageResolvedAsync(r, target.Path);
            return outcome.Failure ?? (outcome.Staged
                ? new ProcessResult(0, "", "", TimedOut: false)
                : new ProcessResult(-1, "", RefusalTextFor(target.Path, outcome), TimedOut: false));
        }, label, repo, gen);

        if (!IsCurrent(gen)) return;
        if (outcome is { Staged: false } refused && refused.Failure is null)
        {
            await RefreshConflicts();
            if (!IsCurrent(gen)) return;
            ConflictErrorText = RefusalTextFor(target.Path, refused);
            ConflictStatusText = "";
            return;
        }
        await AfterConflictOpAsync(ok, gen, label, $"{target.Path} staged as resolved.");
    }

    /// <summary>Why a stage did not happen, in the terms the reader can act on.</summary>
    internal static string RefusalTextFor(string path, ConflictResolver.StageResolvedResult outcome) =>
        outcome.Marker == ConflictResolver.MarkerScanUnreadable
            ? $"{path} could not be read, so it could not be checked for conflict markers. Nothing was staged."
        : outcome.Marker is { } marker ? MarkerRefusal(path, marker)
        : outcome.ChangedWhileStaging && outcome.ConflictRestored
            ? $"{path} changed while it was being staged, so what would have been staged was never checked for " +
              "conflict markers. Nothing was staged and the conflict is back as it was — try again."
        : outcome.ChangedWhileStaging
            ? $"{path} changed while it was being staged, and putting the conflict back failed. It is now staged " +
              "with content this app did not check — unstage it and resolve it again, or use a terminal."
        : $"{path} was not staged.";

    /// <summary>
    /// Opens the conflicted file in whatever the OS associates with it, markers intact — the
    /// escape valve for everything the two side buttons cannot express. The path is resolved and
    /// re-checked against the repository root before it is handed to the shell.
    /// </summary>
    [RelayCommand]
    private void OpenConflictInEditor(ConflictFile? file)
    {
        var target = file ?? SelectedConflictRow;
        if (target is null || RepoPath.Length == 0) return;
        OpenRepoFile(RepoPath, target.Path);
    }

    /// <summary>
    /// Overridable so the command is reachable in a test without launching whatever the machine
    /// associates with a file extension.
    /// </summary>
    internal virtual void OpenRepoFile(string repoPath, string relativePath)
    {
        if (ResolveInsideRepo(repoPath, relativePath) is not { } full)
        {
            ConflictErrorText = "Not opened — that path does not resolve inside this repository.";
            return;
        }
        if (!File.Exists(full))
        {
            ConflictErrorText = $"Not opened — {relativePath} is not in the working tree.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            ConflictStatusText = $"Opened {relativePath}. Save it, then stage it as resolved.";
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open {relativePath} in an editor", ex);
            ConflictErrorText = $"Could not open {relativePath}: {ex.Message}";
        }
    }

    /// <summary>The full path a repository-relative name resolves to, or null when it leaves the repository.</summary>
    internal static string? ResolveInsideRepo(string repoPath, string relativePath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not resolve {relativePath} inside {repoPath}", ex);
            return null;
        }
    }

    // ── Continue and abort ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task ContinueSequence()
    {
        if (Conflicts is null || RepoPath.Length == 0) return;
        if (IsBusy) { ConflictErrorText = BusyNotice("Continue"); return; }

        // The gates are re-read from the repository rather than trusted from the last render: the
        // panel stays open across a watcher refresh, and the state it was gated on can have moved.
        await RefreshConflicts();
        if (!ConflictContinueOffered)
        {
            ConflictErrorText = ConflictContinueRefusal;
            return;
        }

        // The signing question is answered before the lease is taken, on the same terms as the
        // commit box: it is a question, not an operation.
        if (CommitSigningChoicePending)
        {
            HoldCommitSigningOffer("Continue", RunContinueAsync);
            return;
        }
        await RunContinueAsync();
    }

    /// <summary>
    /// The continue itself, past every gate. Entered from the button and from the signing offer's
    /// two answers, which are the same continue under a choice the reader has now made.
    /// </summary>
    private async Task RunContinueAsync()
    {
        if (Conflicts is not { } resolver) return;
        var repo = RepoPath;
        var gen = _generation;
        var activity = ConflictActivity;
        var signing = _commitSigning;
        var verb = ConflictResolver.Describe(activity);
        if (repo.Length == 0 || ConflictResolver.ContinueVerb(activity) is null) return;

        var edited = ConflictMessage.Trim();
        var message = string.Equals(edited, _conflictMessageAsPrepared.Trim(), StringComparison.Ordinal)
            ? null
            : edited;
        if (message is { Length: 0 })
        {
            ConflictErrorText = "A commit message cannot be empty — write one, or put back the one git prepared.";
            return;
        }

        var confirmed = await ConfirmAsync(
            $"Continue the {verb}?",
            $"Every conflict is resolved, so the {verb} continues and a commit is written" +
            (message is null ? " with the message git prepared." : " with the message in the box.") +
            $"\n\nIf the {verb} stops again on a later commit, this panel comes back with the next conflict.",
            "Continue");
        if (!confirmed) return;
        if (!IsCurrent(gen) || repo != RepoPath)
        {
            ConflictStatusText = ProjectSwitchedNotice("Continue");
            return;
        }

        ConflictErrorText = "";
        ProcessResult? outcome = null;
        var label = $"Continue {verb}";
        var ok = await RunOp(async r => outcome = await resolver.ContinueAsync(r, activity, message, signing),
            label, repo, gen, advice: r => CommitSigningAdvice(r, signing),
            category: OperationCategory.Surgery);
        if (!IsCurrent(gen)) return;

        // A signing run that failed on the signing leaves the other answer unreachable unless the
        // offer comes back: the choice is already made, so a second Continue would repeat it.
        if (!ok && outcome is { } failed && CommitSigningTroubled(failed, signing))
            ReofferCommitSigningAfterFailure("Continue", RunContinueAsync);

        await AfterConflictOpAsync(ok, gen, label, "");
        if (!IsCurrent(gen)) return;

        // What the repository is NOW is the whole outcome of a continue: a sequence can finish or
        // stop again on the next commit, and "done" alone tells those two apart for nobody.
        if (ok)
            ConflictStatusText = ConflictActivity == RepoActivity.None
                ? $"The {verb} is finished — nothing is in progress here now."
                : ConflictRows.Count > 0
                    ? $"The {verb} stopped again with {ConflictRows.Count} conflicted file(s)."
                    : $"The {verb} is still in progress.";
        if (ok && ConflictActivity == RepoActivity.None) await ReloadCommitsAsync();
    }

    [RelayCommand]
    private async Task AbortSequence()
    {
        if (Conflicts is not { } resolver || RepoPath.Length == 0) return;
        if (IsBusy) { ConflictErrorText = BusyNotice("Abort"); return; }
        if (!ConflictAbortOffered)
        {
            ConflictErrorText = ConflictAbortRefusal;
            return;
        }

        var repo = RepoPath;
        var gen = _generation;
        var activity = ConflictActivity;
        var verb = ConflictResolver.Describe(activity);

        var confirmed = await ConfirmAsync(
            $"Abort the {verb}?",
            $"The {verb} is abandoned and the repository returns to where it was before it started. " +
            "Every resolution recorded here is discarded.\n\n" +
            "Files this repository was not tracking are left where they are.",
            $"Abort the {verb}");
        if (!confirmed) return;
        if (!IsCurrent(gen) || repo != RepoPath)
        {
            ConflictStatusText = ProjectSwitchedNotice("Abort");
            return;
        }

        ConflictErrorText = "";
        var label = $"Abort {verb}";
        var ok = await RunOp(r => resolver.AbortAsync(r, activity), label, repo, gen,
            category: OperationCategory.Surgery);
        await AfterConflictOpAsync(ok, gen, label, "");
        if (!IsCurrent(gen) || !ok) return;

        ConflictStatusText = ConflictActivity == RepoActivity.None
            ? $"The {verb} was abandoned; the repository is back where it started."
            : $"git reported the abort done, and the repository still reads as mid-{verb}.";
        await ReloadCommitsAsync();
    }

    /// <summary>
    /// Re-reads everything an operation could have changed and mirrors the page's own outcome line
    /// into the panel, which draws over it. A failure is never reported as a resolution.
    /// </summary>
    private async Task AfterConflictOpAsync(bool ok, int gen, string label, string success)
    {
        await RefreshConflicts();
        if (!IsCurrent(gen)) return;
        if (!ok)
        {
            if (ConflictErrorText.Length == 0) ConflictErrorText = SyncStatusText;
            ConflictStatusText = $"{label} did not complete.";
            return;
        }
        if (success.Length > 0) ConflictStatusText = success;
    }
}
