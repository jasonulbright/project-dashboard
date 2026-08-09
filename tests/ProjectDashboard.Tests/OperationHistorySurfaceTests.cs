using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The operation history as the product surfaces it: what each writer records, that one attempt
/// produces one record, that the overlay never claims more than the ledger holds, and that a record
/// naming a pruned bundle says so instead of offering a link to nothing.
///
/// Backups and the journal live under AppPaths, so these join the serialized sandbox collection.
/// Each test gives its writers a ledger root of its own, so a count is a count of that test's
/// records and nothing else.
/// </summary>
[Collection("app-data-sandbox")]
public class OperationHistorySurfaceTests
{
    private readonly ITestOutputHelper _output;

    public OperationHistorySurfaceTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("ops-surface"));

    private static BackupService NewBackups(OperationHistory history) =>
        new(new GitService(), new SettingsService(), history);

    private static ProjectInfo ProjectFor(RailsRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static ProjectDetailViewModel NewVm(
        OperationHistory history,
        BackupService? backups = null,
        RewriteRecoveryService? recovery = null,
        RepoBusyRegistry? busy = null,
        GitService? git = null) =>
        new(null!, git ?? new GitService(), null!, null, busy,
            backups: backups, recovery: recovery, history: history);

    // ── RunOp, the detail page's write point ────────────────────────────────

    [Fact]
    public async Task ASuccessfulOperation_IsRecordedOnceWithItsCategory()
    {
        using var repo = await RailsRepo.CreateAsync("ops-success");
        repo.Write("new.txt", "content\n");
        var history = NewHistory();
        var vm = NewVm(history);
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.StageAllCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal("Stage all", record.Label);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
        Assert.Equal(OperationCategory.Working, record.Category);
        Assert.Equal("", record.Detail);
        Assert.Null(record.BackupStamp);
    }

    /// <summary>
    /// The failure's verbatim first error line is the whole point of the record: paraphrasing it
    /// would leave the reader with a summary of a message git already wrote precisely.
    /// </summary>
    [Fact]
    public async Task AFailedOperation_RecordsGitsOwnFirstErrorLine()
    {
        using var repo = await RailsRepo.CreateAsync("ops-failure");
        var history = NewHistory();
        var vm = NewVm(history);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.NewBranchName = "main";

        await vm.CreateBranchCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationOutcome.Failed, record.Outcome);
        Assert.Equal(OperationCategory.Branch, record.Category);
        Assert.NotEqual("", record.Detail);
        Assert.Contains("main", record.Detail, StringComparison.Ordinal);
        // The failure arms the route to the record that explains it.
        Assert.True(vm.OperationHistoryHintVisible);
        _output.WriteLine($"recorded failure detail: {record.Detail}");

        // And the next outcome lowers it: an affordance still standing beside a success would
        // open the history at a failure that has since been dealt with.
        vm.NewBranchName = "feature";
        await vm.CreateBranchCommand.ExecuteAsync(null);
        Assert.False(vm.OperationHistoryHintVisible);
        Assert.Equal(2, history.Tail(repo.Path).Records.Count);
    }

    /// <summary>
    /// A button that did nothing is exactly what a history has to explain, so the lease refusal is
    /// recorded rather than left as a status line the next operation overwrites.
    /// </summary>
    [Fact]
    public async Task AnOperationRefusedByTheRepositoryLease_IsRecordedAsRefused()
    {
        using var repo = await RailsRepo.CreateAsync("ops-refused");
        var history = NewHistory();
        var busy = new RepoBusyRegistry();
        var vm = NewVm(history, busy: busy);
        await vm.SetProjectAsync(ProjectFor(repo));

        Assert.True(busy.TryAcquire(repo.Path, out var lease));
        using (lease) await vm.FetchCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationOutcome.Refused, record.Outcome);
        Assert.Equal(OperationCategory.Remote, record.Category);
        Assert.Contains("another operation is running", record.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project switch landing inside an operation suppresses the UI write, not the record: the
    /// operation still ran against the repository it was bound to, and that is the fact the ledger
    /// exists to keep.
    /// </summary>
    [Fact]
    public async Task AnOperationWhoseProjectSwitchedMidFlight_IsStillRecordedAgainstItsOwnRepository()
    {
        using var first = await RailsRepo.CreateAsync("ops-switch-a");
        using var second = await RailsRepo.CreateAsync("ops-switch-b");
        first.Write("new.txt", "content\n");

        var git = new SwitchMidReadGitService();
        var history = NewHistory();
        var vm = NewVm(history, git: git);
        await vm.SetProjectAsync(ProjectFor(first));

        git.OnNextCall = () => vm.SetProjectAsync(ProjectFor(second));
        await vm.StageAllCommand.ExecuteAsync(null);

        var record = Assert.Single(history.Tail(first.Path).Records);
        Assert.Equal("Stage all", record.Label);
        Assert.Equal(RepoKey.For(first.Path), record.RepoKey);
        Assert.Empty(history.Tail(second.Path).Records);
    }

    /// <summary>
    /// A ledger that cannot be written is a warning in the log, never an operation that fails. The
    /// operation's own result stays authoritative.
    /// </summary>
    [Fact]
    public async Task AnUnwritableLedger_DoesNotStopTheOperation()
    {
        using var repo = await RailsRepo.CreateAsync("ops-blocked-vm");
        repo.Write("new.txt", "content\n");
        var root = TestEnv.NewDir("ops-blocked-root");
        // A FILE where the per-repo directory belongs, so every append fails.
        File.WriteAllText(Path.Combine(root, RepoKey.For(repo.Path)), "not a directory");

        var vm = NewVm(new OperationHistory(root));
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.StageAllCommand.ExecuteAsync(null);

        Assert.Contains("Stage all done", vm.SyncStatusText, StringComparison.Ordinal);
        Assert.Contains(vm.StagedFiles, f => f.Path == "new.txt");
    }

    // ── The coordinators ────────────────────────────────────────────────────

    [Fact]
    public async Task ARestoreFromABackup_IsRecordedAsARecoveringAction()
    {
        using var repo = await RailsRepo.CreateAsync("ops-restore");
        var history = NewHistory();
        var backups = NewBackups(history);
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");

        var result = await backups.RestoreAsync(handle, allowDirty: false);
        Assert.True(result.Success, result.Message);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationCategory.BackupRestore, record.Category);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
        Assert.Equal(handle.UtcStamp, record.BackupStamp);
        Assert.Equal(RecoveryKind.RestoreFromBackup, record.Recovery!.Kind);
    }

    /// <summary>
    /// One attempt, one record. A gate refusal reaches neither git nor a second write point, and a
    /// coordinator that recorded twice would make the ledger count code paths, not operations.
    /// </summary>
    [Fact]
    public async Task ASurgeryRefusedByTheLease_IsRecordedExactlyOnce()
    {
        using var repo = await RailsRepo.CreateAsync("ops-surgery");
        var history = NewHistory();
        var busy = new RepoBusyRegistry();
        var coordinator = new SurgeryCoordinator(
            NewBackups(history), busy, new GitService(), history: history);

        Assert.True(busy.TryAcquire(repo.Path, out var lease));
        SurgeryResult result;
        using (lease) result = await coordinator.ResetAsync(repo.Path, "HEAD", ResetMode.Soft);

        Assert.False(result.Success);
        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationCategory.Surgery, record.Category);
        Assert.Equal(OperationOutcome.Refused, record.Outcome);
        Assert.Equal("Commit surgery (reset)", record.Label);
    }

    [Fact]
    public async Task ADeepCleanRefusedByTheLease_IsRecordedAsRefused()
    {
        using var repo = await RailsRepo.CreateAsync("ops-deepclean");
        var history = NewHistory();
        var busy = new RepoBusyRegistry();
        var service = new DeepCleanService(new GitService(), busy, new RewriteJournal(), history);

        Assert.True(busy.TryAcquire(repo.Path, out var lease));
        using (lease) await service.RunAsync(repo.Path);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationCategory.DeepClean, record.Category);
        Assert.Equal(OperationOutcome.Refused, record.Outcome);
    }

    [Fact]
    public async Task AForcePushWithNothingToPush_IsRecordedAsRefused()
    {
        using var repo = await RailsRepo.CreateAsync("ops-forcepush");
        var history = NewHistory();
        var service = new ForcePushService(new GitService(), new RepoBusyRegistry(), history);

        await service.PushAsync(repo.Path, []);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationCategory.ForcePush, record.Category);
        Assert.Equal(OperationOutcome.Refused, record.Outcome);
    }

    // ── Interrupted operations and their recovery ───────────────────────────

    /// <summary>
    /// The journal entry survives every launch until the reader rules on it, so detection must
    /// record the interruption once rather than once per start.
    /// </summary>
    [Fact]
    public async Task AnInterruptedOperation_IsRecordedOnceAcrossRepeatedDetection()
    {
        using var repo = await RailsRepo.CreateAsync("ops-interrupted");
        var history = NewHistory();
        var backups = NewBackups(history);
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        var journal = new RewriteJournal();
        await journal.BeginAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path,
            BackupHandle = handle,
            Phase = "swap",
            UtcStamp = "20260809-121314151"
        });

        await new RewriteRecoveryService(journal, history).StartAsync(CancellationToken.None);
        await new RewriteRecoveryService(journal, history).StartAsync(CancellationToken.None);

        var interrupted = history.Tail(repo.Path).Records
            .Where(r => r.Outcome == OperationOutcome.Interrupted).ToList();
        var only = Assert.Single(interrupted);
        Assert.Equal(handle.UtcStamp, only.BackupStamp);
        Assert.Contains("swap", only.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// Abandoning the marker is not abandoning the backup, and the record says which of the two
    /// happened — linked to the interruption it answers.
    /// </summary>
    [Fact]
    public async Task ClearingTheMarker_RecordsALinkedRecoveryAndLeavesTheBundle()
    {
        using var repo = await RailsRepo.CreateAsync("ops-marker");
        var history = NewHistory();
        var backups = NewBackups(history);
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        var journal = new RewriteJournal();
        await journal.BeginAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path,
            BackupHandle = handle,
            Phase = "swap",
            UtcStamp = "20260809-121314151"
        });
        var recovery = new RewriteRecoveryService(journal, history);
        await recovery.StartAsync(CancellationToken.None);

        await recovery.ClearAsync(repo.Path);

        var records = history.Tail(repo.Path).Records;
        var interrupted = Assert.Single(records, r => r.Outcome == OperationOutcome.Interrupted);
        var cleared = Assert.Single(records, r => r.Recovery?.Kind == RecoveryKind.MarkerCleared);
        Assert.Equal(interrupted.Id, cleared.Recovery!.OfId);
        Assert.True(File.Exists(handle.BundlePath), "clearing the marker must not remove the backup");
        Assert.Contains("still on disk", cleared.Detail, StringComparison.Ordinal);
    }

    // ── The overlay ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOverlay_ListsOperationsNewestFirstWithACategoryFilter()
    {
        using var repo = await RailsRepo.CreateAsync("ops-overlay");
        var history = NewHistory();
        var vm = NewVm(history, NewBackups(history));
        await vm.SetProjectAsync(ProjectFor(repo));

        repo.Write("new.txt", "content\n");
        await vm.StageAllCommand.ExecuteAsync(null);
        vm.NewBranchName = "feature";
        await vm.CreateBranchCommand.ExecuteAsync(null);

        await vm.OpenOperationHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.OperationHistoryVisible);
        Assert.False(vm.OperationHistoryEmpty);
        Assert.False(vm.SafetyOverlayHidden);
        Assert.Equal(["Create branch", "Stage all"], vm.OperationHistoryRows.Select(r => r.Label));

        // Chips are built from the categories the ledger holds, so none is offered for a category
        // this repository has no record of.
        Assert.Equal(["all", "Working", "Branch"], vm.OperationHistoryFilters.Select(f => f.Key));
        Assert.True(vm.OperationHistoryFilters[0].IsActive);

        vm.SelectOperationHistoryFilterCommand.Execute("Branch");
        Assert.Equal(["Create branch"], vm.OperationHistoryRows.Select(r => r.Label));
        Assert.True(vm.OperationHistoryFilters.Single(f => f.Key == "Branch").IsActive);
        Assert.False(vm.OperationHistoryFilters[0].IsActive);
    }

    /// <summary>
    /// Nothing recorded is not the same fact as nothing performed. The empty state has to say which
    /// of the two it is reporting, because operations run from a terminal never reach this ledger.
    /// </summary>
    [Fact]
    public async Task WithNothingRecorded_TheEmptyStateSaysRecordedRatherThanPerformed()
    {
        using var repo = await RailsRepo.CreateAsync("ops-empty");
        var vm = NewVm(NewHistory());
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.OpenOperationHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.OperationHistoryEmpty);
        Assert.Empty(vm.OperationHistoryRows);
        Assert.Contains("recorded", vm.OperationHistoryEmptyText, StringComparison.Ordinal);
        Assert.Contains("terminal", vm.OperationHistoryEmptyText, StringComparison.Ordinal);
    }

    /// <summary>
    /// History outlives the bundles it references. A record whose backup retention has since pruned
    /// says so, and offers no link to a bundle that is not there.
    /// </summary>
    [Fact]
    public async Task ARecordWhoseBundleWasPruned_ReportsThePruneAndOffersNoLink()
    {
        using var repo = await RailsRepo.CreateAsync("ops-pruned");
        var history = NewHistory();
        var backups = NewBackups(history);
        var kept = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        history.Append(OperationRecord.For(repo.Path, OperationCategory.Rewrite, "History rewrite",
            OperationOutcome.Failed, "swap failed", DateTimeOffset.UtcNow, backupStamp: "20200101-000000000"));
        history.Append(OperationRecord.For(repo.Path, OperationCategory.Rewrite, "History rewrite",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow, backupStamp: kept.UtcStamp));

        var vm = NewVm(history, backups);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenOperationHistoryCommand.ExecuteAsync(null);

        var pruned = Assert.Single(vm.OperationHistoryRows, r => r.Record.BackupStamp == "20200101-000000000");
        Assert.Equal(RecordedBackupState.Pruned, pruned.BackupState);
        Assert.Equal("Backup pruned", pruned.Backup);
        Assert.False(pruned.CanOpenBackup);

        var live = Assert.Single(vm.OperationHistoryRows, r => r.Record.BackupStamp == kept.UtcStamp);
        Assert.Equal(RecordedBackupState.Available, live.BackupState);
        Assert.True(live.CanOpenBackup);
    }

    /// <summary>
    /// The cross-link hands off to the Backups browser, which carries the restore's own typed gate;
    /// nothing is restored from the history surface.
    /// </summary>
    [Fact]
    public async Task OpeningABackupFromARecord_LandsOnThatBundleInTheBackupsBrowser()
    {
        using var repo = await RailsRepo.CreateAsync("ops-crosslink");
        var history = NewHistory();
        var backups = NewBackups(history);
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        history.Append(OperationRecord.For(repo.Path, OperationCategory.Rewrite, "History rewrite",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow, backupStamp: handle.UtcStamp));

        var vm = NewVm(history, backups);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenOperationHistoryCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.OperationHistoryRows);

        await vm.OpenBackupForRecordCommand.ExecuteAsync(row);

        Assert.False(vm.OperationHistoryVisible);
        Assert.True(vm.BackupsVisible);
        Assert.Equal(handle.UtcStamp, vm.SelectedBackup!.Handle.UtcStamp);
        // The restore gate is untouched by arriving here from a record.
        Assert.False(vm.RestoreSelectedBackupCommand.CanExecute(null));
    }

    /// <summary>
    /// The overlay covers a typed restore confirmation when it opens over the Backups browser, and
    /// a scrim stops the mouse but no keystroke.
    /// </summary>
    [Fact]
    public async Task WhileTheOverlayIsUp_TheSurfacesItCoversAreDisabled()
    {
        using var repo = await RailsRepo.CreateAsync("ops-covers");
        var vm = NewVm(NewHistory());
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.OpenOperationHistoryCommand.ExecuteAsync(null);

        Assert.False(vm.SafetyOverlayHidden);
        Assert.False(vm.MaintenanceOverlayHidden);

        vm.CloseOperationHistoryCommand.Execute(null);
        Assert.True(vm.SafetyOverlayHidden);
        Assert.True(vm.MaintenanceOverlayHidden);
    }

    [Fact]
    public async Task LeavingTheRepository_DropsTheOverlayAndTheFailureAffordance()
    {
        using var first = await RailsRepo.CreateAsync("ops-leave-a");
        using var second = await RailsRepo.CreateAsync("ops-leave-b");
        var vm = NewVm(NewHistory());
        await vm.SetProjectAsync(ProjectFor(first));
        vm.NewBranchName = "main";
        await vm.CreateBranchCommand.ExecuteAsync(null);
        await vm.OpenOperationHistoryCommand.ExecuteAsync(null);
        Assert.True(vm.OperationHistoryVisible);

        await vm.SetProjectAsync(ProjectFor(second));

        Assert.False(vm.OperationHistoryVisible);
        Assert.Empty(vm.OperationHistoryRows);
        Assert.False(vm.OperationHistoryHintVisible);
    }

    /// <summary>
    /// A tail presented without its limits reads as the whole account of a repository, which it is
    /// not: it is capped, it rotates, and it never held anything a terminal did.
    /// </summary>
    [Fact]
    public void TheLimitsLine_StatesTheCapTheRotationAndWhatWasNeverRecorded()
    {
        var began = DateTimeOffset.UtcNow.AddDays(-3);
        var page = new OperationHistoryPage(
            [OperationRecord.For("C:\\x", OperationCategory.Working, "op", OperationOutcome.Succeeded, "", began)],
            Truncated: true, Rotated: true, SkippedLines: 0, ReadError: null, OldestRetainedUtc: began);

        var text = ProjectDetailViewModel.DescribeLimits(page);

        Assert.Contains("These records begin", text, StringComparison.Ordinal);
        Assert.Contains($"newest {OperationHistory.DefaultTailCount}", text, StringComparison.Ordinal);
        Assert.Contains("rotated out", text, StringComparison.Ordinal);
        Assert.Contains("terminal", text, StringComparison.Ordinal);
        _output.WriteLine(text);
    }

    /// <summary>
    /// The log holds the swallowed failures a one-line detail cannot carry, and this is the only
    /// route to it from inside the app.
    /// </summary>
    [Fact]
    public async Task TheLogButton_ReportsWhereItLooked()
    {
        using var repo = await RailsRepo.CreateAsync("ops-log");
        var vm = new RecordingRevealViewModel(NewHistory());
        await vm.SetProjectAsync(ProjectFor(repo));

        vm.RevealOperationLogCommand.Execute(null);

        Assert.Equal(AppPaths.LogFile, vm.Revealed);
        Assert.Contains(AppPaths.LogFile, vm.OperationHistoryStatusText, StringComparison.Ordinal);
    }

    /// <summary>Overrides the shell seam so the suite spawns no explorer.</summary>
    private sealed class RecordingRevealViewModel : ProjectDetailViewModel
    {
        public RecordingRevealViewModel(OperationHistory history)
            : base(null!, new GitService(), null!, history: history) { }

        public string Revealed { get; private set; } = "";

        internal override string? RevealInShell(string path)
        {
            Revealed = path;
            return null;
        }
    }

    // ── Accessible naming ───────────────────────────────────────────────────

    /// <summary>
    /// The row name is composed from parts that can each be absent. Every part carries its own
    /// separator, so an operation with no backup and no recovery is announced without punctuation
    /// standing in for values it never had.
    /// </summary>
    [Fact]
    public void ARowName_CarriesNoSeparatorForAnAbsentPart()
    {
        var markup = MarkupName.Markup("src/ProjectDashboard/Views/Pages/OperationHistoryView.xaml");
        var setter = MarkupName.Element(markup,
            "//*[local-name()='ListBox.ItemContainerStyle']//*[local-name()='Setter']",
            "OperationHistoryView.xaml");
        Assert.Equal("AutomationProperties.Name", setter.GetAttribute("Property"));

        var bare = Row(OperationOutcome.Succeeded, backup: RecordedBackupState.None, recovery: null);
        var name = MarkupName.From(setter.GetAttribute("Value"), bare);
        Assert.Equal("2026-01-02 03:04:05, Stage all, Succeeded", name);

        var full = Row(OperationOutcome.Failed, RecordedBackupState.Pruned, RecoveryKind.StaleLockCleared);
        var fullName = MarkupName.From(setter.GetAttribute("Value"), full);
        Assert.Equal(
            "2026-01-02 03:04:05, Stage all, Failed, Backup pruned, Retried after a stale lock was cleared",
            fullName);
    }

    private static OperationHistoryRow Row(
        OperationOutcome outcome, RecordedBackupState backup, RecoveryKind? recovery) =>
        new()
        {
            Record = OperationRecord.For("C:\\x", OperationCategory.Working, "Stage all", outcome, "",
                DateTimeOffset.UtcNow),
            When = "2026-01-02 03:04:05",
            Label = "Stage all",
            Outcome = ProjectDetailViewModel.OutcomeLabel(outcome),
            BackupState = backup,
            Backup = ProjectDetailViewModel.BackupLabel(backup),
            Recovery = recovery is { } kind ? ProjectDetailViewModel.RecoveryLabel(kind) : "",
            Detail = ""
        };

    [Fact]
    public void TheDetailPage_ReachesTheHistoryFromBesideBackups()
    {
        var page = RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");
        Assert.Contains("OpenOperationHistoryCommand", page, StringComparison.Ordinal);
        Assert.Contains("Open the operation history", page, StringComparison.Ordinal);
        Assert.Contains("OpenOperationHistoryForFailureCommand", page, StringComparison.Ordinal);

        var backupsView = RepoSource.Read("src/ProjectDashboard/Views/Pages/BackupsView.xaml");
        Assert.Contains("OpenOperationHistoryForBackupCommand", backupsView, StringComparison.Ordinal);

        var overlay = RepoSource.Read("src/ProjectDashboard/Views/Pages/OperationHistoryView.xaml");
        Assert.Contains("CloseOperationHistoryCommand", overlay, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", overlay, StringComparison.Ordinal);
        Assert.Contains("RevealOperationLogCommand", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records hold repository paths and verbatim git output. Nothing carries them off the machine:
    /// the bug-report autofill states the environment only, and the portfolio export describes
    /// projects.
    /// </summary>
    [Fact]
    public void NoOutwardSurface_CarriesTheLedger()
    {
        foreach (var file in new[]
                 {
                     "src/ProjectDashboard/ViewModels/Pages/DashboardViewModel.cs",
                     "src/ProjectDashboard/Services/PortfolioExport.cs"
                 })
        {
            var source = RepoSource.Read(file);
            Assert.DoesNotContain("OperationHistory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HistoryRoot", source, StringComparison.Ordinal);
        }
    }
}
