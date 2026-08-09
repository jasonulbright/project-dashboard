using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Update;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The check as it runs: what it reads, what it refuses to read, what it records, and what
/// it puts on the dashboard. Every read goes through the substituted seam, so the suite
/// makes no outbound request at all — the same guarantee the swapped link launcher gives
/// against a suite run opening a browser.
/// </summary>
[Collection("app-data-sandbox")]
public class UpdateCheckServiceTests
{
    public UpdateCheckServiceTests() => TestSandbox.ResetDataDir();

    /// <summary>
    /// The property the toggle exists for: off means nothing is read, not that a read is
    /// made and its answer discarded.
    /// </summary>
    [Fact]
    public async Task WithTheToggleOff_NothingIsRead_OnEitherPath()
    {
        var settings = Persisted(new AppSettings { EnableUpdateCheck = false });
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));

        var launch = await checker.CheckAsync(manual: false);
        var manual = await checker.CheckAsync(manual: true);

        Assert.Equal(0, checker.Reads);
        Assert.Equal(UpdateOutcome.Disabled, launch.Outcome);
        Assert.Equal(UpdateOutcome.Disabled, manual.Outcome);
        Assert.Equal(UpdateCheckService.DisabledStatus, manual.Status);
        Assert.Null(checker.Available);
        // A refused check records nothing: it learned nothing to record.
        Assert.Null(settings.Load().LastUpdateCheckUtc);
    }

    [Fact]
    public async Task ANewerRelease_IsPublishedAndRecorded()
    {
        var settings = Persisted(new AppSettings());
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));
        var announcements = 0;
        checker.AvailableChanged += () => announcements++;

        var result = await checker.CheckAsync(manual: false);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(1, announcements);
        Assert.Equal("v9.0.0", checker.Available!.TagName);

        var persisted = settings.Load();
        Assert.NotNull(persisted.LastUpdateCheckUtc);
        Assert.Equal(result.Status, persisted.LastUpdateCheckStatus);
    }

    /// <summary>
    /// A release withdrawn between two checks clears the offer, so a notice never outlives
    /// the page it points at.
    /// </summary>
    [Fact]
    public async Task AnAnswerNamingNoNewerVersion_ClearsAnEarlierOffer()
    {
        var settings = Persisted(new AppSettings());
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));
        await checker.CheckAsync(manual: true);
        Assert.NotNull(checker.Available);

        checker.Response = Ok(Release("v1.0.0"));
        await checker.CheckAsync(manual: true);

        Assert.Null(checker.Available);
    }

    /// <summary>
    /// A check that could not read anything knows nothing, so it neither offers nor
    /// withdraws: clearing on a failure would drop a real offer the moment the network did.
    /// </summary>
    [Fact]
    public async Task AFailedCheck_LeavesAnEarlierOfferStanding()
    {
        var settings = Persisted(new AppSettings());
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));
        await checker.CheckAsync(manual: true);

        checker.Response = ReleaseFetch.Unreachable("Couldn't reach GitHub — no such host.");
        await checker.CheckAsync(manual: true);

        Assert.NotNull(checker.Available);
    }

    // ── Cadence ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsideTheCooldown_TheLaunchCheckReadsNothingAndKeepsTheLastOutcome()
    {
        var settings = Persisted(new AppSettings
        {
            LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-1),
            LastUpdateCheckStatus = "Up to date (v2.0.1.0)."
        });
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));

        var result = await checker.CheckAsync(manual: false);

        Assert.Equal(0, checker.Reads);
        Assert.Equal(UpdateOutcome.Cooldown, result.Outcome);
        Assert.Equal("Up to date (v2.0.1.0).", result.Status);
    }

    [Fact]
    public async Task PastTheCooldown_TheLaunchCheckReads()
    {
        var settings = Persisted(new AppSettings
        {
            LastUpdateCheckUtc = DateTimeOffset.UtcNow - UpdateCheckService.LaunchCooldown - TimeSpan.FromMinutes(1)
        });
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));

        var result = await checker.CheckAsync(manual: false);

        Assert.Equal(1, checker.Reads);
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task AManualCheck_IgnoresTheCooldown()
    {
        var settings = Persisted(new AppSettings { LastUpdateCheckUtc = DateTimeOffset.UtcNow });
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));

        var result = await checker.CheckAsync(manual: true);

        Assert.Equal(1, checker.Reads);
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
    }

    /// <summary>
    /// A failure stamps the cooldown too. Failures are the case most likely to repeat, and a
    /// check that retried on every launch is what spends a shared address's quota.
    /// </summary>
    [Fact]
    public async Task AFailedCheck_StillHoldsTheCooldownOff()
    {
        var settings = Persisted(new AppSettings());
        var checker = new StubbedUpdateCheck(settings, new ReleaseFetch(403, "", DateTimeOffset.UtcNow.AddMinutes(20), null));

        var first = await checker.CheckAsync(manual: false);
        var second = await checker.CheckAsync(manual: false);

        Assert.Equal(UpdateOutcome.Failed, first.Outcome);
        Assert.Equal(UpdateOutcome.Cooldown, second.Outcome);
        Assert.Equal(1, checker.Reads);
        // Silent is not hidden: the failure is what the Settings line reports next.
        Assert.Contains("rate limit", settings.Load().LastUpdateCheckStatus);
    }

    /// <summary>A clock that moved backwards must not suppress checks until it catches up.</summary>
    [Fact]
    public async Task AStampInTheFuture_DoesNotSuppressTheCheck()
    {
        var settings = Persisted(new AppSettings { LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(3) });
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));

        await checker.CheckAsync(manual: false);

        Assert.Equal(1, checker.Reads);
    }

    // ── Quiet on launch, specific when asked ────────────────────────────────

    /// <summary>
    /// Both paths run the same read and reach the same conclusion; what differs is who is
    /// told. The launch check publishes no offer and puts nothing on the dashboard, and the
    /// reason it failed survives on the Settings line rather than in nothing at all.
    /// </summary>
    [Fact]
    public async Task ALaunchFailure_ShowsNothingAndStillLeavesItsReasonBehind()
    {
        var settings = Persisted(new AppSettings());
        var reset = DateTimeOffset.UtcNow.AddMinutes(20);
        var checker = new StubbedUpdateCheck(settings, new ReleaseFetch(403, "", reset, null));

        var result = await checker.CheckAsync(manual: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Null(checker.Available);
        Assert.Equal(result.Status, settings.Load().LastUpdateCheckStatus);
        Assert.StartsWith(result.Status, SettingsViewModel.DescribeLastCheck(settings.Load()));
    }

    [Fact]
    public async Task AManualFailure_NamesTheReasonIncludingWhenTheQuotaRefills()
    {
        var settings = Persisted(new AppSettings());
        var reset = DateTimeOffset.UtcNow.AddMinutes(20);
        var checker = new StubbedUpdateCheck(settings, new ReleaseFetch(403, "", reset, null));

        var result = await checker.CheckAsync(manual: true);

        Assert.Contains("rate limit", result.Status);
        Assert.Contains(reset.ToLocalTime().ToString("HH:mm"), result.Status);
    }

    [Fact]
    public async Task AnUnwritableSettingsFile_DoesNotFailTheCheck()
    {
        var settings = Persisted(new AppSettings());
        var checker = new StubbedUpdateCheck(settings, Ok(Release("v9.0.0")));

        UpdateCheckResult result;
        using (new BlockedSettingsWrites())
            result = await checker.CheckAsync(manual: true);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.NotNull(checker.Available);
    }

    // ── The dashboard notice ────────────────────────────────────────────────

    [Fact]
    public async Task AnOfferedUpdate_ReachesTheDashboardAndOpensItsReleasePage()
    {
        var (dashboard, checker) = await NewDashboardAsync("update-offer", Ok(Release("v9.0.0")));

        Assert.True(dashboard.UpdateBannerVisible);
        Assert.Contains("v9.0.0", dashboard.UpdateBannerText);
        Assert.Contains(AppVersionInfo.Display, dashboard.UpdateBannerText);
        Assert.NotNull(checker.Available);

        var launched = new List<string>();
        var shell = ProjectDetailPage.LaunchNavigable;
        ProjectDetailPage.LaunchNavigable = launched.Add;
        try
        {
            dashboard.OpenUpdateReleaseCommand.Execute(null);
        }
        finally
        {
            ProjectDetailPage.LaunchNavigable = shell;
        }

        Assert.Equal(["https://github.com/jasonulbright/project-dashboard/releases/tag/v9.0.0"], launched);
    }

    /// <summary>
    /// The refusal, end to end: a newer release whose link points at another host produces no
    /// notice, so there is no button and nothing that could be launched.
    /// </summary>
    [Fact]
    public async Task AHostileReleaseLink_ProducesNoNoticeAndLaunchesNothing()
    {
        var hostile = Release("v9.0.0", url: "https://evil.example/jasonulbright/project-dashboard/releases/tag/v9.0.0");
        var (dashboard, checker) = await NewDashboardAsync("update-hostile", Ok(hostile));

        Assert.Null(checker.Available);
        Assert.False(dashboard.UpdateBannerVisible);

        var launched = new List<string>();
        var shell = ProjectDetailPage.LaunchNavigable;
        ProjectDetailPage.LaunchNavigable = launched.Add;
        try
        {
            dashboard.OpenUpdateReleaseCommand.Execute(null);
        }
        finally
        {
            ProjectDetailPage.LaunchNavigable = shell;
        }

        Assert.Empty(launched);
        Assert.Contains("refused", dashboard.OpStatusText);
    }

    [Fact]
    public async Task ADismissedNotice_StaysDismissedForTheSession()
    {
        var (dashboard, checker) = await NewDashboardAsync("update-dismiss", Ok(Release("v9.0.0")));
        Assert.True(dashboard.UpdateBannerVisible);

        dashboard.DismissUpdateBannerCommand.Execute(null);
        Assert.False(dashboard.UpdateBannerVisible);

        // A later check that finds the same update does not bring it back.
        await checker.CheckAsync(manual: true);
        Assert.False(dashboard.UpdateBannerVisible);
    }

    // ── The Settings line ───────────────────────────────────────────────────

    [Fact]
    public void TheSettingsLine_ReportsTheStateItIsIn()
    {
        Assert.Equal(UpdateCheckService.DisabledStatus,
            SettingsViewModel.DescribeLastCheck(new AppSettings { EnableUpdateCheck = false }));

        Assert.Equal("Not checked yet.", SettingsViewModel.DescribeLastCheck(new AppSettings()));

        var checked3 = SettingsViewModel.DescribeLastCheck(new AppSettings
        {
            LastUpdateCheckUtc = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
            LastUpdateCheckStatus = "No releases published yet."
        });
        Assert.StartsWith("No releases published yet. Last checked ", checked3);
    }

    [Fact]
    public void TheToggleRoundTripsThroughTheSettingsPage()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { EnableUpdateCheck = true });

        var page = new SettingsViewModel(service, null!, null!);
        Assert.True(page.EnableUpdateCheck);

        page.EnableUpdateCheck = false;
        page.SaveSettingsCommand.Execute(null);

        Assert.False(service.Load().EnableUpdateCheck);
        Assert.Equal(UpdateCheckService.DisabledStatus, page.UpdateCheckStatus);
    }

    /// <summary>
    /// The manual button persists the toggle before it reads, so a tick that has not been
    /// saved is not answered as though the feature were off.
    /// </summary>
    [Fact]
    public async Task TheManualButton_SavesTheToggleBeforeItReads()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { EnableUpdateCheck = false });

        var checker = new StubbedUpdateCheck(service, Ok(Release("v9.0.0")));
        var page = new SettingsViewModel(service, null!, null!, checker)
        {
            EnableUpdateCheck = true
        };

        await page.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, checker.Reads);
        Assert.True(service.Load().EnableUpdateCheck);
        Assert.Contains("v9.0.0", page.UpdateCheckStatus);
    }

    // ── Fixtures and plumbing ───────────────────────────────────────────────

    /// <summary>
    /// The service with its one outbound read replaced. Nothing in the suite reaches the
    /// production read, so no test makes a network request.
    /// </summary>
    private sealed class StubbedUpdateCheck(SettingsService settings, ReleaseFetch response)
        : UpdateCheckService(settings)
    {
        public ReleaseFetch Response { get; set; } = response;

        public int Reads { get; private set; }

        protected internal override Task<ReleaseFetch> FetchLatestAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Response);
        }
    }

    private static SettingsService Persisted(AppSettings settings)
    {
        var service = new SettingsService();
        service.Save(settings);
        return service;
    }

    private static ReleaseFetch Ok(string body) => new(200, body, null, null);

    private static string Release(
        string tag,
        string? url = null,
        bool draft = false,
        bool prerelease = false)
    {
        var link = url ?? $"https://github.com/jasonulbright/project-dashboard/releases/tag/{tag}";
        return $$"""
                 {"tag_name":"{{tag}}","html_url":"{{link}}",
                  "draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}}}
                 """;
    }

    private static async Task<(DashboardViewModel Dashboard, StubbedUpdateCheck Checker)> NewDashboardAsync(
        string prefix, ReleaseFetch response)
    {
        var root = TestEnv.NewDir(prefix);
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
            RefreshIntervalSeconds = 7200,
        });

        var checker = new StubbedUpdateCheck(settings, response);
        // Run before the dashboard exists: the launch check can finish first, and the
        // dashboard has to read the answer rather than wait for an event that already fired.
        await checker.CheckAsync(manual: false);

        var gitHub = new GitHubService(settings);
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            new ProjectWatcherService(),
            new RepoBusyRegistry(),
            uiPost: callback => callback(),
            updateCheck: checker);
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        return (dashboard, checker);
    }

    /// <summary>
    /// Fails every settings write while held: a directory occupying the staging path fails
    /// the write and leaves the live file exactly as it was.
    /// </summary>
    private sealed class BlockedSettingsWrites : IDisposable
    {
        private readonly string _staging = AppPaths.SettingsFile + ".tmp";

        public BlockedSettingsWrites() => Directory.CreateDirectory(_staging);

        public void Dispose() => Directory.Delete(_staging, recursive: true);
    }
}
