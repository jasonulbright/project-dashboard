using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The two surfaces that reach a backup from outside the session that took it: the Backups
/// browser and the interrupted-operation banner. What is asserted is that the restore cannot
/// happen without its gates, that neither surface claims more than it knows, and that an empty
/// crash record is never presented as proof nothing was interrupted.
///
/// Backups and the journal live under AppPaths, so these join the serialized sandbox collection.
/// </summary>
[Collection("app-data-sandbox")]
public class BackupsSurfaceTests
{
    private readonly ITestOutputHelper _output;

    public BackupsSurfaceTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static BackupService NewBackups() => new(new GitService(), new SettingsService());

    private static ProjectInfo ProjectFor(RailsRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static ProjectDetailViewModel NewVm(
        BackupService? backups = null,
        RewriteRecoveryService? recovery = null,
        RepoBusyRegistry? busy = null) =>
        new(null!, new GitService(), null!, null, busy, backups: backups, recovery: recovery);

    private static async Task<RewriteRecoveryService> DetectedRecoveryAsync(RewriteJournalEntry entry)
    {
        var journal = new RewriteJournal();
        await journal.BeginAsync(entry);
        var recovery = new RewriteRecoveryService(journal);
        await recovery.StartAsync(CancellationToken.None);
        return recovery;
    }

    // ── The browser ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheBrowser_ListsEveryBackupWithWhatItsSidecarRecords()
    {
        using var repo = await RailsRepo.CreateAsync("backups-list");
        await repo.GitAsync("branch", "feature");
        var backups = NewBackups();
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");

        var vm = NewVm(backups);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.BackupsVisible);
        Assert.False(vm.BackupsEmpty);
        var entry = Assert.Single(vm.BackupList);
        Assert.Equal(handle.UtcStamp, entry.Handle.UtcStamp);
        Assert.Equal("History rewrite", entry.Operation);
        Assert.True(entry.Restorable);
        // main + feature: the ref count is read from the sidecar, not guessed.
        Assert.Contains("2 ref(s)", entry.Detail);
        Assert.Contains("refs/heads/main", entry.Detail);
        Assert.Same(entry, vm.SelectedBackup);
        _output.WriteLine($"listed backup: {entry.Taken} · {entry.Operation} · {entry.Detail}");
    }

    /// <summary>
    /// A backup whose sidecar is gone cannot be restored, and <see cref="BackupService.RestoreAsync"/>
    /// refuses on exactly that. The row says so instead of rendering an empty backup as usable.
    /// </summary>
    [Fact]
    public async Task ABackupWithAnUnreadableSidecar_IsListedAsUnrestorable()
    {
        using var repo = await RailsRepo.CreateAsync("backups-broken");
        var backups = NewBackups();
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        await File.WriteAllTextAsync(handle.RefsSnapshotPath, "{ not json");

        var vm = NewVm(backups);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);

        var entry = Assert.Single(vm.BackupList);
        Assert.False(entry.Restorable);
        Assert.Equal("Unreadable", entry.Operation);

        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;
        Assert.False(vm.RestoreSelectedBackupCommand.CanExecute(null));
    }

    /// <summary>
    /// With no backups on disk the browser may not imply nothing was ever interrupted: a lost
    /// journal reports exactly the same thing as a clean shutdown, so the empty state has to
    /// state the limit of what it knows.
    /// </summary>
    [Fact]
    public async Task WithNoBackupsOnDisk_TheEmptyStateRefusesToClaimNothingWasInterrupted()
    {
        using var repo = await RailsRepo.CreateAsync("backups-empty");
        var vm = NewVm(NewBackups());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.BackupsEmpty);
        Assert.False(vm.BackupsHasEntries);
        Assert.Empty(vm.BackupList);

        var markup = await File.ReadAllTextAsync(ViewSource("BackupsView.xaml"));
        Assert.Contains("No backups on disk for this repository.", markup);
        Assert.Contains("That is not proof nothing was ever interrupted here", markup);
        // And the browser's own header says the crash record can be lost.
        Assert.Contains("an empty crash record is not proof nothing was interrupted", markup);
    }

    /// <summary>Backups exist regardless of what the journal says, so a repository with no crash record still gets its list.</summary>
    [Fact]
    public async Task WithAnEmptyJournal_TheBackupsThatExistAreStillShown()
    {
        using var repo = await RailsRepo.CreateAsync("backups-nojournal");
        var backups = NewBackups();
        await backups.CreateBackupAsync(repo.Path, "History rewrite");

        var recovery = new RewriteRecoveryService(new RewriteJournal());
        await recovery.StartAsync(CancellationToken.None);
        Assert.Empty(recovery.Pending);

        var vm = NewVm(backups, recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);

        Assert.False(vm.RecoveryBannerVisible);
        Assert.Single(vm.BackupList);
        Assert.Equal("", vm.BackupsJournalNote);
    }

    // ── The restore gate ────────────────────────────────────────────────────

    [Fact]
    public async Task RestoringWithoutTheTypedRepositoryName_IsRefusedAndNothingIsRestored()
    {
        using var repo = await RailsRepo.CreateAsync("backups-typed");
        var backups = NewBackups();
        await backups.CreateBackupAsync(repo.Path, "History rewrite");
        var before = await repo.RefStateAsync();

        repo.Write("later.txt", "written after the backup\n");
        await repo.CommitAllAsync("after the backup");
        var afterCommit = await repo.RefStateAsync();
        Assert.NotEqual(before, afterCommit);

        var vm = NewVm(backups);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);

        Assert.False(vm.RestoreSelectedBackupCommand.CanExecute(null));
        vm.BackupsConfirmInput = "yes";
        Assert.False(vm.RestoreSelectedBackupCommand.CanExecute(null));

        // The guard holds on the command itself, not only on the button's enabled state.
        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);
        Assert.Equal(afterCommit, await repo.RefStateAsync());

        // Typed exactly, the same click restores.
        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;
        Assert.True(vm.RestoreSelectedBackupCommand.CanExecute(null));
        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);

        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Contains("History restored", vm.BackupsStatusText);
        // The confirmation is spent: the next restore is its own decision.
        Assert.Equal("", vm.BackupsConfirmInput);
        _output.WriteLine($"typed-confirm restore: refs byte-identical to the backup — {vm.BackupsStatusText}");
    }

    /// <summary>
    /// The restore ends in a hard reset and the bundle holds committed history only, so an
    /// uncommitted change it discarded would exist in no backup at all. It is refused first.
    /// </summary>
    [Fact]
    public async Task RestoringOverADirtyWorkingTree_IsRefusedBeforeAnythingIsWritten()
    {
        using var repo = await RailsRepo.CreateAsync("backups-dirty");
        var backups = NewBackups();
        await backups.CreateBackupAsync(repo.Path, "History rewrite");
        repo.Write("later.txt", "committed after the backup\n");
        await repo.CommitAllAsync("after the backup");
        var before = await repo.RefStateAsync();

        repo.Write("file.txt", "an uncommitted edit\n");

        var vm = NewVm(backups);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);
        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;

        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);

        Assert.Contains("uncommitted change", vm.BackupsErrorText);
        Assert.Equal("", vm.BackupsStatusText);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Equal("an uncommitted edit\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, "file.txt")));
    }

    /// <summary>The restore is refused rather than interleaved when something else already holds the repository.</summary>
    [Fact]
    public async Task RestoringWhileTheRepositoryIsBusy_IsRefusedWithoutRestoring()
    {
        using var repo = await RailsRepo.CreateAsync("backups-busy");
        var backups = NewBackups();
        await backups.CreateBackupAsync(repo.Path, "History rewrite");
        repo.Write("later.txt", "committed after the backup\n");
        await repo.CommitAllAsync("after the backup");
        var before = await repo.RefStateAsync();

        var busy = new RepoBusyRegistry();
        var vm = NewVm(backups, busy: busy);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);
        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;

        using (busy.Acquire(repo.Path))
        {
            await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);
            Assert.Contains("busy", vm.BackupsErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await repo.RefStateAsync());
        }
    }

    /// <summary>A restore takes the lease for its whole run, so a background fetch cannot land inside the ref reconciliation.</summary>
    [Fact]
    public async Task ARestore_HoldsTheRepositoryLeaseAndReleasesIt()
    {
        using var repo = await RailsRepo.CreateAsync("backups-lease");
        var backups = NewBackups();
        await backups.CreateBackupAsync(repo.Path, "History rewrite");
        repo.Write("later.txt", "committed after the backup\n");
        await repo.CommitAllAsync("after the backup");

        var busy = new RepoBusyRegistry();
        var transitions = new List<bool>();
        var refusedDuringRestore = false;
        busy.Changed += r =>
        {
            var isBusy = busy.IsBusy(r);
            transitions.Add(isBusy);
            if (isBusy) refusedDuringRestore = !busy.TryAcquire(r, out _);
        };

        var vm = NewVm(backups, busy: busy);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsCommand.ExecuteAsync(null);
        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;

        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);

        Assert.Equal([true, false], transitions);
        Assert.True(refusedDuringRestore);
        Assert.False(busy.IsBusy(repo.Path));
        Assert.False(vm.IsBusy);
    }

    // ── Crash recovery ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnInterruptedOperation_RaisesABannerOnItsOwnProjectAndNeverRestoresItself()
    {
        using var repo = await RailsRepo.CreateAsync("recover-banner");
        var backups = NewBackups();
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        repo.Write("later.txt", "landed after the backup\n");
        await repo.CommitAllAsync("after the backup");
        var interrupted = await repo.RefStateAsync();

        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path,
            BackupHandle = handle,
            Phase = "swap",
            UtcStamp = "20260807-120000000",
        });

        var vm = NewVm(backups, recovery);
        await vm.SetProjectAsync(ProjectFor(repo));

        Assert.True(vm.RecoveryBannerVisible);
        Assert.Contains("interrupted", vm.RecoveryBannerText);
        Assert.Contains("phase 'swap'", vm.RecoveryBannerText);
        Assert.Contains("Nothing has been restored", vm.RecoveryBannerText);
        // The repository is exactly as the interrupted run left it: detection restores nothing.
        Assert.Equal(interrupted, await repo.RefStateAsync());
        _output.WriteLine($"recovery banner: {vm.RecoveryBannerText}");
    }

    /// <summary>The banner routes to the backup the journal named, not to whichever one happens to be newest.</summary>
    [Fact]
    public async Task TheBannerOpensTheBrowserOnTheBackupTheJournalRecorded()
    {
        using var repo = await RailsRepo.CreateAsync("recover-open");
        var backups = NewBackups();
        var recorded = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        repo.Write("later.txt", "landed after the backup\n");
        await repo.CommitAllAsync("after the backup");
        var newer = await backups.CreateBackupAsync(repo.Path, "Commit surgery (reset)");
        Assert.NotEqual(recorded.UtcStamp, newer.UtcStamp);

        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path,
            BackupHandle = recorded,
            Phase = "swap",
            UtcStamp = recorded.UtcStamp,
        });

        var vm = NewVm(backups, recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsForRecoveryCommand.ExecuteAsync(null);

        Assert.True(vm.BackupsVisible);
        Assert.Equal(recorded.UtcStamp, vm.SelectedBackup!.Handle.UtcStamp);
        Assert.Contains("An interrupted", vm.BackupsJournalNote);
        Assert.Equal("", vm.BackupsErrorText);
    }

    /// <summary>The backup the journal named can be pruned or deleted; the surface says so rather than silently selecting another.</summary>
    [Fact]
    public async Task WhenTheRecordedBackupIsGone_TheBrowserSaysSoRatherThanSelectingAnother()
    {
        using var repo = await RailsRepo.CreateAsync("recover-missing");
        var backups = NewBackups();
        var present = await backups.CreateBackupAsync(repo.Path, "History rewrite");

        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path,
            BackupHandle = new BackupHandle { RepoPath = repo.Path, UtcStamp = "19990101-000000000" },
            Phase = "swap",
            UtcStamp = "19990101-000000000",
        });

        var vm = NewVm(backups, recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsForRecoveryCommand.ExecuteAsync(null);

        Assert.Contains("no longer on disk", vm.BackupsErrorText);
        Assert.Equal(present.UtcStamp, vm.SelectedBackup!.Handle.UtcStamp);
    }

    /// <summary>Keeping is not discarding: the record survives on disk so the next launch offers it again.</summary>
    [Fact]
    public async Task KeepingTheRecord_HidesTheBannerButLeavesTheRecordOnDisk()
    {
        using var repo = await RailsRepo.CreateAsync("recover-keep");
        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path, Phase = "swap", UtcStamp = "20260807-120000000",
        });

        var vm = NewVm(NewBackups(), recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        Assert.True(vm.RecoveryBannerVisible);

        vm.KeepRecoveryRecordCommand.Execute(null);

        Assert.False(vm.RecoveryBannerVisible);
        Assert.NotNull(await new RewriteJournal().ReadPendingAsync(repo.Path));
        Assert.Single(recovery.Pending);
    }

    /// <summary>
    /// Discarding abandons the only record that this repository may be mid-operation, so it
    /// carries the same typed confirmation the restore does — and leaves the backup alone.
    /// </summary>
    [Fact]
    public async Task DiscardingTheRecord_NeedsTheTypedNameAndKeepsTheBackup()
    {
        using var repo = await RailsRepo.CreateAsync("recover-discard");
        var backups = NewBackups();
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path, BackupHandle = handle, Phase = "swap", UtcStamp = handle.UtcStamp,
        });

        var vm = NewVm(backups, recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.BeginDiscardRecoveryRecordCommand.Execute(null);

        Assert.True(vm.RecoveryDiscardVisible);
        Assert.False(vm.DiscardRecoveryRecordCommand.CanExecute(null));

        vm.RecoveryDiscardInput = "discard";
        Assert.False(vm.DiscardRecoveryRecordCommand.CanExecute(null));
        await vm.DiscardRecoveryRecordCommand.ExecuteAsync(null);
        Assert.NotNull(await new RewriteJournal().ReadPendingAsync(repo.Path));

        vm.RecoveryDiscardInput = vm.RecoveryDiscardPhrase;
        Assert.True(vm.DiscardRecoveryRecordCommand.CanExecute(null));
        await vm.DiscardRecoveryRecordCommand.ExecuteAsync(null);

        Assert.False(vm.RecoveryBannerVisible);
        Assert.Null(await new RewriteJournal().ReadPendingAsync(repo.Path));
        Assert.Empty(recovery.Pending);
        // The record is gone; the backup it named is not.
        Assert.True(File.Exists(handle.BundlePath));
        Assert.Single(await backups.ListBackupsAsync(repo.Path));
    }

    /// <summary>A completed restore means the repository is no longer mid-operation, so the marker goes with it.</summary>
    [Fact]
    public async Task ARestoreThatLands_ClearsTheInterruptedRecord()
    {
        using var repo = await RailsRepo.CreateAsync("recover-restore");
        var backups = NewBackups();
        var handle = await backups.CreateBackupAsync(repo.Path, "History rewrite");
        var before = await repo.RefStateAsync();
        repo.Write("later.txt", "landed after the backup\n");
        await repo.CommitAllAsync("after the backup");

        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path, BackupHandle = handle, Phase = "swap", UtcStamp = handle.UtcStamp,
        });

        var vm = NewVm(backups, recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsForRecoveryCommand.ExecuteAsync(null);
        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;

        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);

        Assert.Contains("History restored", vm.BackupsStatusText);
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Null(await new RewriteJournal().ReadPendingAsync(repo.Path));
        Assert.Empty(recovery.Pending);
        Assert.False(vm.RecoveryBannerVisible);
    }

    /// <summary>
    /// A restore runs against the repository it captured, so a project switch mid-restore does
    /// not stop it — and the marker it clears belongs to that repository, not to the page. The
    /// page has moved on, so nothing else it would have written may land; and the busy flag the
    /// restore raised must not follow the reader onto the next repository, where it would refuse
    /// every close of a browser that has no restore of its own.
    /// </summary>
    [Fact]
    public async Task ARestoreLandingAfterAProjectSwitch_ClearsTheMarkerAndStrandsNoBrowser()
    {
        using var repo = await RailsRepo.CreateAsync("restore-switch-a");
        using var other = await RailsRepo.CreateAsync("restore-switch-b");
        var handle = await NewBackups().CreateBackupAsync(repo.Path, "History rewrite");
        var before = await repo.RefStateAsync();
        repo.Write("later.txt", "landed after the backup\n");
        await repo.CommitAllAsync("after the backup");

        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path, BackupHandle = handle, Phase = "swap", UtcStamp = handle.UtcStamp,
        });

        var git = new SwitchWhileRestoring();
        var vm = NewVm(new BackupService(git, new SettingsService()), recovery);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenBackupsForRecoveryCommand.ExecuteAsync(null);
        vm.BackupsConfirmInput = vm.BackupsConfirmPhrase;

        var busyDuringRestore = true;
        var closedDuringRestore = false;
        git.OnRestoreEntry = async () =>
        {
            await vm.SetProjectAsync(ProjectFor(other));
            busyDuringRestore = vm.BackupsBusy;
            await vm.OpenBackupsCommand.ExecuteAsync(null);
            vm.CloseBackupsCommand.Execute(null);
            closedDuringRestore = !vm.BackupsVisible;
        };

        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);

        // The other repository's browser was neither busy nor stuck open while the restore ran.
        Assert.False(busyDuringRestore);
        Assert.True(closedDuringRestore);
        Assert.False(vm.BackupsBusy);

        // The restore still landed, and the record it made obsolete is gone with it.
        Assert.Equal(before, await repo.RefStateAsync());
        Assert.Null(await new RewriteJournal().ReadPendingAsync(repo.Path));
        Assert.Empty(recovery.Pending);

        // Nothing else was written onto the page that had moved on.
        Assert.Equal("", vm.BackupsStatusText);
        Assert.Equal("", vm.BackupsErrorText);
        _output.WriteLine("restore landed after the switch: marker cleared, next repository's browser free");
    }

    /// <summary>Runs a callback once, at the first git call the restore makes, so the switch lands inside it.</summary>
    private sealed class SwitchWhileRestoring : GitService
    {
        private int _fired;

        public Func<Task>? OnRestoreEntry { get; set; }

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            if (list.Contains("verify") && Interlocked.Exchange(ref _fired, 1) == 0 && OnRestoreEntry is not null)
                await OnRestoreEntry();
            return await base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    /// <summary>
    /// A reader who never opens the affected project would otherwise never learn the operation
    /// was interrupted, so the dashboard names the repositories — and offers no action, because
    /// every gate lives on the project's own page.
    /// </summary>
    [Fact]
    public async Task TheDashboard_NamesRepositoriesWithAnInterruptedOperation()
    {
        using var repo = await RailsRepo.CreateAsync("recover-dash");
        var recovery = await DetectedRecoveryAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path, Phase = "swap", UtcStamp = "20260807-120000000",
        });

        var text = DashboardViewModel.DescribeInterrupted(recovery.Pending);

        Assert.NotNull(text);
        Assert.Contains(System.IO.Path.GetFileName(repo.Path), text);
        Assert.Contains("was interrupted", text);
        Assert.Contains("nothing has been restored", text, StringComparison.OrdinalIgnoreCase);

        // Dropping the record on the project's page leaves the dashboard nothing to report.
        await recovery.ClearAsync(repo.Path);
        Assert.Null(DashboardViewModel.DescribeInterrupted(recovery.Pending));
    }

    /// <summary>Several repositories can be pending at once; naming only the first would strand the others' backups.</summary>
    [Fact]
    public void TheDashboardBanner_NamesEveryPendingRepository()
    {
        var text = DashboardViewModel.DescribeInterrupted(
        [
            new RewriteJournalEntry { RepoPath = @"C:\repos\alpha", Phase = "swap" },
            new RewriteJournalEntry { RepoPath = @"C:\repos\beta\", Phase = "rebase" },
        ]);

        Assert.NotNull(text);
        Assert.Contains("2 repositories", text);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    /// <summary>
    /// A legacy entry can record no path at all. It is still one of the repositories the count
    /// claims, so it is listed as an unnamed remainder rather than dropped — a count larger than
    /// the names beside it reads as a missing name for a repository that was never pending.
    /// </summary>
    [Fact]
    public void TheDashboardBanner_AccountsForEveryPendingEntryItCounts()
    {
        var text = DashboardViewModel.DescribeInterrupted(
        [
            new RewriteJournalEntry { RepoPath = @"C:\repos\alpha", Phase = "swap" },
            new RewriteJournalEntry { RepoPath = "", Phase = "swap" },
        ]);

        Assert.NotNull(text);
        Assert.Contains("2 repositories", text);
        Assert.Contains("alpha, an unnamed repository", text);

        var allUnnamed = DashboardViewModel.DescribeInterrupted(
        [
            new RewriteJournalEntry { RepoPath = "", Phase = "swap" },
            new RewriteJournalEntry { RepoPath = "", Phase = "rebase" },
        ]);

        Assert.NotNull(allUnnamed);
        Assert.Contains("2 repositories", allUnnamed);
        Assert.Contains("2 unnamed repositories", allUnnamed);
    }

    private static string ViewSource(string name, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
