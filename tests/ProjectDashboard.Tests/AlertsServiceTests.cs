using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The conditional-read machinery: what a `gh api --include` answer parses to, how a 304 keeps
/// the held count, how a refusal is held as words rather than as a zero, and which failures are
/// answers at all. State lives under the app-data sandbox.
/// </summary>
[Collection("app-data-sandbox")]
public class AlertsServiceTests
{
    public AlertsServiceTests() => TestSandbox.ResetDataDir();

    private const string Slug = "acme/widgets";

    private static readonly int SourceCount = AlertsService.Sources.Count;

    private static string Ok(string etag, string body, string link = "") =>
        "HTTP/2.0 200 OK\r\n"
        + $"Etag: \"{etag}\"\r\n"
        + (link.Length > 0 ? $"Link: {link}\r\n" : "")
        + "\r\n"
        + body;

    // ── Parsing ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnOkAnswerWithAnEmptyArray_IsZeroAlertsWithItsETag()
    {
        var read = AlertsService.ParseApiResponse(Ok("abc123", "[]"), "");

        Assert.Equal(200, read.Status);
        Assert.Equal("\"abc123\"", read.ETag);
        Assert.Equal(0, read.Count);
    }

    /// <summary>One item per page makes the last-page number the count; no Link with one item is one.</summary>
    [Fact]
    public void TheOpenCount_ComesFromTheLastPageLink()
    {
        var linked = AlertsService.ParseApiResponse(
            Ok("e", "[{}]", "<https://api.github.com/repos/a/b/dependabot/alerts?state=open&per_page=1&page=7>; rel=\"last\", <https://api.github.com/x?page=2>; rel=\"next\""),
            "");
        Assert.Equal(7, linked.Count);

        var single = AlertsService.ParseApiResponse(Ok("e", "[{\"number\":1}]"), "");
        Assert.Equal(1, single.Count);
    }

    /// <summary>gh exits nonzero on a 304, so the status line decides and the exit code never does.</summary>
    [Fact]
    public void ANotModifiedAnswer_IsReadFromTheStatusLine()
    {
        var read = AlertsService.ParseApiResponse("HTTP/2.0 304 Not Modified\r\nDate: x\r\n\r\n", "gh: HTTP 304");

        Assert.Equal(304, read.Status);
    }

    [Fact]
    public void ARefusalCarriesItsStatusAndMessage_AndNoAnswerAtAllIsStatusZero()
    {
        var refused = AlertsService.ParseApiResponse(
            "HTTP/2.0 404 Not Found\r\n\r\n{\"message\":\"Not Found\"}", "");
        Assert.Equal(404, refused.Status);
        Assert.Equal("Not Found", refused.Message);

        var dead = AlertsService.ParseApiResponse("", "gh: could not connect");
        Assert.Equal(0, dead.Status);
    }

    [Fact]
    public void TheRefusalWording_NamesTheSourceAndNeverClaimsZero()
    {
        var forbidden = AlertsService.Refusal(AlertSource.SecretScanning, 403, "Must have admin rights");
        Assert.Contains("secret scanning", forbidden);
        Assert.Contains("403", forbidden);
        Assert.DoesNotContain("0 ", forbidden);

        var missing = AlertsService.Refusal(AlertSource.CodeScanning, 404, "");
        Assert.Contains("not enabled", missing);
    }

    /// <summary>A rate limit says nothing about the repository; calling it a token failure would misdirect the fix.</summary>
    [Fact]
    public void ARateLimit403_IsRecognisedAsNoVerdict()
    {
        Assert.True(AlertsService.IsRateLimited("API rate limit exceeded for user"));
        Assert.False(AlertsService.IsRateLimited("Resource not accessible by personal access token"));
    }

    // ── The conditional loop ────────────────────────────────────────────────

    /// <summary>Answers each source read from a queue, recording the args each call carried.</summary>
    private sealed class CannedGitHubService : GitHubService
    {
        public CannedGitHubService() : base(new SettingsService()) { }

        public Queue<ProcessResult> Answers { get; } = new();

        public List<List<string>> Calls { get; } = [];

        public override Task<ProcessResult> RunAsync(
            IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Calls.Add([.. args]);
            return Task.FromResult(Answers.Dequeue());
        }
    }

    private static ProcessResult Answer(string stdout, int exit = 0, string stderr = "") =>
        new(exit, stdout, stderr, TimedOut: false);

    private static void EnqueueOk(CannedGitHubService gh, string etagPrefix = "tag")
    {
        for (var i = 0; i < AlertsService.Sources.Count; i++)
            gh.Answers.Enqueue(Answer(Ok($"{etagPrefix}{i}", "[]")));
    }

    [Fact]
    public async Task AFirstRead_CoversAllFiveSources_AndSendsEachETagOnTheNextOne()
    {
        var gh = new CannedGitHubService();
        EnqueueOk(gh);
        var service = new AlertsService(gh);

        var first = await service.RefreshAsync(Slug);
        Assert.Equal(SourceCount, first.Changed);
        Assert.Equal(0, service.Cached(Slug, AlertSource.Dependabot)!.Count);
        Assert.Equal(0, service.Cached(Slug, AlertSource.IssuesAndPrs)!.Count);
        Assert.Equal(0, service.Cached(Slug, AlertSource.PullRequests)!.Count);
        Assert.All(gh.Calls, call => Assert.DoesNotContain("-H", call));
        Assert.Contains(gh.Calls, call => call.Last().Contains("/issues?state=open"));
        Assert.Contains(gh.Calls, call => call.Last().Contains("/pulls?state=open"));

        gh.Calls.Clear();
        for (var i = 0; i < SourceCount; i++)
            gh.Answers.Enqueue(Answer("HTTP/2.0 304 Not Modified\r\n\r\n", exit: 1, stderr: "gh: HTTP 304"));
        var second = await service.RefreshAsync(Slug);

        Assert.Equal(SourceCount, second.Unchanged);
        Assert.All(gh.Calls, call =>
        {
            var header = call[call.IndexOf("-H") + 1];
            Assert.StartsWith("If-None-Match: \"tag", header);
        });
    }

    [Fact]
    public async Task ARefusedSource_IsHeldAsItsReasonAndAskedAgainNextTime()
    {
        var gh = new CannedGitHubService();
        gh.Answers.Enqueue(Answer(Ok("d", "[]")));
        gh.Answers.Enqueue(Answer(Ok("c", "[]")));
        gh.Answers.Enqueue(Answer(
            "HTTP/2.0 404 Not Found\r\n\r\n{\"message\":\"Not Found\"}", exit: 1, stderr: "gh: HTTP 404"));
        gh.Answers.Enqueue(Answer(Ok("i", "[]")));
        gh.Answers.Enqueue(Answer(Ok("p", "[]")));
        var service = new AlertsService(gh);

        var outcome = await service.RefreshAsync(Slug);
        Assert.Equal(1, outcome.Refused);
        var held = service.Cached(Slug, AlertSource.SecretScanning)!;
        Assert.Null(held.Count);
        Assert.Contains("secret scanning", held.Unreadable);

        // The next pass asks again — a scope granted since must be discoverable.
        gh.Answers.Enqueue(Answer(Ok("d2", "[]")));
        gh.Answers.Enqueue(Answer(Ok("c2", "[]")));
        gh.Answers.Enqueue(Answer(Ok("s2", "[{}]", "<https://x?per_page=1&page=2>; rel=\"last\"")));
        gh.Answers.Enqueue(Answer(Ok("i2", "[]")));
        gh.Answers.Enqueue(Answer(Ok("p2", "[]")));
        await service.RefreshAsync(Slug);

        var recovered = service.Cached(Slug, AlertSource.SecretScanning)!;
        Assert.Equal(2, recovered.Count);
        Assert.Equal("", recovered.Unreadable);
    }

    /// <summary>
    /// No reply is not an answer: the held value stays, its stamp untouched, and the outcome
    /// says unanswered — never unchanged, which is a claim GitHub did not make.
    /// </summary>
    [Fact]
    public async Task AGhThatCannotAnswer_LeavesTheHeldAnswerStanding_AndIsCountedUnansweredNotConfirmed()
    {
        var gh = new CannedGitHubService();
        for (var i = 0; i < SourceCount; i++)
            gh.Answers.Enqueue(Answer(Ok("t", "[{}]", "<https://x?per_page=1&page=4>; rel=\"last\"")));
        var service = new AlertsService(gh);
        await service.RefreshAsync(Slug);
        var stampBefore = service.Cached(Slug, AlertSource.Dependabot)!.FetchedUtc;

        for (var i = 0; i < SourceCount; i++) gh.Answers.Enqueue(Answer("", exit: 1, stderr: "gh: spawn failed"));
        var outcome = await service.RefreshAsync(Slug);

        Assert.Equal(SourceCount, outcome.Unanswered);
        Assert.Equal(0, outcome.Unchanged);
        var held = service.Cached(Slug, AlertSource.Dependabot)!;
        Assert.Equal(4, held.Count);
        Assert.Equal(stampBefore, held.FetchedUtc);
    }

    /// <summary>A rate-limited 403 is no verdict either: the held answer must not become a scope refusal.</summary>
    [Fact]
    public async Task ARateLimited403_KeepsTheHeldAnswerInsteadOfCallingItAPermissionFailure()
    {
        var gh = new CannedGitHubService();
        for (var i = 0; i < SourceCount; i++)
            gh.Answers.Enqueue(Answer(Ok("t", "[{}]")));
        var service = new AlertsService(gh);
        await service.RefreshAsync(Slug);

        for (var i = 0; i < SourceCount; i++)
            gh.Answers.Enqueue(Answer(
                "HTTP/2.0 403 Forbidden\r\n\r\n{\"message\":\"API rate limit exceeded\"}",
                exit: 1, stderr: "gh: HTTP 403"));
        var outcome = await service.RefreshAsync(Slug);

        Assert.Equal(SourceCount, outcome.Unanswered);
        Assert.Equal(0, outcome.Refused);
        Assert.Equal(1, service.Cached(Slug, AlertSource.Dependabot)!.Count);
        Assert.Equal("", service.Cached(Slug, AlertSource.Dependabot)!.Unreadable);
    }

    [Fact]
    public async Task TheCache_SurvivesToANewServiceInstance()
    {
        var gh = new CannedGitHubService();
        EnqueueOk(gh, "t");
        var service = new AlertsService(gh);
        service.EnsureAccount("jason@github.com");
        await service.RefreshAsync(Slug);

        var reloaded = new AlertsService(new CannedGitHubService());

        Assert.Equal(0, reloaded.Cached(Slug, AlertSource.CodeScanning)!.Count);
        Assert.Equal("\"t1\"", reloaded.Cached(Slug, AlertSource.CodeScanning)!.ETag);
    }

    /// <summary>
    /// What a token can see is a property of the account. Answers read as one identity are
    /// dropped whole when another takes over; an unknown identity drops nothing, because trading
    /// known facts for none would be the worse claim.
    /// </summary>
    [Fact]
    public async Task ADifferentGhIdentity_DropsTheCacheWhole_AndAnUnknownOneKeepsIt()
    {
        var gh = new CannedGitHubService();
        EnqueueOk(gh);
        var service = new AlertsService(gh);
        service.EnsureAccount("alice@github.com");
        await service.RefreshAsync(Slug);
        Assert.NotNull(service.Cached(Slug, AlertSource.Dependabot));

        service.EnsureAccount("");
        Assert.NotNull(service.Cached(Slug, AlertSource.Dependabot));

        service.EnsureAccount("bob@github.com");
        Assert.Null(service.Cached(Slug, AlertSource.Dependabot));
    }
}

/// <summary>
/// The rows the Alerts page shows: one per GitHub repository however many clones, what a
/// non-GitHub remote's cells say, what the filters hide, and what a pass report may claim.
/// </summary>
[Collection("app-data-sandbox")]
public class AlertsViewModelTests
{
    public AlertsViewModelTests() => TestSandbox.ResetDataDir();

    private static ProjectInfo Project(string name, string remote = "", int? issues = null)
    {
        var p = new ProjectInfo
        {
            DirectoryName = name,
            DisplayName = name,
            FullPath = $@"C:\projects\{name}",
            OpenIssueCount = issues,
        };
        p.GitStatus.RemoteUrl = remote;
        return p;
    }

    private static async Task<(AlertsViewModel Alerts, AlertsService Service)> NewAlertsAsync(
        params ProjectInfo[] projects)
    {
        var root = TestEnv.NewDir("alerts-vm");
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            GhPath = System.IO.Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
            RefreshIntervalSeconds = 7200,
        });
        var gitHub = new GitHubService(settings);
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!, settings, gitHub, new GitService(),
            new ProjectWatcherService(), new RepoBusyRegistry(),
            uiPost: callback => callback());
        await dashboard.LoadProjectsCommand.ExecutionTask!;
        foreach (var project in projects) dashboard.Projects.Add(project);
        var service = new AlertsService(gitHub);
        return (new AlertsViewModel(dashboard, service, gitHub), service);
    }

    [Fact]
    public async Task Opening_MakesNoRequestAndSaysTheRowsAreFromTheCache()
    {
        var (alerts, _) = await NewAlertsAsync(Project("hub", "https://github.com/acme/hub.git"));

        alerts.Open();

        Assert.Contains("Opened from what was last read", alerts.StatusText);
        Assert.Equal("not read yet", alerts.Rows.Single().AsOfText);
    }

    [Fact]
    public async Task ARowWithAGitHubRemote_CarriesItsSlug_AndANonGitHubRemoteSaysWhyItsCellsAreEmpty()
    {
        var (alerts, _) = await NewAlertsAsync(
            Project("hub", "https://github.com/acme/hub.git"),
            Project("lab", "git@gitlab.example.com:team/lab.git"));
        alerts.Open();

        var hub = alerts.Rows.Single(r => r.Name == "hub");
        Assert.Equal("acme/hub", hub.Slug);

        var lab = alerts.Rows.Single(r => r.Name == "lab");
        Assert.Equal("", lab.Slug);
        Assert.Equal("—", lab.DependabotText);
        Assert.Contains("not GitHub", lab.DependabotDetail);
    }

    /// <summary>Two checkouts of one repository are one repository: one row, one crawl target.</summary>
    [Fact]
    public async Task TwoClonesOfOneSlug_ShareOneRow()
    {
        var (alerts, _) = await NewAlertsAsync(
            Project("hub", "https://github.com/acme/hub.git"),
            Project("hub-worktree", "https://github.com/acme/hub.git"));
        alerts.Open();

        var row = Assert.Single(alerts.Rows);
        Assert.Equal("acme/hub", row.Slug);
        Assert.Equal("hub, hub-worktree", row.Name);
    }

    [Fact]
    public async Task AnUnfetchedIssueCount_IsADashNeverAZero()
    {
        var (alerts, _) = await NewAlertsAsync(Project("quiet"));
        alerts.Open();

        Assert.Equal("—", alerts.Rows.Single().IssuesText);
    }

    [Fact]
    public async Task TheOnlyWithAlertsFilter_HidesQuietRowsAndTheSourceComboNarrowsIt()
    {
        var (alerts, _) = await NewAlertsAsync(
            Project("busy", issues: 3),
            Project("quiet", issues: 0));
        alerts.Open();
        Assert.Equal(2, alerts.Rows.Count);

        alerts.OnlyWithAlerts = true;
        Assert.Equal("busy", alerts.Rows.Single().Name);

        alerts.SourceFilter = "Dependabot";
        Assert.Empty(alerts.Rows);
        Assert.Contains("filter hides rows", alerts.EmptyNotice);
    }

    /// <summary>A refusal is on the row in words, not only behind a tooltip, and the row's name carries it.</summary>
    [Fact]
    public async Task ARefusedSource_SaysUnreadableOnTheRowAndInItsAccessibleName()
    {
        // Seeded before the page's own service loads the cache: the service reads it at
        // construction and never re-reads the file.
        WriteRefusal("acme/hub");
        var (alerts, _) = await NewAlertsAsync(Project("hub", "https://github.com/acme/hub.git"));
        alerts.Open();

        var row = alerts.Rows.Single();
        Assert.Equal("unreadable", row.SecretScanningText);
        Assert.Contains("secret scanning", row.RefusalSummary);
        Assert.Contains("secret scanning", row.AccessibleName);
    }

    /// <summary>Writes one refused secret-scanning read into the cache through the service's own path.</summary>
    private static void WriteRefusal(string slug) =>
        new AlertsService(new RefusingGh()).RefreshAsync(slug).GetAwaiter().GetResult();

    private sealed class RefusingGh : GitHubService
    {
        public RefusingGh() : base(new SettingsService()) { }

        public override Task<ProcessResult> RunAsync(
            IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var path = args.Last();
            return Task.FromResult(path.Contains("secret-scanning")
                ? new ProcessResult(1, "HTTP/2.0 404 Not Found\r\n\r\n{\"message\":\"Not Found\"}", "gh: HTTP 404", false)
                : new ProcessResult(0, "HTTP/2.0 200 OK\r\nEtag: \"e\"\r\n\r\n[]", "", false));
        }
    }

    [Fact]
    public async Task WithNoGitHubRepositories_ARefreshSaysSoInsteadOfSpinningQuietly()
    {
        var (alerts, _) = await NewAlertsAsync(Project("local-only"));
        alerts.Open();

        alerts.RefreshAllCommand.Execute(null);
        await alerts.RefreshPass;

        Assert.Contains("no discovered repository has a GitHub remote", alerts.StatusText);
    }

    /// <summary>The pass report keeps its outcome classes apart; nothing unanswered reads as confirmed.</summary>
    [Fact]
    public void ThePassReport_WordsAnswersAndSilenceApart()
    {
        var report = AlertsViewModel.DescribePass(4, 4,
            new AlertRefreshOutcome(Changed: 2, Unchanged: 10, Refused: 1, Unanswered: 7), cancelled: false);

        Assert.Contains("2 answers changed", report);
        Assert.Contains("10 confirmed unchanged", report);
        Assert.Contains("1 refused", report);
        Assert.Contains("7 unanswered", report);
        Assert.Contains("unconfirmed", report);

        var cancelled = AlertsViewModel.DescribePass(9, 3, AlertRefreshOutcome.Zero, cancelled: true);
        Assert.Contains("cancelled after 3 of 9", cancelled);
        Assert.Contains("kept", cancelled);
    }
}
