using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// The account read, parsed from the shapes gh actually emits. The payloads below are captured
/// from `gh auth status --json hosts` rather than composed from the flag's documentation, because
/// the one risk this read carries is a shape that changed under it — and a shape it does not
/// recognise has to reach the caller as a failed read, never as an answer about accounts.
/// </summary>
public class GhAuthStatusParsingTests
{
    /// <summary>One signed-in account on one host, as gh 2.87.2 prints it.</summary>
    private const string OneAccount =
        """
        {"hosts":{"github.com":[{"state":"success","active":true,"host":"github.com","login":"jasonulbright","tokenSource":"keyring","scopes":"admin:org, delete_repo, gist, read:org, repo, workflow","gitProtocol":"https"}]}}
        """;

    /// <summary>Signed in nowhere: gh prints its notice on stderr and an empty map on stdout.</summary>
    private const string SignedOut = """{"hosts":{}}""";

    private const string TwoAccountsTwoHosts =
        """
        {"hosts":{
          "github.com":[
            {"state":"success","active":true,"host":"github.com","login":"alice","tokenSource":"keyring","scopes":"repo, workflow","gitProtocol":"https"},
            {"state":"success","active":false,"host":"github.com","login":"bob","tokenSource":"keyring","scopes":"repo","gitProtocol":"ssh"}
          ],
          "ghe.example.com":[
            {"state":"success","active":true,"host":"ghe.example.com","login":"a.worker","tokenSource":"oauth_token","scopes":"repo","gitProtocol":"https"}
          ]
        }}
        """;

    private const string FailedAccount =
        """
        {"hosts":{"github.com":[{"state":"error","error":"authentication failed","active":true,"host":"github.com","login":"alice","tokenSource":"keyring","gitProtocol":"https"}]}}
        """;

    [Fact]
    public void OneSignedInAccount_ParsesHostLoginActiveAndScopes()
    {
        var state = GitHubService.ParseAuthState(OneAccount);

        var account = Assert.Single(state!.Accounts);
        Assert.Equal("github.com", account.Host);
        Assert.Equal("jasonulbright", account.Login);
        Assert.True(account.Active);
        Assert.True(account.IsUsable);
        Assert.Equal(["admin:org", "delete_repo", "gist", "read:org", "repo", "workflow"], account.Scopes);
        Assert.True(state.AnySignedIn);
    }

    /// <summary>An empty map is an answer — the machine holds no accounts — and not a failed read.</summary>
    [Fact]
    public void SignedOut_IsAnAnswerWithNoAccounts()
    {
        var state = GitHubService.ParseAuthState(SignedOut);

        Assert.NotNull(state);
        Assert.Empty(state.Accounts);
        Assert.False(state.AnySignedIn);
    }

    [Fact]
    public void SeveralAccountsAcrossHosts_KeepTheirOwnHostAndActiveFlag()
    {
        var state = GitHubService.ParseAuthState(TwoAccountsTwoHosts)!;

        Assert.Equal(3, state.Accounts.Count);
        Assert.Equal(["alice", "bob"], state.ForHost("github.com").Select(a => a.Login));
        Assert.Equal("alice", state.ActiveFor("github.com")!.Login);
        Assert.Equal("a.worker", state.ActiveFor("ghe.example.com")!.Login);
    }

    /// <summary>
    /// gh exits zero for a failed account under --json and reports the failure per account, so an
    /// account whose token did not pass its own check must not read as one that works.
    /// </summary>
    [Fact]
    public void AnAccountWhoseSignInFailed_IsNotSignedIn()
    {
        var state = GitHubService.ParseAuthState(FailedAccount)!;

        var account = Assert.Single(state.Accounts);
        Assert.Equal("error", account.State);
        Assert.False(account.IsUsable);
        Assert.False(state.AnySignedIn);
        Assert.Empty(account.Scopes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"hosts":[]}""")]                                  // hosts as an array
    [InlineData("""{"accounts":{"github.com":[]}}""")]                // no hosts member
    [InlineData("""["github.com"]""")]                                // not an object
    [InlineData("unknown flag: --json")]                              // a gh too old for the flag
    public void AnUnrecognisedShape_IsAFailedReadRatherThanAnEmptyOne(string payload)
        => Assert.Null(GitHubService.ParseAuthState(payload));

    /// <summary>
    /// The read never asks for a token, so gh omits the field. A payload carrying one anyway —
    /// a gh invoked with --show-token by some future caller — must still leave nothing holding it.
    /// </summary>
    [Fact]
    public void ATokenInThePayload_ReachesNothingTheAppCarries()
    {
        const string withToken =
            """
            {"hosts":{"github.com":[{"state":"success","active":true,"host":"github.com","login":"alice","token":"gho_SECRETVALUE","tokenSource":"keyring","scopes":"repo","gitProtocol":"https"}]}}
            """;

        var account = Assert.Single(GitHubService.ParseAuthState(withToken)!.Accounts);

        foreach (var property in typeof(GhAccount).GetProperties())
            Assert.DoesNotContain("SECRETVALUE", $"{property.GetValue(account)}", StringComparison.Ordinal);
        Assert.DoesNotContain("token", account.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Not asking is the only way to be certain a token never reaches a log or a screen.</summary>
    [Fact]
    public void TheStatusRead_NeverAsksForATokenValue()
    {
        Assert.Equal(["auth", "status", "--json", "hosts"], GitHubService.AuthStatusArgs);
        Assert.DoesNotContain("--show-token", GitHubService.AuthStatusArgs);
        Assert.DoesNotContain("-t", GitHubService.AuthStatusArgs);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("repo", new[] { "repo" })]
    [InlineData("repo, workflow,  gist ", new[] { "repo", "workflow", "gist" })]
    [InlineData(",,", new string[0])]
    public void ScopesAreSplitOnGhsOwnSeparator(string printed, string[] expected)
        => Assert.Equal(expected, GitHubService.ParseScopeList(printed));

    /// <summary>
    /// A remote URL carries whichever host form the clone used; gh stores the canonical one.
    /// Matching them literally would report "no account for this host" for a www clone.
    /// </summary>
    [Theory]
    [InlineData("github.com")]
    [InlineData("GitHub.com")]
    [InlineData("www.github.com")]
    public void HostsMatchWithoutCaseOrAWwwPrefix(string asked)
    {
        var state = GitHubService.ParseAuthState(OneAccount)!;

        Assert.Single(state.ForHost(asked));
        Assert.NotNull(state.ActiveFor(asked));
    }

    [Fact]
    public void AnUnknownHost_HoldsNoAccountsAndNoActiveOne()
    {
        var state = GitHubService.ParseAuthState(OneAccount)!;

        Assert.Empty(state.ForHost("gitlab.com"));
        Assert.Null(state.ActiveFor("gitlab.com"));
        Assert.Empty(state.ForHost(""));
    }

    /// <summary>
    /// gh keys the map by host and repeats the host inside each entry. An entry that omits it is
    /// still an account on the host it was listed under.
    /// </summary>
    [Fact]
    public void AnEntryWithoutItsOwnHost_TakesTheOneItWasListedUnder()
    {
        var state = GitHubService.ParseAuthState(
            """{"hosts":{"ghe.example.com":[{"state":"success","active":true,"login":"alice"}]}}""")!;

        Assert.Equal("ghe.example.com", Assert.Single(state.Accounts).Host);
    }

    /// <summary>Answers a canned payload and counts what was asked, so no test here spawns gh.</summary>
    private sealed class CannedGh(params string[] payloads) : GitHubService(new SettingsService())
    {
        public List<string> Runs { get; } = [];

        public override Task<ProcessResult> RunAsync(
            IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Runs.Add(string.Join(" ", args));
            var payload = payloads[Math.Min(Runs.Count - 1, payloads.Length - 1)];
            return Task.FromResult(new ProcessResult(0, payload, "", TimedOut: false));
        }
    }

    /// <summary>
    /// The account read is machine-wide and every repository page asks for it. Spending a process
    /// spawn per page open on a fact that changes only when the reader runs gh is the cost this
    /// holds down; the explicit re-read is the only thing that asks again.
    /// </summary>
    [Fact]
    public async Task TheAnswerIsHeldForTheSession_AndOnlyARefreshAsksAgain()
    {
        var gh = new CannedGh(OneAccount);

        var first = await gh.GetAuthStateAsync();
        var second = await gh.GetAuthStateAsync();
        await gh.GetAuthStateAsync(refresh: true);

        Assert.Same(first, second);
        Assert.Equal(2, gh.Runs.Count);
        Assert.Equal("auth status --json hosts", gh.Runs[0]);
    }

    /// <summary>
    /// A gh that answered nothing this read understands is not an answer to hold: holding it would
    /// fix the degraded line for the rest of the session, including across the fix for it.
    /// </summary>
    [Fact]
    public async Task AFailedReadIsNotHeld()
    {
        var gh = new CannedGh("not json", OneAccount);

        Assert.Null(await gh.GetAuthStateAsync());
        Assert.NotNull(await gh.GetAuthStateAsync());
        Assert.Equal(2, gh.Runs.Count);
    }

    /// <summary>
    /// Signing in through the app adds an account the held answer was read before. Dropping it is
    /// what stops the dashboard and every repository page naming the state from before the sign-in.
    /// </summary>
    [Fact]
    public async Task ASignInDropsTheHeldAnswer()
    {
        var gh = new CannedGh(OneAccount);

        await gh.GetAuthStateAsync();
        gh.InvalidateAuthState();
        await gh.GetAuthStateAsync();

        Assert.Equal(2, gh.Runs.Count);
    }
}
