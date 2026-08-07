using ProjectDashboard.Services;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// Live end-to-end smoke of every GitHubService mutation against a throwaway private
/// repo (pd-scratch-*) created and deleted inside the run. Gated on PD_GH_SMOKE=1:
/// without it the test is a no-op, so CI never touches the network. Requires a
/// signed-in gh; repo deletion additionally requires the delete_repo scope.
/// </summary>
[Collection("app-data-sandbox")]
public class GitHubSmokeTests(ITestOutputHelper output)
{
    private readonly List<string> _transcript = [];

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task LiveSmoke_AllMutations_ScratchRepoOnly()
    {
        if (Environment.GetEnvironmentVariable("PD_GH_SMOKE") != "1")
        {
            output.WriteLine("skipped: PD_GH_SMOKE not set");
            return;
        }

        // The sandboxed XDG_CONFIG_HOME (TestEnv) also relocates gh's config, so gh
        // reads signed-out. GH_CONFIG_DIR outranks XDG — point it at the real config.
        Environment.SetEnvironmentVariable("GH_CONFIG_DIR",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitHub CLI"));

        var svc = new GitHubService(new SettingsService());
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = $"pd-scratch-{suffix}";
        var forkName = $"pd-scratch-fork-{suffix}";
        string? owner = null;
        string? slug = null;      // tracks the rename
        string? forkSlug = null;

        try
        {
            var who = await svc.RunAsync(["api", "user", "--jq", ".login"], timeout: TimeSpan.FromSeconds(30));
            Check("gh api user (signed-in login)", who.Success, who.FirstError);
            owner = who.StdOut.Trim();
            slug = $"{owner}/{repoName}";

            // Sandboxed GIT_CONFIG_GLOBAL has no credential helper; without this the
            // clone/push legs of the smoke cannot authenticate.
            var setupGit = await svc.RunAsync(["auth", "setup-git"], timeout: TimeSpan.FromSeconds(30));
            Check("gh auth setup-git (sandbox credential helper)", setupGit.Success, setupGit.FirstError);

            var create = await svc.RunAsync(["repo", "create", slug, "--private", "--add-readme"],
                timeout: TimeSpan.FromSeconds(60));
            Check($"gh repo create {slug} --private --add-readme", create.Success, create.FirstError);
            await Task.Delay(3000);

            var dir = Path.Combine(TestEnv.NewDir("gh-smoke"), "clone");
            var clone = await svc.RunAsync(["repo", "clone", slug, dir], timeout: GitTimeout);
            Check($"gh repo clone {slug}", clone.Success, clone.FirstError);

            // ---- Issues ----
            var issueCreate = await Retry(() => svc.CreateIssueAsync(slug, "Smoke issue",
                "Body with unicode: 项目-ünïcode.\n\nSecond line.", ["bug"]));
            Check("CreateIssueAsync (gh issue create --label bug)", issueCreate.Success, issueCreate.FirstError);
            var issueNumber = TrailingNumber(issueCreate.StdOut);

            var comment = await svc.CommentIssueAsync(slug, issueNumber, "First comment from the smoke run.");
            Check("CommentIssueAsync (gh issue comment)", comment.Success, comment.FirstError);

            var close = await svc.CloseIssueAsync(slug, issueNumber);
            Check("CloseIssueAsync (gh issue close)", close.Success, close.FirstError);

            var reopen = await svc.ReopenIssueAsync(slug, issueNumber);
            Check("ReopenIssueAsync (gh issue reopen)", reopen.Success, reopen.FirstError);

            var labels = await svc.EditIssueLabelsAsync(slug, issueNumber, ["documentation"], ["bug"]);
            Check("EditIssueLabelsAsync (add documentation, remove bug)", labels.Success, labels.FirstError);

            var assign = await svc.AssignIssueAsync(slug, issueNumber, owner);
            Check($"AssignIssueAsync ({owner})", assign.Success, assign.FirstError);

            var issueDetail = await svc.GetIssueDetailAsync(slug, issueNumber);
            Check("GetIssueDetailAsync (live read-back)",
                issueDetail is { State: "open" } && issueDetail.Comments.Count == 1 &&
                issueDetail.Labels.Contains("documentation") && issueDetail.Assignees.Contains(owner) &&
                issueDetail.Body.Contains("项目-ünïcode"),
                issueDetail is null ? "null" : $"state={issueDetail.State} comments={issueDetail.Comments.Count} labels={issueDetail.Labels}");

            var labelList = await svc.GetLabelsAsync(slug);
            Check("GetLabelsAsync (default label set)", labelList is { Count: > 0 }, $"count={labelList?.Count}");

            var milestoneCreate = await svc.RunAsync(
                ["api", $"repos/{slug}/milestones", "-f", "title=Smoke milestone"], timeout: TimeSpan.FromSeconds(30));
            Check("gh api POST milestones (setup)", milestoneCreate.Success, milestoneCreate.FirstError);
            var milestones = await svc.GetMilestonesAsync(slug);
            Check("GetMilestonesAsync (live read-back)",
                milestones is { Count: 1 } && milestones[0].Title == "Smoke milestone", $"count={milestones?.Count}");

            // ---- Pull requests ----
            Check("git checkout -b smoke-feature", (await Git(dir, "checkout", "-b", "smoke-feature")).Success);
            File.WriteAllText(Path.Combine(dir, "smoke.txt"), "smoke change\n");
            Check("git add/commit", (await Git(dir, "add", ".")).Success &&
                                   (await Git(dir, "commit", "-m", "Smoke change")).Success);
            var push = await Git(dir, "push", "-u", "origin", "smoke-feature");
            Check("git push -u origin smoke-feature", push.Success, push.FirstError);

            var prCreate = await svc.CreatePullRequestAsync(dir, "Smoke PR", "PR body from smoke run.", "main", draft: true);
            Check("CreatePullRequestAsync (gh pr create --base main --draft, in repo dir)", prCreate.Success, prCreate.FirstError);
            var prNumber = TrailingNumber(prCreate.StdOut);

            Check("git checkout main", (await Git(dir, "checkout", "main")).Success);
            var checkout = await svc.CheckoutPullRequestAsync(dir, prNumber);
            var branchNow = (await Git(dir, "branch", "--show-current")).StdOut.Trim();
            Check("CheckoutPullRequestAsync (gh pr checkout, in repo dir)",
                checkout.Success && branchNow == "smoke-feature", $"branch={branchNow} {checkout.FirstError}");

            var ready = await svc.MarkPullRequestReadyAsync(slug, prNumber);
            Check("MarkPullRequestReadyAsync (gh pr ready)", ready.Success, ready.FirstError);

            var prComment = await svc.CommentPullRequestAsync(slug, prNumber, "PR comment from smoke run.");
            Check("CommentPullRequestAsync (gh pr comment)", prComment.Success, prComment.FirstError);

            var review = await svc.ReviewPullRequestAsync(slug, prNumber, "comment", "Review note from smoke run.");
            Check("ReviewPullRequestAsync (gh pr review --comment)", review.Success, review.FirstError);

            // GitHub rejects self-approval; a failed result (not a crash) is the expected shape.
            var selfApprove = await svc.ReviewPullRequestAsync(slug, prNumber, "approve", "");
            Info("ReviewPullRequestAsync approve on own PR (expected server refusal)",
                selfApprove.Success ? "unexpectedly succeeded" : selfApprove.FirstError);

            var prDetail = await svc.GetPullRequestDetailAsync(slug, prNumber);
            Check("GetPullRequestDetailAsync (live read-back)",
                prDetail is { State: "open", BaseRef: "main", HeadRef: "smoke-feature", IsDraft: false } &&
                prDetail.Comments.Count >= 1,
                prDetail is null ? "null" : $"state={prDetail.State} base={prDetail.BaseRef} head={prDetail.HeadRef}");

            var merge = await svc.MergePullRequestAsync(slug, prNumber, "squash", deleteBranch: true);
            Check("MergePullRequestAsync (gh pr merge --squash --delete-branch)", merge.Success, merge.FirstError);

            // ---- Workflow runs ----
            Check("git checkout main + pull after merge",
                (await Git(dir, "checkout", "main")).Success &&
                (await Git(dir, "pull", "--ff-only", "origin", "main")).Success);
            var wfDir = Path.Combine(dir, ".github", "workflows");
            Directory.CreateDirectory(wfDir);
            File.WriteAllText(Path.Combine(wfDir, "smoke-pass.yml"),
                "name: smoke-pass\non: push\njobs:\n  ok:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo smoke-ok-marker\n");
            File.WriteAllText(Path.Combine(wfDir, "smoke-fail.yml"),
                "name: smoke-fail\non: push\njobs:\n  boom:\n    runs-on: ubuntu-latest\n    steps:\n      - run: exit 1\n");
            Check("git push workflows", (await Git(dir, "add", ".")).Success &&
                                        (await Git(dir, "commit", "-m", "Add smoke workflows")).Success &&
                                        (await Git(dir, "push")).Success);

            Models.WorkflowRun? passRun = null, failRun = null;
            var deadline = DateTimeOffset.Now.AddSeconds(240);
            while (DateTimeOffset.Now < deadline)
            {
                var runs = await svc.GetWorkflowRunsAsync(slug);
                passRun = runs?.FirstOrDefault(r => r.Name == "smoke-pass" && r.Status == "completed");
                failRun = runs?.FirstOrDefault(r => r.Name == "smoke-fail" && r.Status == "completed");
                if (passRun is not null && failRun is not null) break;
                await Task.Delay(6000);
            }
            Check("GetWorkflowRunsAsync (both runs completed within 240s)",
                passRun is not null && failRun is not null,
                $"pass={passRun?.Conclusion} fail={failRun?.Conclusion}");
            Assert.NotNull(passRun);
            Assert.NotNull(failRun);
            Check("run conclusions (success + failure)",
                passRun.Conclusion == "success" && failRun.Conclusion == "failure",
                $"pass={passRun.Conclusion} fail={failRun.Conclusion}");

            var log = await svc.GetWorkflowRunLogAsync(slug, passRun.Id);
            Check("GetWorkflowRunLogAsync (gh run view --log)",
                log is not null && log.Contains("smoke-ok-marker"), $"len={log?.Length}");

            var cappedLog = await svc.GetWorkflowRunLogAsync(slug, passRun.Id, maxBytes: 200);
            Check("GetWorkflowRunLogAsync capped at 200 bytes (truncation marker)",
                cappedLog is not null && cappedLog.Contains(GitHubService.TruncationMarker(200)) && cappedLog.Length < 600,
                $"len={cappedLog?.Length}");

            var rerunFailed = await svc.RerunWorkflowAsync(slug, failRun.Id, failedOnly: true);
            Check("RerunWorkflowAsync (gh run rerun --failed)", rerunFailed.Success, rerunFailed.FirstError);

            var cancelOk = false;
            var cancelDetail = "";
            var cancelDeadline = DateTimeOffset.Now.AddSeconds(90);
            while (DateTimeOffset.Now < cancelDeadline)
            {
                var runs = await svc.GetWorkflowRunsAsync(slug, limit: 10);
                var current = runs?.FirstOrDefault(r => r.Id == failRun.Id);
                if (current is not null && current.Status != "completed")
                {
                    var cancel = await svc.CancelWorkflowRunAsync(slug, failRun.Id);
                    cancelOk = cancel.Success;
                    cancelDetail = cancel.FirstError;
                    if (cancelOk) break;
                }
                await Task.Delay(4000);
            }
            Check("CancelWorkflowRunAsync (gh run cancel, on re-running run)", cancelOk, cancelDetail);

            var rerunAll = await svc.RerunWorkflowAsync(slug, passRun.Id, failedOnly: false);
            Check("RerunWorkflowAsync (gh run rerun, all jobs)", rerunAll.Success, rerunAll.FirstError);

            // ---- Releases ----
            var draftRelease = await svc.CreateReleaseAsync(dir, "v0.0.1-smoke", "Smoke draft",
                "Draft notes line one.\n\n- bullet with unicode 项目\n", draft: true);
            Check("CreateReleaseAsync draft (gh release create --draft --notes-file)", draftRelease.Success, draftRelease.FirstError);

            var releases = await svc.GetReleasesAsync(slug);
            Check("GetReleasesAsync (draft visible via REST)",
                releases is not null && releases.Any(r => r.Name == "Smoke draft" && r.IsDraft),
                $"count={releases?.Count}");

            var deleteDraft = await svc.DeleteReleaseAsync(slug, FindTag(releases, "Smoke draft") ?? "v0.0.1-smoke");
            Check("DeleteReleaseAsync on draft (guard allows)", deleteDraft.Success, deleteDraft.FirstError);

            var published = await svc.CreateReleaseAsync(dir, "v0.0.2-smoke", "Smoke published",
                "Published notes.", draft: false, prerelease: true);
            Check("CreateReleaseAsync published prerelease", published.Success, published.FirstError);

            var refuse = await svc.DeleteReleaseAsync(slug, "v0.0.2-smoke");
            Check("DeleteReleaseAsync on published without allowNonDraft (guard refuses)",
                !refuse.Success && refuse.FirstError.Contains("published"), refuse.FirstError);

            var deletePublished = await svc.DeleteReleaseAsync(slug, "v0.0.2-smoke", allowNonDraft: true);
            Check("DeleteReleaseAsync on published with allowNonDraft", deletePublished.Success, deletePublished.FirstError);

            // ---- Repo admin ----
            var settings = await svc.GetRepoSettingsAsync(slug);
            Check("GetRepoSettingsAsync (private, main, not a fork)",
                settings is { Visibility: "private", DefaultBranch: "main", IsFork: false },
                settings is null ? "null" : $"vis={settings.Visibility} branch={settings.DefaultBranch}");

            var edit = await svc.EditRepoAsync(slug, "pd smoke scratch", "https://example.com",
                ["pd-smoke", "scratch-repo"], null);
            Check("EditRepoAsync (description, homepage, add topics)", edit.Success, edit.FirstError);
            settings = await svc.GetRepoSettingsAsync(slug);
            Check("EditRepoAsync read-back",
                settings is { Description: "pd smoke scratch", Homepage: "https://example.com" } &&
                settings.Topics.Contains("pd-smoke"),
                settings is null ? "null" : $"desc={settings.Description} topics={string.Join('|', settings.Topics)}");

            var removeTopic = await svc.EditRepoAsync(slug, removeTopics: ["scratch-repo"]);
            Check("EditRepoAsync (remove topic)", removeTopic.Success, removeTopic.FirstError);

            var toPublic = await svc.SetRepoVisibilityAsync(slug, "public");
            Check("SetRepoVisibilityAsync public (--accept-visibility-change-consequences)", toPublic.Success, toPublic.FirstError);
            var toPrivate = await svc.SetRepoVisibilityAsync(slug, "private");
            Check("SetRepoVisibilityAsync private", toPrivate.Success, toPrivate.FirstError);

            var rename = await svc.RenameRepoAsync(slug, $"{repoName}-rn");
            Check("RenameRepoAsync (gh repo rename --yes)", rename.Success, rename.FirstError);
            slug = $"{owner}/{repoName}-rn";
            settings = await svc.GetRepoSettingsAsync(slug);
            Check("RenameRepoAsync read-back", settings?.Name == $"{repoName}-rn", settings?.Name ?? "null");

            var archive = await svc.ArchiveRepoAsync(slug);
            Check("ArchiveRepoAsync (gh repo archive --yes)", archive.Success, archive.FirstError);
            var unarchive = await svc.UnarchiveRepoAsync(slug);
            Check("UnarchiveRepoAsync (gh repo unarchive --yes)", unarchive.Success, unarchive.FirstError);

            // ---- Fork sync ----
            var fork = await svc.RunAsync(
                ["repo", "fork", "octocat/Spoon-Knife", "--fork-name", forkName, "--clone=false"],
                timeout: TimeSpan.FromSeconds(60));
            Check("gh repo fork octocat/Spoon-Knife (setup)", fork.Success, fork.FirstError);
            forkSlug = $"{owner}/{forkName}";
            await Task.Delay(3000);

            var forkDir = Path.Combine(TestEnv.NewDir("gh-smoke-fork"), "clone");
            var forkClone = await svc.RunAsync(["repo", "clone", forkSlug, forkDir], timeout: GitTimeout);
            Check("gh repo clone fork (setup)", forkClone.Success, forkClone.FirstError);

            var sync = await svc.SyncForkAsync(forkDir);
            Check("SyncForkAsync (gh repo sync, in fork clone)", sync.Success, sync.FirstError);
        }
        finally
        {
            if (slug is not null && slug.Contains("/pd-scratch-"))
            {
                var del = await svc.RunAsync(["repo", "delete", slug, "--yes"], timeout: TimeSpan.FromSeconds(30));
                Info($"cleanup: gh repo delete {slug} --yes", del.Success ? "deleted" : del.FirstError);
            }
            if (forkSlug is not null && forkSlug.Contains("/pd-scratch-fork-"))
            {
                var del = await svc.RunAsync(["repo", "delete", forkSlug, "--yes"], timeout: TimeSpan.FromSeconds(30));
                Info($"cleanup: gh repo delete {forkSlug} --yes", del.Success ? "deleted" : del.FirstError);
            }
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", null);

            var transcriptPath = Environment.GetEnvironmentVariable("PD_GH_SMOKE_LOG")
                ?? Path.Combine(Path.GetTempPath(), "pd-gh-smoke.log");
            try { File.WriteAllLines(transcriptPath, _transcript); } catch { /* transcript is best effort */ }
            foreach (var line in _transcript) output.WriteLine(line);
        }
    }

    /// <summary>Records the step and fails the test immediately on a critical failure.</summary>
    private void Check(string step, bool ok, string detail = "")
    {
        _transcript.Add($"{(ok ? "PASS" : "FAIL")}  {step}{(detail.Length > 0 ? $" — {detail}" : "")}");
        if (!ok)
            Assert.Fail($"smoke step failed: {step} — {detail}\n{string.Join('\n', _transcript)}");
    }

    private void Info(string step, string detail) => _transcript.Add($"INFO  {step} — {detail}");

    private static Task<ProcessResult> Git(string dir, params string[] args) =>
        ProcessRunner.RunAsync("git", args, dir, GitTimeout);

    /// <summary>Issue/PR number from the URL gh prints as the last stdout line.</summary>
    private static int TrailingNumber(string stdOut)
    {
        var last = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                      StringSplitOptions.TrimEntries).Last();
        return int.Parse(last[(last.LastIndexOf('/') + 1)..]);
    }

    private static string? FindTag(List<Models.Release>? releases, string name) =>
        releases?.FirstOrDefault(r => r.Name == name)?.TagName;

    private static async Task<ProcessResult> Retry(Func<Task<ProcessResult>> op)
    {
        ProcessResult result = new(-1, "", "not run", false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = await op();
            if (result.Success) return result;
            await Task.Delay(3000);
        }
        return result;
    }
}
