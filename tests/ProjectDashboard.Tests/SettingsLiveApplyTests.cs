using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Windows;
using Wpf.Ui.Appearance;

namespace ProjectDashboard.Tests;

/// <summary>
/// Live-apply settings (X-09). One notification path — <see cref="SettingsService.Changed"/>
/// — carries every write to the running app, and <see cref="SettingsDelta"/> decides which
/// consumers it wakes. A trigger that returns false where it should return true is the whole
/// failure this covers: the page shows the new value while the app keeps running the old one
/// until relaunch, with nothing on screen saying so.
/// </summary>
public class SettingsDeltaTests
{
    private static SettingsChange Change(Action<AppSettings> mutate)
    {
        var previous = new AppSettings();
        var current = new AppSettings();
        mutate(current);
        return new SettingsChange(previous, current);
    }

    [Fact]
    public void AnUnchangedWrite_TriggersNothing()
    {
        var change = Change(_ => { });

        Assert.False(SettingsDelta.ThemeChanged(change));
        Assert.False(SettingsDelta.RefreshIntervalChanged(change));
        Assert.False(SettingsDelta.WatcherTargetChanged(change));
        Assert.False(SettingsDelta.RediscoveryRequired(change));
        Assert.False(SettingsDelta.ViewPreferencesChanged(change));
    }

    [Fact]
    public void WindowGeometryAndPaneState_TriggerNothing()
    {
        // The close handler writes these on every exit; waking a re-scan there would
        // re-scan the whole portfolio as the app shuts down.
        var change = Change(s =>
        {
            s.WindowDeviceRect = new SavedWindowRect(10, 20, 800, 600);
            s.WindowMaximized = true;
            s.PaneOpen = false;
        });

        Assert.False(SettingsDelta.ThemeChanged(change));
        Assert.False(SettingsDelta.RefreshIntervalChanged(change));
        Assert.False(SettingsDelta.WatcherTargetChanged(change));
        Assert.False(SettingsDelta.RediscoveryRequired(change));
        Assert.False(SettingsDelta.ViewPreferencesChanged(change));
    }

    [Fact]
    public void ThemeChange_IsDetectedCaseInsensitively()
    {
        Assert.True(SettingsDelta.ThemeChanged(Change(s => s.Theme = "Light")));
        Assert.False(SettingsDelta.ThemeChanged(Change(s => s.Theme = "dark")));
    }

    [Fact]
    public void RefreshInterval_BelowTheFloor_ReadsAsTheFloor()
    {
        Assert.Equal(SettingsDelta.MinimumRefreshSeconds, SettingsDelta.EffectiveRefreshSeconds(0));
        Assert.Equal(SettingsDelta.MinimumRefreshSeconds, SettingsDelta.EffectiveRefreshSeconds(-5));
        Assert.Equal(900, SettingsDelta.EffectiveRefreshSeconds(900));
    }

    [Fact]
    public void TwoIntervalsBelowTheFloor_AreNotAnIntervalChange()
    {
        var change = new SettingsChange(
            new AppSettings { RefreshIntervalSeconds = 1 },
            new AppSettings { RefreshIntervalSeconds = 5 });

        // Both clamp to the same running interval; retuning the timer would only
        // restart its countdown for no change in behaviour.
        Assert.False(SettingsDelta.RefreshIntervalChanged(change));
    }

    [Fact]
    public void RefreshIntervalChange_AboveTheFloor_RetunesTheTimer()
    {
        Assert.True(SettingsDelta.RefreshIntervalChanged(Change(s => s.RefreshIntervalSeconds = 60)));
    }

    [Fact]
    public void DisablingAutoRefresh_EmptiesTheWatcherTarget()
    {
        var change = Change(s => s.EnableAutoRefresh = false);

        Assert.True(SettingsDelta.WatcherTargetChanged(change));
        Assert.Equal("", SettingsDelta.WatcherRoot(change.Current));
    }

    [Fact]
    public void RootChange_WhileAutoRefreshIsOff_LeavesTheWatcherTargetEmpty()
    {
        var change = new SettingsChange(
            new AppSettings { EnableAutoRefresh = false, ProjectsRootPath = @"C:\one" },
            new AppSettings { EnableAutoRefresh = false, ProjectsRootPath = @"C:\two" });

        Assert.False(SettingsDelta.WatcherTargetChanged(change));
        Assert.True(SettingsDelta.RediscoveryRequired(change));
    }

    [Fact]
    public void RootChange_RepointsTheWatcherAndForcesARescan()
    {
        var change = Change(s => s.ProjectsRootPath = @"C:\elsewhere");

        Assert.True(SettingsDelta.WatcherTargetChanged(change));
        Assert.True(SettingsDelta.RediscoveryRequired(change));
    }

    [Fact]
    public void ARootSpelledDifferently_IsNotAChange()
    {
        var change = new SettingsChange(
            new AppSettings { ProjectsRootPath = @"C:\Projects" },
            new AppSettings { ProjectsRootPath = @"c:\projects" });

        Assert.False(SettingsDelta.WatcherTargetChanged(change));
        Assert.False(SettingsDelta.RediscoveryRequired(change));
    }

    [Fact]
    public void ExcludedDirectoriesChange_ForcesARescan()
    {
        Assert.True(SettingsDelta.RediscoveryRequired(Change(s => s.ExcludedDirectories = ["Internal"])));
        Assert.True(SettingsDelta.RediscoveryRequired(Change(s => s.ExcludedDirectories = ["Internal", "games", "extra"])));
        Assert.False(SettingsDelta.RediscoveryRequired(Change(s => s.ExcludedDirectories = ["internal", "GAMES"])));
    }

    [Fact]
    public void GitHubDiscoveryToggle_ForcesARescan()
    {
        Assert.True(SettingsDelta.RediscoveryRequired(Change(s => s.EnableGitHubDiscovery = false)));
    }

    [Fact]
    public void GhPathChange_ForcesARescan_ButSurroundingWhitespaceDoesNot()
    {
        Assert.True(SettingsDelta.RediscoveryRequired(Change(s => s.GhPath = @"C:\tools\gh.exe")));

        var whitespaceOnly = new SettingsChange(
            new AppSettings { GhPath = @"C:\tools\gh.exe" },
            new AppSettings { GhPath = @"  C:\tools\gh.exe  " });
        Assert.False(SettingsDelta.RediscoveryRequired(whitespaceOnly));
    }

    [Fact]
    public void DensityAndPinChanges_ReloadTheGridWithoutARescan()
    {
        var density = Change(s => s.CardDensity = "compact");
        Assert.True(SettingsDelta.ViewPreferencesChanged(density));
        Assert.False(SettingsDelta.RediscoveryRequired(density));

        var pins = Change(s => s.PinnedProjectPaths = [@"C:\projects\alpha"]);
        Assert.True(SettingsDelta.ViewPreferencesChanged(pins));
        Assert.False(SettingsDelta.RediscoveryRequired(pins));
    }

    [Fact]
    public void BackupRetentionAndDangerZone_DriveNoLiveApplyPath()
    {
        // Both are read per use by their consumer, so a write needs no notification.
        var change = Change(s =>
        {
            s.BackupRetentionCount = 25;
            s.DangerZoneEnabled = true;
        });

        Assert.False(SettingsDelta.RediscoveryRequired(change));
        Assert.False(SettingsDelta.ViewPreferencesChanged(change));
        Assert.False(SettingsDelta.WatcherTargetChanged(change));
        Assert.False(SettingsDelta.RefreshIntervalChanged(change));
        Assert.False(SettingsDelta.ThemeChanged(change));
    }
}

/// <summary>
/// The notification itself: a write that reaches disk publishes what changed, a write that
/// fails publishes nothing (a subscriber acting on a save that never landed would apply a
/// value the file does not hold).
/// </summary>
[Collection("app-data-sandbox")]
public class SettingsChangeNotificationTests
{
    public SettingsChangeNotificationTests() => TestSandbox.ResetDataDir();

    [Fact]
    public void SuccessfulSave_PublishesTheStateBeforeAndAfter()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\before", Theme = "Dark" });

        SettingsChange? seen = null;
        service.Changed += change => seen = change;

        service.Save(new AppSettings { ProjectsRootPath = @"C:\after", Theme = "Light" });

        Assert.NotNull(seen);
        Assert.Equal(@"C:\before", seen!.Previous.ProjectsRootPath);
        Assert.Equal(@"C:\after", seen.Current.ProjectsRootPath);
        Assert.True(SettingsDelta.ThemeChanged(seen));
        Assert.True(SettingsDelta.RediscoveryRequired(seen));
    }

    [Fact]
    public void AWriteFromAnotherInstance_IsTheBaselineForTheNextChange()
    {
        var publisher = new SettingsService();
        publisher.Save(new AppSettings { ProjectsRootPath = @"C:\first" });

        SettingsChange? seen = null;
        publisher.Changed += change => seen = change;

        // Some other writer (the window close handler, an external edit) lands in between.
        new SettingsService().Save(new AppSettings { ProjectsRootPath = @"C:\second" });
        publisher.Save(new AppSettings { ProjectsRootPath = @"C:\third" });

        Assert.Equal(@"C:\second", seen!.Previous.ProjectsRootPath);
        Assert.Equal(@"C:\third", seen.Current.ProjectsRootPath);
    }

    [Fact]
    public void FailedSave_PublishesNothing()
    {
        var service = new SettingsService();
        var raised = 0;
        service.Changed += _ => raised++;

        Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            Assert.False(service.Save(new AppSettings { ProjectsRootPath = @"C:\never" }));
        }
        finally
        {
            Directory.Delete(AppPaths.SettingsFile, recursive: true);
        }

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ASubscriberThatSaves_DoesNotDeadlockTheFileLock()
    {
        var service = new SettingsService();
        var reentered = false;
        service.Changed += change =>
        {
            if (reentered) return;
            reentered = true;
            var settings = change.Current;
            settings.CardDensity = "compact";
            service.Save(settings);
        };

        Assert.True(service.Save(new AppSettings { ProjectsRootPath = @"C:\root" }));
        Assert.True(reentered);
        Assert.Equal("compact", service.Load().CardDensity);
    }

    [Fact]
    public void ASubscriberThatThrows_DoesNotFailTheSave()
    {
        var service = new SettingsService();
        service.Changed += _ => throw new InvalidOperationException("subscriber blew up");

        Assert.True(service.Save(new AppSettings { ProjectsRootPath = @"C:\root" }));
        Assert.Equal(@"C:\root", service.Load().ProjectsRootPath);
    }
}

/// <summary>
/// Publication ordering for the Changed event. The event is raised outside the file lock,
/// so with more than one writer the raise order is not the write order; a subscriber handed
/// the older snapshot last would keep running the app on settings the file no longer holds,
/// with nothing to correct it until the next write.
/// </summary>
public class PublicationOrderTests
{
    [Fact]
    public void WritesThatPublishInOrder_AreAllDelivered()
    {
        var order = new PublicationOrder();

        Assert.True(order.TryPublish(order.NextSequence()));
        Assert.True(order.TryPublish(order.NextSequence()));
        Assert.True(order.TryPublish(order.NextSequence()));
    }

    [Fact]
    public void TwoInterleavedWrites_DeliverOnlyTheNewestSnapshot()
    {
        var order = new PublicationOrder();

        // Both writes are stamped before either publishes — the interleaving a writer off
        // the UI thread creates.
        var older = order.NextSequence();
        var newer = order.NextSequence();

        var delivered = new List<string>();
        if (order.TryPublish(newer)) delivered.Add("newer");
        if (order.TryPublish(older)) delivered.Add("older");

        Assert.Equal(["newer"], delivered);
    }

    [Fact]
    public void AWriteAlreadyOvertaken_StaysDropped()
    {
        var order = new PublicationOrder();
        var older = order.NextSequence();

        Assert.True(order.TryPublish(order.NextSequence()));
        Assert.False(order.TryPublish(older));
        Assert.False(order.TryPublish(older));
    }
}

/// <summary>
/// The gate on a settings-driven re-scan. R-08 keeps a repository under a rewrite or
/// surgery off-limits to background readers; a settings change must queue behind that
/// operation rather than interrupt it or be dropped.
/// </summary>
public class DashboardRescanTests
{
    [Fact]
    public void WithNothingRunning_TheRescanRunsNow()
    {
        Assert.True(DashboardRescan.Allowed(
            bulkOpRunning: false, anyRepoBusy: false, loadRunning: false, forceRefreshRunning: false));
    }

    [Fact]
    public void ARepoUnderARewrite_QueuesTheRescan()
    {
        Assert.False(DashboardRescan.Allowed(
            bulkOpRunning: false, anyRepoBusy: true, loadRunning: false, forceRefreshRunning: false));
    }

    [Fact]
    public void ABulkOperationOrAnInFlightScan_QueuesTheRescan()
    {
        Assert.False(DashboardRescan.Allowed(
            bulkOpRunning: true, anyRepoBusy: false, loadRunning: false, forceRefreshRunning: false));
        Assert.False(DashboardRescan.Allowed(
            bulkOpRunning: false, anyRepoBusy: false, loadRunning: true, forceRefreshRunning: false));
        Assert.False(DashboardRescan.Allowed(
            bulkOpRunning: false, anyRepoBusy: false, loadRunning: false, forceRefreshRunning: true));
    }

    [Fact]
    public void TheQueuedAndRunningNotices_AreDistinct()
    {
        Assert.NotEqual(DashboardRescan.RunningStatus, DashboardRescan.QueuedStatus);
        Assert.NotEqual("", DashboardRescan.RunningStatus);
        Assert.NotEqual("", DashboardRescan.QueuedStatus);
    }
}

/// <summary>
/// The busy-lease aggregate the re-scan gate reads. Without it the gate would have to
/// enumerate every discovered project, and a repo that has not been discovered yet — the
/// exact case a root change creates — would not be checked at all.
/// </summary>
public class RepoBusyRegistryAnyBusyTests
{
    [Fact]
    public void AnEmptyRegistry_IsNotBusy()
    {
        Assert.False(new RepoBusyRegistry().AnyBusy);
    }

    [Fact]
    public void AnyBusy_TracksTheLastOutstandingLease()
    {
        var registry = new RepoBusyRegistry();
        var first = registry.Acquire(Path.Combine(Path.GetTempPath(), "pd-anybusy-one"));
        var second = registry.Acquire(Path.Combine(Path.GetTempPath(), "pd-anybusy-two"));

        Assert.True(registry.AnyBusy);
        first.Dispose();
        Assert.True(registry.AnyBusy);
        second.Dispose();
        Assert.False(registry.AnyBusy);
    }
}

/// <summary>
/// Theme application is the shell's job: the Settings page is constructed on first
/// navigation, so a launch that never opens Settings would otherwise render in the XAML
/// default whatever the user saved.
/// </summary>
public class ThemeApplicationTests
{
    [Fact]
    public void ASavedThemeDifferentFromTheAppliedOne_IsApplied()
    {
        Assert.Equal(ApplicationTheme.Light, MainWindow.ThemeToApply("Light", ApplicationTheme.Dark));
    }

    [Fact]
    public void TheThemeAlreadyInForce_IsNotReapplied()
    {
        Assert.Null(MainWindow.ThemeToApply("Dark", ApplicationTheme.Dark));
    }

    [Fact]
    public void AnUnparseableSavedTheme_LeavesTheRunningThemeAlone()
    {
        Assert.Null(MainWindow.ThemeToApply("Chartreuse", ApplicationTheme.Dark));
        Assert.Null(MainWindow.ThemeToApply("", ApplicationTheme.Light));
    }
}

/// <summary>
/// The Save notice. It has to keep reporting a failed write, and now also report a re-scan
/// that could not start yet — a queued re-scan with a bare "Saved" reads as a save that did
/// nothing to the grid.
/// </summary>
public class SettingsSaveNoticeTests
{
    private static readonly DateTime At = new(2026, 8, 7, 14, 5, 9);

    [Fact]
    public void WithNoRescan_TheNoticeIsTheSaveTimeAlone()
    {
        Assert.Equal("Saved at 14:05:09", SettingsViewModel.SavedMessage(At, ""));
    }

    [Fact]
    public void AQueuedRescan_IsCarriedIntoTheNotice()
    {
        Assert.Equal(
            $"Saved at 14:05:09 — {SettingsViewModel.QueuedRescanNotice}",
            SettingsViewModel.SavedMessage(At, DashboardRescan.QueuedStatus));
    }

    [Fact]
    public void ARunningRescan_IsNotCarriedIntoTheNotice()
    {
        // The scan finishes seconds later; the notice stays until the next save, so a
        // snapshot of it would leave the page claiming a scan is running all session.
        Assert.Equal("Saved at 14:05:09", SettingsViewModel.SavedMessage(At, DashboardRescan.RunningStatus));
    }
}

/// <summary>
/// The wiring, end to end: a real dashboard over real services against fixture roots under
/// %TEMP%. The pure tests above prove which paths a change should wake; these prove the
/// dashboard is actually subscribed and that each path lands on the running app.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardLiveApplyTests
{
    public DashboardLiveApplyTests() => TestSandbox.ResetDataDir();

    private static AppSettings BaseSettings(string root) => new()
    {
        ProjectsRootPath = root,
        // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
        GhPath = Path.Combine(root, "no-such-gh.exe"),
        EnableGitHubDiscovery = false,
        RefreshIntervalSeconds = 7200,
    };

    private static DashboardViewModel NewDashboard(
        SettingsService settings, ProjectWatcherService watcher, RepoBusyRegistry busy,
        ProjectDiscoveryService? discovery = null)
    {
        var gitHub = new GitHubService(settings);
        return new DashboardViewModel(
            discovery ?? new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            watcher,
            busy,
            // There is no Application in the test host, so the default post target has no
            // dispatcher and would drop every callback — including the lease-release drain,
            // which is the only signal that starts a queued re-scan.
            uiPost: callback => callback());
    }

    /// <summary>
    /// A discovery service whose force refresh parks until released, so another scan trigger
    /// can be fired while the first scan is provably still in flight, and counts how many
    /// full fan-outs actually started.
    /// </summary>
    private sealed class GatedDiscovery : ProjectDiscoveryService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public GatedDiscovery(SettingsService settings, GitHubService gitHub)
            : base(new GitService(), gitHub, settings, new ManifestStore()) { }

        public int Started => Volatile.Read(ref _started);

        public void Release() => _gate.TrySetResult();

        public override async Task<List<ProjectInfo>> ForceRefreshAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _started);
            await _gate.Task;
            return await base.ForceRefreshAllAsync(ct);
        }
    }

    [Fact]
    public async Task SavedRefreshInterval_RetunesTheRunningTimer()
    {
        var root = TestEnv.NewDir("live-interval");
        var settings = new SettingsService();
        settings.Save(BaseSettings(root));

        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, new RepoBusyRegistry());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        Assert.Equal(TimeSpan.FromSeconds(7200), dashboard.RefreshInterval);

        var updated = settings.Load();
        updated.RefreshIntervalSeconds = 45;
        settings.Save(updated);

        Assert.Equal(TimeSpan.FromSeconds(45), dashboard.RefreshInterval);
    }

    [Fact]
    public async Task TogglingAutoRefresh_StopsAndRestartsTheWatcherWithoutARelaunch()
    {
        var root = TestEnv.NewDir("live-watcher");
        var settings = new SettingsService();
        settings.Save(BaseSettings(root));

        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, new RepoBusyRegistry());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        Assert.Equal(root, watcher.WatchedRoot, ignoreCase: true);

        var off = settings.Load();
        off.EnableAutoRefresh = false;
        settings.Save(off);
        Assert.Equal("", watcher.WatchedRoot);

        var on = settings.Load();
        on.EnableAutoRefresh = true;
        settings.Save(on);
        Assert.Equal(root, watcher.WatchedRoot, ignoreCase: true);
    }

    [Fact]
    public async Task ANewProjectsRoot_RediscoversWithoutARelaunch()
    {
        var first = TestEnv.NewDir("live-root-first");
        var second = TestEnv.NewDir("live-root-second");
        var settings = new SettingsService();
        settings.Save(BaseSettings(first));

        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, new RepoBusyRegistry());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        Assert.Equal(first, dashboard.ConfiguredRootPath);

        var moved = settings.Load();
        moved.ProjectsRootPath = second;
        settings.Save(moved);
        await dashboard.PendingRescan;

        // The re-scan is what re-probes the root; the discovery cache is keyed on age
        // alone, so a plain reload would keep serving the first root's projects.
        Assert.Equal(second, dashboard.ConfiguredRootPath);
        Assert.Equal(second, watcher.WatchedRoot, ignoreCase: true);
        Assert.Equal("", dashboard.RescanStatus);
    }

    [Fact]
    public async Task ARootChangeDuringARepoOperation_QueuesTheRescanAndSaysSo()
    {
        var first = TestEnv.NewDir("live-busy-first");
        var second = TestEnv.NewDir("live-busy-second");
        var settings = new SettingsService();
        settings.Save(BaseSettings(first));

        using var watcher = new ProjectWatcherService();
        var busy = new RepoBusyRegistry();
        var dashboard = NewDashboard(settings, watcher, busy);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        // A rewrite holds the lease: nothing may read the repositories underneath it.
        var lease = busy.Acquire(TestEnv.NewDir("live-busy-repo"));

        var moved = settings.Load();
        moved.ProjectsRootPath = second;
        settings.Save(moved);
        await dashboard.PendingRescan;

        Assert.Equal(DashboardRescan.QueuedStatus, dashboard.RescanStatus);
        Assert.Equal(first, dashboard.ConfiguredRootPath);
        // The watcher is not a repository reader; it re-points immediately.
        Assert.Equal(second, watcher.WatchedRoot, ignoreCase: true);

        // Releasing the last lease is the only signal left — no further settings write is
        // coming — so the queued scan has to start from the registry notification alone.
        lease.Dispose();
        await dashboard.PendingRescan;

        Assert.Equal(second, dashboard.ConfiguredRootPath);
        Assert.Equal("", dashboard.RescanStatus);
    }

    [Fact]
    public async Task AScanTriggeredDuringADrain_IsCoalescedIntoIt()
    {
        var first = TestEnv.NewDir("live-drain-first");
        var second = TestEnv.NewDir("live-drain-second");
        var settings = new SettingsService();
        settings.Save(BaseSettings(first));

        using var watcher = new ProjectWatcherService();
        var discovery = new GatedDiscovery(settings, new GitHubService(settings));
        var dashboard = NewDashboard(settings, watcher, new RepoBusyRegistry(), discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var moved = settings.Load();
        moved.ProjectsRootPath = second;
        settings.Save(moved);

        // The drain is parked inside the gated scan, and the command says so — the toolbar
        // button is disabled for the whole drain, not just for a direct press.
        Assert.Equal(1, discovery.Started);
        Assert.True(dashboard.ForceRefreshCommand.IsRunning);
        Assert.False(dashboard.ForceRefreshCommand.CanExecute(null));

        // The palette/F5 and Settings "Force sync" paths execute without consulting
        // CanExecute; both must join the running scan rather than start a second fan-out.
        dashboard.ForceRefreshCommand.Execute(null);
        var forceSync = dashboard.ForceRefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, discovery.Started);

        discovery.Release();
        await forceSync;
        await dashboard.PendingRescan;

        Assert.Equal(1, discovery.Started);
        Assert.Equal(second, dashboard.ConfiguredRootPath);
        Assert.Equal("", dashboard.RescanStatus);
    }

    [Fact]
    public async Task ARootChangeDuringAProjectScaffold_QueuesTheRescanAndLandsOnRelease()
    {
        var first = TestEnv.NewDir("live-scaffold-first");
        var second = TestEnv.NewDir("live-scaffold-second");
        var settings = new SettingsService();
        settings.Save(BaseSettings(first));

        using var watcher = new ProjectWatcherService();
        var discovery = new GatedDiscovery(settings, new GitHubService(settings));
        var dashboard = NewDashboard(settings, watcher, new RepoBusyRegistry(), discovery);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        Assert.Equal(first, dashboard.ConfiguredRootPath);

        // New Project refreshes off the command, so nothing but its own flag tells the
        // re-scan gate that a scan owns the project list.
        var scaffold = dashboard.ScaffoldProjectAsync(Path.Combine(first, "alpha"), "alpha");
        await WaitUntil(() => discovery.Started == 1);

        var moved = settings.Load();
        moved.ProjectsRootPath = second;
        settings.Save(moved);

        // Coalescing onto the parked scan would hand the root change a scan that read the
        // old root before the write existed, and drop it with nothing left to re-fire.
        Assert.Equal(DashboardRescan.QueuedStatus, dashboard.RescanStatus);
        Assert.True(dashboard.RescanQueued);
        Assert.Equal(first, dashboard.ConfiguredRootPath);
        Assert.Equal(1, discovery.Started);

        discovery.Release();
        Assert.Null(await scaffold);
        await dashboard.PendingRescan;

        Assert.Equal(2, discovery.Started);
        Assert.Equal(second, dashboard.ConfiguredRootPath);
        Assert.Equal("", dashboard.RescanStatus);
    }

    /// <summary>Polls until the condition holds; a scan starts on a continuation, not inline.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the awaited condition never became true");
            await Task.Delay(15);
        }
    }

    [Fact]
    public async Task HidingWithTheRescanQueued_SaysTheGridHasNotCaughtUp()
    {
        var root = TestEnv.NewDir("live-hide");
        var settings = new SettingsService();
        settings.Save(BaseSettings(root));

        using var watcher = new ProjectWatcherService();
        var busy = new RepoBusyRegistry();
        var dashboard = NewDashboard(settings, watcher, busy);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var project = new ProjectInfo
        {
            DirectoryName = "alpha",
            DisplayName = "alpha",
            FullPath = Path.Combine(root, "alpha")
        };

        var lease = busy.Acquire(TestEnv.NewDir("live-hide-repo"));
        await dashboard.HideProjectCommand.ExecuteAsync(project);

        // The card is still on the grid: without a notice the click reads as ignored.
        Assert.Equal(DashboardRescan.QueuedStatus, dashboard.RescanStatus);
        Assert.Contains("alpha", dashboard.OpStatusText);
        Assert.Contains("queued rescan", dashboard.OpStatusText);

        lease.Dispose();
        await dashboard.PendingRescan;
    }

    [Fact]
    public async Task UnhidingWithTheRescanQueued_SaysTheGridHasNotCaughtUp()
    {
        var root = TestEnv.NewDir("live-unhide");
        var settings = new SettingsService();
        var seeded = BaseSettings(root);
        seeded.ExcludedDirectories = ["alpha"];
        settings.Save(seeded);

        using var watcher = new ProjectWatcherService();
        var busy = new RepoBusyRegistry();
        var dashboard = NewDashboard(settings, watcher, busy);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        var project = new ProjectInfo
        {
            DirectoryName = "alpha",
            DisplayName = "alpha",
            FullPath = Path.Combine(root, "alpha")
        };

        var lease = busy.Acquire(TestEnv.NewDir("live-unhide-repo"));
        await dashboard.UnhideProjectCommand.ExecuteAsync(project);

        // It has left the hidden view and has not reached the grid; saying nothing would
        // leave the repository apparently gone from both.
        Assert.Equal(DashboardRescan.QueuedStatus, dashboard.RescanStatus);
        Assert.Contains("alpha", dashboard.OpStatusText);
        Assert.Contains("queued rescan", dashboard.OpStatusText);

        lease.Dispose();
        await dashboard.PendingRescan;
    }

    [Fact]
    public async Task ADensityChangeWrittenElsewhere_ReachesTheGrid()
    {
        var root = TestEnv.NewDir("live-density");
        var settings = new SettingsService();
        settings.Save(BaseSettings(root));

        using var watcher = new ProjectWatcherService();
        var dashboard = NewDashboard(settings, watcher, new RepoBusyRegistry());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        Assert.False(dashboard.IsCompactDensity);

        var compact = settings.Load();
        compact.CardDensity = "compact";
        settings.Save(compact);

        Assert.True(dashboard.IsCompactDensity);
    }
}
