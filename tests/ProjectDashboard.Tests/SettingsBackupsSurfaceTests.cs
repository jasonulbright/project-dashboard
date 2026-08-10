using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The backups block on the Settings page and the store reads behind it: the retention count that
/// had no control at all until now, the deep-capture toggle, the storage figure, and the prune
/// that applies a lowered count to repositories nothing is about to back up.
///
/// What is asserted is that the count reaching disk is the one the service will honour, that the
/// figures shown are read fresh rather than cached, and that the prune states what it would remove
/// before removing anything and what it did remove afterwards — never the plan reported as the
/// outcome.
/// </summary>
[Collection("app-data-sandbox")]
public class SettingsBackupsSurfaceTests
{
    public SettingsBackupsSurfaceTests()
    {
        TestSandbox.ResetDataDir();
        // The tally and the prune are portfolio-wide, and the sandbox reset clears files rather
        // than folders: a backup directory another test left behind would be counted — and pruned —
        // by assertions written about this test's own repositories.
        TestEnv.TryDeleteTree(SafetyPaths.BackupsRoot);
    }

    private static BackupService NewBackups() => new(new GitService(), new SettingsService());

    /// <summary>Answers the prune confirmation without a window, and keeps what it was asked.</summary>
    private sealed class ConfirmingSettings(SettingsService settings, BackupService backups, bool answer)
        : SettingsViewModel(settings, null!, null!, backups: backups)
    {
        public string LastMessage { get; private set; } = "";

        public int Confirmations { get; private set; }

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastMessage = message;
            return Task.FromResult(answer);
        }
    }

    private static async Task<List<BackupHandle>> SeedBackupsAsync(BackupService service, RailsRepo repo, int count)
    {
        var handles = new List<BackupHandle>();
        for (var i = 0; i < count; i++)
            handles.Add(await service.CreateBackupAsync(repo.Path, "History rewrite"));
        return handles;
    }

    // ── The store reads ─────────────────────────────────────────────────────

    [Fact]
    public async Task Storage_CountsEveryRepositorysBackupsAndWhatTheyOccupy()
    {
        new SettingsService().Save(new AppSettings { BackupRetentionCount = 10 });
        using var first = await RailsRepo.CreateAsync("storage-a");
        using var second = await RailsRepo.CreateAsync("storage-b");
        var service = NewBackups();

        var handles = await SeedBackupsAsync(service, first, 2);
        handles.AddRange(await SeedBackupsAsync(service, second, 1));

        var tally = service.MeasureStorage();

        Assert.Null(tally.Error);
        Assert.Equal(2, tally.RepoCount);
        Assert.Equal(3, tally.BackupCount);
        Assert.Equal(handles.Sum(h => service.MeasureBackupBytes(h) ?? 0), tally.Bytes);
    }

    [Fact]
    public void Storage_WithNothingOnDisk_IsZeroRatherThanUnreadable()
    {
        var tally = NewBackups().MeasureStorage();

        Assert.Null(tally.Error);
        Assert.Equal(0, tally.BackupCount);
        Assert.Equal("No backups are on disk.", SettingsViewModel.DescribeStorage(tally));
    }

    /// <summary>
    /// The plan and the prune read the same newest-first ordering the per-repository prune applies,
    /// so the set a confirmation counted is the set that goes.
    /// </summary>
    [Fact]
    public async Task PruneNow_RemovesExactlyWhatIsPastTheSavedCount_KeepingTheNewest()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 10 });
        using var repo = await RailsRepo.CreateAsync("prune-order");
        var service = NewBackups();
        var handles = await SeedBackupsAsync(service, repo, 5);

        settings.Save(new AppSettings { BackupRetentionCount = 2 });
        var ordered = handles.OrderByDescending(h => h.UtcStamp, StringComparer.Ordinal).ToList();
        var doomed = ordered.Skip(2).ToList();
        var expectedBytes = doomed.Sum(h => service.MeasureBackupBytes(h) ?? 0);

        var plan = service.PreviewPrune();
        Assert.Equal(1, plan.RepoCount);
        Assert.Equal(3, plan.BackupCount);
        Assert.Equal(expectedBytes, plan.Bytes);
        // A preview writes nothing.
        Assert.Equal(5, (await service.ListBackupsAsync(repo.Path)).Count);

        var removed = service.PruneEveryRepository();

        Assert.Null(removed.Error);
        Assert.Equal(3, removed.BackupCount);
        Assert.Equal(expectedBytes, removed.Bytes);
        var survivors = (await service.ListBackupsAsync(repo.Path)).Select(h => h.UtcStamp).ToList();
        Assert.Equal(ordered.Take(2).Select(h => h.UtcStamp), survivors);
        foreach (var gone in doomed)
        {
            Assert.False(File.Exists(gone.BundlePath), $"{gone.UtcStamp} bundle should be pruned");
            Assert.False(File.Exists(gone.RefsSnapshotPath), $"{gone.UtcStamp} sidecar should be pruned");
        }
    }

    [Fact]
    public async Task PruneNow_LeavesARepositoryAlreadyWithinTheCountUntouched()
    {
        new SettingsService().Save(new AppSettings { BackupRetentionCount = 10 });
        using var repo = await RailsRepo.CreateAsync("prune-noop");
        var service = NewBackups();
        await SeedBackupsAsync(service, repo, 2);

        var removed = service.PruneEveryRepository();

        Assert.Equal(0, removed.BackupCount);
        Assert.Equal(0, removed.RepoCount);
        Assert.Equal(2, (await service.ListBackupsAsync(repo.Path)).Count);
    }

    /// <summary>
    /// A count below one keeps one. A destructive operation's whole safety net is the backup it
    /// took, and a setting that pruned every backup would remove it moments after it was written.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    public void EffectiveRetention_NeverFallsBelowOne(int configured, int expected) =>
        Assert.Equal(expected, BackupService.EffectiveRetention(configured));

    // ── The page ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePage_LoadsTheSavedCountAndToggleAndTheStorageFigure()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 4, DeepBackupCapture = true });
        using var repo = await RailsRepo.CreateAsync("settings-load");
        var backups = NewBackups();
        await SeedBackupsAsync(backups, repo, 2);

        var page = new SettingsViewModel(settings, null!, null!, backups: backups);
        await page.BackupStorageLoad;

        Assert.Equal(4, page.BackupRetentionCount);
        Assert.True(page.DeepBackupCapture);
        Assert.Contains("2 backups across 1 repository", page.BackupStorageSummary);
    }

    [Fact]
    public void ThePage_SavesTheCountTheServiceWillHonour_NotTheOneTyped()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());
        var page = new SettingsViewModel(settings, null!, null!, backups: NewBackups())
        {
            BackupRetentionCount = 0,
            DeepBackupCapture = true
        };

        page.SaveSettingsCommand.Execute(null);

        Assert.Equal(1, settings.Load().BackupRetentionCount);
        Assert.True(settings.Load().DeepBackupCapture);
        // Read back, so the field never shows a value the service silently treats as another.
        Assert.Equal(1, page.BackupRetentionCount);
    }

    [Fact]
    public void ThePage_RoundTripsTheCountUnchangedWhenItIsAboveTheFloor()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());
        var page = new SettingsViewModel(settings, null!, null!, backups: NewBackups())
        {
            BackupRetentionCount = 30
        };

        page.SaveSettingsCommand.Execute(null);

        Assert.Equal(30, settings.Load().BackupRetentionCount);
        Assert.False(settings.Load().DeepBackupCapture);
    }

    [Fact]
    public void ThePage_WithNoBackupStore_SaysSoRatherThanReportingNoBackups()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings());

        var page = new SettingsViewModel(settings, null!, null!);

        Assert.Equal(SettingsViewModel.BackupsUnavailable, page.BackupStorageSummary);
    }

    [Fact]
    public async Task PruneNow_StatesWhatWouldGoBeforeAnythingIsDeleted_AndCancellingKeepsItAll()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 10 });
        using var repo = await RailsRepo.CreateAsync("prune-confirm");
        var backups = NewBackups();
        await SeedBackupsAsync(backups, repo, 4);
        settings.Save(new AppSettings { BackupRetentionCount = 1 });

        var page = new ConfirmingSettings(settings, backups, answer: false);
        await page.PruneBackupsNowCommand.ExecuteAsync(null);

        Assert.Equal(1, page.Confirmations);
        Assert.Contains("Prune 3 backups from 1 repository?", page.LastMessage);
        Assert.Contains("About ", page.LastMessage);
        Assert.Contains("Newest backups are kept", page.LastMessage);
        Assert.Contains("cannot be undone", page.LastMessage);
        Assert.Equal(4, (await backups.ListBackupsAsync(repo.Path)).Count);
    }

    [Fact]
    public async Task PruneNow_Confirmed_ReportsWhatWentAndRefreshesTheStorageFigure()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 10 });
        using var repo = await RailsRepo.CreateAsync("prune-confirmed");
        var backups = NewBackups();
        await SeedBackupsAsync(backups, repo, 4);
        settings.Save(new AppSettings { BackupRetentionCount = 1 });

        var page = new ConfirmingSettings(settings, backups, answer: true);
        await page.PruneBackupsNowCommand.ExecuteAsync(null);
        await page.BackupStorageLoad;

        Assert.Contains("Pruned 3 backups from 1 repository", page.BackupPruneStatus);
        Assert.Single(await backups.ListBackupsAsync(repo.Path));
        Assert.Contains("1 backup across 1 repository", page.BackupStorageSummary);
    }

    /// <summary>
    /// Nothing to prune is not a confirmation the reader has to answer, and it names the count that
    /// made it a no-op rather than leaving the button looking broken.
    /// </summary>
    [Fact]
    public async Task PruneNow_WithNothingPastTheCount_AsksNothingAndSaysWhy()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 10 });
        using var repo = await RailsRepo.CreateAsync("prune-nothing");
        var backups = NewBackups();
        await SeedBackupsAsync(backups, repo, 2);

        var page = new ConfirmingSettings(settings, backups, answer: true);
        await page.PruneBackupsNowCommand.ExecuteAsync(null);

        Assert.Equal(0, page.Confirmations);
        Assert.Contains("Nothing to prune", page.BackupPruneStatus);
        Assert.Contains("10 backups", page.BackupPruneStatus);
        Assert.Equal(2, (await backups.ListBackupsAsync(repo.Path)).Count);
    }

    // ── What the figures claim ──────────────────────────────────────────────

    /// <summary>
    /// A walk that did not finish reports a floor, never a total: a reader told 12 backups occupy
    /// 40 MB acts on it, and the same words over a partial read would understate what a prune has
    /// to remove.
    /// </summary>
    [Fact]
    public void APartialWalk_IsStatedAsAFloor()
    {
        var whole = SettingsViewModel.DescribeStorage(new BackupStorageTally(2, 5, 4096, null));
        var partial = SettingsViewModel.DescribeStorage(
            new BackupStorageTally(2, 5, 4096, BackupService.UnsizedNotice));

        Assert.StartsWith("5 backups across 2 repositories", whole);
        Assert.DoesNotContain("At least", whole, StringComparison.Ordinal);
        Assert.StartsWith("At least 5 backups across 2 repositories", partial);
        Assert.Contains(BackupService.UnsizedNotice, partial, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backup another process holds open is still on disk, so it is not counted as reclaimed and
    /// the leftover is named — the difference between a reader checking their free space and a
    /// reader trusting a number.
    /// </summary>
    [Fact]
    public void APruneThatLeftSomethingBehind_SaysSo()
    {
        var clean = SettingsViewModel.DescribePrune(new BackupStorageTally(1, 3, 2048, null));
        var partial = SettingsViewModel.DescribePrune(
            new BackupStorageTally(1, 2, 2048, BackupService.UnremovedNotice));
        var none = SettingsViewModel.DescribePrune(new BackupStorageTally(0, 0, 0, null));

        Assert.StartsWith("Pruned 3 backups from 1 repository", clean);
        Assert.DoesNotContain("Some were left", clean, StringComparison.Ordinal);
        Assert.Contains("Some were left", partial, StringComparison.Ordinal);
        Assert.Contains(BackupService.UnremovedNotice, partial, StringComparison.Ordinal);
        Assert.Equal("Nothing was pruned.", none);
    }

    [Fact]
    public void ThePruneConfirmation_CallsItsSizeAnEstimate()
    {
        var message = SettingsViewModel.PruneMessage(new BackupStorageTally(3, 7, 1048576, null));

        Assert.Contains("Prune 7 backups from 3 repositories?", message);
        Assert.Contains("About 1.0 MB would be freed", message);
        Assert.Contains("Nothing in any repository changes", message);
    }
}
