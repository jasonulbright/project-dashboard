using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The conditional-read machinery: what a `gh api --include` answer parses to, how a 304 keeps
/// the held count, and how a refusal is held as words rather than as a zero. State lives under
/// the app-data sandbox.
/// </summary>
[Collection("app-data-sandbox")]
public class AlertsServiceTests
{
    public AlertsServiceTests() => TestSandbox.ResetDataDir();

    private const string Slug = "acme/widgets";

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

    [Fact]
    public async Task AFirstRead_StoresTheCountAndSendsItsETagOnTheNextOne()
    {
        var gh = new CannedGitHubService();
        for (var i = 0; i < 3; i++) gh.Answers.Enqueue(Answer(Ok($"tag{i}", "[]")));
        var service = new AlertsService(gh);

        var first = await service.RefreshAsync(Slug);
        Assert.Equal(3, first.Changed);
        Assert.Equal(0, service.Cached(Slug, AlertSource.Dependabot)!.Count);
        Assert.All(gh.Calls, call => Assert.DoesNotContain("-H", call));

        gh.Calls.Clear();
        for (var i = 0; i < 3; i++)
            gh.Answers.Enqueue(Answer("HTTP/2.0 304 Not Modified\r\n\r\n", exit: 1, stderr: "gh: HTTP 304"));
        var second = await service.RefreshAsync(Slug);

        Assert.Equal(3, second.Unchanged);
        Assert.Equal(0, service.Cached(Slug, AlertSource.Dependabot)!.Count);
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
        var service = new AlertsService(gh);

        var outcome = await service.RefreshAsync(Slug);
        Assert.Equal(1, outcome.Refused);
        var held = service.Cached(Slug, AlertSource.SecretScanning)!;
        Assert.Null(held.Count);
        Assert.Contains("secret scanning", held.Unreadable);

        // The next pass asks again — a scope granted since must be discoverable.
        gh.Answers.Enqueue(Answer(Ok("d2", "[]")));
        gh.Answers.Enqueue(Answer(Ok("c2", "[]")));
        gh.Answers.Enqueue(Answer(Ok("s2", "[{}]",
            "<https://x?per_page=1&page=2>; rel=\"last\"")));
        await service.RefreshAsync(Slug);

        var recovered = service.Cached(Slug, AlertSource.SecretScanning)!;
        Assert.Equal(2, recovered.Count);
        Assert.Equal("", recovered.Unreadable);
    }

    /// <summary>A launch failure replaces nothing: the held answer outlives a gh that did not run.</summary>
    [Fact]
    public async Task AGhThatCannotAnswer_LeavesTheHeldAnswerStanding()
    {
        var gh = new CannedGitHubService();
        for (var i = 0; i < 3; i++)
            gh.Answers.Enqueue(Answer(Ok("t", "[{}]", "<https://x?per_page=1&page=4>; rel=\"last\"")));
        var service = new AlertsService(gh);
        await service.RefreshAsync(Slug);

        for (var i = 0; i < 3; i++) gh.Answers.Enqueue(Answer("", exit: 1, stderr: "gh: spawn failed"));
        await service.RefreshAsync(Slug);

        Assert.Equal(4, service.Cached(Slug, AlertSource.Dependabot)!.Count);
    }

    [Fact]
    public async Task TheCache_SurvivesToANewServiceInstance()
    {
        var gh = new CannedGitHubService();
        for (var i = 0; i < 3; i++) gh.Answers.Enqueue(Answer(Ok("t", "[]")));
        await new AlertsService(gh).RefreshAsync(Slug);

        var reloaded = new AlertsService(new CannedGitHubService());

        Assert.Equal(0, reloaded.Cached(Slug, AlertSource.CodeScanning)!.Count);
        Assert.Equal("\"t\"", reloaded.Cached(Slug, AlertSource.CodeScanning)!.ETag);
    }
}

/// <summary>
/// The rows the Alerts page shows: where a slug comes from, what a non-GitHub remote's cells
/// say, and what the filters hide.
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

    private static async Task<(AlertsViewModel Alerts, DashboardViewModel Dashboard)> NewAlertsAsync(
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
        return (new AlertsViewModel(dashboard, new AlertsService(gitHub)), dashboard);
    }

    [Fact]
    public async Task ARowWithAGitHubRemote_CarriesItsSlug_AndANonGitHubRemoteSaysWhyItsCellsAreEmpty()
    {
        var (alerts, _) = await NewAlertsAsync(
            Project("hub", "https://github.com/acme/hub.git"),
            Project("lab", "git@gitlab.example.com:team/lab.git"));
        await alerts.OpenAsync();

        var hub = alerts.Rows.Single(r => r.Name == "hub");
        Assert.Equal("acme/hub", hub.Slug);

        var lab = alerts.Rows.Single(r => r.Name == "lab");
        Assert.Equal("", lab.Slug);
        Assert.Equal("—", lab.DependabotText);
        Assert.Contains("not GitHub", lab.DependabotDetail);
    }

    [Fact]
    public async Task AnUnfetchedIssueCount_IsADashNeverAZero()
    {
        var (alerts, _) = await NewAlertsAsync(Project("quiet"));
        await alerts.OpenAsync();

        Assert.Equal("—", alerts.Rows.Single().IssuesText);
    }

    [Fact]
    public async Task TheOnlyWithAlertsFilter_HidesQuietRowsAndTheSourceComboNarrowsIt()
    {
        var (alerts, _) = await NewAlertsAsync(
            Project("busy", issues: 3),
            Project("quiet", issues: 0));
        await alerts.OpenAsync();
        Assert.Equal(2, alerts.Rows.Count);

        alerts.OnlyWithAlerts = true;
        Assert.Equal("busy", alerts.Rows.Single().Name);

        alerts.SourceFilter = "Dependabot";
        Assert.Empty(alerts.Rows);
        Assert.Contains("filter hides rows", alerts.EmptyNotice);
    }

    [Fact]
    public async Task WithNoGitHubRepositories_ARefreshSaysSoInsteadOfSpinningQuietly()
    {
        var (alerts, _) = await NewAlertsAsync(Project("local-only"));
        await alerts.OpenAsync();

        Assert.Contains("no discovered repository has a GitHub remote", alerts.StatusText);
    }
}
