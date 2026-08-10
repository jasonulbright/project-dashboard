using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

public class GitHubArgBuilderTests
{
    [Fact]
    public void CreateIssue_LabelsRepeatPerFlag()
    {
        Assert.Equal(
            ["issue", "create", "--repo", "o/r", "--title", "Crash", "--body", "It broke.",
             "--label", "bug", "--label", "p1"],
            GitHubService.BuildCreateIssueArgs("o/r", "Crash", "It broke.", ["bug", "p1"]));
    }

    [Fact]
    public void CreateIssue_EmptyBodyStillPassesBodyFlag()
    {
        Assert.Equal(
            ["issue", "create", "--repo", "o/r", "--title", "t", "--body", ""],
            GitHubService.BuildCreateIssueArgs("o/r", "t", "", null));
    }

    [Fact]
    public void LabelEdit_AddThenRemove_ExactOrder()
    {
        Assert.Equal(
            ["issue", "edit", "41", "--repo", "o/r",
             "--add-label", "confirmed", "--add-label", "ui", "--remove-label", "triage"],
            GitHubService.BuildIssueLabelEditArgs("o/r", 41, ["confirmed", "ui"], ["triage"]));
    }

    [Fact]
    public void Merge_SquashWithDeleteBranch()
    {
        Assert.Equal(
            ["pr", "merge", "55", "--repo", "o/r", "--squash", "--delete-branch"],
            GitHubService.BuildMergeArgs("o/r", 55, "squash", deleteBranch: true));
    }

    [Theory]
    [InlineData("merge", "--merge")]
    [InlineData("squash", "--squash")]
    [InlineData("rebase", "--rebase")]
    public void Merge_StrategyMapsToFlag(string strategy, string flag)
    {
        var args = GitHubService.BuildMergeArgs("o/r", 1, strategy, deleteBranch: false);
        Assert.Equal(["pr", "merge", "1", "--repo", "o/r", flag], args);
    }

    /// <summary>
    /// An unmapped token builds nothing, and the mutation turns that into a failed result
    /// rather than a throw: a mutation is the one place a throw escapes the callers'
    /// result handling and unwinds the busy gate through an exception path.
    /// </summary>
    [Fact]
    public void Merge_UnknownStrategy_BuildsNothing()
        => Assert.Null(GitHubService.BuildMergeArgs("o/r", 1, "octopus", false));

    [Theory]
    [InlineData("approve", "--approve")]
    [InlineData("requestChanges", "--request-changes")]
    [InlineData("request-changes", "--request-changes")]
    [InlineData("comment", "--comment")]
    public void Review_ActionMapsToFlag(string action, string flag)
    {
        var args = GitHubService.BuildReviewArgs("o/r", 9, action, "");
        Assert.Equal(["pr", "review", "9", "--repo", "o/r", flag], args);
    }

    [Fact]
    public void Review_BodyAppendedWhenPresent()
    {
        Assert.Equal(
            ["pr", "review", "9", "--repo", "o/r", "--comment", "--body", "Nice."],
            GitHubService.BuildReviewArgs("o/r", 9, "comment", "Nice."));
    }

    [Fact]
    public void Review_UnknownAction_BuildsNothing()
        => Assert.Null(GitHubService.BuildReviewArgs("o/r", 9, "dismiss", ""));

    [Fact]
    public void CreatePullRequest_BaseOmittedWhenNull()
    {
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b", "--draft"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", null, draft: true));
    }

    [Fact]
    public void CreatePullRequest_BaseIncludedWhenSet()
    {
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b", "--base", "main"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", "main", draft: false));
    }

    [Fact]
    public void CreatePullRequest_HeadPinsTheSourceBranch()
    {
        // Without --head gh reads whatever is checked out when it spawns, which need
        // not be the branch the compose form named.
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b", "--base", "main", "--head", "feature/x", "--draft"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", "main", draft: true, headBranch: "feature/x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreatePullRequest_HeadOmittedWhenBlank(string? head)
    {
        Assert.Equal(
            ["pr", "create", "--title", "t", "--body", "b"],
            GitHubService.BuildCreatePullRequestArgs("t", "b", null, draft: false, headBranch: head));
    }

    [Fact]
    public void ReleaseCreate_NotesFileAndFlags()
    {
        Assert.Equal(
            ["release", "create", "v2.0.0", "--title", "Project Dashboard 2.0",
             "--notes-file", @"C:\temp\notes.md", "--draft", "--prerelease"],
            GitHubService.BuildReleaseCreateArgs("v2.0.0", "Project Dashboard 2.0", @"C:\temp\notes.md",
                draft: true, prerelease: true));
    }

    [Fact]
    public void ReleaseCreate_NoOptionalFlagsWhenFalse()
    {
        Assert.Equal(
            ["release", "create", "v1.0.0", "--title", "One", "--notes-file", "n.md"],
            GitHubService.BuildReleaseCreateArgs("v1.0.0", "One", "n.md", draft: false, prerelease: false));
    }

    [Fact]
    public void ReleaseCreate_PinsTheCommitTheLocalTagNames()
    {
        // The tag the picker offered may not be on the remote yet. --target is what the
        // automatic tag creation uses, so the remote tag lands on that commit instead of
        // the default branch head.
        Assert.Equal(
            ["release", "create", "v1.0.0", "--title", "One", "--notes-file", "n.md",
             "--target", "9f1c0de4a2b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3"],
            GitHubService.BuildReleaseCreateArgs("v1.0.0", "One", "n.md", draft: false, prerelease: false,
                targetSha: "9f1c0de4a2b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3"));
    }

    [Fact]
    public void ReleaseCreate_WithNoResolvedCommit_OmitsTheTargetFlag()
        => Assert.DoesNotContain("--target",
            GitHubService.BuildReleaseCreateArgs("v1.0.0", "One", "n.md", draft: false, prerelease: false,
                targetSha: ""));

    [Fact]
    public void Visibility_CarriesConsequencesFlag()
    {
        Assert.Equal(
            ["repo", "edit", "o/r", "--visibility", "public", "--accept-visibility-change-consequences"],
            GitHubService.BuildVisibilityArgs("o/r", "public"));
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("secret")]
    [InlineData("")]
    public void Visibility_UnknownValue_BuildsNothing(string visibility)
        => Assert.Null(GitHubService.BuildVisibilityArgs("o/r", visibility));

    [Fact]
    public void RepoEdit_NullMeansOmit_EmptyMeansClear()
    {
        // description null -> flag absent (unchanged); homepage "" -> flag present (clears).
        Assert.Equal(
            ["repo", "edit", "o/r", "--homepage", "", "--add-topic", "wpf", "--remove-topic", "old"],
            GitHubService.BuildRepoEditArgs("o/r", null, "", ["wpf"], ["old"]));
    }

    [Fact]
    public void RepoEdit_AllUnset_BuildsBareArgs()
    {
        Assert.Equal(
            ["repo", "edit", "o/r"],
            GitHubService.BuildRepoEditArgs("o/r", null, null, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rerun_FailedFlagOnlyWhenRequested(bool failedOnly)
    {
        var expected = failedOnly
            ? new[] { "run", "rerun", "16752341890", "--repo", "o/r", "--failed" }
            : ["run", "rerun", "16752341890", "--repo", "o/r"];
        Assert.Equal(expected, GitHubService.BuildRerunArgs("o/r", 16752341890L, failedOnly));
    }

    [Fact]
    public void DefaultBranch_CarriesTheBranchName()
    {
        Assert.Equal(
            ["repo", "edit", "o/r", "--default-branch", "main"],
            GitHubService.BuildDefaultBranchArgs("o/r", "main"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultBranch_BlankValue_BuildsNothing(string branch)
    {
        // A blank flag argument makes gh consume the next token instead of failing.
        Assert.Null(GitHubService.BuildDefaultBranchArgs("o/r", branch));
    }

    [Fact]
    public void RepoFeatures_ValueRidesOnTheFlagToken()
    {
        // A bare --enable-issues means true; false can only be expressed as =false.
        Assert.Equal(
            ["repo", "edit", "o/r", "--enable-issues=false", "--enable-wiki=true"],
            GitHubService.BuildRepoFeatureArgs("o/r", issues: false, wiki: true, projects: null));
    }

    [Fact]
    public void RepoFeatures_AllUnset_BuildsBareArgs()
    {
        Assert.Equal(
            ["repo", "edit", "o/r"],
            GitHubService.BuildRepoFeatureArgs("o/r", null, null, null));
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void RepoFeatures_ProjectsFlagCarriesItsOwnValue(bool projects, string value)
    {
        Assert.Equal(
            ["repo", "edit", "o/r", $"--enable-projects={value}"],
            GitHubService.BuildRepoFeatureArgs("o/r", null, null, projects));
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void Notifications_UnreadOnlyUnlessAllRequested(bool includeRead, string all)
    {
        Assert.Equal(
            ["api", $"repos/o/r/notifications?all={all}&per_page=100", "--paginate"],
            GitHubService.BuildNotificationsArgs("o/r", includeRead));
    }

    /// <summary>
    /// Each of these reads fills a list or a picker that the reader takes for the repository's
    /// own contents, and none of them carries a control for reaching what a cap left behind.
    /// Following the pages to the end is what makes that reading true.
    /// </summary>
    [Theory]
    [InlineData("repos/o/r/releases?per_page=100")]
    [InlineData("repos/o/r/labels?per_page=100")]
    [InlineData("repos/o/r/milestones?state=all&per_page=100")]
    [InlineData("repos/o/r/notifications?all=false&per_page=100")]
    public void EveryRestListRead_FollowsThePagesToTheEnd(string path)
    {
        List<List<string>> reads =
        [
            GitHubService.BuildReleasesArgs("o/r"),
            GitHubService.BuildLabelsArgs("o/r"),
            GitHubService.BuildMilestonesArgs("o/r"),
            GitHubService.BuildNotificationsArgs("o/r", includeRead: false),
        ];

        var read = Assert.Single(reads, r => r.Contains(path));
        Assert.Equal(["api", path, "--paginate"], read);
    }

    /// <summary>
    /// The label read moved off `gh label list`, whose --limit is the only depth it has. A read
    /// still spending that flag would cap the picker again while the endpoint pages past it.
    /// </summary>
    [Fact]
    public void Labels_AreReadFromTheEndpointThatPages_NotTheCappedCommand()
    {
        var args = GitHubService.BuildLabelsArgs("o/r");

        Assert.DoesNotContain("label", args);
        Assert.DoesNotContain("--limit", args);
    }

    [Fact]
    public void MarkNotificationRead_PatchesTheThread()
    {
        Assert.Equal(
            ["api", "--method", "PATCH", "notifications/threads/14231733865"],
            GitHubService.BuildMarkNotificationReadArgs("14231733865"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../repos/o/r")]
    [InlineData("123abc")]
    [InlineData(" 123")]
    public void MarkNotificationRead_NonDigitThreadId_BuildsNothing(string threadId)
    {
        // The id lands inside a REST path; anything else could address another endpoint.
        Assert.Null(GitHubService.BuildMarkNotificationReadArgs(threadId));
    }

    [Fact]
    public void MarkRepoNotificationsRead_PutsTheRepoCollection()
    {
        Assert.Equal(
            ["api", "--method", "PUT", "repos/o/r/notifications"],
            GitHubService.BuildMarkRepoNotificationsReadArgs("o/r"));
    }

    [Fact]
    public void AssetDownload_WritesTheChosenPathAndReplaces()
    {
        Assert.Equal(
            ["release", "download", "v2.0.0", "--repo", "o/r", "--pattern", "setup.exe",
             "--output", @"C:\downloads\setup.exe", "--clobber"],
            GitHubService.BuildAssetDownloadArgs("o/r", "v2.0.0", "setup.exe", @"C:\downloads\setup.exe"));
    }

    [Fact]
    public void ReleaseDirDownload_TargetsTheScratchDirectory()
    {
        Assert.Equal(
            ["release", "download", "v2.0.0", "--repo", "o/r", "--dir", @"C:\scratch", "--clobber"],
            GitHubService.BuildReleaseDirDownloadArgs("o/r", "v2.0.0", @"C:\scratch"));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("Project Dashboard 2.0.0.msi")]
    [InlineData("sha256sums.txt")]
    public void PlainAssetNames_DownloadByPattern(string name)
        => Assert.False(GitHubService.NeedsFullReleaseFetch(name));

    [Theory]
    [InlineData("build[1].zip")]
    [InlineData("report*.txt")]
    [InlineData("what?.bin")]
    public void GlobbedAssetNames_FallBackToTheWholeRelease(string name)
    {
        // gh selects assets with filepath.Match, which has no escape on Windows: the
        // pattern "build[1].zip" matches "build1.zip" and never the literal name.
        Assert.True(GitHubService.NeedsFullReleaseFetch(name));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("build[1].zip")]
    [InlineData("Project Dashboard 2.0.0.msi")]
    public void PlainAssetNames_AreUsableAsAPathComponent(string name)
        => Assert.True(GitHubService.IsPlainAssetFileName(name));

    [Theory]
    [InlineData(@"..\..\Users\me\.ssh\id_ed25519[1]")]
    [InlineData("../../etc/hosts*")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts?")]
    [InlineData(@"sub\build[1].zip")]
    [InlineData("sub/build[1].zip")]
    [InlineData(@"\\server\share\payload[1].exe")]
    public void AssetNamesCarryingAPath_AreRefused(string name)
    {
        // The name comes from the release payload and is combined with the scratch
        // directory on the glob-fallback path: a rooted or traversing name resolves
        // outside the scratch and the move would relocate an unrelated local file.
        Assert.False(GitHubService.IsPlainAssetFileName(name));
    }

    [Fact]
    public async Task ATraversingGlobbedAssetName_IsRefusedBeforeAnythingIsFetched()
    {
        // The refusal has to land before the scratch fetch: past it, the move out of the
        // scratch directory resolves outside it and relocates a local file to the
        // reader's save destination.
        var outside = Path.Combine(TestEnv.NewDir("asset-guard"), "keep.txt");
        await File.WriteAllTextAsync(outside, "not an asset");
        var destination = Path.Combine(TestEnv.NewDir("asset-guard-dest"), "setup.exe");
        var name = Path.Combine("..", "..", "keep[1].txt");

        var result = await new GitHubService(new SettingsService())
            .DownloadReleaseAssetAsync("o/r", "v1.0.0", name, destination);

        Assert.False(result.Success);
        Assert.Contains("not a plain asset file name", result.FirstError);
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("HTTP 403: Must have admin rights to Repository.")]
    [InlineData("")]
    [InlineData("failed to delete repository: network unreachable")]
    public void DeleteFailure_WithoutTheScopeName_IsNotAScopeProblem(string error)
        => Assert.False(GitHubService.NeedsDeleteRepoScope(error));

    [Theory]
    [InlineData("needs the \"delete_repo\" scope")]
    [InlineData("gh auth refresh -h github.com -s DELETE_REPO")]
    public void DeleteFailure_NamingTheScope_IsAScopeProblem(string error)
        => Assert.True(GitHubService.NeedsDeleteRepoScope(error));

    /// <summary>
    /// Every mutation whose argument builder can refuse a token comes back as a failed
    /// result naming the token, never as an exception. A throw here is the one failure a
    /// caller's result handling does not cover, and nothing spawns: the refusal lands
    /// before gh is resolved, so this runs with no GitHub CLI present.
    /// </summary>
    [Fact]
    public async Task AnUnmappedToken_ComesBackAsAFailedResult_AndSpawnsNothing()
    {
        var gh = new GitHubService(new SettingsService());

        foreach (var (mutation, expected) in new (Func<Task<ProcessResult>>, string)[]
                 {
                     (() => gh.MergePullRequestAsync("o/r", 1, "octopus"), "octopus"),
                     (() => gh.ReviewPullRequestAsync("o/r", 1, "dismiss"), "dismiss"),
                     (() => gh.SetRepoVisibilityAsync("o/r", "secret"), "secret"),
                     (() => gh.SetDefaultBranchAsync("o/r", "   "), "blank"),
                     (() => gh.MarkNotificationReadAsync("../../repos/o/r"), "notification id"),
                 })
        {
            var result = await mutation();

            Assert.False(result.Success);
            Assert.False(result.TimedOut);
            Assert.Contains(expected, result.FirstError);
        }
    }
}

/// <summary>
/// The issue and pull-request list reads. gh exposes no cursor on either command — --limit is
/// the whole of its depth control — so the window is the argument that has to be exact, and the
/// facets are asserted as the tokens gh receives rather than as anything applied afterwards.
/// </summary>
public class GitHubListArgBuilderTests
{
    private static List<string> IssueArgs(GitHubService.GitHubListQuery query) =>
        GitHubService.BuildIssueListArgs("o/r", query);

    [Fact]
    public void IssueList_DefaultQuery_ReadsOpenAtTheFirstWindow()
        => Assert.Equal(
            ["issue", "list", "--repo", "o/r", "--state", "open",
             "--json", "number,title,state,createdAt,updatedAt,author,labels", "--limit", "100"],
            IssueArgs(new GitHubService.GitHubListQuery()));

    [Theory]
    [InlineData(GitHubListState.Open, "open")]
    [InlineData(GitHubListState.Closed, "closed")]
    [InlineData(GitHubListState.All, "all")]
    public void IssueList_StateRidesOnTheEnumsToken(GitHubListState state, string token)
        => Assert.Equal(token, IssueArgs(new GitHubService.GitHubListQuery(state.Token()))[5]);

    /// <summary>
    /// The second window asks for the first one again plus a page. A read that asked only for
    /// the new rows would need a cursor gh does not have, and the page it returned could not be
    /// proved to continue the one on screen.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    public void IssueList_WindowTravelsAsTheWholeLimit(int window)
        => Assert.Equal(["--limit", window.ToString()],
            IssueArgs(new GitHubService.GitHubListQuery(Limit: window))[^2..]);

    [Fact]
    public void IssueList_SearchTravelsVerbatim()
        => Assert.Equal(["--search", "crash in:title label:\"needs triage\""],
            IssueArgs(new GitHubService.GitHubListQuery(Search: "crash in:title label:\"needs triage\""))[^2..]);

    [Fact]
    public void IssueList_BlankSearch_CarriesNoSearchFlag()
        => Assert.DoesNotContain("--search", IssueArgs(new GitHubService.GitHubListQuery(Search: "   ")));

    /// <summary>
    /// A search that names a state is left as the only state in the query. gh lets such a search
    /// overrule --state, so any other value would be a facet the surface displays and the read
    /// never applied; "all" is the value that adds no qualifier of its own.
    /// </summary>
    [Theory]
    [InlineData("state:closed")]
    [InlineData("crash is:closed sort:created-asc")]
    [InlineData("is:merged")]
    [InlineData("STATE:OPEN")]
    public void IssueList_SearchNamingAState_LeavesTheStateFlagOutOfTheWay(string search)
    {
        var args = IssueArgs(new GitHubService.GitHubListQuery("closed", search));

        Assert.Equal("all", args[5]);
        Assert.Equal(search, args[^1]);
    }

    [Theory]
    [InlineData("crash on this: line")]
    [InlineData("misstate:thing")]
    [InlineData("label:is:open")]
    [InlineData("")]
    public void AnythingElse_LeavesTheStatePickerInForce(string search)
        => Assert.False(GitHubService.SearchSetsState(search));

    /// <summary>
    /// Text inside a quoted phrase is searched for, not interpreted. Read as a qualifier it would
    /// drop the state flag and put a notice on screen saying the picker was overruled by a phrase
    /// that never touched the state.
    /// </summary>
    [Theory]
    [InlineData("title:\"a state:closed b\"")]
    [InlineData("\"is:closed\"")]
    [InlineData("\"unterminated is:closed")]
    public void AStateQualifierInsideAQuotedPhrase_IsPartOfThePhrase(string search)
    {
        Assert.False(GitHubService.SearchSetsState(search));
        Assert.Equal("closed", GitHubService.BuildIssueListArgs("o/r", new GitHubService.GitHubListQuery("closed", search))[5]);
    }

    [Theory]
    [InlineData("title:\"a b\" state:closed")]
    [InlineData("state:closed title:\"a b\"")]
    [InlineData("\"a b\" is:merged")]
    public void AStateQualifierOutsideThePhrase_StillNamesTheState(string search)
        => Assert.True(GitHubService.SearchSetsState(search));

    /// <summary>
    /// What a failed read can tell its reader. gh answers a malformed search with an empty result
    /// rather than an error, so the failures that do reach here are the ones only gh can explain —
    /// a connection, a repository, a sign-in — and its own first line is what says which.
    /// </summary>
    [Fact]
    public void AFailure_CarriesGhsFirstLine()
        => Assert.Equal("error connecting to nonexistent.invalid",
            GitHubService.FailureText(new ProcessResult(1,
                "", "\nerror connecting to nonexistent.invalid\ncheck your internet connection\n", TimedOut: false)));

    [Fact]
    public void ATimeout_SaysSoRatherThanCarryingASilentStderr()
        => Assert.Equal("The GitHub CLI did not answer in time.",
            GitHubService.FailureText(new ProcessResult(1, "", "", TimedOut: true)));

    [Fact]
    public void AFailureThatSaidNothing_CarriesNothing()
        => Assert.Equal("", GitHubService.FailureText(new ProcessResult(1, "", "   ", TimedOut: false)));

    /// <summary>The line lands in a status row beside the app's own sentence, so it is capped.</summary>
    [Fact]
    public void AVeryLongFailureLine_IsCut()
    {
        var text = GitHubService.FailureText(new ProcessResult(1, "", new string('x', 500), TimedOut: false));

        Assert.Equal(201, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void AQuotedPhrase_IsOneTerm()
        => Assert.Equal(["title:\"a state:closed b\"", "label:bug"],
            GitHubService.SearchTerms("  title:\"a state:closed b\"   label:bug "));

    [Fact]
    public void PullRequestList_ReadsTheStateFieldItsRowsNowRender()
    {
        var args = GitHubService.BuildPullRequestListArgs("o/r", new GitHubService.GitHubListQuery("all", Limit: 200));

        Assert.Equal(
            ["pr", "list", "--repo", "o/r", "--state", "all",
             "--json", "number,title,state,author,isDraft,updatedAt,statusCheckRollup", "--limit", "200"],
            args);
    }

    /// <summary>
    /// A window that came back full says only that more may be behind it — the next read at a
    /// larger window is what answers it. Anything short of full is the whole answer.
    /// </summary>
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, true)]
    [InlineData(99, 100, false)]
    [InlineData(0, 100, false)]
    public void APageIsOnlyOpenEnded_WhenItCameBackFull(int loaded, int limit, bool mayHaveMore)
        => Assert.Equal(mayHaveMore, GitHubService.PageMayHaveMore(loaded, limit));

    // ── Milestone facet ─────────────────────────────────────────────────────────

    /// <summary>
    /// The number, not the title: gh reads a numeric value as a milestone number, so a milestone
    /// whose title happens to read as a number is still the one the read addresses.
    /// </summary>
    [Fact]
    public void IssueList_AMilestone_TravelsAsItsNumber()
        => Assert.Equal(["--milestone", "7"],
            IssueArgs(new GitHubService.GitHubListQuery(Milestone: new MilestoneFacet(7, "12")))[^2..]);

    [Fact]
    public void IssueList_NoMilestone_SendsNoMilestoneFlag()
        => Assert.DoesNotContain("--milestone", IssueArgs(new GitHubService.GitHubListQuery()));

    /// <summary>
    /// gh turns the milestone flag into a <c>milestone:</c> qualifier, so a search carrying one of
    /// its own would leave two that intersect to nothing while the picker still names a milestone.
    /// </summary>
    [Theory]
    [InlineData("milestone:\"v2.0\"")]
    [InlineData("bug milestone:v2.0")]
    [InlineData("no:milestone")]
    public void IssueList_ASearchNamingAMilestone_IsLeftAsTheOnlyOneInForce(string search)
        => Assert.DoesNotContain("--milestone",
            IssueArgs(new GitHubService.GitHubListQuery(Search: search, Milestone: new MilestoneFacet(7, "v1.0"))));

    /// <summary>Read as a qualifier, a quoted phrase would drop a filter it never named.</summary>
    [Theory]
    [InlineData("title:\"a milestone:v2 b\"")]
    [InlineData("\"no:milestone\"")]
    public void AMilestoneQualifierInsideAQuotedPhrase_IsPartOfThePhrase(string search)
    {
        Assert.False(GitHubService.SearchSetsMilestone(search));
        Assert.Contains("--milestone",
            IssueArgs(new GitHubService.GitHubListQuery(Search: search, Milestone: new MilestoneFacet(7, "v1.0"))));
    }

    [Theory]
    [InlineData("bug")]
    [InlineData("state:closed")]
    [InlineData("")]
    public void AnythingElse_LeavesTheMilestonePickerInForce(string search)
        => Assert.False(GitHubService.SearchSetsMilestone(search));

    /// <summary>
    /// gh pr list carries no milestone flag, so a milestone set on a pull-request query reaches
    /// no argument rather than reaching a flag gh would reject.
    /// </summary>
    [Fact]
    public void PullRequestList_TakesNoMilestone()
        => Assert.DoesNotContain("--milestone",
            GitHubService.BuildPullRequestListArgs("o/r",
                new GitHubService.GitHubListQuery(Milestone: new MilestoneFacet(7, "v1.0"))));

    /// <summary>Creating an issue addresses a milestone by name; only the list read takes a number.</summary>
    [Fact]
    public void IssueCreate_AMilestone_TravelsAsItsTitle()
        => Assert.Equal(
            ["issue", "create", "--repo", "o/r", "--title", "t", "--body", "b", "--milestone", "v2.0"],
            GitHubService.BuildCreateIssueArgs("o/r", "t", "b", null, "v2.0"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IssueCreate_NoMilestone_SendsNoMilestoneFlag(string? milestone)
        => Assert.DoesNotContain("--milestone",
            GitHubService.BuildCreateIssueArgs("o/r", "t", "b", null, milestone));
}

/// <summary>
/// A notification's only link is its REST url, which answers JSON and names the
/// pull-request collection differently from the site. Anything the mapping cannot
/// vouch for reads as "" so the UI falls back to the repository page rather than
/// launching a guessed address.
/// </summary>
public class GitHubNotificationUrlTests
{
    [Fact]
    public void IssueUrl_MapsToTheIssuePage()
        => Assert.Equal("https://github.com/o/r/issues/41",
            GitHubService.NotificationWebUrl("https://api.github.com/repos/o/r/issues/41"));

    [Fact]
    public void PullsUrl_MapsToTheSingularPullPath()
        => Assert.Equal("https://github.com/o/r/pull/12",
            GitHubService.NotificationWebUrl("https://api.github.com/repos/o/r/pulls/12"));

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases/99")]        // no web equivalent by id
    [InlineData("https://api.github.com/repos/o/r/issues/comments/5")]  // too many segments
    [InlineData("https://api.github.com/repos/o/r/issues/notanumber")]
    [InlineData("https://api.github.com/repos/o/r/issues")]
    [InlineData("https://evil.example.com/repos/o/r/issues/1")]
    [InlineData("http://api.github.com/repos/o/r/issues/1")]
    [InlineData("https://api.github.com/repos/../r/issues/1")]  // would resolve elsewhere on the site
    [InlineData("https://api.github.com/repos/o/../issues/1")]
    [InlineData("https://api.github.com/repos//r/issues/1")]
    [InlineData("https://api.github.com/repos/o/r/issues/41?x=1")]
    [InlineData("")]
    public void AnythingElse_MapsToNothing(string apiUrl)
        => Assert.Equal("", GitHubService.NotificationWebUrl(apiUrl));
}

/// <summary>
/// The two gh calls Set 2's fork surface spawns. Both are asserted as exact argument lists
/// because each carries a flag or a path segment whose absence changes what the command does
/// rather than making it fail.
/// </summary>
public class GitHubForkArgBuilderTests
{
    [Fact]
    public void SyncWithoutForce_AsksForNoHardReset()
        => Assert.Equal(["repo", "sync"], GitHubService.BuildSyncForkArgs(force: false));

    [Fact]
    public void SyncWithForce_CarriesTheFlagThatDiscardsLocalCommits()
        => Assert.Equal(["repo", "sync", "--force"], GitHubService.BuildSyncForkArgs(force: true));

    /// <summary>
    /// Both sides carry an owner: an unqualified head resolves inside the base repository, which
    /// compares the parent with itself and answers zero ahead, zero behind for every fork.
    /// </summary>
    [Fact]
    public void Compare_QualifiesBothSidesWithTheirOwner()
        => Assert.Equal(["api", "repos/upstream/tool/compare/upstream:main...me:main"],
            GitHubService.BuildForkCompareArgs("upstream/tool", "upstream", "me", "main"));

    [Fact]
    public void Compare_KeepsASlashedBranchNameWhole()
        => Assert.Equal(["api", "repos/upstream/tool/compare/upstream:release/2.x...me:release/2.x"],
            GitHubService.BuildForkCompareArgs("upstream/tool", "upstream", "me", "release/2.x"));

    [Theory]
    [InlineData("", "upstream", "me", "main")]
    [InlineData("upstream/tool", "", "me", "main")]
    [InlineData("upstream/tool", "upstream", "", "main")]
    [InlineData("upstream/tool", "upstream", "me", "")]
    public void Compare_BuildsNothingFromAnIncompletelyNamedComparison(
        string parentSlug, string parentOwner, string forkOwner, string branch)
        => Assert.Null(GitHubService.BuildForkCompareArgs(parentSlug, parentOwner, forkOwner, branch));
}
