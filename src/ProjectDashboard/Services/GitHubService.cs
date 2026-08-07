using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProjectDashboard.Tests")]

namespace ProjectDashboard.Services;

public class GitHubService(SettingsService settingsService)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogFetchTimeout = TimeSpan.FromSeconds(60);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var exitCode = await RunGhExitCodeAsync(["auth", "status"], ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches `gh auth login` interactively in its own console window (it needs a console for the
    /// device-code/browser prompts). Returns the process so the caller can await completion, or null
    /// if gh couldn't be started.
    /// </summary>
    public Process? StartInteractiveAuthLogin()
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = ResolveGhExe(),
                Arguments = "auth login",
                UseShellExecute = true   // give gh a real console for its interactive prompts
            });
        }
        catch (Exception ex)
        {
            Log.Warn("gh auth login could not be launched", ex);
            return null;
        }
    }

    /// <summary>Human-readable gh state for Settings: not found vs. found-but-not-signed-in vs. signed in.</summary>
    public async Task<string> GetAuthSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            var exit = await RunGhExitCodeAsync(["auth", "status"], ct);
            return exit == 0 ? "Signed in" : "Found, not signed in";
        }
        catch (Win32Exception)
        {
            return "GitHub CLI not found";
        }
        catch
        {
            return "Unavailable";
        }
    }

    /// <summary>
    /// Remote facts for one repo from the batch query. Null counts mean "couldn't
    /// fetch" — callers must not render them as zero.
    /// </summary>
    public sealed record RepoRemoteData(string Visibility, int? OpenIssues, int? OpenPrs, bool Found);

    /// <summary>
    /// Fetches visibility + open issue/PR counts for MANY repos in a few
    /// `gh api graphql` calls (aliased repository fields, ~25 per call) instead of
    /// three gh spawns per repo. Partial failures poison only their own alias.
    /// Returns a map keyed by slug; missing key = fetch failed for that repo.
    /// </summary>
    public async Task<Dictionary<string, RepoRemoteData>> GetRepoDataBatchAsync(
        IReadOnlyList<string> slugs, CancellationToken ct = default)
    {
        var results = new Dictionary<string, RepoRemoteData>(StringComparer.OrdinalIgnoreCase);

        const int chunkSize = 25;
        for (var offset = 0; offset < slugs.Count; offset += chunkSize)
        {
            var chunk = slugs.Skip(offset).Take(chunkSize).ToList();
            var query = new System.Text.StringBuilder("query {\n");
            for (var i = 0; i < chunk.Count; i++)
            {
                var parts = chunk[i].Split('/', 2);
                if (parts.Length != 2) continue;
                var owner = parts[0].Replace("\\", "\\\\").Replace("\"", "\\\"");
                var name = parts[1].Replace("\\", "\\\\").Replace("\"", "\\\"");
                query.Append($"  r{i}: repository(owner: \"{owner}\", name: \"{name}\") {{ ...F }}\n");
            }
            query.Append("}\nfragment F on Repository { visibility issues(states: OPEN) { totalCount } pullRequests(states: OPEN) { totalCount } }");

            // gh exits 1 when ANY alias errors, but stdout still carries the data
            // for every alias that resolved — parse stdout regardless of exit code.
            var run = await RunAsync(["api", "graphql", "-f", $"query={query}"], ct, TimeSpan.FromSeconds(30));
            if (run.TimedOut || string.IsNullOrWhiteSpace(run.StdOut))
            {
                Log.Warn($"gh graphql batch failed ({chunk.Count} repos): {run.FirstError}");
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(run.StdOut);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

                for (var i = 0; i < chunk.Count; i++)
                {
                    // Each alias is isolated: a malformed or null one must not drop the
                    // 24 siblings in its chunk (they'd wrongly read as "fetch failed").
                    try
                    {
                        if (!data.TryGetProperty($"r{i}", out var repo)) continue;
                        if (repo.ValueKind == JsonValueKind.Null)
                        {
                            // Alias errored (repo missing / no access): known-not-found.
                            results[chunk[i]] = new RepoRemoteData("unknown", null, null, Found: false);
                            continue;
                        }
                        var vis = repo.TryGetProperty("visibility", out var v) ? v.GetString()?.ToLowerInvariant() ?? "unknown" : "unknown";
                        int? issues = repo.TryGetProperty("issues", out var iss) && iss.TryGetProperty("totalCount", out var ic) && ic.ValueKind == JsonValueKind.Number ? ic.GetInt32() : null;
                        int? prs = repo.TryGetProperty("pullRequests", out var pr) && pr.TryGetProperty("totalCount", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : null;
                        results[chunk[i]] = new RepoRemoteData(vis, issues, prs, Found: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"gh graphql alias r{i} unparseable ({chunk[i]})", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("gh graphql batch response unparseable", ex);
            }
        }

        return results;
    }

    public async Task<List<GitHubIssue>> GetIssuesAsync(string repoSlug, string state = "open", CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                ["issue", "list", "--repo", repoSlug, "--state", state,
                 "--json", "number,title,state,createdAt,updatedAt,author,labels", "--limit", "100"], ct);

            if (string.IsNullOrWhiteSpace(output))
                return [];

            var issues = new List<GitHubIssue>();
            using var doc = JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                issues.Add(new GitHubIssue
                {
                    Number = el.GetProperty("number").GetInt32(),
                    Title = el.GetProperty("title").GetString() ?? "",
                    State = el.TryGetProperty("state", out var st) ? st.GetString()?.ToLowerInvariant() ?? "" : "",
                    CreatedAt = el.TryGetProperty("createdAt", out var ca) ? ca.GetDateTimeOffset() : default,
                    UpdatedAt = el.TryGetProperty("updatedAt", out var ua) ? ua.GetDateTimeOffset() : default,
                    Author = el.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object &&
                             au.TryGetProperty("login", out var lg) ? lg.GetString() ?? "" : "",
                    Labels = el.TryGetProperty("labels", out var lb) && lb.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", lb.EnumerateArray().Select(l =>
                            l.TryGetProperty("name", out var n) ? n.GetString() : null).Where(n => n is not null))
                        : ""
                });
            }
            return issues;
        }
        catch (Exception ex)
        {
            Log.Warn($"gh issue list failed for {repoSlug} (showing 0 issues)", ex);
            return [];
        }
    }

    public async Task<List<GitHubPullRequest>> GetPullRequestsAsync(string repoSlug, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                ["pr", "list", "--repo", repoSlug, "--state", "open",
                 "--json", "number,title,author,isDraft,updatedAt,statusCheckRollup", "--limit", "100"], ct);

            if (string.IsNullOrWhiteSpace(output))
                return [];

            var prs = new List<GitHubPullRequest>();
            using var doc = JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                prs.Add(new GitHubPullRequest
                {
                    Number = el.GetProperty("number").GetInt32(),
                    Title = el.GetProperty("title").GetString() ?? "",
                    Author = el.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object &&
                             au.TryGetProperty("login", out var lg) ? lg.GetString() ?? "" : "",
                    IsDraft = el.TryGetProperty("isDraft", out var dr) && dr.GetBoolean(),
                    UpdatedAt = el.TryGetProperty("updatedAt", out var ua) ? ua.GetDateTimeOffset() : default,
                    ChecksState = el.TryGetProperty("statusCheckRollup", out var checks)
                        ? SummarizeChecks(checks) : ""
                });
            }
            return prs;
        }
        catch (Exception ex)
        {
            Log.Warn($"gh pr list failed for {repoSlug} (showing 0 PRs)", ex);
            return [];
        }
    }

    /// <summary>Aggregates a PR's statusCheckRollup into failing / pending / passing / "".</summary>
    private static string SummarizeChecks(JsonElement rollup)
    {
        if (rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0) return "";

        bool anyPending = false;
        foreach (var check in rollup.EnumerateArray())
        {
            // CheckRun: status (COMPLETED/IN_PROGRESS/...) + conclusion (SUCCESS/FAILURE/...)
            // StatusContext: state (SUCCESS/FAILURE/PENDING/ERROR)
            var state = check.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String && c.GetString()!.Length > 0
                ? c.GetString()!
                : check.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()!
                    : check.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString()!
                        : "";

            switch (state.ToUpperInvariant())
            {
                case "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE":
                    return "failing";
                case "PENDING" or "IN_PROGRESS" or "QUEUED" or "WAITING" or "EXPECTED" or "REQUESTED":
                    anyPending = true;
                    break;
            }
        }
        return anyPending ? "pending" : "passing";
    }

    /// <summary>The signed-in user's repositories, newest activity first (clone picker).</summary>
    public async Task<List<RemoteRepo>> GetUserReposAsync(CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                ["repo", "list", "--json", "nameWithOwner,description,visibility,updatedAt", "--limit", "200"], ct);
            if (string.IsNullOrWhiteSpace(output)) return [];

            var repos = JsonSerializer.Deserialize<List<RemoteRepo>>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
            return repos
                .Select(r => { r.Visibility = r.Visibility.ToLowerInvariant(); return r; })
                .OrderByDescending(r => r.UpdatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("gh repo list failed", ex);
            return [];
        }
    }

    /// <summary>Full issue view with comment thread. Null = fetch failed, never an empty success.</summary>
    public async Task<IssueDetail?> GetIssueDetailAsync(string repoSlug, int number, CancellationToken ct = default)
    {
        var run = await RunAsync(
            ["issue", "view", number.ToString(), "--repo", repoSlug,
             "--json", "number,title,state,body,author,createdAt,updatedAt,labels,assignees,milestone,comments,url"],
            ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh issue view #{number} failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseIssueDetail(run.StdOut);
    }

    internal static IssueDetail? ParseIssueDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            if (el.ValueKind != JsonValueKind.Object) return null;
            return new IssueDetail
            {
                Number = Int(el, "number") ?? 0,
                Title = Str(el, "title"),
                State = Str(el, "state").ToLowerInvariant(),
                Body = Str(el, "body"),
                Author = Login(el, "author"),
                CreatedAt = Date(el, "createdAt") ?? default,
                UpdatedAt = Date(el, "updatedAt") ?? default,
                Labels = JoinNames(el, "labels", "name"),
                LabelNames = ReadNames(el, "labels", "name"),
                Assignees = JoinNames(el, "assignees", "login"),
                Milestone = el.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object
                    ? Str(ms, "title") : "",
                Comments = ParseComments(el),
                Url = Str(el, "url")
            };
        }
        catch (Exception ex)
        {
            Log.Warn("gh issue view response unparseable", ex);
            return null;
        }
    }

    /// <summary>Full PR view with refs, merge state, checks, and comments. Null = fetch failed.</summary>
    public async Task<PullRequestDetail?> GetPullRequestDetailAsync(string repoSlug, int number, CancellationToken ct = default)
    {
        var run = await RunAsync(
            ["pr", "view", number.ToString(), "--repo", repoSlug,
             "--json", "number,title,state,body,author,isDraft,baseRefName,headRefName,mergeable,mergeStateStatus," +
                       "changedFiles,additions,deletions,statusCheckRollup,reviewDecision,createdAt,updatedAt,comments,url"],
            ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh pr view #{number} failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParsePullRequestDetail(run.StdOut);
    }

    internal static PullRequestDetail? ParsePullRequestDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            if (el.ValueKind != JsonValueKind.Object) return null;
            return new PullRequestDetail
            {
                Number = Int(el, "number") ?? 0,
                Title = Str(el, "title"),
                State = Str(el, "state").ToLowerInvariant(),
                Body = Str(el, "body"),
                Author = Login(el, "author"),
                IsDraft = Bool(el, "isDraft"),
                BaseRef = Str(el, "baseRefName"),
                HeadRef = Str(el, "headRefName"),
                Mergeable = Str(el, "mergeable").ToLowerInvariant(),
                MergeStateStatus = Str(el, "mergeStateStatus").ToLowerInvariant(),
                ChangedFiles = Int(el, "changedFiles"),
                Additions = Int(el, "additions"),
                Deletions = Int(el, "deletions"),
                ChecksState = el.TryGetProperty("statusCheckRollup", out var checks) ? SummarizeChecks(checks) : "",
                ReviewDecision = Str(el, "reviewDecision").ToLowerInvariant(),
                CreatedAt = Date(el, "createdAt") ?? default,
                UpdatedAt = Date(el, "updatedAt") ?? default,
                Comments = ParseComments(el),
                Url = Str(el, "url")
            };
        }
        catch (Exception ex)
        {
            Log.Warn("gh pr view response unparseable", ex);
            return null;
        }
    }

    /// <summary>
    /// All releases incl. drafts, newest first. REST endpoint rather than `gh release list`:
    /// the list command's --json field set carries no assets. Null = fetch failed.
    /// </summary>
    public async Task<List<Release>?> GetReleasesAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(["api", $"repos/{repoSlug}/releases?per_page=100"], ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api releases failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseReleases(run.StdOut);
    }

    internal static List<Release>? ParseReleases(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // An API error payload is an object ({"message": ...}), never a list.
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var releases = new List<Release>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var assets = new List<ReleaseAsset>();
                if (el.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var a in arr.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.Object)
                            assets.Add(new ReleaseAsset
                            {
                                Name = Str(a, "name"),
                                Size = Long(a, "size") ?? 0,
                                DownloadUrl = Str(a, "browser_download_url")
                            });
                releases.Add(new Release
                {
                    TagName = Str(el, "tag_name"),
                    Name = Str(el, "name"),
                    IsDraft = Bool(el, "draft"),
                    IsPrerelease = Bool(el, "prerelease"),
                    PublishedAt = Date(el, "published_at"),
                    Assets = assets,
                    Url = Str(el, "html_url")
                });
            }
            return releases;
        }
        catch (Exception ex)
        {
            Log.Warn("gh api releases response unparseable", ex);
            return null;
        }
    }

    /// <summary>Latest workflow runs, newest first. Null = fetch failed.</summary>
    public async Task<List<WorkflowRun>?> GetWorkflowRunsAsync(string repoSlug, int limit = 30, CancellationToken ct = default)
    {
        var run = await RunAsync(
            ["run", "list", "--repo", repoSlug, "--limit", limit.ToString(),
             "--json", "databaseId,workflowName,displayTitle,headBranch,event,status,conclusion,startedAt,url"],
            ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh run list failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseWorkflowRuns(run.StdOut);
    }

    internal static List<WorkflowRun>? ParseWorkflowRuns(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var runs = new List<WorkflowRun>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                runs.Add(new WorkflowRun
                {
                    Id = Long(el, "databaseId") ?? 0,
                    Name = Str(el, "workflowName"),
                    DisplayTitle = Str(el, "displayTitle"),
                    Branch = Str(el, "headBranch"),
                    Event = Str(el, "event"),
                    Status = Str(el, "status").ToLowerInvariant(),
                    Conclusion = Str(el, "conclusion").ToLowerInvariant(),
                    StartedAt = Date(el, "startedAt"),
                    Url = Str(el, "url")
                });
            }
            return runs;
        }
        catch (Exception ex)
        {
            Log.Warn("gh run list response unparseable", ex);
            return null;
        }
    }

    /// <summary>Repo settings for the Repo tab. Null = fetch failed.</summary>
    public async Task<RepoSettings?> GetRepoSettingsAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(
            ["repo", "view", repoSlug,
             "--json", "name,description,homepageUrl,repositoryTopics,visibility,isArchived,defaultBranchRef,parent"],
            ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh repo view failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseRepoSettings(run.StdOut);
    }

    internal static RepoSettings? ParseRepoSettings(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            if (el.ValueKind != JsonValueKind.Object) return null;

            var topics = new List<string>();
            if (el.TryGetProperty("repositoryTopics", out var tp) && tp.ValueKind == JsonValueKind.Array)
                foreach (var t in tp.EnumerateArray())
                {
                    // Topics export both as bare strings and as {name} objects.
                    var name = t.ValueKind == JsonValueKind.String ? t.GetString() ?? ""
                        : t.ValueKind == JsonValueKind.Object ? Str(t, "name") : "";
                    if (name.Length > 0) topics.Add(name);
                }

            var parentSlug = "";
            if (el.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object)
            {
                parentSlug = Str(parent, "nameWithOwner");
                if (parentSlug.Length == 0)
                {
                    var owner = Login(parent, "owner");
                    var parentName = Str(parent, "name");
                    if (owner.Length > 0 && parentName.Length > 0) parentSlug = $"{owner}/{parentName}";
                }
            }

            return new RepoSettings
            {
                Name = Str(el, "name"),
                Description = Str(el, "description"),
                Homepage = Str(el, "homepageUrl"),
                Topics = topics,
                Visibility = Str(el, "visibility").ToLowerInvariant(),
                IsArchived = Bool(el, "isArchived"),
                DefaultBranch = el.TryGetProperty("defaultBranchRef", out var db) && db.ValueKind == JsonValueKind.Object
                    ? Str(db, "name") : "",
                ParentSlug = parentSlug
            };
        }
        catch (Exception ex)
        {
            Log.Warn("gh repo view response unparseable", ex);
            return null;
        }
    }

    /// <summary>Labels defined on the repo. Null = fetch failed.</summary>
    public async Task<List<Label>?> GetLabelsAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(
            ["label", "list", "--repo", repoSlug, "--json", "name,color,description", "--limit", "100"],
            ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh label list failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseLabels(run.StdOut);
    }

    internal static List<Label>? ParseLabels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var labels = new List<Label>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                labels.Add(new Label
                {
                    Name = Str(el, "name"),
                    Color = Str(el, "color"),
                    Description = Str(el, "description")
                });
            }
            return labels;
        }
        catch (Exception ex)
        {
            Log.Warn("gh label list response unparseable", ex);
            return null;
        }
    }

    /// <summary>Milestones (open and closed). REST — gh has no milestone list command. Null = fetch failed.</summary>
    public async Task<List<Milestone>?> GetMilestonesAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(["api", $"repos/{repoSlug}/milestones?state=all&per_page=100"], ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api milestones failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseMilestones(run.StdOut);
    }

    internal static List<Milestone>? ParseMilestones(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var milestones = new List<Milestone>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                milestones.Add(new Milestone
                {
                    Number = Int(el, "number") ?? 0,
                    Title = Str(el, "title"),
                    State = Str(el, "state").ToLowerInvariant(),
                    DueOn = Date(el, "due_on"),
                    OpenIssues = Int(el, "open_issues"),
                    ClosedIssues = Int(el, "closed_issues")
                });
            }
            return milestones;
        }
        catch (Exception ex)
        {
            Log.Warn("gh api milestones response unparseable", ex);
            return null;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static long? Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Null for absent or zero timestamps — gh serializes "not yet" times as the year-1 zero value.</summary>
    private static DateTimeOffset? Date(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        v.TryGetDateTimeOffset(out var d) && d.Year > 1 ? d : null;

    /// <summary>Deleted accounts serialize as author: null — read as "".</summary>
    private static string Login(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? Str(v, "login") : "";

    private static string JoinNames(JsonElement el, string arrayName, string field) =>
        string.Join(", ", ReadNames(el, arrayName, field));

    /// <summary>Names as the API returned them; a joined form cannot be split back apart
    /// because a name may contain the separator.</summary>
    private static List<string> ReadNames(JsonElement el, string arrayName, string field) =>
        el.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? [.. arr.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.Object ? Str(x, field) : "")
                .Where(n => n.Length > 0)]
            : [];

    private static List<IssueComment> ParseComments(JsonElement el)
    {
        var comments = new List<IssueComment>();
        if (!el.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return comments;
        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            comments.Add(new IssueComment
            {
                Author = Login(c, "author"),
                CreatedAt = Date(c, "createdAt") ?? default,
                Body = Str(c, "body")
            });
        }
        return comments;
    }

    public Task<ProcessResult> CreateIssueAsync(string repoSlug, string title, string body,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default)
        => RunMutationAsync($"gh issue create ({repoSlug})",
            BuildCreateIssueArgs(repoSlug, title, body, labels), ct: ct);

    internal static List<string> BuildCreateIssueArgs(string repoSlug, string title, string body, IReadOnlyList<string>? labels)
    {
        // --body always present, even empty: without it gh falls into its interactive
        // prompt, which GH_PROMPT_DISABLED turns into a hard failure.
        var args = new List<string> { "issue", "create", "--repo", repoSlug, "--title", title, "--body", body };
        foreach (var label in labels ?? [])
        {
            args.Add("--label");
            args.Add(label);
        }
        return args;
    }

    public Task<ProcessResult> CommentIssueAsync(string repoSlug, int number, string body, CancellationToken ct = default)
        => RunMutationAsync($"gh issue comment #{number} ({repoSlug})",
            ["issue", "comment", number.ToString(), "--repo", repoSlug, "--body", body], ct: ct);

    public Task<ProcessResult> CloseIssueAsync(string repoSlug, int number, CancellationToken ct = default)
        => RunMutationAsync($"gh issue close #{number} ({repoSlug})",
            ["issue", "close", number.ToString(), "--repo", repoSlug], ct: ct);

    public Task<ProcessResult> ReopenIssueAsync(string repoSlug, int number, CancellationToken ct = default)
        => RunMutationAsync($"gh issue reopen #{number} ({repoSlug})",
            ["issue", "reopen", number.ToString(), "--repo", repoSlug], ct: ct);

    public Task<ProcessResult> EditIssueLabelsAsync(string repoSlug, int number,
        IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct = default)
        // A flagless `gh issue edit` is an interactive prompt (hard failure) — skip the spawn.
        => add.Count == 0 && remove.Count == 0
            ? Task.FromResult(NoOpSuccess())
            : RunMutationAsync($"gh issue edit #{number} labels ({repoSlug})",
                BuildIssueLabelEditArgs(repoSlug, number, add, remove), ct: ct);

    internal static List<string> BuildIssueLabelEditArgs(string repoSlug, int number,
        IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var args = new List<string> { "issue", "edit", number.ToString(), "--repo", repoSlug };
        foreach (var label in add)
        {
            args.Add("--add-label");
            args.Add(label);
        }
        foreach (var label in remove)
        {
            args.Add("--remove-label");
            args.Add(label);
        }
        return args;
    }

    public Task<ProcessResult> AssignIssueAsync(string repoSlug, int number, string assignee, CancellationToken ct = default)
        => RunMutationAsync($"gh issue edit #{number} assign ({repoSlug})",
            ["issue", "edit", number.ToString(), "--repo", repoSlug, "--add-assignee", assignee], ct: ct);

    /// <summary>
    /// Runs in the repo directory. Pass <paramref name="headBranch"/> to pin the source
    /// branch: without it gh reads whatever is checked out when the process spawns, which
    /// need not be the branch the caller showed the user.
    /// </summary>
    public Task<ProcessResult> CreatePullRequestAsync(string repoPath, string title, string body,
        string? baseBranch = null, bool draft = false, string? headBranch = null, CancellationToken ct = default)
        => RunMutationAsync("gh pr create",
            BuildCreatePullRequestArgs(title, body, baseBranch, draft, headBranch), repoPath, ct: ct);

    internal static List<string> BuildCreatePullRequestArgs(string title, string body, string? baseBranch,
        bool draft, string? headBranch = null)
    {
        var args = new List<string> { "pr", "create", "--title", title, "--body", body };
        if (!string.IsNullOrWhiteSpace(baseBranch))
        {
            args.Add("--base");
            args.Add(baseBranch);
        }
        if (!string.IsNullOrWhiteSpace(headBranch))
        {
            args.Add("--head");
            args.Add(headBranch);
        }
        if (draft) args.Add("--draft");
        return args;
    }

    public Task<ProcessResult> CommentPullRequestAsync(string repoSlug, int number, string body, CancellationToken ct = default)
        => RunMutationAsync($"gh pr comment #{number} ({repoSlug})",
            ["pr", "comment", number.ToString(), "--repo", repoSlug, "--body", body], ct: ct);

    public Task<ProcessResult> ClosePullRequestAsync(string repoSlug, int number, CancellationToken ct = default)
        => RunMutationAsync($"gh pr close #{number} ({repoSlug})",
            ["pr", "close", number.ToString(), "--repo", repoSlug], ct: ct);

    public Task<ProcessResult> MergePullRequestAsync(string repoSlug, int number, string strategy,
        bool deleteBranch = false, CancellationToken ct = default)
        => RunMutationAsync($"gh pr merge #{number} ({repoSlug})",
            BuildMergeArgs(repoSlug, number, strategy, deleteBranch), ct: ct);

    internal static List<string> BuildMergeArgs(string repoSlug, int number, string strategy, bool deleteBranch)
    {
        var flag = strategy switch
        {
            "merge" => "--merge",
            "squash" => "--squash",
            "rebase" => "--rebase",
            _ => throw new ArgumentException($"unknown merge strategy '{strategy}'", nameof(strategy))
        };
        var args = new List<string> { "pr", "merge", number.ToString(), "--repo", repoSlug, flag };
        if (deleteBranch) args.Add("--delete-branch");
        return args;
    }

    /// <summary>Fetches and checks out the PR head branch in the local clone.</summary>
    public Task<ProcessResult> CheckoutPullRequestAsync(string repoPath, int number, CancellationToken ct = default)
        => RunMutationAsync($"gh pr checkout #{number}",
            ["pr", "checkout", number.ToString()], repoPath, ct: ct);

    /// <summary>
    /// GitHub rejects approve / request-changes on the caller's own PR; that comes back
    /// as a failed result with the server's message.
    /// </summary>
    public Task<ProcessResult> ReviewPullRequestAsync(string repoSlug, int number, string action,
        string body = "", CancellationToken ct = default)
        => RunMutationAsync($"gh pr review #{number} ({repoSlug})",
            BuildReviewArgs(repoSlug, number, action, body), ct: ct);

    internal static List<string> BuildReviewArgs(string repoSlug, int number, string action, string body)
    {
        var flag = action switch
        {
            "approve" => "--approve",
            "requestChanges" or "request-changes" => "--request-changes",
            "comment" => "--comment",
            _ => throw new ArgumentException($"unknown review action '{action}'", nameof(action))
        };
        var args = new List<string> { "pr", "review", number.ToString(), "--repo", repoSlug, flag };
        if (body.Length > 0)
        {
            args.Add("--body");
            args.Add(body);
        }
        return args;
    }

    public Task<ProcessResult> MarkPullRequestReadyAsync(string repoSlug, int number, CancellationToken ct = default)
        => RunMutationAsync($"gh pr ready #{number} ({repoSlug})",
            ["pr", "ready", number.ToString(), "--repo", repoSlug], ct: ct);

    /// <summary>
    /// Runs in the repo directory so a tag that doesn't exist on the remote yet is created
    /// there from the current branch.
    /// </summary>
    public async Task<ProcessResult> CreateReleaseAsync(string repoPath, string tag, string title,
        string notes, bool draft = false, bool prerelease = false, CancellationToken ct = default)
    {
        // Notes travel via --notes-file: a command-line argument caps out (and mangles
        // quoting) long before real release notes do.
        var notesFile = Path.Combine(Path.GetTempPath(), $"pd-release-notes-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(notesFile, notes, ct);
            return await RunMutationAsync($"gh release create {tag}",
                BuildReleaseCreateArgs(tag, title, notesFile, draft, prerelease), repoPath, ct: ct);
        }
        finally
        {
            try { File.Delete(notesFile); } catch { /* best effort */ }
        }
    }

    internal static List<string> BuildReleaseCreateArgs(string tag, string title, string notesFile, bool draft, bool prerelease)
    {
        // --notes-file always present, even for empty notes: without any notes flag gh
        // falls into its interactive prompt, which GH_PROMPT_DISABLED turns into a failure.
        var args = new List<string> { "release", "create", tag, "--title", title, "--notes-file", notesFile };
        if (draft) args.Add("--draft");
        if (prerelease) args.Add("--prerelease");
        return args;
    }

    /// <summary>
    /// Published releases are refused unless allowNonDraft — deleting one is irreversible
    /// and public-facing. A failed draft probe also refuses: unknown state must not
    /// default to deletion.
    /// </summary>
    public async Task<ProcessResult> DeleteReleaseAsync(string repoSlug, string tag,
        bool allowNonDraft = false, CancellationToken ct = default)
    {
        if (!allowNonDraft)
        {
            var view = await RunAsync(["release", "view", tag, "--repo", repoSlug, "--json", "isDraft"], ct, ReadTimeout);
            if (!view.Success || !TryParseIsDraft(view.StdOut, out var isDraft))
            {
                Log.Warn($"gh release delete {tag} refused for {repoSlug}: draft state unknown ({view.FirstError})");
                return new ProcessResult(1, "", $"Could not verify {tag} is a draft — not deleting.", TimedOut: false);
            }
            if (!isDraft)
            {
                Log.Warn($"gh release delete {tag} refused for {repoSlug}: release is published");
                return new ProcessResult(1, "", $"{tag} is a published release — deletion refused.", TimedOut: false);
            }
        }
        return await RunMutationAsync($"gh release delete {tag} ({repoSlug})",
            ["release", "delete", tag, "--repo", repoSlug, "--yes"], ct: ct);
    }

    internal static bool TryParseIsDraft(string json, out bool isDraft)
    {
        isDraft = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("isDraft", out var v) ||
                v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            isDraft = v.ValueKind == JsonValueKind.True;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>--failed on a run with no failed jobs is a gh error, surfaced as a failed result.</summary>
    public Task<ProcessResult> RerunWorkflowAsync(string repoSlug, long runId, bool failedOnly = false, CancellationToken ct = default)
        => RunMutationAsync($"gh run rerun {runId} ({repoSlug})",
            BuildRerunArgs(repoSlug, runId, failedOnly), ct: ct);

    internal static List<string> BuildRerunArgs(string repoSlug, long runId, bool failedOnly)
    {
        var args = new List<string> { "run", "rerun", runId.ToString(), "--repo", repoSlug };
        if (failedOnly) args.Add("--failed");
        return args;
    }

    public Task<ProcessResult> CancelWorkflowRunAsync(string repoSlug, long runId, CancellationToken ct = default)
        => RunMutationAsync($"gh run cancel {runId} ({repoSlug})",
            ["run", "cancel", runId.ToString(), "--repo", repoSlug], ct: ct);

    /// <summary>
    /// Full run log, capped: run logs can reach tens of MB, and crossing the cap kills the
    /// fetch instead of buffering the remainder. The cap counts UTF-16 chars, which for
    /// ASCII-dominated logs tracks bytes closely. Null = fetch failed, never an empty log.
    /// </summary>
    public async Task<string?> GetWorkflowRunLogAsync(string repoSlug, long runId,
        int maxBytes = 2_000_000, CancellationToken ct = default)
    {
        var capture = new System.Text.StringBuilder();
        var gate = new object();
        var capped = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunStreamingAsync(ResolveGhExe(),
                ["run", "view", runId.ToString(), "--repo", repoSlug, "--log"],
                null, LogFetchTimeout, GhEnvironment,
                onStdOutLine: line =>
                {
                    lock (gate)
                    {
                        if (capped) return;
                        if (capture.Length + line.Length > maxBytes)
                        {
                            capped = true;
                            cts.Cancel();
                            return;
                        }
                        capture.AppendLine(line);
                    }
                },
                onStdErrLine: null, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The cap's own cancellation, not the caller's: return what was captured.
            lock (gate)
            {
                if (capped)
                    return capture.AppendLine(TruncationMarker(maxBytes)).ToString();
            }
            Log.Warn($"gh run view --log {runId} canceled unexpectedly for {repoSlug}");
            return null;
        }

        // The child can exit between the cap firing and the cancel landing — the cap
        // still holds: never hand back the oversized full capture.
        lock (gate)
        {
            if (capped)
                return capture.AppendLine(TruncationMarker(maxBytes)).ToString();
        }

        if (!result.Success)
        {
            Log.Warn($"gh run view --log {runId} failed for {repoSlug}: {result.FirstError}");
            return null;
        }
        return result.StdOut;
    }

    internal static string TruncationMarker(int maxBytes) => $"[log truncated at {maxBytes} bytes]";

    /// <summary>Null description/homepage means "leave unchanged"; empty string clears the field.</summary>
    public Task<ProcessResult> EditRepoAsync(string repoSlug, string? description = null, string? homepage = null,
        IReadOnlyList<string>? addTopics = null, IReadOnlyList<string>? removeTopics = null, CancellationToken ct = default)
    {
        var args = BuildRepoEditArgs(repoSlug, description, homepage, addTopics, removeTopics);
        // A flagless `gh repo edit` is an interactive prompt (hard failure) — skip the spawn.
        return args.Count == 3
            ? Task.FromResult(NoOpSuccess())
            : RunMutationAsync($"gh repo edit ({repoSlug})", args, ct: ct);
    }

    internal static List<string> BuildRepoEditArgs(string repoSlug, string? description, string? homepage,
        IReadOnlyList<string>? addTopics, IReadOnlyList<string>? removeTopics)
    {
        var args = new List<string> { "repo", "edit", repoSlug };
        if (description is not null)
        {
            args.Add("--description");
            args.Add(description);
        }
        if (homepage is not null)
        {
            args.Add("--homepage");
            args.Add(homepage);
        }
        foreach (var topic in addTopics ?? [])
        {
            args.Add("--add-topic");
            args.Add(topic);
        }
        foreach (var topic in removeTopics ?? [])
        {
            args.Add("--remove-topic");
            args.Add(topic);
        }
        return args;
    }

    public Task<ProcessResult> RenameRepoAsync(string repoSlug, string newName, CancellationToken ct = default)
        => RunMutationAsync($"gh repo rename ({repoSlug} -> {newName})",
            ["repo", "rename", newName, "--repo", repoSlug, "--yes"], ct: ct);

    /// <summary>
    /// Older gh has no --accept-visibility-change-consequences flag; there the call comes
    /// back as a failed result (unknown flag), never a crash.
    /// </summary>
    public Task<ProcessResult> SetRepoVisibilityAsync(string repoSlug, string visibility, CancellationToken ct = default)
        => RunMutationAsync($"gh repo edit --visibility {visibility} ({repoSlug})",
            BuildVisibilityArgs(repoSlug, visibility), ct: ct);

    internal static List<string> BuildVisibilityArgs(string repoSlug, string visibility)
    {
        if (visibility is not ("public" or "private" or "internal"))
            throw new ArgumentException($"unknown visibility '{visibility}'", nameof(visibility));
        return ["repo", "edit", repoSlug, "--visibility", visibility, "--accept-visibility-change-consequences"];
    }

    public Task<ProcessResult> ArchiveRepoAsync(string repoSlug, CancellationToken ct = default)
        => RunMutationAsync($"gh repo archive ({repoSlug})", ["repo", "archive", repoSlug, "--yes"], ct: ct);

    public Task<ProcessResult> UnarchiveRepoAsync(string repoSlug, CancellationToken ct = default)
        => RunMutationAsync($"gh repo unarchive ({repoSlug})", ["repo", "unarchive", repoSlug, "--yes"], ct: ct);

    /// <summary>Syncs the local clone's default branch from the fork's parent repo.</summary>
    public Task<ProcessResult> SyncForkAsync(string repoPath, CancellationToken ct = default)
        => RunMutationAsync("gh repo sync", ["repo", "sync"], repoPath, ct: ct);

    /// <summary>Mutation runner: never throws on failure — callers toast ProcessResult.FirstError.</summary>
    private async Task<ProcessResult> RunMutationAsync(string what, IReadOnlyList<string> args,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(ResolveGhExe(), args, workingDirectory,
            timeout ?? MutationTimeout, GhEnvironment, ct);
        if (!result.Success)
            Log.Warn($"{what} failed: {result.FirstError}");
        return result;
    }

    private static ProcessResult NoOpSuccess() => new(0, "", "", TimedOut: false);

    /// <summary>
    /// Environment for every gh call: no ANSI color in parsed output, no update banner
    /// on stderr, no interactive prompts from a windowless process.
    /// </summary>
    private static readonly Dictionary<string, string> GhEnvironment = new()
    {
        ["NO_COLOR"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["GH_PROMPT_DISABLED"] = "1"
    };

    /// <summary>Structured run for callers that need exit codes and stderr (no throw on failure).</summary>
    public async Task<ProcessResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        => await ProcessRunner.RunAsync(ResolveGhExe(), args, null, timeout ?? Timeout, GhEnvironment, ct);

    private async Task<string> RunGhAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var result = await RunAsync(args, ct);
        if (result.TimedOut)
            throw new TimeoutException("gh timed out");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"gh failed ({result.ExitCode}): {result.FirstError}");
        return result.StdOut;
    }

    private async Task<int> RunGhExitCodeAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var result = await RunAsync(args, ct);
        if (result.TimedOut)
            throw new TimeoutException("gh timed out");
        return result.ExitCode;
    }

    /// <summary>
    /// Resolves the gh executable: configured GhPath (file or its folder) first, then known
    /// install locations, then bare "gh" (PATH). Lets a Start-Menu launch with a stale PATH
    /// still find gh when the user points us at it in Settings.
    /// </summary>
    private string ResolveGhExe()
    {
        var configured = settingsService.Load().GhPath?.Trim() ?? "";
        if (configured.Length > 0)
        {
            if (File.Exists(configured)) return configured;
            if (Directory.Exists(configured))
            {
                var inDir = Path.Combine(configured, "gh.exe");
                if (File.Exists(inDir)) return inDir;
            }
        }

        string[] known =
        [
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("LocalAppData") ?? "", "Microsoft", "WinGet", "Links", "gh.exe"),
        ];
        foreach (var p in known)
            if (p.Length > 0 && File.Exists(p)) return p;

        return "gh"; // last resort: PATH
    }
}
