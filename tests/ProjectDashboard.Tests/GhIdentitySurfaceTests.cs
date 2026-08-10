using System.Text.RegularExpressions;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Which GitHub CLI account a repository's actions would run as, and what the page says when the
/// answer is not the obvious one. Every case here is driven through the view models' own status
/// read, so no test in this file spawns gh, and none of them can change which account gh targets —
/// the surface names commands and runs none.
/// </summary>
[Collection("app-data-sandbox")]
public class GhIdentitySurfaceTests
{
    private static GhAccount Account(
        string login, string host = "github.com", bool active = true, string state = "success") =>
        new(host, login, active, ["repo"], state, "");

    private static GitRemote Remote(string owner, string host = "github.com") => new(host, owner, "repo");

    // ── The line, as a value ────────────────────────────────────────────────────

    [Fact]
    public void ARepositoryWithNoRemote_SaysNothingAboutAccounts()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(new GhAuthState([Account("alice")]), null);

        Assert.Equal("", line.Text);
        Assert.Equal("", line.Command);
    }

    /// <summary>
    /// A read that failed establishes nothing. Naming an account, or naming none, would both be
    /// claims about a status this app did not read.
    /// </summary>
    [Fact]
    public void AFailedStatusRead_ClaimsNoAccountEitherWay()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(null, Remote("alice"));

        Assert.Equal(ProjectDetailViewModel.GhAuthUnreadable, line.Text);
        Assert.False(line.Warning);
        Assert.Equal("", line.Command);
    }

    [Fact]
    public void TheActiveAccountForTheHost_IsNamedPlainly()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice")]), Remote("alice"));

        Assert.Equal("alice @ github.com", line.Text);
        Assert.False(line.Warning);
        Assert.Equal("", line.Command);
    }

    /// <summary>An owner that is not an account gh holds is an organisation or somebody else's —
    /// neither is the mismatch this surface can establish, and neither is warned about.</summary>
    [Fact]
    public void AnOwnerThatIsNotOneOfTheReadersAccounts_IsNotAMismatch()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice")]), Remote("some-org"));

        Assert.Equal("alice @ github.com", line.Text);
        Assert.False(line.Warning);
    }

    /// <summary>
    /// The mismatch worth naming: the repository belongs to another account the reader is signed
    /// in to, so actions run as somebody who may not see it at all. The remedy is named as text.
    /// </summary>
    [Fact]
    public void ARepositoryOwnedByAnotherSignedInAccount_NamesTheAccountActionsRunAs()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice"), Account("bob", active: false)]), Remote("bob"));

        Assert.True(line.Warning);
        Assert.Contains("alice @ github.com", line.Text);
        Assert.Contains("bob", line.Text);
        Assert.Equal("gh auth switch --hostname github.com --user bob", line.Command);
    }

    /// <summary>Matching an account login is not case work the reader should have to do.</summary>
    [Fact]
    public void TheOwnerMatchesAnAccountWhateverItsCase()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice"), Account("Bob", active: false)]), Remote("bOB"));

        Assert.True(line.Warning);
        Assert.Equal("gh auth switch --hostname github.com --user Bob", line.Command);
    }

    /// <summary>
    /// A clone made through the www host is the same host to gh. Naming the URL's form would put a
    /// host of that name into the command, which signs in to a host gh had no account for.
    /// </summary>
    [Fact]
    public void AWwwCloneIsTheSameHostAsTheOneGhHolds()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice"), Account("bob", active: false)]),
            Remote("bob", "www.github.com"));

        Assert.Contains("alice @ github.com", line.Text);
        Assert.Equal("gh auth switch --hostname github.com --user bob", line.Command);
    }

    [Fact]
    public void NoAccountAtAll_SaysGitHubActionsAreUnavailableAndNamesTheSignIn()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(new GhAuthState([]), Remote("alice"));

        Assert.True(line.Warning);
        Assert.Contains("Not signed in", line.Text);
        Assert.Equal("gh auth login --hostname github.com", line.Command);
    }

    /// <summary>
    /// The honest answer for a host gh has no session for: named, with the reason, rather than the
    /// silence a repository with no GitHub remote at all gets.
    /// </summary>
    [Fact]
    public void AHostWithNoSession_IsNamedRatherThanLeftSilent()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("a.worker", "ghe.example.com")]), Remote("alice"));

        Assert.True(line.Warning);
        Assert.Contains("No GitHub CLI account for github.com", line.Text);
        Assert.Equal("gh auth login --hostname github.com", line.Command);
    }

    /// <summary>
    /// A host gh does have a session for, that this app's GitHub surfaces do not read. Saying so
    /// is the point: the tabs are empty for a reason the repository cannot show.
    /// </summary>
    [Fact]
    public void AnEnterpriseHostWithASession_NamesTheAccountAndTheLimit()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("a.worker", "ghe.example.com")]), Remote("team", "ghe.example.com"));

        Assert.True(line.Warning);
        Assert.Contains("a.worker @ ghe.example.com", line.Text);
        Assert.Contains("github.com only", line.Text);
        Assert.Equal("", line.Command);
    }

    /// <summary>
    /// A host this app does not read is answered with the limit and no command: a sign-in on that
    /// host would resolve nothing, so offering one would send the reader after a fix that is not.
    /// </summary>
    [Fact]
    public void AnEnterpriseHostWithNoSession_NamesTheLimitAndOffersNoSignIn()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice")]), Remote("team", "ghe.example.com"));

        Assert.True(line.Warning);
        Assert.Contains("ghe.example.com", line.Text);
        Assert.Contains("github.com only", line.Text);
        Assert.Equal("", line.Command);
    }

    [Fact]
    public void AccountsButNoActiveOneForTheHost_SaysGhTargetsNoneOfThem()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice", active: false), Account("bob", active: false)]), Remote("alice"));

        Assert.True(line.Warning);
        Assert.Contains("No active GitHub CLI account", line.Text);
        Assert.Equal("gh auth switch --hostname github.com", line.Command);
    }

    /// <summary>
    /// gh exits zero under --json even for an account whose token failed its check, so the state
    /// per account is the only thing that separates a working sign-in from a broken one.
    /// </summary>
    [Theory]
    [InlineData("error", "failed")]
    [InlineData("timeout", "timed out")]
    public void AnAccountWhoseSignInDidNotPass_SaysSoRatherThanNamingItAsWorking(
        string state, string expected)
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice", state: state)]), Remote("alice"));

        Assert.True(line.Warning);
        Assert.Contains(expected, line.Text);
        Assert.Equal("gh auth login --hostname github.com", line.Command);
    }

    /// <summary>
    /// A repository owned by an account whose own sign-in failed is not the switch case: switching
    /// to it would land on the sign-in that does not work.
    /// </summary>
    [Fact]
    public void AFailedNonActiveAccount_IsNotOfferedAsTheAccountToSwitchTo()
    {
        var line = ProjectDetailViewModel.DescribeGhIdentity(
            new GhAuthState([Account("alice"), Account("bob", active: false, state: "error")]), Remote("bob"));

        Assert.Equal("alice @ github.com", line.Text);
        Assert.False(line.Warning);
    }

    /// <summary>Nothing this surface produces runs a command; the mutating gh verbs appear as text only.</summary>
    [Theory]
    [InlineData("switch")]
    [InlineData("login")]
    public void TheCommandsAreOfferedAsTextAndNeverRun(string verb)
    {
        var source = RepoSource.Read("src/ProjectDashboard/ViewModels/Pages/ProjectDetailViewModel.GhIdentity.cs");

        Assert.Contains($"gh auth {verb}", source);
        // The one process this file may reach is the status read behind the fetch seam.
        Assert.DoesNotContain("Process.Start", source);
        Assert.DoesNotContain("RunAsync", source);
    }

    // ── The line, on the page ───────────────────────────────────────────────────

    private sealed class StubIdentityViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        public GhAuthState? State { get; set; }
        public Queue<TaskCompletionSource<GhAuthState?>> Gates { get; } = new();

        /// <summary>Every read this page made, in order, and whether it asked gh again.</summary>
        public List<bool> Reads { get; } = [];

        internal override Task<GhAuthState?> FetchAuthStateAsync(bool refresh)
        {
            Reads.Add(refresh);
            return Gates.Count > 0 ? Gates.Dequeue().Task : Task.FromResult(State);
        }
    }

    private static ProjectInfo Project(string? remoteUrl, string name = "gh-identity")
    {
        var project = new ProjectInfo
        {
            DirectoryName = name, DisplayName = name, FullPath = TestEnv.NewDir(name)
        };
        if (remoteUrl is not null) project.GitStatus.RemoteUrl = remoteUrl;
        return project;
    }

    [Fact]
    public async Task OpeningARepository_NamesTheAccountItsActionsRunAs()
    {
        var vm = new StubIdentityViewModel { State = new GhAuthState([Account("alice")]) };

        await vm.SetProjectAsync(Project("https://github.com/alice/repo.git"));
        await vm.GhIdentityRefresh;

        Assert.True(vm.GhIdentityVisible);
        Assert.Equal("alice @ github.com", vm.GhIdentityText);
        Assert.False(vm.GhIdentityCommandVisible);
    }

    /// <summary>
    /// The status read is answered for the app, not per repository: a page load takes the held
    /// answer, and only the reader's own re-check asks gh again. Polling a machine-wide setting
    /// per repository spends a process spawn on a fact that changes when the reader runs gh.
    /// </summary>
    [Fact]
    public async Task ARepositoryLoad_ReadsTheHeldAnswerAndTheRecheckAsksAgain()
    {
        var vm = new StubIdentityViewModel { State = new GhAuthState([Account("alice")]) };

        await vm.SetProjectAsync(Project("https://github.com/alice/repo.git"));
        await vm.GhIdentityRefresh;
        await vm.RecheckGhIdentityCommand.ExecuteAsync(null);
        await vm.GhIdentityRefresh;

        Assert.Equal([false, true], vm.Reads);
    }

    [Fact]
    public async Task ARepositoryWithNoRemote_ShowsNoLineAndSpendsNoRead()
    {
        var vm = new StubIdentityViewModel { State = new GhAuthState([Account("alice")]) };

        await vm.SetProjectAsync(Project(null));
        await vm.GhIdentityRefresh;

        Assert.False(vm.GhIdentityVisible);
        Assert.Equal("", vm.GhIdentityText);
        Assert.Empty(vm.Reads);
    }

    /// <summary>A card for a repository with no clone still names the account that would clone it.</summary>
    [Fact]
    public async Task ACardWithNoLocalClone_ResolvesItsOwnerFromTheSlug()
    {
        var vm = new StubIdentityViewModel
        {
            State = new GhAuthState([Account("alice"), Account("bob", active: false)])
        };
        var project = Project(null, "cloud");
        project.IsRemoteOnly = true;
        project.RemoteSlug = "bob/thing";

        await vm.SetProjectAsync(project);
        await vm.GhIdentityRefresh;

        Assert.True(vm.GhIdentityWarning);
        Assert.Equal("gh auth switch --hostname github.com --user bob", vm.GhIdentityCommand);
        Assert.True(vm.GhIdentityCommandVisible);
    }

    [Fact]
    public async Task AFailedRead_LeavesTheLineClaimingNoAccount()
    {
        var vm = new StubIdentityViewModel { State = null };

        await vm.SetProjectAsync(Project("https://github.com/alice/repo.git"));
        await vm.GhIdentityRefresh;

        Assert.Equal(ProjectDetailViewModel.GhAuthUnreadable, vm.GhIdentityText);
        Assert.False(vm.GhIdentityCommandVisible);
    }

    /// <summary>
    /// The read outlives the page that started it. An answer read against one repository's remote
    /// must never be written under the next repository's name.
    /// </summary>
    [Fact]
    public async Task AReadThatLandsAfterASwitch_IsDropped()
    {
        var vm = new StubIdentityViewModel();
        var gate = new TaskCompletionSource<GhAuthState?>();
        vm.Gates.Enqueue(gate);
        vm.State = new GhAuthState([Account("carol")]);

        await vm.SetProjectAsync(Project("https://github.com/bob/first.git", "first"));
        var stranded = vm.GhIdentityRefresh;

        await vm.SetProjectAsync(Project("https://github.com/carol/second.git", "second"));
        await vm.GhIdentityRefresh;

        gate.SetResult(new GhAuthState([Account("bob")]));
        await stranded;

        Assert.Equal("carol @ github.com", vm.GhIdentityText);
    }

    /// <summary>The line describes the repository on screen, so leaving one takes it with it.</summary>
    [Fact]
    public async Task LeavingARepositoryForOneWithNoRemote_ClearsTheLine()
    {
        var vm = new StubIdentityViewModel { State = new GhAuthState([Account("alice")]) };

        await vm.SetProjectAsync(Project("https://github.com/alice/repo.git", "first"));
        await vm.GhIdentityRefresh;
        await vm.SetProjectAsync(Project(null, "second"));
        await vm.GhIdentityRefresh;

        Assert.False(vm.GhIdentityVisible);
        Assert.Equal("", vm.GhIdentityText);
    }

    // ── The account table on Settings ───────────────────────────────────────────

    private sealed class StubSettingsViewModel(SettingsService settings)
        : SettingsViewModel(settings, null!, null!)
    {
        public string? Summary { get; set; } = "Signed in";
        public GhAuthState? State { get; set; }

        /// <summary>
        /// Every read the page made, in order. The first is the one the constructor starts: the
        /// page opening takes the held answer, and only Re-check asks gh again.
        /// </summary>
        public List<bool> Reads { get; } = [];

        internal override Task<string> FetchAuthSummaryAsync() => Task.FromResult(Summary ?? "Unavailable");

        internal override Task<GhAuthState?> FetchAuthStateAsync(bool refresh)
        {
            Reads.Add(refresh);
            return Task.FromResult(State);
        }
    }

    [Fact]
    public async Task SettingsListsEveryAccountGhHolds_WithTheActiveOneMarked()
    {
        TestSandbox.ResetDataDir();
        var vm = new StubSettingsViewModel(new SettingsService())
        {
            State = new GhAuthState([Account("alice"), Account("bob", active: false),
                                     Account("a.worker", "ghe.example.com")])
        };

        await vm.RecheckGitHubCommand.ExecuteAsync(null);

        Assert.True(vm.GhAccountsVisible);
        Assert.Equal(["alice", "bob", "a.worker"], vm.GhAccounts.Select(a => a.Login));
        Assert.Equal(["Active", "", "Active"], vm.GhAccounts.Select(a => a.ActiveLabel));
        Assert.Equal("ghe.example.com", vm.GhAccounts[2].Host);
        Assert.Equal("repo", vm.GhAccounts[0].Scopes);
        Assert.Equal("", vm.GhAccountsNotice);
        Assert.Equal([false, true], vm.Reads);
    }

    /// <summary>
    /// A gh whose status shape this app does not read leaves the exit-code line standing alone.
    /// Without the notice, one account on the machine and a shape nobody could parse look the same.
    /// </summary>
    [Fact]
    public async Task AStatusShapeTheAppCannotRead_DegradesToTheSummaryAndSaysSo()
    {
        TestSandbox.ResetDataDir();
        var vm = new StubSettingsViewModel(new SettingsService()) { Summary = "Signed in", State = null };

        await vm.RecheckGitHubCommand.ExecuteAsync(null);

        Assert.False(vm.GhAccountsVisible);
        Assert.Empty(vm.GhAccounts);
        Assert.Equal("Signed in", vm.GitHubStatus);
        Assert.Equal(SettingsViewModel.GhAccountsUnreadable, vm.GhAccountsNotice);
    }

    /// <summary>A gh that is not installed explains the empty table on the line above it already.</summary>
    [Fact]
    public async Task AMissingGh_IsNotReportedTwice()
    {
        TestSandbox.ResetDataDir();
        var vm = new StubSettingsViewModel(new SettingsService())
        {
            Summary = "GitHub CLI not found", State = null
        };

        await vm.RecheckGitHubCommand.ExecuteAsync(null);

        Assert.Equal("", vm.GhAccountsNotice);
        Assert.False(vm.GhAccountsVisible);
    }

    [Fact]
    public async Task SignedInNowhere_ShowsNoTableAndNoParseComplaint()
    {
        TestSandbox.ResetDataDir();
        var vm = new StubSettingsViewModel(new SettingsService())
        {
            Summary = "Found, not signed in", State = new GhAuthState([])
        };

        await vm.RecheckGitHubCommand.ExecuteAsync(null);

        Assert.False(vm.GhAccountsVisible);
        Assert.Equal("", vm.GhAccountsNotice);
    }

    [Fact]
    public void ARowIsNarratedWithTheHostAndWhetherGhTargetsIt()
    {
        var active = SettingsViewModel.ToRow(new GhAccount("github.com", "alice", true, ["repo", "gist"], "success", ""));
        var other = SettingsViewModel.ToRow(new GhAccount("github.com", "bob", false, [], "success", ""));
        var broken = SettingsViewModel.ToRow(new GhAccount("github.com", "carol", true, ["repo"], "error", "bad token"));

        Assert.Equal("alice on github.com, active, scopes repo, gist", active.AccessibleName);
        Assert.Contains("not the account gh targets", other.AccessibleName);
        Assert.Contains("no scopes reported", other.AccessibleName);
        Assert.Contains("sign-in state error", broken.AccessibleName);
    }
}

/// <summary>
/// The identity surfaces asserted at the source: what markup alone decides is whether a reader can
/// reach any of it — a line nothing renders, a command with no way to select it, or a re-check with
/// no button, is a disclosure nothing on screen makes.
/// </summary>
public class GhIdentityMarkupTests
{
    private static string DetailMarkup => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");
    private static string SettingsMarkup => RepoSource.Read("src/ProjectDashboard/Views/Pages/SettingsPage.xaml");

    [Fact]
    public void TheIdentityLineIsRenderedAndAnnouncedWhenItChanges()
    {
        var block = Regex.Match(DetailMarkup,
            @"<TextBlock[^>]*?Text=""\{Binding GhIdentityText\}""[^>]*?/>", RegexOptions.Singleline);

        Assert.True(block.Success, "no text block bound to GhIdentityText");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
    }

    /// <summary>
    /// The whole bar is hidden for a repository the surface has nothing to say about, rather than
    /// standing empty above the tabs.
    /// </summary>
    [Fact]
    public void TheIdentityBarIsShownOnlyWhenItHasSomethingToSay()
    {
        var bar = Regex.Match(DetailMarkup,
            @"<Border Grid\.Row=""3"" x:Name=""GhIdentityBar"".*?</Border>", RegexOptions.Singleline);

        Assert.True(bar.Success, "no identity bar in its own row above the tabs");
        Assert.Contains(
            @"Visibility=""{Binding GhIdentityVisible, Converter={StaticResource BooleanToVisibilityConverter}}""",
            bar.Value);
        // Its row is its own: a bar sharing the tabs' row would render on top of them.
        Assert.Contains(@"<TabControl Grid.Row=""4"" x:Name=""WorkTabs""", DetailMarkup);
    }

    /// <summary>
    /// The command is text the reader selects and runs. A button that ran `gh auth switch` would
    /// change which account every tool on the machine targets from a click inside this app.
    /// </summary>
    [Fact]
    public void TheCommandIsSelectableTextAndNotAButton()
    {
        var box = Regex.Match(DetailMarkup,
            @"<TextBox[^>]*?\{Binding GhIdentityCommand, Mode=OneWay\}.*?/>", RegexOptions.Singleline);

        Assert.True(box.Success, "no read-only box carrying the command");
        Assert.Contains(@"IsReadOnly=""True""", box.Value);
        Assert.Contains("AutomationProperties.Name=", box.Value);
        Assert.DoesNotContain("AuthSwitchCommand", DetailMarkup);
    }

    [Fact]
    public void TheRecheckHasANamedButton()
    {
        var button = Regex.Match(DetailMarkup,
            @"<ui:Button[^>]*?\{Binding RecheckGhIdentityCommand\}[^>]*?/>", RegexOptions.Singleline);

        Assert.True(button.Success, "no button bound to RecheckGhIdentityCommand");
        Assert.Contains(@"AutomationProperties.Name=""Re-check the GitHub CLI account""", button.Value);
    }

    [Fact]
    public void SettingsRendersTheAccountTableWithARowNarration()
    {
        var list = Regex.Match(SettingsMarkup,
            @"<ItemsControl ItemsSource=""\{Binding GhAccounts\}"".*?</ItemsControl>", RegexOptions.Singleline);

        Assert.True(list.Success, "no account list on the settings page");
        Assert.Contains(
            @"Visibility=""{Binding GhAccountsVisible, Converter={StaticResource BooleanToVisibilityConverter}}""",
            list.Value);
        Assert.Contains(@"AutomationProperties.Name=""{Binding AccessibleName}""", list.Value);
        foreach (var column in new[] { "Login", "Host", "ActiveLabel", "Scopes" })
            Assert.Contains($"{{Binding {column}}}", list.Value);
    }

    [Fact]
    public void SettingsRendersTheDegradeNoticeAndAnnouncesIt()
    {
        var block = Regex.Match(SettingsMarkup,
            @"<TextBlock Text=""\{Binding GhAccountsNotice\}"".*?</TextBlock>", RegexOptions.Singleline);

        Assert.True(block.Success, "no text block bound to GhAccountsNotice");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
    }
}
