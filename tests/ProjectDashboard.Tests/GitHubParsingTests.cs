using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

public class GitHubIssueDetailParsingTests
{
    [Fact]
    public void CommaInLabelName_SurvivesOnLabelNamesButNotTheJoinedString()
    {
        var detail = GitHubService.ParseIssueDetail(
            """{"number":1,"labels":[{"name":"needs docs, maybe"},{"name":"ui"}]}""");

        Assert.NotNull(detail);
        // The joined form is display only — splitting it back yields three names.
        Assert.Equal("needs docs, maybe, ui", detail.Labels);
        Assert.Equal(["needs docs, maybe", "ui"], detail.LabelNames);
    }

    [Fact]
    public void FullIssue_ParsesAllFields()
    {
        var detail = GitHubService.ParseIssueDetail("""
            {
              "assignees": [{"id":"U_1","login":"jasonulbright","name":"Jason"},{"id":"U_2","login":"alice","name":""}],
              "author": {"id":"U_1","is_bot":false,"login":"jasonulbright","name":"Jason"},
              "body": "Crash when the repo has no commits.",
              "comments": [
                {"id":"IC_1","author":{"login":"alice"},"authorAssociation":"MEMBER","body":"Repro attached.","createdAt":"2026-08-01T10:00:00Z","reactionGroups":[]},
                {"id":"IC_2","author":null,"body":"Ghost comment.","createdAt":"2026-08-02T11:30:00Z"}
              ],
              "createdAt": "2026-07-30T09:15:00Z",
              "labels": [
                {"id":"L_1","name":"bug","description":"Something broken","color":"d73a4a"},
                {"id":"L_2","name":"ui","description":"","color":"c2e0c6"}
              ],
              "milestone": {"number":1,"title":"v2.0","description":"","dueOn":"2026-09-01T00:00:00Z"},
              "number": 41,
              "state": "OPEN",
              "title": "Crash on empty repo",
              "updatedAt": "2026-08-02T11:30:00Z",
              "url": "https://github.com/o/r/issues/41"
            }
            """);

        Assert.NotNull(detail);
        Assert.Equal(41, detail.Number);
        Assert.Equal("Crash on empty repo", detail.Title);
        Assert.Equal("open", detail.State);
        Assert.Equal("Crash when the repo has no commits.", detail.Body);
        Assert.Equal("jasonulbright", detail.Author);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 9, 15, 0, TimeSpan.Zero), detail.CreatedAt);
        Assert.Equal("bug, ui", detail.Labels);
        Assert.Equal(["bug", "ui"], detail.LabelNames);
        Assert.Equal("jasonulbright, alice", detail.Assignees);
        Assert.Equal("v2.0", detail.Milestone);
        Assert.Equal("https://github.com/o/r/issues/41", detail.Url);

        Assert.Equal(2, detail.Comments.Count);
        Assert.Equal("alice", detail.Comments[0].Author);
        Assert.Equal("Repro attached.", detail.Comments[0].Body);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), detail.Comments[0].CreatedAt);
        // Deleted account: author null must read as "", not drop the comment.
        Assert.Equal("", detail.Comments[1].Author);
        Assert.Equal("Ghost comment.", detail.Comments[1].Body);
    }

    [Fact]
    public void MinimalIssue_AbsentFieldsReadAsDefaults()
    {
        var detail = GitHubService.ParseIssueDetail("""{"number":7,"title":"Bare"}""");

        Assert.NotNull(detail);
        Assert.Equal(7, detail.Number);
        Assert.Equal("Bare", detail.Title);
        Assert.Equal("", detail.State);
        Assert.Equal("", detail.Body);
        Assert.Equal("", detail.Author);
        Assert.Equal("", detail.Labels);
        Assert.Empty(detail.LabelNames);
        Assert.Equal("", detail.Assignees);
        Assert.Equal("", detail.Milestone);
        Assert.Empty(detail.Comments);
    }

    [Fact]
    public void NullMilestone_ReadsEmpty()
    {
        var detail = GitHubService.ParseIssueDetail("""{"number":1,"title":"t","milestone":null,"comments":[]}""");

        Assert.NotNull(detail);
        Assert.Equal("", detail.Milestone);
        Assert.Empty(detail.Comments);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void MalformedOrNonObject_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseIssueDetail(json));
}

public class GitHubPullRequestDetailParsingTests
{
    [Fact]
    public void FullPr_ParsesAllFields()
    {
        var detail = GitHubService.ParsePullRequestDetail("""
            {
              "additions": 120,
              "author": {"login":"jasonulbright"},
              "baseRefName": "master",
              "body": "Adds the Issues tab.",
              "changedFiles": 5,
              "comments": [{"author":{"login":"bob"},"body":"LGTM","createdAt":"2026-08-03T08:00:00Z"}],
              "createdAt": "2026-08-02T18:00:00Z",
              "deletions": 14,
              "headRefName": "feature/issues-tab",
              "isDraft": false,
              "mergeStateStatus": "CLEAN",
              "mergeable": "MERGEABLE",
              "number": 55,
              "reviewDecision": "APPROVED",
              "state": "OPEN",
              "statusCheckRollup": [
                {"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS","name":"build"},
                {"__typename":"StatusContext","state":"SUCCESS","context":"ci/lint"}
              ],
              "title": "Issues tab",
              "updatedAt": "2026-08-03T08:00:00Z",
              "url": "https://github.com/o/r/pull/55"
            }
            """);

        Assert.NotNull(detail);
        Assert.Equal(55, detail.Number);
        Assert.Equal("open", detail.State);
        Assert.False(detail.IsDraft);
        Assert.Equal("master", detail.BaseRef);
        Assert.Equal("feature/issues-tab", detail.HeadRef);
        Assert.Equal("mergeable", detail.Mergeable);
        Assert.Equal("clean", detail.MergeStateStatus);
        Assert.Equal(5, detail.ChangedFiles);
        Assert.Equal(120, detail.Additions);
        Assert.Equal(14, detail.Deletions);
        Assert.Equal("passing", detail.ChecksState);
        Assert.Equal("approved", detail.ReviewDecision);
        var comment = Assert.Single(detail.Comments);
        Assert.Equal("bob", comment.Author);
    }

    [Fact]
    public void MergedPr_FailingChecks()
    {
        var detail = GitHubService.ParsePullRequestDetail("""
            {
              "number": 3,
              "state": "MERGED",
              "title": "Old work",
              "statusCheckRollup": [{"__typename":"CheckRun","status":"COMPLETED","conclusion":"FAILURE","name":"build"}]
            }
            """);

        Assert.NotNull(detail);
        Assert.Equal("merged", detail.State);
        Assert.Equal("failing", detail.ChecksState);
    }

    [Fact]
    public void MinimalDraft_AbsentCountsReadNull_NotZero()
    {
        var detail = GitHubService.ParsePullRequestDetail("""{"number":9,"title":"WIP","isDraft":true}""");

        Assert.NotNull(detail);
        Assert.True(detail.IsDraft);
        Assert.Null(detail.ChangedFiles);
        Assert.Null(detail.Additions);
        Assert.Null(detail.Deletions);
        Assert.Equal("", detail.ChecksState);
        Assert.Equal("", detail.Mergeable);
        Assert.Empty(detail.Comments);
    }

    [Fact]
    public void InProgressRollup_ReadsPending()
    {
        var detail = GitHubService.ParsePullRequestDetail("""
            {"number":4,"title":"t","statusCheckRollup":[{"__typename":"CheckRun","status":"IN_PROGRESS","conclusion":"","name":"build"}]}
            """);

        Assert.NotNull(detail);
        Assert.Equal("pending", detail.ChecksState);
    }

    [Theory]
    [InlineData("<html>rate limited</html>")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public void MalformedOrNonObject_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParsePullRequestDetail(json));
}

public class GitHubReleaseParsingTests
{
    [Fact]
    public void TwoReleases_AssetsAndDraftParse()
    {
        var releases = GitHubService.ParseReleases("""
            [
              {
                "id": 1, "tag_name": "v1.2.0", "name": "Project Dashboard 1.2.0",
                "body": "## Added\n\n- The Releases tab",
                "draft": false, "prerelease": false,
                "created_at": "2026-07-17T20:00:00Z", "published_at": "2026-07-17T20:05:00Z",
                "html_url": "https://github.com/o/r/releases/tag/v1.2.0",
                "assets": [
                  {"id": 9, "name": "Setup-1.2.0.exe", "size": 18874368,
                   "browser_download_url": "https://github.com/o/r/releases/download/v1.2.0/Setup-1.2.0.exe",
                   "content_type": "application/x-msdownload", "download_count": 3}
                ]
              },
              {"id": 2, "tag_name": "v1.3.0-rc1", "name": "", "draft": true, "prerelease": true,
               "published_at": null, "assets": []}
            ]
            """);

        Assert.NotNull(releases);
        Assert.Equal(2, releases.Count);

        Assert.Equal("v1.2.0", releases[0].TagName);
        Assert.Equal("Project Dashboard 1.2.0", releases[0].Name);
        Assert.Equal("v1.2.0 — Project Dashboard 1.2.0", releases[0].DisplayTitle);
        Assert.Equal("", releases[0].StateLabel);
        Assert.Contains("The Releases tab", releases[0].Body);
        Assert.False(releases[0].IsDraft);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 20, 5, 0, TimeSpan.Zero), releases[0].PublishedAt);
        var asset = Assert.Single(releases[0].Assets);
        Assert.Equal("Setup-1.2.0.exe", asset.Name);
        Assert.Equal(18874368L, asset.Size);
        Assert.Equal("https://github.com/o/r/releases/download/v1.2.0/Setup-1.2.0.exe", asset.DownloadUrl);

        Assert.True(releases[1].IsDraft);
        Assert.True(releases[1].IsPrerelease);
        // A draft outranks the prerelease flag: one chip, and "draft" is the one that
        // decides whether anybody else can see it.
        Assert.Equal("draft", releases[1].StateLabel);
        // Untitled: the tag is the whole title rather than a trailing dash.
        Assert.Equal("v1.3.0-rc1", releases[1].DisplayTitle);
        Assert.Equal("", releases[1].Body);
        // A draft has no publish moment — null, not a zero date.
        Assert.Null(releases[1].PublishedAt);
        Assert.Empty(releases[1].Assets);
    }

    [Fact]
    public void EmptyArray_ReadsEmptyList()
    {
        var releases = GitHubService.ParseReleases("[]");
        Assert.NotNull(releases);
        Assert.Empty(releases);
    }

    [Fact]
    public void AssetsKeyAbsent_ReadsEmptyAssets()
    {
        var releases = GitHubService.ParseReleases("""[{"tag_name":"v1.0.0","name":"x","draft":false,"prerelease":false}]""");
        Assert.NotNull(releases);
        Assert.Empty(Assert.Single(releases).Assets);
    }

    [Theory]
    [InlineData("""{"message":"Not Found","status":"404"}""")]
    [InlineData("{ bad")]
    [InlineData("")]
    public void ErrorPayloadOrMalformed_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseReleases(json));
}

public class GitHubWorkflowRunParsingTests
{
    [Fact]
    public void CompletedAndQueuedRuns_Parse()
    {
        var runs = GitHubService.ParseWorkflowRuns("""
            [
              {"conclusion":"success","databaseId":16752341890,"displayTitle":"Fix crash on empty repo",
               "event":"push","headBranch":"master","name":"CI","startedAt":"2026-08-05T14:00:05Z",
               "updatedAt":"2026-08-05T14:04:35Z",
               "status":"completed","url":"https://github.com/o/r/actions/runs/16752341890","workflowName":"CI"},
              {"conclusion":"","databaseId":16752341999,"displayTitle":"Bump version",
               "event":"workflow_dispatch","headBranch":"release","name":"Release",
               "startedAt":"0001-01-01T00:00:00Z","status":"queued","url":"https://github.com/o/r/actions/runs/16752341999",
               "workflowName":"Release"}
            ]
            """);

        Assert.NotNull(runs);
        Assert.Equal(2, runs.Count);

        Assert.Equal(16752341890L, runs[0].Id);
        Assert.Equal("CI", runs[0].Name);
        Assert.Equal("Fix crash on empty repo", runs[0].DisplayTitle);
        Assert.Equal("master", runs[0].Branch);
        Assert.Equal("push", runs[0].Event);
        Assert.Equal("completed", runs[0].Status);
        Assert.Equal("success", runs[0].Conclusion);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 14, 0, 5, TimeSpan.Zero), runs[0].StartedAt);
        Assert.True(runs[0].IsCompleted);
        Assert.Equal("success", runs[0].OutcomeLabel);
        // A finished run's elapsed is fixed by its own timestamps, not the clock.
        Assert.Equal("4m 30s", runs[0].ElapsedLabel);

        Assert.Equal("queued", runs[1].Status);
        Assert.Equal("", runs[1].Conclusion);
        Assert.Equal("queued", runs[1].OutcomeLabel);
        // gh serializes a not-yet-started run with the year-1 zero time — read as null.
        Assert.Null(runs[1].StartedAt);
        Assert.Equal("", runs[1].ElapsedLabel);
    }

    [Fact]
    public void CompletedRunWithoutAConclusion_StillReadsAsFinished()
    {
        var runs = GitHubService.ParseWorkflowRuns("""[{"databaseId":5,"workflowName":"CI","status":"completed","conclusion":""}]""");
        Assert.NotNull(runs);
        Assert.Equal("completed", Assert.Single(runs).OutcomeLabel);
    }

    [Theory]
    // Ended before it started (runner clock skew): a duration is never negative.
    [InlineData("2026-08-05T14:05:00Z", "2026-08-05T14:00:00Z", "0s")]
    [InlineData("2026-08-05T14:00:00Z", "2026-08-05T14:00:07Z", "7s")]
    [InlineData("2026-08-05T14:00:00Z", "2026-08-05T14:02:03Z", "2m 3s")]
    [InlineData("2026-08-05T14:00:00Z", "2026-08-05T17:30:00Z", "3h 30m")]
    public void Elapsed_FormatsToTheLargestUsefulUnit(string started, string ended, string expected)
        => Assert.Equal(expected, Models.WorkflowRun.FormatElapsed(
            DateTimeOffset.Parse(started), DateTimeOffset.Parse(ended), DateTimeOffset.Parse(ended)));

    [Fact]
    public void Elapsed_OfARunningRun_IsMeasuredToNow()
    {
        var started = DateTimeOffset.Parse("2026-08-05T14:00:00Z");
        Assert.Equal("5m 0s",
            Models.WorkflowRun.FormatElapsed(started, null, started.AddMinutes(5)));
    }

    [Fact]
    public void Elapsed_OfANeverStartedRun_IsBlank()
        => Assert.Equal("", Models.WorkflowRun.FormatElapsed(null, null, DateTimeOffset.Now));

    [Fact]
    public void NullConclusion_ReadsEmpty()
    {
        var runs = GitHubService.ParseWorkflowRuns("""[{"databaseId":5,"workflowName":"CI","status":"in_progress","conclusion":null}]""");
        Assert.NotNull(runs);
        Assert.Equal("", Assert.Single(runs).Conclusion);
    }

    [Theory]
    [InlineData("""{"message":"Server Error"}""")]
    [InlineData("nope")]
    [InlineData("")]
    public void ErrorPayloadOrMalformed_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseWorkflowRuns(json));
}

public class GitHubRepoSettingsParsingTests
{
    [Fact]
    public void FullRepo_TopicsVisibilityDefaultBranchParse()
    {
        var settings = GitHubService.ParseRepoSettings("""
            {
              "name": "project-dashboard",
              "description": "Local project dashboard",
              "homepageUrl": "https://example.com",
              "repositoryTopics": [{"name":"wpf"},{"name":"dashboard"}],
              "visibility": "PRIVATE",
              "isArchived": false,
              "defaultBranchRef": {"name":"master"},
              "parent": null,
              "hasIssuesEnabled": true,
              "hasWikiEnabled": false,
              "hasProjectsEnabled": true
            }
            """);

        Assert.NotNull(settings);
        Assert.Equal("project-dashboard", settings.Name);
        Assert.Equal("Local project dashboard", settings.Description);
        Assert.Equal("https://example.com", settings.Homepage);
        Assert.Equal(["wpf", "dashboard"], settings.Topics);
        Assert.Equal("private", settings.Visibility);
        Assert.False(settings.IsArchived);
        Assert.Equal("master", settings.DefaultBranch);
        Assert.Equal("", settings.ParentSlug);
        Assert.False(settings.IsFork);
        Assert.True(settings.HasIssues);
        Assert.False(settings.HasWiki);
        Assert.True(settings.HasProjects);
        Assert.Equal("wpf, dashboard", settings.TopicsText);
    }

    [Fact]
    public void AbsentFeatureFlags_ReadNull_NotOff()
    {
        // Null is "the response didn't say", which the editor must not save back as
        // a deliberate "off".
        var settings = GitHubService.ParseRepoSettings("""{"name":"bare","visibility":"PUBLIC"}""");

        Assert.NotNull(settings);
        Assert.Null(settings.HasIssues);
        Assert.Null(settings.HasWiki);
        Assert.Null(settings.HasProjects);
    }

    [Fact]
    public void NonBooleanFeatureFlag_ReadsNull()
    {
        var settings = GitHubService.ParseRepoSettings("""{"name":"bare","hasIssuesEnabled":"yes"}""");
        Assert.NotNull(settings);
        Assert.Null(settings.HasIssues);
    }

    [Fact]
    public void ForkParent_ComposedFromOwnerAndName()
    {
        var settings = GitHubService.ParseRepoSettings("""
            {"name":"Spoon-Knife","parent":{"id":"R_1","name":"Spoon-Knife","owner":{"id":"U_1","login":"octocat"}}}
            """);

        Assert.NotNull(settings);
        Assert.Equal("octocat/Spoon-Knife", settings.ParentSlug);
        Assert.True(settings.IsFork);
    }

    [Fact]
    public void ForkParent_NameWithOwnerPreferred()
    {
        var settings = GitHubService.ParseRepoSettings("""
            {"name":"fork","parent":{"nameWithOwner":"upstream/fork","name":"fork","owner":{"login":"upstream"}}}
            """);

        Assert.NotNull(settings);
        Assert.Equal("upstream/fork", settings.ParentSlug);
    }

    [Fact]
    public void NullFields_ReadAsDefaults()
    {
        var settings = GitHubService.ParseRepoSettings("""
            {"name":"bare","description":null,"homepageUrl":null,"repositoryTopics":null,
             "visibility":"PUBLIC","isArchived":true,"defaultBranchRef":null,"parent":null}
            """);

        Assert.NotNull(settings);
        Assert.Equal("", settings.Description);
        Assert.Equal("", settings.Homepage);
        Assert.Empty(settings.Topics);
        Assert.True(settings.IsArchived);
        Assert.Equal("", settings.DefaultBranch);
        Assert.Equal("", settings.ParentSlug);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("oops")]
    [InlineData("")]
    public void MalformedOrNonObject_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseRepoSettings(json));
}

public class GitHubLabelParsingTests
{
    [Fact]
    public void Labels_DescriptionAbsentReadsEmpty()
    {
        var labels = GitHubService.ParseLabels("""
            [
              {"id":"LA_1","name":"bug","description":"Something isn't working","color":"d73a4a"},
              {"name":"triage","color":"ffffff"}
            ]
            """);

        Assert.NotNull(labels);
        Assert.Equal(2, labels.Count);
        Assert.Equal("bug", labels[0].Name);
        Assert.Equal("d73a4a", labels[0].Color);
        Assert.Equal("Something isn't working", labels[0].Description);
        Assert.Equal("", labels[1].Description);
    }

    [Fact]
    public void EmptyArray_ReadsEmptyList()
    {
        var labels = GitHubService.ParseLabels("[]");
        Assert.NotNull(labels);
        Assert.Empty(labels);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("x")]
    [InlineData("")]
    public void MalformedOrNonArray_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseLabels(json));
}

public class GitHubMilestoneParsingTests
{
    [Fact]
    public void Milestones_DueOnNullAndCountsParse()
    {
        var milestones = GitHubService.ParseMilestones("""
            [
              {"id":1,"number":1,"title":"v2.0","description":"Big one","open_issues":4,"closed_issues":8,
               "state":"open","due_on":"2026-09-01T07:00:00Z","closed_at":null},
              {"number":2,"title":"backlog","state":"closed","due_on":null,"open_issues":0,"closed_issues":2}
            ]
            """);

        Assert.NotNull(milestones);
        Assert.Equal(2, milestones.Count);
        Assert.Equal(1, milestones[0].Number);
        Assert.Equal("v2.0", milestones[0].Title);
        Assert.Equal("open", milestones[0].State);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero), milestones[0].DueOn);
        Assert.Equal(4, milestones[0].OpenIssues);
        Assert.Equal(8, milestones[0].ClosedIssues);
        Assert.Null(milestones[1].DueOn);
        Assert.Equal(0, milestones[1].OpenIssues);
    }

    [Fact]
    public void AbsentCounts_ReadNull_NotZero()
    {
        var milestones = GitHubService.ParseMilestones("""[{"number":3,"title":"thin","state":"open"}]""");
        Assert.NotNull(milestones);
        var milestone = Assert.Single(milestones);
        Assert.Null(milestone.OpenIssues);
        Assert.Null(milestone.ClosedIssues);
        Assert.Null(milestone.DueOn);
    }

    [Theory]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("~")]
    [InlineData("")]
    public void ErrorPayloadOrMalformed_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseMilestones(json));
}

public class GitHubDraftProbeParsingTests
{
    [Theory]
    [InlineData("""{"isDraft":true}""", true)]
    [InlineData("""{"isDraft":false}""", false)]
    public void ValidPayload_ParsesDraftFlag(string json, bool expected)
    {
        Assert.True(GitHubService.TryParseIsDraft(json, out var isDraft));
        Assert.Equal(expected, isDraft);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"isDraft":"yes"}""")]
    [InlineData("[]")]
    [InlineData("garbage")]
    [InlineData("")]
    public void MissingOrMalformed_ReportsUnknown(string json)
        => Assert.False(GitHubService.TryParseIsDraft(json, out _));
}

public class GitHubWorkflowJobParsingTests
{
    [Fact]
    public void JobsAndSteps_Parse()
    {
        var jobs = GitHubService.ParseWorkflowJobs("""
            {
              "total_count": 2,
              "jobs": [
                {
                  "id": 47536432101, "run_id": 16752341890, "name": "build",
                  "status": "completed", "conclusion": "success",
                  "started_at": "2026-08-05T14:00:10Z", "completed_at": "2026-08-05T14:03:40Z",
                  "html_url": "https://github.com/o/r/actions/runs/16752341890/job/47536432101",
                  "steps": [
                    {"name":"Set up job","status":"completed","conclusion":"success","number":1},
                    {"name":"Run tests","status":"completed","conclusion":"failure","number":2}
                  ]
                },
                {
                  "id": 47536432102, "name": "package",
                  "status": "in_progress", "conclusion": null,
                  "started_at": "2026-08-05T14:03:41Z", "completed_at": null,
                  "steps": []
                }
              ]
            }
            """);

        Assert.NotNull(jobs);
        Assert.Equal(2, jobs.Count);

        Assert.Equal(47536432101L, jobs[0].Id);
        Assert.Equal("build", jobs[0].Name);
        Assert.Equal("completed", jobs[0].Status);
        Assert.Equal("success", jobs[0].Conclusion);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 14, 0, 10, TimeSpan.Zero), jobs[0].StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 14, 3, 40, TimeSpan.Zero), jobs[0].CompletedAt);
        Assert.Equal(2, jobs[0].Steps.Count);
        Assert.Equal("Run tests", jobs[0].Steps[1].Name);
        Assert.Equal(2, jobs[0].Steps[1].Number);
        Assert.Equal("failure", jobs[0].Steps[1].Conclusion);

        Assert.Equal("in_progress", jobs[1].Status);
        Assert.Equal("", jobs[1].Conclusion);
        // Still running: no completion moment, and the label falls back to the status.
        Assert.Null(jobs[1].CompletedAt);
        Assert.Equal("in progress", jobs[1].OutcomeLabel);
        Assert.Empty(jobs[1].Steps);
    }

    [Fact]
    public void StepsKeyAbsent_ReadsEmptySteps()
    {
        var jobs = GitHubService.ParseWorkflowJobs("""{"total_count":1,"jobs":[{"id":1,"name":"solo","status":"completed"}]}""");
        Assert.NotNull(jobs);
        Assert.Empty(Assert.Single(jobs).Steps);
    }

    [Fact]
    public void NoJobs_ReadsEmptyList()
    {
        var jobs = GitHubService.ParseWorkflowJobs("""{"total_count":0,"jobs":[]}""");
        Assert.NotNull(jobs);
        Assert.Empty(jobs);
    }

    [Theory]
    [InlineData("""{"message":"Not Found","status":"404"}""")]   // error payload carries no jobs array
    [InlineData("[]")]
    [InlineData("{ bad")]
    [InlineData("")]
    public void ErrorPayloadOrMalformed_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseWorkflowJobs(json));
}

public class GitHubNotificationParsingTests
{
    [Fact]
    public void UnreadThreads_Parse()
    {
        var notifications = GitHubService.ParseNotifications("""
            [
              {
                "id": "14231733865", "unread": true, "reason": "review_requested",
                "updated_at": "2026-08-06T09:12:00Z",
                "subject": {"title":"Add the Releases tab","url":"https://api.github.com/repos/o/r/pulls/12","type":"PullRequest"},
                "repository": {"full_name":"o/r"}
              },
              {
                "id": "14231733900", "unread": true, "reason": "mention",
                "updated_at": "2026-08-06T10:00:00Z",
                "subject": {"title":"Crash on empty repo","url":"https://api.github.com/repos/o/r/issues/41","type":"Issue"}
              }
            ]
            """);

        Assert.NotNull(notifications);
        Assert.Equal(2, notifications.Count);

        Assert.Equal("14231733865", notifications[0].ThreadId);
        Assert.Equal("review_requested", notifications[0].Reason);
        Assert.Equal("review requested", notifications[0].ReasonLabel);
        Assert.True(notifications[0].Unread);
        Assert.Equal("Add the Releases tab", notifications[0].Title);
        Assert.Equal("PullRequest", notifications[0].SubjectType);
        Assert.Equal("https://github.com/o/r/pull/12", notifications[0].WebUrl);
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 9, 12, 0, TimeSpan.Zero), notifications[0].UpdatedAt);

        Assert.Equal("https://github.com/o/r/issues/41", notifications[1].WebUrl);
    }

    [Fact]
    public void SubjectWithNoWebEquivalent_KeepsAnEmptyLink()
    {
        var notifications = GitHubService.ParseNotifications("""
            [{"id":"7","unread":true,"reason":"subscribed",
              "subject":{"title":"v2.0.0","url":"https://api.github.com/repos/o/r/releases/99","type":"Release"}}]
            """);

        Assert.NotNull(notifications);
        var notification = Assert.Single(notifications);
        Assert.Equal("v2.0.0", notification.Title);
        // No guessed address: the caller opens the repository instead.
        Assert.Equal("", notification.WebUrl);
    }

    [Fact]
    public void SubjectAbsent_ReadsAsDefaults()
    {
        var notifications = GitHubService.ParseNotifications("""[{"id":"7","unread":false,"reason":"subscribed"}]""");
        Assert.NotNull(notifications);
        var notification = Assert.Single(notifications);
        Assert.Equal("", notification.Title);
        Assert.Equal("", notification.WebUrl);
        Assert.False(notification.Unread);
    }

    [Fact]
    public void EmptyArray_ReadsEmptyList()
    {
        var notifications = GitHubService.ParseNotifications("[]");
        Assert.NotNull(notifications);
        Assert.Empty(notifications);
    }

    [Theory]
    [InlineData("""{"message":"Requires authentication","status":"401"}""")]
    [InlineData("{ bad")]
    [InlineData("")]
    public void ErrorPayloadOrMalformed_ReturnsNull(string json)
        => Assert.Null(GitHubService.ParseNotifications(json));
}
