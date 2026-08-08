using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Actions, Releases, Repo-settings, notification and danger-zone surfaces: the VM
/// state transitions and guards that hold without a live gh. Remote reads come from the
/// view model's own overridable fetch members, and the one mutation whose outcome the
/// caller inspects (repository delete) is stubbed the same way — no test in this file
/// spawns a process or touches the network.
/// </summary>
public class ProjectDetailViewModelGitHubTabsTests
{
    private static ProjectInfo LocalProject()
    {
        var dir = TestEnv.NewDir("gh-tabs");
        return new ProjectInfo { DirectoryName = "gh-tabs", DisplayName = "gh-tabs", FullPath = dir };
    }

    /// <summary>A project whose slug is o/r: guards past the slug check without a repo.</summary>
    private static ProjectInfo RemoteProject()
    {
        var project = LocalProject();
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    private static WorkflowRun Run(long id, string status = "completed", string conclusion = "success") =>
        new() { Id = id, Name = "CI", Branch = "master", Event = "push", Status = status, Conclusion = conclusion };

    private static ProcessResult Failed(string error) => new(1, "", error, TimedOut: false);

    // ── Actions ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowRuns_LoadIntoTheListAndMarkTheTabLoaded()
    {
        var vm = new StubTabsViewModel { Runs = [Run(1), Run(2, "in_progress", "")] };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.WorkflowRuns.Count);
        Assert.True(vm.WorkflowRunsLoaded);
        Assert.Equal("", vm.WorkflowRunsError);
    }

    [Fact]
    public async Task AFailedRunFetch_ShowsAnErrorAndLeavesTheTabUnloaded()
    {
        // Null is a failed fetch. Marking the tab loaded would make the next visit skip
        // its own load and show an empty list as though the repo had no runs.
        var vm = new StubTabsViewModel { Runs = null };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        Assert.False(vm.WorkflowRunsLoaded);
        Assert.Empty(vm.WorkflowRuns);
        Assert.Contains("Couldn't load workflow runs", vm.WorkflowRunsError);
    }

    [Fact]
    public async Task WorkflowRuns_WithoutARemote_SaySoInsteadOfFetching()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(LocalProject());

        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        Assert.Equal("This project has no GitHub remote.", vm.WorkflowRunsError);
        Assert.Equal(0, vm.RunFetches);
    }

    [Fact]
    public async Task SelectingARun_LoadsItsJobsAndSteps()
    {
        var vm = new StubTabsViewModel
        {
            Runs = [Run(1)],
            Jobs = [new WorkflowJob { Id = 9, Name = "build", Status = "completed", Conclusion = "success",
                                      Steps = [new WorkflowStep { Number = 1, Name = "Set up job" }] }]
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];

        var job = Assert.Single(vm.WorkflowJobs);
        Assert.Equal("build", job.Name);
        Assert.Single(job.Steps);
        Assert.Equal("", vm.WorkflowJobsError);
    }

    [Fact]
    public async Task AFailedJobFetch_ShowsAnErrorRatherThanAnEmptyRun()
    {
        var vm = new StubTabsViewModel { Runs = [Run(1)], Jobs = null };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];

        Assert.Empty(vm.WorkflowJobs);
        Assert.Contains("Couldn't load this run's jobs", vm.WorkflowJobsError);
    }

    [Fact]
    public async Task ASupersededJobFetch_LeavesTheLiveFetchsSpinnerUp()
    {
        // The first fetch returns after the reader has moved to another run. Clearing the
        // flag there flashes the empty-state text over a run that is still loading.
        var first = new TaskCompletionSource<List<WorkflowJob>?>();
        var second = new TaskCompletionSource<List<WorkflowJob>?>();
        var vm = new StubTabsViewModel { Runs = [Run(1), Run(2)], JobGates = new Queue<TaskCompletionSource<List<WorkflowJob>?>>([first, second]) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];
        vm.SelectedWorkflowRun = vm.WorkflowRuns[1];
        first.SetResult([new WorkflowJob { Id = 1, Name = "stale" }]);

        Assert.True(vm.WorkflowJobsLoading);
        Assert.Empty(vm.WorkflowJobs);

        second.SetResult([new WorkflowJob { Id = 2, Name = "live" }]);

        Assert.False(vm.WorkflowJobsLoading);
        Assert.Equal("live", Assert.Single(vm.WorkflowJobs).Name);
    }

    [Fact]
    public async Task DeselectingTheRun_TakesTheSpinnerDownWithIt()
    {
        var gate = new TaskCompletionSource<List<WorkflowJob>?>();
        var vm = new StubTabsViewModel { Runs = [Run(1)], JobGates = new Queue<TaskCompletionSource<List<WorkflowJob>?>>([gate]) };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];

        vm.SelectedWorkflowRun = null;

        Assert.False(vm.WorkflowJobsLoading);
    }

    [Fact]
    public async Task ASupersededSettingsFetch_LeavesTheLiveFetchsSpinnerUp()
    {
        var first = new TaskCompletionSource<RepoSettings?>();
        var second = new TaskCompletionSource<RepoSettings?>();
        var vm = new StubTabsViewModel { SettingsGates = new Queue<TaskCompletionSource<RepoSettings?>>([first, second]) };
        await vm.SetProjectAsync(RemoteProject());

        var stale = vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        var live = vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        first.SetResult(new RepoSettings { DefaultBranch = "main" });

        Assert.True(vm.RepoSettingsLoading);

        second.SetResult(new RepoSettings { DefaultBranch = "main" });
        await stale;
        await live;

        Assert.False(vm.RepoSettingsLoading);
    }

    [Fact]
    public async Task ASupersededNotificationFetch_LeavesTheLiveFetchsSpinnerUp()
    {
        var first = new TaskCompletionSource<List<GitHubNotification>?>();
        var second = new TaskCompletionSource<List<GitHubNotification>?>();
        var vm = new StubTabsViewModel { NotificationGates = new Queue<TaskCompletionSource<List<GitHubNotification>?>>([first, second]) };
        await vm.SetProjectAsync(RemoteProject());

        var stale = vm.LoadNotificationsCommand.ExecuteAsync(null);
        var live = vm.LoadNotificationsCommand.ExecuteAsync(null);
        first.SetResult([]);

        Assert.True(vm.NotificationsLoading);

        second.SetResult([]);
        await stale;
        await live;

        Assert.False(vm.NotificationsLoading);
    }

    [Fact]
    public async Task RefreshingTheRunList_KeepsTheSelectedRun()
    {
        // Every refresh builds new instances; dropping the selection would blank the
        // jobs pane the reader is watching.
        var vm = new StubTabsViewModel { Runs = [Run(1), Run(2)], Jobs = [] };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        vm.SelectedWorkflowRun = vm.WorkflowRuns[1];

        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedWorkflowRun);
        Assert.Equal(2L, vm.SelectedWorkflowRun.Id);
    }

    [Fact]
    public async Task CancellingAFinishedRun_SaysSoInsteadOfCallingGh()
    {
        var vm = new StubTabsViewModel { Runs = [Run(1)] };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];

        await vm.CancelWorkflowRunCommand.ExecuteAsync(null);

        Assert.Equal("That run has already finished — there is nothing to cancel.", vm.GitHubStatusText);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// Nothing selected: no gh spawn, no busy gate, no launch — and the two mutating
    /// commands name the row to pick rather than returning with no trace at all.
    /// </summary>
    [Fact]
    public async Task ActionCommands_WithoutARunSelected_SpawnNothingAndSayWhatToSelect()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(RemoteProject());

        await vm.RerunWorkflowRunCommand.ExecuteAsync(null);
        Assert.Equal("Select a workflow run first.", vm.GitHubStatusText);

        await vm.CancelWorkflowRunCommand.ExecuteAsync(null);
        Assert.Equal("Select a workflow run first.", vm.GitHubStatusText);

        vm.OpenWorkflowRunCommand.Execute(null);

        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Opened);
    }

    [Fact]
    public async Task OpeningARun_LaunchesItsUrl()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(RemoteProject());

        vm.OpenWorkflowRunCommand.Execute(new WorkflowRun { Url = "https://github.com/o/r/actions/runs/5" });

        Assert.Equal("https://github.com/o/r/actions/runs/5", Assert.Single(vm.Opened));
    }

    // ── Releases ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Releases_LoadIntoTheListAndMarkTheTabLoaded()
    {
        var vm = new StubTabsViewModel
        {
            SeedReleases = [new Release { TagName = "v1.0.0", Name = "One" }]
        };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadReleasesCommand.ExecuteAsync(null);

        Assert.Single(vm.Releases);
        Assert.True(vm.ReleasesLoaded);
        Assert.Equal("", vm.ReleasesError);
    }

    [Fact]
    public async Task AFailedReleaseFetch_ShowsAnErrorAndLeavesTheTabUnloaded()
    {
        var vm = new StubTabsViewModel { SeedReleases = null };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadReleasesCommand.ExecuteAsync(null);

        Assert.False(vm.ReleasesLoaded);
        Assert.Contains("Couldn't load releases", vm.ReleasesError);
    }

    [Fact]
    public async Task NewRelease_WithoutARemote_SaysSoAndKeepsComposeClosed()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(LocalProject());

        await vm.ShowNewReleaseCommand.ExecuteAsync(null);

        Assert.False(vm.ReleaseComposeVisible);
        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SubmitRelease_WithNoTagPicked_SaysSo()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(RemoteProject());
        vm.NewReleaseTitle = "Two";

        await vm.SubmitNewReleaseCommand.ExecuteAsync(null);

        Assert.Equal("Pick an existing tag to release from.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SubmitRelease_WithATagThatIsNotInTheRepository_RefusesBeforeSpawningGh()
    {
        // Publishing creates the tag on the remote when it is missing there, so a name
        // the repository does not hold would become a tag on the default branch head.
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(RemoteProject());
        vm.AvailableTagNames = ["v1.0.0"];
        vm.NewReleaseTag = "v9.9.9";
        vm.NewReleaseTitle = "Nine";

        await vm.SubmitNewReleaseCommand.ExecuteAsync(null);

        Assert.Equal("v9.9.9 isn't a tag in this repository — refresh the tag list.", vm.GitHubStatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SubmitRelease_WithAnEmptyTitle_SaysSo()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(RemoteProject());
        vm.AvailableTagNames = ["v1.0.0"];
        vm.NewReleaseTag = "v1.0.0";
        vm.NewReleaseTitle = "   ";

        await vm.SubmitNewReleaseCommand.ExecuteAsync(null);

        Assert.Equal("Enter a release title first.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task SubmitRelease_PinsTheCommitTheSelectedTagNames()
    {
        // The picker lists LOCAL tags. An unpushed one is created on the remote by
        // publishing, and without the commit it would be created on the default branch
        // head — a release pointing at code the tag never named.
        var vm = new StubTabsViewModel
        {
            SeedTags = [new TagInfo { Name = "v1.0.0", TargetSha = "9f1c0de4a2b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3" },
                    new TagInfo { Name = "v0.9.0", TargetSha = "1111111111111111111111111111111111111111" }]
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.ShowNewReleaseCommand.ExecuteAsync(null);
        vm.NewReleaseTag = "v1.0.0";
        vm.NewReleaseTitle = "One";

        await vm.SubmitNewReleaseCommand.ExecuteAsync(null);

        Assert.Equal("v1.0.0", vm.CreatedTag);
        Assert.Equal("9f1c0de4a2b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3", vm.CreatedTargetSha);
    }

    [Fact]
    public async Task SubmitRelease_WithNoCommitResolvedForTheTag_SendsNoTarget()
    {
        var vm = new StubTabsViewModel { SeedTags = [new TagInfo { Name = "v1.0.0", TargetSha = "" }] };
        await vm.SetProjectAsync(RemoteProject());
        await vm.ShowNewReleaseCommand.ExecuteAsync(null);
        vm.NewReleaseTag = "v1.0.0";
        vm.NewReleaseTitle = "One";

        await vm.SubmitNewReleaseCommand.ExecuteAsync(null);

        Assert.Equal("v1.0.0", vm.CreatedTag);
        Assert.Equal("", vm.CreatedTargetSha);
    }

    /// <summary>
    /// An empty picker after a refused ref read reads as "this repository has no tags", and the
    /// only remaining guidance — "pick an existing tag" — points at a list nothing could fill.
    /// The failure is reported instead so the reader knows the list is unread, not empty.
    /// </summary>
    [Fact]
    public async Task ShowNewRelease_WhenTheTagReadFails_SaysSoRatherThanOfferingAnEmptyPicker()
    {
        var vm = new StubTabsViewModel { TagReadError = "refused by the fixture" };
        await vm.SetProjectAsync(RemoteProject());

        await vm.ShowNewReleaseCommand.ExecuteAsync(null);

        Assert.Empty(vm.AvailableTagNames);
        Assert.Contains("Could not read this repository's tags", vm.GitHubStatusText);
        Assert.Contains("refused by the fixture", vm.GitHubStatusText);
    }

    [Fact]
    public async Task ProjectSwitch_DropsTheResolvedTagCommits()
    {
        var vm = new StubTabsViewModel
        {
            SeedTags = [new TagInfo { Name = "v1.0.0", TargetSha = "9f1c0de4a2b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3" }]
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.ShowNewReleaseCommand.ExecuteAsync(null);

        await vm.SetProjectAsync(RemoteProject());

        Assert.Empty(vm.AvailableTagNames);
        Assert.Null(vm.ResolveReleaseTagTarget("v1.0.0"));
    }

    [Fact]
    public async Task CancellingTheSaveDialog_DownloadsNothing()
    {
        var vm = new StubTabsViewModel
        {
            SeedReleases = [new Release { TagName = "v1.0.0", Assets = [new ReleaseAsset { Name = "setup.exe" }] }],
            SavePath = null
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadReleasesCommand.ExecuteAsync(null);
        vm.SelectedRelease = vm.Releases[0];

        await vm.DownloadReleaseAssetCommand.ExecuteAsync(vm.Releases[0].Assets[0]);

        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.GitHubStatusText);
    }

    [Theory]
    [InlineData(true, "published release v2.0.0")]
    [InlineData(false, "draft release v2.0.0")]
    public void ReleaseDeleteConfirm_NamesTheTagAndWhetherItIsPublished(bool published, string expected)
    {
        var message = ProjectDetailViewModel.ReleaseDeleteMessage("v2.0.0", published);
        Assert.Contains(expected, message);
        // The tag outlives the release either way; the confirmation must not imply otherwise.
        Assert.Contains("git tag stays", message);
    }

    [Fact]
    public void ReleaseSize_ReadsInTheLargestUsefulUnit()
    {
        Assert.Equal("512 B", ReleaseAsset.FormatSize(512));
        Assert.Equal("1 KB", ReleaseAsset.FormatSize(1024));
        Assert.Equal("18 MB", ReleaseAsset.FormatSize(18874368));
        Assert.Equal("1.5 GB", ReleaseAsset.FormatSize(1610612736));
    }

    // ── Repo settings ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RepoSettings_SeedTheEditorsFromWhatTheRemoteReports()
    {
        var vm = new StubTabsViewModel
        {
            Settings = new RepoSettings
            {
                Name = "r",
                Description = "A dashboard",
                Homepage = "https://example.com",
                Topics = ["wpf", "dotnet"],
                Visibility = "public",
                DefaultBranch = "main",
                HasIssues = true,
                HasWiki = false,
                HasProjects = true
            }
        };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);

        Assert.True(vm.RepoSettingsLoaded);
        Assert.Equal("A dashboard", vm.RepoDescriptionDraft);
        Assert.Equal("https://example.com", vm.RepoHomepageDraft);
        Assert.Equal("wpf, dotnet", vm.RepoTopicsDraft);
        Assert.Equal("main", vm.RepoDefaultBranchDraft);
        Assert.Equal(RepoVisibility.Public, vm.SelectedRepoVisibility);
        Assert.True(vm.RepoIssuesEnabled);
        Assert.False(vm.RepoWikiEnabled);
        Assert.True(vm.RepoProjectsEnabled);
    }

    [Fact]
    public async Task AFailedSettingsFetch_ShowsAnErrorAndLeavesTheEditorsEmpty()
    {
        var vm = new StubTabsViewModel { Settings = null };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);

        Assert.False(vm.RepoSettingsLoaded);
        Assert.Null(vm.RepoSettings);
        Assert.Contains("Couldn't load repository settings", vm.RepoSettingsError);
    }

    [Fact]
    public async Task SavingUnchangedDetails_SaysSoInsteadOfWritingToTheRemote()
    {
        var vm = new StubTabsViewModel
        {
            Settings = new RepoSettings { Description = "A dashboard", Homepage = "", Topics = ["wpf"] }
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);

        await vm.SaveRepoDetailsCommand.ExecuteAsync(null);

        Assert.Equal("Nothing to save — description, homepage and topics are unchanged.", vm.GitHubStatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SavingUnchangedFeatures_SaysSoInsteadOfWritingToTheRemote()
    {
        var vm = new StubTabsViewModel
        {
            Settings = new RepoSettings { HasIssues = true, HasWiki = false, HasProjects = false }
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);

        await vm.SaveRepoFeaturesCommand.ExecuteAsync(null);

        Assert.Equal("Nothing to save — the feature toggles are unchanged.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task ChangingTheDefaultBranchToItself_SaysSo()
    {
        var vm = new StubTabsViewModel { Settings = new RepoSettings { DefaultBranch = "main" } };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);

        await vm.ChangeDefaultBranchCommand.ExecuteAsync(null);

        Assert.Equal("main is already the default branch.", vm.GitHubStatusText);
    }

    [Fact]
    public async Task ChangingTheDefaultBranchToNothing_SaysSo()
    {
        var vm = new StubTabsViewModel { Settings = new RepoSettings { DefaultBranch = "main" } };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        vm.RepoDefaultBranchDraft = "  ";

        await vm.ChangeDefaultBranchCommand.ExecuteAsync(null);

        Assert.Equal("Enter the branch to make default.", vm.GitHubStatusText);
    }

    [Fact]
    public void TopicDiff_AddsAndRemovesOnlyWhatChanged()
    {
        var (add, remove) = ProjectDetailViewModel.DiffTopics(["wpf", "old"], ["wpf", "dotnet"]);
        Assert.Equal(["dotnet"], add);
        Assert.Equal(["old"], remove);
    }

    [Fact]
    public void TopicDiff_TreatsARecasedTopicAsUnchanged()
    {
        // GitHub lowercases every topic it stores, so "WPF" is the topic already there —
        // not a remove plus an add that would churn the repository for nothing.
        var (add, remove) = ProjectDetailViewModel.DiffTopics(["wpf"], ["WPF"]);
        Assert.Empty(add);
        Assert.Empty(remove);
    }

    [Fact]
    public void TopicsText_TrimsBlanksAndDuplicates()
        => Assert.Equal(["wpf", "dotnet"], ProjectDetailViewModel.SplitTopics(" wpf , dotnet ,, WPF "));

    [Theory]
    [InlineData(true, true, null)]     // unchanged
    [InlineData(true, false, false)]   // turned off
    [InlineData(false, true, true)]    // turned on
    public void FeatureChange_SendsOnlyWhatMoved(bool loaded, bool wanted, bool? expected)
        => Assert.Equal(expected, ProjectDetailViewModel.FeatureChange(loaded, wanted));

    [Fact]
    public void FeatureChange_LeavesAnUnreadFlagAlone()
    {
        // The response never reported the flag, so the checkbox showed a default. Saving
        // that default would turn a feature off nobody asked about.
        Assert.Null(ProjectDetailViewModel.FeatureChange(null, false));
        Assert.Null(ProjectDetailViewModel.FeatureChange(null, true));
    }

    [Fact]
    public async Task VisibilityChange_WithTheWrongNameTyped_ChangesNothing()
    {
        var vm = new StubTabsViewModel
        {
            Settings = new RepoSettings { Visibility = "private" },
            TypedConfirmation = "r"   // the bare name, not the slug
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        vm.SelectedRepoVisibility = RepoVisibility.Public;

        await vm.ChangeRepoVisibilityCommand.ExecuteAsync(null);

        Assert.Equal("Visibility unchanged — that isn't o/r.", vm.GitHubStatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task VisibilityChange_ToTheCurrentValue_SaysSoWithoutPrompting()
    {
        var vm = new StubTabsViewModel { Settings = new RepoSettings { Visibility = "private" } };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);

        await vm.ChangeRepoVisibilityCommand.ExecuteAsync(null);

        Assert.Equal("o/r is already private.", vm.GitHubStatusText);
        Assert.Equal(0, vm.TextPrompts);
    }

    [Fact]
    public void VisibilityConfirm_NamesBothEndsAndTheSlugToType()
    {
        var message = ProjectDetailViewModel.VisibilityConfirmMessage("o/r", "private", "public");
        Assert.Contains("from private to public", message);
        Assert.Contains("readable by anyone", message);
        Assert.Contains("Type o/r to confirm.", message);
    }

    [Theory]
    [InlineData("HTTP 422: Visibility change already in progress")]
    [InlineData("HTTP 409: a visibility change is IN PROGRESS for this repository")]
    public void VisibilityFailure_DuringTheServerLock_IsNamedRatherThanGeneric(string error)
        => Assert.Contains("still in progress — retry shortly",
            ProjectDetailViewModel.VisibilityFailureMessage(error));

    [Theory]
    // An organization policy refusal is a 422 the reader has to read: relabelling it as
    // the server lock hides the only sentence that says what to do about it.
    [InlineData("HTTP 422: Organization members are not permitted to make repositories public")]
    [InlineData("HTTP 422: Repository is archived and cannot be modified")]
    [InlineData("HTTP 409: reference already exists")]
    // The code appears only inside an echoed URL — no 4xx status at all.
    [InlineData("HTTP 403: Forbidden (https://api.github.com/repos/o/r-422/x)")]
    public void VisibilityFailure_ForAnother4xx_CarriesTheServerMessage(string error)
        => Assert.Equal($"Change visibility failed: {error}",
            ProjectDetailViewModel.VisibilityFailureMessage(error));

    [Fact]
    public void VisibilityFailure_ForAnythingElse_CarriesTheServerMessage()
        => Assert.Equal("Change visibility failed: HTTP 403: admin rights required",
            ProjectDetailViewModel.VisibilityFailureMessage("HTTP 403: admin rights required"));

    [Theory]
    [InlineData("o/r", true)]
    [InlineData("  o/r  ", true)]
    [InlineData("O/R", true)]      // GitHub resolves repository names case-insensitively
    [InlineData("r", false)]       // the bare name would match somebody else's repo too
    [InlineData("o/r2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RepoNameConfirmation_AcceptsOnlyTheSlug(string? typed, bool expected)
        => Assert.Equal(expected, ProjectDetailViewModel.RepoNameConfirmed(typed, "o/r"));

    [Fact]
    public async Task Notifications_LoadForTheCurrentRepo()
    {
        var vm = new StubTabsViewModel
        {
            SeedNotifications = [new GitHubNotification { ThreadId = "1", Title = "Review me", Unread = true }]
        };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadNotificationsCommand.ExecuteAsync(null);

        Assert.Equal("Review me", Assert.Single(vm.Notifications).Title);
        Assert.Equal("", vm.NotificationsError);
    }

    [Fact]
    public async Task AFailedNotificationFetch_ShowsAnErrorRatherThanAnEmptyInbox()
    {
        var vm = new StubTabsViewModel { SeedNotifications = null };
        await vm.SetProjectAsync(RemoteProject());

        await vm.LoadNotificationsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Notifications);
        Assert.Contains("Couldn't load notifications", vm.NotificationsError);
    }

    [Fact]
    public async Task OpeningANotification_NeverMarksItRead()
    {
        // Mark-as-read is an explicit act: opening the subject leaves the thread unread.
        var notification = new GitHubNotification
        {
            ThreadId = "1", Title = "Review me", Unread = true,
            WebUrl = "https://github.com/o/r/pull/12"
        };
        var vm = new StubTabsViewModel { SeedNotifications = [notification] };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadNotificationsCommand.ExecuteAsync(null);

        vm.OpenNotificationCommand.Execute(vm.Notifications[0]);

        Assert.Equal("https://github.com/o/r/pull/12", Assert.Single(vm.Opened));
        Assert.True(vm.Notifications[0].Unread);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.GitHubStatusText);
    }

    [Fact]
    public async Task ANotificationWithNoWebPage_OpensTheRepository()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(RemoteProject());

        vm.OpenNotificationCommand.Execute(new GitHubNotification { ThreadId = "1", WebUrl = "" });

        Assert.Equal("https://github.com/o/r", Assert.Single(vm.Opened));
    }

    [Fact]
    public async Task MarkAllRead_WithNothingUnread_SaysSoWithoutConfirming()
    {
        var vm = new StubTabsViewModel { SeedNotifications = [] };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadNotificationsCommand.ExecuteAsync(null);

        await vm.MarkAllNotificationsReadCommand.ExecuteAsync(null);

        Assert.Equal("No unread notifications on this repository.", vm.GitHubStatusText);
        Assert.Equal(0, vm.Confirms);
    }

    [Fact]
    public void MarkAllReadConfirm_NamesTheWholeRepositoryNotThePageShown()
    {
        // The list is one page; the call clears every thread on the repository. A count
        // taken from the visible rows would understate what the reader is agreeing to.
        var message = ProjectDetailViewModel.MarkAllReadMessage("o/r", 50);
        Assert.Contains("every unread notification thread on o/r", message);
        Assert.Contains("clears threads beyond the 50 shown here", message);
        Assert.DoesNotContain("Mark all 50", message);
    }

    [Fact]
    public async Task WithTheDangerZoneOff_ItIsHiddenAndDeleteRefusesBeforeAnyPrompt()
    {
        var vm = new StubTabsViewModel { DangerZone = false };
        await vm.SetProjectAsync(RemoteProject());

        Assert.False(vm.DangerZoneEnabled);

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        // Refused at the gate: no typed prompt, no gh call, no busy gate taken.
        Assert.Equal(ProjectDetailViewModel.DangerZoneOffNotice, vm.GitHubStatusText);
        Assert.Equal(0, vm.TextPrompts);
        Assert.Equal(0, vm.DeleteAttempts);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.RepoDeleteNotice);
    }

    [Fact]
    public async Task TurningTheDangerZoneOffMidSession_ClosesTheCommandToo()
    {
        // The bound flag drives visibility only; the command re-reads the setting, so a
        // stale panel left on screen cannot delete anything.
        var vm = new StubTabsViewModel { DangerZone = true };
        await vm.SetProjectAsync(RemoteProject());
        Assert.True(vm.DangerZoneEnabled);

        vm.DangerZone = false;
        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.DangerZoneOffNotice, vm.GitHubStatusText);
        Assert.False(vm.DangerZoneEnabled);
        Assert.Equal(0, vm.DeleteAttempts);
    }

    [Fact]
    public async Task DeleteRepo_WithoutARemote_SaysSo()
    {
        var vm = new StubTabsViewModel { DangerZone = true };
        await vm.SetProjectAsync(LocalProject());

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
        Assert.Equal(0, vm.DeleteAttempts);
    }

    [Fact]
    public async Task DeleteRepo_WithTheWrongNameTyped_DeletesNothing()
    {
        var vm = new StubTabsViewModel { DangerZone = true, TypedConfirmation = "r" };
        await vm.SetProjectAsync(RemoteProject());

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.Equal("Repository not deleted — that isn't o/r.", vm.GitHubStatusText);
        Assert.Equal(0, vm.DeleteAttempts);
    }

    [Fact]
    public async Task DeleteRepo_WithThePromptCancelled_DeletesNothingAndSaysNothing()
    {
        var vm = new StubTabsViewModel { DangerZone = true, TypedConfirmation = null };
        await vm.SetProjectAsync(RemoteProject());

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.TextPrompts);
        Assert.Equal(0, vm.DeleteAttempts);
        Assert.Equal("", vm.GitHubStatusText);
    }

    [Fact]
    public async Task DeleteRepo_Succeeding_SaysTheRemoteIsGoneAndTheLocalFilesAreNot()
    {
        var project = RemoteProject();
        project.OpenIssueCount = 3;
        project.OpenPrCount = 1;
        var vm = new StubTabsViewModel
        {
            DangerZone = true,
            TypedConfirmation = "o/r",
            Settings = new RepoSettings { Name = "r" },
            Runs = [Run(1)],
            Jobs = [],
            SeedReleases = [new Release { TagName = "v1.0.0" }],
            DeleteResult = new ProcessResult(0, "", "", TimedOut: false)
        };
        await vm.SetProjectAsync(project);
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        await vm.LoadReleasesCommand.ExecuteAsync(null);
        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];
        vm.SelectedRelease = vm.Releases[0];

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.DeleteAttempts);
        Assert.Contains("o/r is deleted on GitHub.", vm.RepoDeleteNotice);
        Assert.Contains("The local files remain at", vm.RepoDeleteNotice);
        Assert.Contains(project.FullPath, vm.RepoDeleteNotice);
        Assert.Contains("no longer exists", vm.RepoDeleteNotice);
        // The card's remote facts described a repository that is gone: unknown, not zero.
        Assert.Null(project.OpenIssueCount);
        Assert.Null(project.OpenPrCount);
        // The remote-backed surfaces stop showing a repository that no longer exists.
        Assert.Null(vm.RepoSettings);
        Assert.False(vm.RepoSettingsLoaded);
        Assert.False(vm.DeleteScopeHintVisible);
        // A live selection keeps its detail pane and its row commands armed against a
        // repository that is gone.
        Assert.Null(vm.SelectedRelease);
        Assert.Null(vm.SelectedWorkflowRun);
    }

    [Fact]
    public async Task DeleteRepo_WithTheGateTakenWhileTheDialogWasOpen_SaysTheConfirmationWasSpent()
    {
        // The typed slug is spent the moment the dialog closes. Dropping it silently
        // leaves a reader who typed the name watching a button that did nothing.
        var vm = new StubTabsViewModel { DangerZone = true, TypedConfirmation = "o/r", TakeGateWhilePrompting = true };
        await vm.SetProjectAsync(RemoteProject());

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.DeleteAttempts);
        Assert.Equal(ProjectDetailViewModel.BusyGateNotice("Repository delete"), vm.GitHubStatusText);
    }

    [Fact]
    public async Task VisibilityChange_WithTheGateTakenWhileTheDialogWasOpen_SaysTheConfirmationWasSpent()
    {
        var vm = new StubTabsViewModel
        {
            Settings = new RepoSettings { Visibility = "private" },
            TypedConfirmation = "o/r",
            TakeGateWhilePrompting = true
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadRepoSettingsCommand.ExecuteAsync(null);
        vm.SelectedRepoVisibility = RepoVisibility.Public;

        await vm.ChangeRepoVisibilityCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.BusyGateNotice("Visibility change"), vm.GitHubStatusText);
    }

    [Fact]
    public async Task DeleteRepo_RefusedForTheMissingScope_OffersTheGrant()
    {
        var vm = new StubTabsViewModel
        {
            DangerZone = true,
            TypedConfirmation = "o/r",
            DeleteResult = Failed("error: needs the \"delete_repo\" scope")
        };
        await vm.SetProjectAsync(RemoteProject());

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.True(vm.DeleteScopeHintVisible);
        Assert.Contains("gh auth refresh -h github.com -s delete_repo", vm.RepoDeleteNotice);
    }

    [Fact]
    public async Task DeleteRepo_RefusedForAnythingElse_DoesNotBlameTheScope()
    {
        var vm = new StubTabsViewModel
        {
            DangerZone = true,
            TypedConfirmation = "o/r",
            DeleteResult = Failed("HTTP 403: Must have admin rights to Repository.")
        };
        await vm.SetProjectAsync(RemoteProject());

        await vm.DeleteRepoCommand.ExecuteAsync(null);

        Assert.False(vm.DeleteScopeHintVisible);
        Assert.Equal("", vm.RepoDeleteNotice);
        Assert.Contains("admin rights", vm.GitHubStatusText);
    }

    [Fact]
    public void RepoDeleteConfirm_NamesTheSlugAndSparesTheLocalFiles()
    {
        var message = ProjectDetailViewModel.RepoDeleteMessage("o/r");
        Assert.Contains("Delete o/r from GitHub?", message);
        Assert.Contains("local files on this machine are not touched", message);
        Assert.Contains("Type o/r to confirm.", message);
    }

    // ── Cross-cutting ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectSwitch_ResetsEveryTabSurface()
    {
        var vm = new StubTabsViewModel
        {
            Runs = [Run(1)],
            SeedReleases = [new Release { TagName = "v1.0.0" }],
            Settings = new RepoSettings { Description = "A dashboard", DefaultBranch = "main" },
            SeedNotifications = [new GitHubNotification { ThreadId = "1" }]
        };
        await vm.SetProjectAsync(RemoteProject());
        await vm.LoadWorkflowRunsCommand.ExecuteAsync(null);
        await vm.LoadReleasesCommand.ExecuteAsync(null);
        await vm.LoadRepoTabCommand.ExecuteAsync(null);
        vm.SelectedWorkflowRun = vm.WorkflowRuns[0];
        vm.SelectedRelease = vm.Releases[0];
        vm.ReleaseComposeVisible = true;

        await vm.SetProjectAsync(RemoteProject());

        Assert.Empty(vm.WorkflowRuns);
        Assert.False(vm.WorkflowRunsLoaded);
        Assert.Null(vm.SelectedWorkflowRun);
        Assert.Empty(vm.Releases);
        Assert.False(vm.ReleasesLoaded);
        Assert.Null(vm.SelectedRelease);
        Assert.False(vm.ReleaseComposeVisible);
        Assert.Null(vm.RepoSettings);
        Assert.False(vm.RepoSettingsLoaded);
        Assert.Equal("", vm.RepoDescriptionDraft);
        Assert.Equal("", vm.RepoDefaultBranchDraft);
        Assert.Empty(vm.Notifications);
        Assert.Equal("", vm.RepoDeleteNotice);
    }

    // ── Issues and Pull Requests without a remote ───────────────────────────────

    /// <summary>
    /// The Issues and Pull Requests surfaces answer an absent remote the way Actions,
    /// Releases and Repo do. An empty list under no notice is a claim that the repository
    /// has no open issues or pull requests, and a repository nothing ever queried supports
    /// no such claim.
    /// </summary>
    [Fact]
    public async Task Issues_WithoutARemote_SaySoRatherThanShowingAnEmptyList()
    {
        var vm = new StubTabsViewModel();

        await vm.SetProjectAsync(LocalProject());

        Assert.Equal("This project has no GitHub remote.", vm.IssuesError);
        Assert.Empty(vm.Issues);
    }

    [Fact]
    public async Task RefreshingIssues_WithoutARemote_SaysSoInsteadOfReportingNothing()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(LocalProject());
        vm.IssuesError = "";

        await vm.RefreshIssuesCommand.ExecuteAsync(null);

        Assert.Equal("This project has no GitHub remote.", vm.IssuesError);
    }

    [Fact]
    public async Task PullRequests_WithoutARemote_SaySoAndLeaveTheTabUnloaded()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(LocalProject());

        await vm.LoadPullRequestsCommand.ExecuteAsync(null);

        Assert.Equal("This project has no GitHub remote.", vm.PullRequestsError);
        Assert.Empty(vm.PullRequests);
        Assert.False(vm.PullRequestsLoaded);
    }

    /// <summary>
    /// The compose openers refuse the same way New Release does. Left armed they walk the
    /// reader through a form whose submit can only fail.
    /// </summary>
    [Fact]
    public async Task NewIssueAndNewPullRequest_WithoutARemote_RefuseWithTheSameNotice()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(LocalProject());

        await vm.ShowNewIssueCommand.ExecuteAsync(null);
        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
        Assert.False(vm.IssueComposeVisible);

        vm.GitHubStatusText = "";
        await vm.ShowNewPrCommand.ExecuteAsync(null);
        Assert.Equal("This project has no GitHub remote.", vm.GitHubStatusText);
        Assert.False(vm.PullRequestComposeVisible);
    }

    [Fact]
    public async Task TheNoRemoteNotice_DoesNotFollowTheReaderToTheNextProject()
    {
        var vm = new StubTabsViewModel();
        await vm.SetProjectAsync(LocalProject());
        await vm.LoadPullRequestsCommand.ExecuteAsync(null);
        Assert.NotEqual("", vm.IssuesError);
        Assert.NotEqual("", vm.PullRequestsError);

        await vm.SetProjectAsync(RemoteProject());

        Assert.Equal("", vm.IssuesError);
        Assert.Equal("", vm.PullRequestsError);
    }

    [Theory]
    [InlineData("https://github.com/o/r", "https://github.com/o/r")]
    [InlineData("  https://github.com/o/r  ", "https://github.com/o/r")]
    public void NavigableUrl_PassesHttpsThrough(string url, string expected)
        => Assert.Equal(expected, ProjectDetailViewModel.NavigableUrl(url));

    [Theory]
    [InlineData(@"file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\server\share\payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("/o/r")]
    [InlineData("")]
    public void NavigableUrl_RefusesAnythingButHttpAndHttps(string url)
        => Assert.Null(ProjectDetailViewModel.NavigableUrl(url));

    /// <summary>
    /// Serves canned remote reads and records what the surfaces asked for, so the whole
    /// Actions/Releases/Repo/danger-zone flow runs without gh. Every fetch completes
    /// synchronously, so a fire-and-forget load has finished by the time the setter that
    /// started it returns.
    /// </summary>
    private sealed class StubTabsViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        public List<WorkflowRun>? Runs { get; init; }
        public List<WorkflowJob>? Jobs { get; init; }
        public List<Release>? SeedReleases { get; init; }
        public RepoSettings? Settings { get; init; }
        public List<GitHubNotification>? SeedNotifications { get; init; }
        public List<TagInfo> SeedTags { get; init; } = [];
        public string? TagReadError { get; init; }
        public string? SavePath { get; init; }
        public string? TypedConfirmation { get; init; }
        public ProcessResult DeleteResult { get; init; } = new(0, "", "", TimedOut: false);
        public bool DangerZone { get; set; }

        /// <summary>Takes the busy gate from inside the typed prompt, as a mutation landing while the dialog pumps does.</summary>
        public bool TakeGateWhilePrompting { get; init; }

        /// <summary>Hand-completed fetches, in call order, for the overlapping-load cases.</summary>
        public Queue<TaskCompletionSource<List<WorkflowJob>?>>? JobGates { get; init; }
        public Queue<TaskCompletionSource<RepoSettings?>>? SettingsGates { get; init; }
        public Queue<TaskCompletionSource<List<GitHubNotification>?>>? NotificationGates { get; init; }

        public int RunFetches { get; private set; }
        public int TextPrompts { get; private set; }
        public int Confirms { get; private set; }
        public int DeleteAttempts { get; private set; }
        public string? CreatedTag { get; private set; }
        public string? CreatedTargetSha { get; private set; }
        public List<string> Opened { get; } = [];

        internal override bool ReadDangerZoneEnabled() => DangerZone;

        internal override Task<List<WorkflowRun>?> FetchWorkflowRunsAsync(string slug)
        {
            RunFetches++;
            return Task.FromResult(Runs);
        }

        internal override Task<List<WorkflowJob>?> FetchWorkflowJobsAsync(string slug, long runId)
            => JobGates is { Count: > 0 } gates ? gates.Dequeue().Task : Task.FromResult(Jobs);

        internal override Task<List<Release>?> FetchReleasesAsync(string slug) => Task.FromResult(SeedReleases);

        internal override Task<RepoSettings?> FetchRepoSettingsAsync(string slug)
            => SettingsGates is { Count: > 0 } gates ? gates.Dequeue().Task : Task.FromResult(Settings);

        internal override Task<List<GitHubNotification>?> FetchNotificationsAsync(string slug)
            => NotificationGates is { Count: > 0 } gates ? gates.Dequeue().Task : Task.FromResult(SeedNotifications);

        internal override Task<TagsResult> FetchReleaseTagsAsync(string repoPath) => Task.FromResult(
            TagReadError is { } failure ? new TagsResult([], true, failure) : new TagsResult(SeedTags));

        internal override Task<ProcessResult> CreateReleaseRemoteAsync(string repoPath, string tag, string title,
            string body, bool draft, bool prerelease, string targetSha)
        {
            CreatedTag = tag;
            CreatedTargetSha = targetSha;
            return Task.FromResult(new ProcessResult(0, "", "", TimedOut: false));
        }

        internal override Task<ProcessResult> DeleteRepoRemoteAsync(string slug)
        {
            DeleteAttempts++;
            return Task.FromResult(DeleteResult);
        }

        internal override Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
        {
            TextPrompts++;
            if (TakeGateWhilePrompting) IsBusy = true;
            return Task.FromResult(TypedConfirmation);
        }

        internal override Task<string?> PromptForSavePathAsync(string suggestedName) => Task.FromResult(SavePath);

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirms++;
            return Task.FromResult(false);
        }

        /// <summary>Records what would launch, through the same scheme guard as the real member.</summary>
        internal override void OpenExternal(string url)
        {
            if (NavigableUrl(url) is { } target) Opened.Add(target);
        }
    }
}
