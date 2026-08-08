using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Rewrite;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// One branch on the force-push plan, written out so the pane renders only strings the view model
/// already committed to. Every line names object ids rather than counts alone: a count says how
/// much would change, an object id says which state the remote must be in for the push to happen
/// at all.
/// </summary>
public sealed partial class ForcePushRow : ObservableObject
{
    public required DivergedBranch Branch { get; init; }

    public required string Headline { get; init; }

    public required string Change { get; init; }

    public required string Impact { get; init; }

    public required string Lease { get; init; }

    /// <summary>Only included rows are pushed; an excluded one leaves its remote ref exactly as it stands.</summary>
    [ObservableProperty] private bool _include = true;
}

/// <summary>
/// Publishing a rewritten history. A rewrite gives every affected commit a new id, so the local
/// branches stop being descendants of what the remote holds and no ordinary push can land — this
/// is the one surface in the app that replaces history on a remote, and it runs only when a reader
/// opens it, reads the plan, and types this repository's name.
///
/// The lease is stated, not implied. Each push carries
/// <c>--force-with-lease=&lt;remote ref&gt;:&lt;expected&gt;</c> where the expected value is the
/// remote-tracking ref as it stands when the plan is built, which is the newest position this
/// repository has ever observed for that remote branch. That buys exactly one guarantee: if
/// anyone moved the remote branch after this repository last fetched, the push is refused and
/// nothing on the remote is replaced. It buys nothing about commits a fetch already brought in —
/// fetching advances the tracking ref, so a fetch after the rewrite makes those commits part of
/// the lease basis, and the plan says how many of them the push would drop.
///
/// Nothing here retries with <c>--force</c>. A rejected lease means the remote is somewhere this
/// repository has never seen, and offering to overwrite it anyway would make the lease decorative.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Null when the host supplied none; the pane then refuses instead of pretending a repository has no divergence.</summary>
    private readonly ForcePushService? _forcePush;

    [ObservableProperty] private bool _forcePushVisible;

    partial void OnForcePushVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SafetyOverlayHidden));
        OnPropertyChanged(nameof(MaintenanceOverlayHidden));
    }

    [ObservableProperty] private ObservableCollection<ForcePushRow> _forcePushRows = [];

    /// <summary>Read from the list itself, so the confirmation field and the push control are absent — not merely disabled — before the first plan lands.</summary>
    public bool ForcePushHasRows => ForcePushRows.Count > 0;

    /// <summary>How many rows the push would actually cover. Zero leaves the command with nothing to run.</summary>
    public int ForcePushIncludedCount => ForcePushRows.Count(r => r.Include);

    partial void OnForcePushRowsChanged(ObservableCollection<ForcePushRow> value)
    {
        foreach (var row in value) row.PropertyChanged += OnForcePushRowChanged;
        OnPropertyChanged(nameof(ForcePushHasRows));
        OnPropertyChanged(nameof(ForcePushIncludedCount));
        PushRewrittenHistoryCommand.NotifyCanExecuteChanged();
    }

    private void OnForcePushRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ForcePushRow.Include)) return;
        OnPropertyChanged(nameof(ForcePushIncludedCount));
        PushRewrittenHistoryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True once a plan has been built and found nothing to force-push. The empty state must not show before that.</summary>
    [ObservableProperty] private bool _forcePushEmpty;

    /// <summary>Branches this flow deliberately leaves alone, and why. Empty when there are none.</summary>
    [ObservableProperty] private string _forcePushExclusions = "";

    [ObservableProperty] private ObservableCollection<string> _forcePushResults = [];

    [ObservableProperty] private string _forcePushStatusText = "";
    [ObservableProperty] private string _forcePushErrorText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushRewrittenHistoryCommand))]
    private bool _forcePushBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushRewrittenHistoryCommand))]
    private string _forcePushConfirmInput = "";

    /// <summary>The exact text the reader must type: the repository folder name, never a generic word.</summary>
    [ObservableProperty] private string _forcePushConfirmPhrase = "";

    /// <summary>
    /// Whether any loaded branch would need a force to publish. Drives the Branches tab's
    /// affordance, and is read from the branch list the tab already loads. Both counts are
    /// required: a branch that is only behind has not been pulled, and publishing it would drop
    /// the remote's commits rather than replace a rewritten history.
    /// </summary>
    public bool BranchesDivergedFromRemote =>
        Branches.Any(b => b.Upstream.Length > 0 && !b.UpstreamGone && b.Ahead > 0 && b.Behind > 0);

    partial void OnBranchesChanged(ObservableCollection<BranchInfo> value) =>
        OnPropertyChanged(nameof(BranchesDivergedFromRemote));

    /// <summary>
    /// The typed phrase is required for the same reason the rewrite requires it, and more so: this
    /// replaces commits on a remote other people pull from, and no backup in this app covers what
    /// it removes from there.
    /// </summary>
    internal bool ForcePushConfirmSatisfied =>
        ForcePushConfirmPhrase.Length > 0
        && string.Equals(ForcePushConfirmInput.Trim(), ForcePushConfirmPhrase, StringComparison.Ordinal);

    [RelayCommand]
    private async Task OpenForcePush()
    {
        if (RepoPath.Length == 0 || ReflogVisible) return;
        ForcePushConfirmPhrase = RepoDisplayName();
        ForcePushConfirmInput = "";
        ForcePushErrorText = "";
        ForcePushStatusText = "";
        ForcePushResults = [];
        ForcePushVisible = true;
        await LoadForcePushPlan();
    }

    [RelayCommand]
    private void CloseForcePush()
    {
        // The push holds the repository lease and this pane is the only report of how each ref
        // ended; closing over a running one would hide that.
        if (ForcePushBusy)
        {
            ForcePushStatusText = "The push is still running — wait for it to finish.";
            return;
        }
        ForcePushVisible = false;
        ForcePushRows = [];
        ForcePushConfirmInput = "";
        ForcePushStatusText = "";
        ForcePushErrorText = "";
        ForcePushExclusions = "";
        ForcePushEmpty = false;
    }

    /// <summary>
    /// Drops the pane as the page leaves this repository. A push in flight holds the repository
    /// lease and its own generation guard, so the pane closing does not end it — what closes is a
    /// plan describing a repository the page no longer shows.
    /// </summary>
    private void CloseForcePushOnProjectSwitch()
    {
        ForcePushBusy = false;
        if (!ForcePushVisible) return;
        ForcePushVisible = false;
        ForcePushRows = [];
        ForcePushResults = [];
        ForcePushConfirmInput = "";
        ForcePushStatusText = "";
        ForcePushErrorText = "";
        ForcePushExclusions = "";
        ForcePushEmpty = false;
    }

    [RelayCommand]
    private async Task LoadForcePushPlan()
    {
        var service = _forcePush;
        var repo = RepoPath;
        if (service is null)
        {
            ForcePushErrorText =
                "Publishing a rewritten history is unavailable — the push service was not configured for this session.";
            return;
        }
        if (repo.Length == 0) return;

        var gen = _generation;
        ForcePushPlan plan;
        try
        {
            plan = await service.PlanAsync(repo);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not build a force-push plan for {repo}", ex);
            if (IsCurrent(gen)) ForcePushErrorText = $"Could not read this repository's branches: {ex.Message}";
            return;
        }
        if (!IsCurrent(gen)) return;

        if (plan.Refusal is { } refusal)
        {
            ForcePushErrorText = refusal;
            ForcePushRows = [];
            ForcePushEmpty = false;
            return;
        }

        ForcePushRows = new ObservableCollection<ForcePushRow>(plan.Diverged.Select(Describe));
        ForcePushEmpty = ForcePushRows.Count == 0;
        ForcePushExclusions = DescribeExclusions(plan);
        // A stale confirmation would carry across a re-plan onto branches the reader never saw.
        ForcePushConfirmInput = "";
    }

    /// <summary>One branch's plan row: what moves, what stops being on the remote, and what the push depends on.</summary>
    internal static ForcePushRow Describe(DivergedBranch branch) => new()
    {
        Branch = branch,
        Headline = $"{branch.BranchName} → {branch.RemoteDisplayName}",
        Change = $"{branch.RemoteRef} on {branch.Remote} moves from {ForcePushService.Short(branch.LeaseOid)} " +
                 $"to {ForcePushService.Short(branch.LocalOid)}.",
        Impact = $"{branch.Behind} commit(s) currently on {branch.RemoteDisplayName} are not in this branch " +
                 $"and stop being reachable from it; {branch.Ahead} commit(s) from this branch take their place. " +
                 "Anyone who already pulled the old commits keeps them.",
        Lease = $"Lease: the push is refused unless {branch.RemoteDisplayName} is still exactly at " +
                $"{ForcePushService.Short(branch.LeaseOid)} — the position this repository last recorded for it. " +
                "It is not refused for the commits already recorded there.",
    };

    /// <summary>
    /// The branches the plan leaves out, named rather than left to be inferred from their absence.
    /// Tags are named unconditionally: a rewrite rewrites them too, and this flow does not publish
    /// them, so silence would read as "tags were handled".
    /// </summary>
    internal static string DescribeExclusions(ForcePushPlan plan)
    {
        var parts = new List<string>();
        if (plan.AheadOnly.Count > 0)
            parts.Add($"Ahead of the remote and needing no force, so not pushed here: {string.Join(", ", plan.AheadOnly)}. " +
                      "Use Push on the toolbar for those.");
        if (plan.BehindOnly.Count > 0)
            parts.Add($"Behind only — pull instead, no force needed: {string.Join(", ", plan.BehindOnly)}. " +
                      "These hold nothing the remote lacks, so forcing them would delete the remote's commits and " +
                      "publish nothing.");
        if (plan.UpstreamGone.Count > 0)
            parts.Add($"No remote-tracking ref to take a lease on, so not pushed here: {string.Join(", ", plan.UpstreamGone)}.");
        parts.Add("Tags are never published by this flow, even when a rewrite changed them.");
        return string.Join("\n", parts);
    }

    private bool CanPushRewrittenHistory() =>
        ForcePushIncludedCount > 0 && ForcePushConfirmSatisfied && !ForcePushBusy;

    [RelayCommand(CanExecute = nameof(CanPushRewrittenHistory))]
    private async Task PushRewrittenHistory()
    {
        // Re-checked rather than trusted from the affordance: the enabled state is what a reader
        // sees, this is the guard that holds.
        if (!CanPushRewrittenHistory()) return;
        var service = _forcePush;
        if (service is null) return;

        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;
        if (IsBusy)
        {
            ForcePushStatusText = "Another operation is running on this repository — wait for it to finish.";
            return;
        }

        // The branches the reader agreed to, with the lease values the pane showed. Re-reading the
        // tracking refs here would push against a position nothing on screen ever named, and an
        // excluded row is a branch the reader declined rather than one the plan omitted.
        var branches = ForcePushRows.Where(r => r.Include).Select(r => r.Branch).ToList();

        var holder = new object();
        IsBusy = true;
        _busyGateHolder = holder;
        ForcePushBusy = true;
        ForcePushErrorText = "";
        ForcePushResults = [];
        ForcePushStatusText = $"Publishing {branches.Count} branch(es)…";
        try
        {
            var outcome = await service.PushAsync(repo, branches);
            if (!IsCurrent(gen)) return;

            if (outcome.RefusalReason is { } refusal)
            {
                ForcePushErrorText = refusal;
                ForcePushStatusText = "Nothing was pushed.";
                return;
            }

            ForcePushResults = new ObservableCollection<string>(
                outcome.Refs.Select(r => $"{(r.Success ? "pushed" : "refused")} — {r.BranchName}: {r.Detail}"));
            var stale = outcome.Refs.Count(r => r.LeaseRejected);
            var failed = outcome.Refs.Count(r => !r.Success);
            ForcePushStatusText = outcome.Success
                ? $"{outcome.Refs.Count} branch(es) published. The remote now holds the rewritten history."
                : stale == failed
                    ? $"{failed} of {outcome.Refs.Count} branch(es) were refused because the remote had moved. " +
                      "Nothing on those was replaced, and this app will not retry them with a plain force."
                    : $"{failed} of {outcome.Refs.Count} branch(es) did not land — see the lines below.";

            // The confirmation is spent, and the lease values the plan held are stale for every
            // ref that landed: a second push is its own decision on a freshly read plan.
            ForcePushConfirmInput = "";
            await LoadForcePushPlan();
            await SafeRefreshWorkingStateAsync();
            await LoadBranches();
        }
        catch (Exception ex)
        {
            Log.Warn($"force-push failed for {repo}", ex);
            if (IsCurrent(gen))
            {
                ForcePushErrorText =
                    "The push failed before it could report which refs it had reached, so some branches on the " +
                    $"remote may already hold the rewritten history. Fetch and check before pushing again. {ex.Message}";
                ForcePushStatusText = "The push did not complete.";
            }
        }
        finally
        {
            // A push started before a project switch raises this flag on an older generation;
            // lowering it from a stale continuation would let the next repository's pane close
            // mid-push.
            if (IsCurrent(gen)) ForcePushBusy = false;
            if (ReferenceEquals(_busyGateHolder, holder))
            {
                _busyGateHolder = null;
                if (IsCurrent(gen)) IsBusy = false;
            }
        }
    }
}
