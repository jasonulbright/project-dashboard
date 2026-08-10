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

    /// <summary>
    /// The allowance for a read that follows GitHub's pages to the end. One request per hundred
    /// rows is spent inside a single gh call, so the single-page allowance would turn a large but
    /// healthy repository into a read that reports a failure.
    /// </summary>
    private static readonly TimeSpan PagedReadTimeout = TimeSpan.FromSeconds(60);

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

    /// <summary>
    /// The server-side facets one issue or pull-request list read carries. State, search and
    /// milestone go to gh rather than being applied to what came back: filtering a capped page
    /// would answer from an arbitrary subset of the repository and call it the whole answer.
    ///
    /// <c>gh pr list</c> carries no milestone flag, so the facet reaches the issue list only; a
    /// milestone set on a pull-request query is not applied and is never claimed to be.
    /// </summary>
    public sealed record GitHubListQuery(string State = "open", string? Search = null, int Limit = 100,
        MilestoneFacet? Milestone = null);

    /// <summary>
    /// One read of the issue list. MayHaveMore is true whenever the read came back full: a full
    /// page cannot distinguish a repository holding exactly that many from one holding more, and
    /// the next read at a larger limit resolves it against gh rather than guessing again.
    /// </summary>
    public sealed record IssuePage(IReadOnlyList<GitHubIssue> Items, bool MayHaveMore, int Limit);

    /// <summary>One read of the pull-request list, on the same terms as <see cref="IssuePage"/>.</summary>
    public sealed record PullRequestPage(IReadOnlyList<GitHubPullRequest> Items, bool MayHaveMore, int Limit);

    /// <summary>
    /// The outcome of one list read. A null page is a failed read and an empty page is an answer,
    /// so the two are never the same value. Error carries what gh said about the failure — the
    /// cause is the reader's to act on and nothing in the app can infer it — and is "" for a read
    /// that succeeded or one that failed without saying anything.
    /// </summary>
    public sealed record ListRead<TPage>(TPage? Page, string Error) where TPage : class;

    /// <summary>
    /// Whether a page of <paramref name="loaded"/> rows read at <paramref name="limit"/> may have
    /// rows behind it.
    /// </summary>
    internal static bool PageMayHaveMore(int loaded, int limit) => limit > 0 && loaded >= limit;

    /// <summary>
    /// The argument vector for a REST list read that follows GitHub's pages to the end.
    ///
    /// gh merges the pages of an array endpoint into one array, so the parsers read a complete
    /// answer through the same shape a single page has. A run that stops part-way exits non-zero
    /// with the pages it did read on stdout; every caller tests the exit code before parsing, so
    /// that output is a failed read rather than a list that quietly stops short.
    ///
    /// The page size is the endpoint maximum, which decides how many requests the run spends
    /// rather than how much it returns.
    /// </summary>
    internal static List<string> BuildPagedApiArgs(string path) => ["api", path, "--paginate"];

    /// <summary>
    /// The repository's issues under <paramref name="query"/>, or null when the read failed. An
    /// empty page is an answer — nothing in this repository matches these facets — and a failed
    /// gh call establishes no such thing, so the two are never the same value. gh prints a JSON
    /// array whenever it succeeds, so blank output is a failure too.
    /// </summary>
    public async Task<ListRead<IssuePage>> GetIssuePageAsync(string repoSlug, GitHubListQuery query,
        CancellationToken ct = default)
    {
        var run = await RunAsync(BuildIssueListArgs(repoSlug, query), ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh issue list failed for {repoSlug}: {run.FirstError}");
            return new ListRead<IssuePage>(null, FailureText(run));
        }
        var issues = ParseIssues(run.StdOut);
        return new ListRead<IssuePage>(
            issues is null ? null : new IssuePage(issues, PageMayHaveMore(issues.Count, query.Limit), query.Limit),
            issues is null ? UnreadableResponse : "");
    }

    /// <summary>
    /// The repository's pull requests under <paramref name="query"/>. Same terms as
    /// <see cref="GetIssuePageAsync"/>: an empty page is an answer and a failure is not one.
    /// </summary>
    public async Task<ListRead<PullRequestPage>> GetPullRequestPageAsync(string repoSlug, GitHubListQuery query,
        CancellationToken ct = default)
    {
        var run = await RunAsync(BuildPullRequestListArgs(repoSlug, query), ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh pr list failed for {repoSlug}: {run.FirstError}");
            return new ListRead<PullRequestPage>(null, FailureText(run));
        }
        var prs = ParsePullRequests(run.StdOut);
        return new ListRead<PullRequestPage>(
            prs is null ? null : new PullRequestPage(prs, PageMayHaveMore(prs.Count, query.Limit), query.Limit),
            prs is null ? UnreadableResponse : "");
    }

    internal const string UnreadableResponse = "The response could not be read.";

    /// <summary>
    /// What a failed read can say about itself: gh's own first line, capped, because it lands in a
    /// status line beside the app's own sentence. Read from the streams rather than from
    /// <see cref="ProcessResult.FirstError"/>, whose "exit code N" fallback names no cause — gh
    /// exits non-zero with nothing on either stream often enough that the fallback would be shown
    /// as though it were an explanation.
    /// </summary>
    internal static string FailureText(ProcessResult run)
    {
        if (run.TimedOut) return "The GitHub CLI did not answer in time.";
        var said = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
        var line = said.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    /// <summary>
    /// The issue list's argument vector. The milestone travels as its number: gh reads a numeric
    /// value as a milestone number and anything else as a title, so a milestone whose title reads
    /// as a number is still the one addressed. A search naming a milestone of its own is left as
    /// the only one in force, on the same terms as the state flag — gh turns the flag into a
    /// second <c>milestone:</c> qualifier, and two of them intersect to nothing while the picker
    /// on screen still names one of them.
    /// </summary>
    internal static List<string> BuildIssueListArgs(string repoSlug, GitHubListQuery query)
    {
        var args = BuildListArgs("issue", repoSlug, "number,title,state,createdAt,updatedAt,author,labels", query);
        if (query.Milestone is { } milestone && !SearchSetsMilestone(query.Search ?? ""))
            args.AddRange(["--milestone", milestone.Number.ToString()]);
        return args;
    }

    internal static List<string> BuildPullRequestListArgs(string repoSlug, GitHubListQuery query) =>
        BuildListArgs("pr", repoSlug, "number,title,state,author,isDraft,updatedAt,statusCheckRollup", query);

    /// <summary>
    /// The argument vector for one list read. The search text travels verbatim — GitHub's search
    /// syntax is the user's to write and GitHub's to reject.
    ///
    /// A search naming a state of its own overrules --state inside gh, which turns the state flag
    /// into a value the surface shows but the read never applied. The flag is therefore set to the
    /// one value that adds no qualifier, leaving the search as the only state in force, and the
    /// surface says the picker is not applied.
    /// </summary>
    private static List<string> BuildListArgs(string entity, string repoSlug, string jsonFields,
        GitHubListQuery query)
    {
        var search = query.Search?.Trim() ?? "";
        var state = search.Length > 0 && SearchSetsState(search) ? "all" : query.State;
        var args = new List<string>
        {
            entity, "list", "--repo", repoSlug, "--state", state,
            "--json", jsonFields, "--limit", query.Limit.ToString()
        };
        if (search.Length > 0) args.AddRange(["--search", search]);
        return args;
    }

    /// <summary>
    /// Whether a search string names the state it wants. GitHub spells that two ways — a
    /// <c>state:</c> qualifier and the <c>is:</c> forms that select one — and each only as a term
    /// of its own. Text inside a quoted phrase is searched for, not interpreted, so
    /// <c>title:"a state:closed b"</c> names no state.
    /// </summary>
    internal static bool SearchSetsState(string search) =>
        SearchTerms(search).Any(term =>
            term.StartsWith("state:", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("is:open", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("is:closed", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("is:merged", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("is:unmerged", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a search string names the milestone it wants. GitHub spells that as a
    /// <c>milestone:</c> qualifier and as <c>no:milestone</c>, each only as a term of its own, so
    /// text inside a quoted phrase names none.
    /// </summary>
    internal static bool SearchSetsMilestone(string search) =>
        SearchTerms(search).Any(term =>
            term.StartsWith("milestone:", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("no:milestone", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The terms of a search string: whitespace separates them, except inside a double-quoted
    /// span. An unclosed quote runs to the end of the string, which is how GitHub reads it too.
    /// </summary>
    internal static List<string> SearchTerms(string search)
    {
        var terms = new List<string>();
        var term = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in search)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (term.Length > 0) terms.Add(term.ToString());
                term.Clear();
                continue;
            }
            term.Append(c);
        }
        if (term.Length > 0) terms.Add(term.ToString());
        return terms;
    }

    internal static List<GitHubIssue>? ParseIssues(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var issues = new List<GitHubIssue>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // A row with no number addresses nothing: every command on it takes the number.
                if (el.ValueKind != JsonValueKind.Object || Int(el, "number") is not { } number) continue;
                issues.Add(new GitHubIssue
                {
                    Number = number,
                    Title = Str(el, "title"),
                    State = Str(el, "state").ToLowerInvariant(),
                    CreatedAt = Date(el, "createdAt") ?? default,
                    UpdatedAt = Date(el, "updatedAt") ?? default,
                    Author = Login(el, "author"),
                    Labels = JoinNames(el, "labels", "name")
                });
            }
            return issues;
        }
        catch (Exception ex)
        {
            Log.Warn("gh issue list response unparseable", ex);
            return null;
        }
    }

    internal static List<GitHubPullRequest>? ParsePullRequests(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var prs = new List<GitHubPullRequest>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object || Int(el, "number") is not { } number) continue;
                prs.Add(new GitHubPullRequest
                {
                    Number = number,
                    Title = Str(el, "title"),
                    State = Str(el, "state").ToLowerInvariant(),
                    Author = Login(el, "author"),
                    IsDraft = Bool(el, "isDraft"),
                    UpdatedAt = Date(el, "updatedAt") ?? default,
                    ChecksState = el.TryGetProperty("statusCheckRollup", out var checks)
                        ? SummarizeChecks(checks) : ""
                });
            }
            return prs;
        }
        catch (Exception ex)
        {
            Log.Warn("gh pr list response unparseable", ex);
            return null;
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
    /// Every release incl. drafts, newest first. REST endpoint rather than `gh release list`:
    /// the list command's --json field set carries no assets. Paged to the end, so the list is
    /// the repository's own rather than its first hundred. Null = fetch failed.
    /// </summary>
    public async Task<List<Release>?> GetReleasesAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(BuildReleasesArgs(repoSlug), ct, PagedReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api releases failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseReleases(run.StdOut);
    }

    internal static List<string> BuildReleasesArgs(string repoSlug) =>
        BuildPagedApiArgs($"repos/{repoSlug}/releases?per_page=100");

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
                    Body = Str(el, "body"),
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

    /// <summary>
    /// The server-side facets one workflow-run list read carries. Each goes to gh rather than
    /// being applied to what came back: a facet applied here would answer from whichever runs the
    /// window happened to hold and report that as the repository's history.
    /// </summary>
    public sealed record WorkflowRunQuery(string? Workflow = null, string? Branch = null,
        string? Status = null, int Limit = 30);

    /// <summary>One read of the workflow-run list, on the same terms as <see cref="IssuePage"/>.</summary>
    public sealed record WorkflowRunPage(IReadOnlyList<WorkflowRun> Items, bool MayHaveMore, int Limit);

    /// <summary>
    /// The repository's workflow runs under <paramref name="query"/>, newest first. Null page = the
    /// read failed; an empty page is an answer, and the two are never the same value.
    /// </summary>
    public async Task<ListRead<WorkflowRunPage>> GetWorkflowRunPageAsync(string repoSlug,
        WorkflowRunQuery query, CancellationToken ct = default)
    {
        var run = await RunAsync(BuildWorkflowRunListArgs(repoSlug, query), ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh run list failed for {repoSlug}: {run.FirstError}");
            return new ListRead<WorkflowRunPage>(null, FailureText(run));
        }
        var runs = ParseWorkflowRuns(run.StdOut);
        return new ListRead<WorkflowRunPage>(
            runs is null ? null : new WorkflowRunPage(runs, PageMayHaveMore(runs.Count, query.Limit), query.Limit),
            runs is null ? UnreadableResponse : "");
    }

    /// <summary>
    /// The workflow-run list's argument vector. A facet with no value set adds no flag: gh reads
    /// an empty <c>--workflow</c> as a workflow named "", which matches no run and would report an
    /// unfiltered repository as an empty one.
    /// </summary>
    internal static List<string> BuildWorkflowRunListArgs(string repoSlug, WorkflowRunQuery query)
    {
        var args = new List<string>
        {
            "run", "list", "--repo", repoSlug,
            "--json", "databaseId,workflowName,displayTitle,headBranch,event,status,conclusion,startedAt,updatedAt,url",
            "--limit", query.Limit.ToString()
        };
        if (Facet(query.Workflow) is { } workflow) args.AddRange(["--workflow", workflow]);
        if (Facet(query.Branch) is { } branch) args.AddRange(["--branch", branch]);
        if (Facet(query.Status) is { } status) args.AddRange(["--status", status]);
        return args;
    }

    private static string? Facet(string? value) =>
        value is null || value.Trim().Length == 0 ? null : value.Trim();

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
                    UpdatedAt = Date(el, "updatedAt"),
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

    /// <summary>
    /// One read of a run's job list. The payload states how many jobs the run has, so a window
    /// that stopped short says by how much rather than that it may have. <see cref="Total"/> is
    /// null when the payload did not state one, and the window itself is the only evidence left.
    /// </summary>
    public sealed record WorkflowJobPage(IReadOnlyList<WorkflowJob> Items, int? Total, int Limit)
    {
        public bool MayHaveMore =>
            Total is { } total ? Items.Count < total : PageMayHaveMore(Items.Count, Limit);
    }

    /// <summary>Rows one read of a run's job list asks for; the endpoint's own maximum.</summary>
    internal const int WorkflowJobLimit = 100;

    /// <summary>
    /// Jobs and steps of one run, in GitHub's own order. REST — `gh run view --json jobs`
    /// carries no per-step detail. The endpoint wraps its rows in an object, which gh cannot merge
    /// across pages, so this read is one page and says so rather than following them.
    /// Null page = fetch failed.
    /// </summary>
    public async Task<ListRead<WorkflowJobPage>> GetWorkflowRunJobsAsync(string repoSlug, long runId,
        CancellationToken ct = default)
    {
        var run = await RunAsync(BuildRunJobsArgs(repoSlug, runId), ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api run jobs {runId} failed for {repoSlug}: {run.FirstError}");
            return new ListRead<WorkflowJobPage>(null, FailureText(run));
        }
        var page = ParseWorkflowJobs(run.StdOut);
        return new ListRead<WorkflowJobPage>(page, page is null ? UnreadableResponse : "");
    }

    internal static List<string> BuildRunJobsArgs(string repoSlug, long runId) =>
        ["api", $"repos/{repoSlug}/actions/runs/{runId}/jobs?per_page={WorkflowJobLimit}"];

    internal static WorkflowJobPage? ParseWorkflowJobs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // The payload wraps the list: {"total_count":n,"jobs":[...]}. An error
            // payload is an object too, but carries no jobs array.
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("jobs", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return null;

            var jobs = new List<WorkflowJob>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var steps = new List<WorkflowStep>();
                if (el.TryGetProperty("steps", out var stepArr) && stepArr.ValueKind == JsonValueKind.Array)
                    foreach (var s in stepArr.EnumerateArray())
                        if (s.ValueKind == JsonValueKind.Object)
                            steps.Add(new WorkflowStep
                            {
                                Number = Int(s, "number") ?? 0,
                                Name = Str(s, "name"),
                                Status = Str(s, "status").ToLowerInvariant(),
                                Conclusion = Str(s, "conclusion").ToLowerInvariant()
                            });

                jobs.Add(new WorkflowJob
                {
                    Id = Long(el, "id") ?? 0,
                    Name = Str(el, "name"),
                    Status = Str(el, "status").ToLowerInvariant(),
                    Conclusion = Str(el, "conclusion").ToLowerInvariant(),
                    StartedAt = Date(el, "started_at"),
                    CompletedAt = Date(el, "completed_at"),
                    Steps = steps,
                    Url = Str(el, "html_url")
                });
            }
            return new WorkflowJobPage(jobs, Int(doc.RootElement, "total_count"), WorkflowJobLimit);
        }
        catch (Exception ex)
        {
            Log.Warn("gh api run jobs response unparseable", ex);
            return null;
        }
    }

    /// <summary>
    /// Every notification thread for one repo, unread only unless <paramref name="includeRead"/>.
    /// Paged to the end: the threads beyond a first page are the older ones, which are exactly the
    /// ones a reader would never otherwise meet. Null = fetch failed, never an empty inbox.
    /// </summary>
    public async Task<List<GitHubNotification>?> GetNotificationsAsync(string repoSlug,
        bool includeRead = false, CancellationToken ct = default)
    {
        var run = await RunAsync(BuildNotificationsArgs(repoSlug, includeRead), ct, PagedReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api notifications failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseNotifications(run.StdOut);
    }

    internal static List<string> BuildNotificationsArgs(string repoSlug, bool includeRead) =>
        BuildPagedApiArgs($"repos/{repoSlug}/notifications?all={(includeRead ? "true" : "false")}&per_page=100");

    internal static List<GitHubNotification>? ParseNotifications(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var notifications = new List<GitHubNotification>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var subject = el.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.Object
                    ? s : default;
                notifications.Add(new GitHubNotification
                {
                    ThreadId = Str(el, "id"),
                    Reason = Str(el, "reason").ToLowerInvariant(),
                    Unread = Bool(el, "unread"),
                    UpdatedAt = Date(el, "updated_at"),
                    Title = subject.ValueKind == JsonValueKind.Object ? Str(subject, "title") : "",
                    SubjectType = subject.ValueKind == JsonValueKind.Object ? Str(subject, "type") : "",
                    WebUrl = subject.ValueKind == JsonValueKind.Object
                        ? NotificationWebUrl(Str(subject, "url")) : ""
                });
            }
            return notifications;
        }
        catch (Exception ex)
        {
            Log.Warn("gh api notifications response unparseable", ex);
            return null;
        }
    }

    /// <summary>
    /// Browser URL for a notification subject, or "" when the REST url names no web page.
    /// The API url is the only link the payload carries and it is not navigable: it
    /// answers JSON, and its "pulls" collection is "pull" on the site. Anything that is
    /// not an exact three-segment issues/pulls resource under api.github.com maps to ""
    /// rather than a guess, so a crafted url cannot become a link somewhere else.
    /// </summary>
    internal static string NotificationWebUrl(string apiUrl)
    {
        const string prefix = "https://api.github.com/repos/";
        if (!apiUrl.StartsWith(prefix, StringComparison.Ordinal)) return "";
        var parts = apiUrl[prefix.Length..].Split('/');
        if (parts.Length != 4) return "";
        var (owner, name, collection, number) = (parts[0], parts[1], parts[2], parts[3]);
        if (!IsRepoPathSegment(owner) || !IsRepoPathSegment(name)) return "";
        if (number.Length == 0 || !number.All(char.IsAsciiDigit)) return "";
        var path = collection switch
        {
            "issues" => "issues",
            "pulls" => "pull",
            _ => "",
        };
        return path.Length == 0 ? "" : $"https://github.com/{owner}/{name}/{path}/{number}";
    }

    /// <summary>
    /// A GitHub owner or repository name. Dot-only segments are excluded: ".." in a
    /// crafted payload would resolve the composed link to a different page on the site.
    /// </summary>
    private static bool IsRepoPathSegment(string segment) =>
        segment.Length > 0 && segment.Trim('.').Length > 0 &&
        segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>Marks one thread read. Never automatic — the caller acts on an explicit request.</summary>
    public Task<ProcessResult> MarkNotificationReadAsync(string threadId, CancellationToken ct = default)
    {
        var what = $"gh api PATCH notifications/threads/{threadId}";
        return BuildMarkNotificationReadArgs(threadId) is { } args
            ? RunMutationAsync(what, args, ct: ct)
            : Task.FromResult(Refused(what, $"thread id '{threadId}' is not a notification id"));
    }

    /// <summary>
    /// Null for anything but digits: the id lands inside a REST path, where any other
    /// character could address a different endpoint entirely. The id comes off a gh
    /// payload, so this is a value the app can actually be handed.
    /// </summary>
    internal static List<string>? BuildMarkNotificationReadArgs(string threadId) =>
        threadId.Length > 0 && threadId.All(char.IsAsciiDigit)
            ? ["api", "--method", "PATCH", $"notifications/threads/{threadId}"]
            : null;

    /// <summary>Marks every thread on one repo read.</summary>
    public Task<ProcessResult> MarkRepoNotificationsReadAsync(string repoSlug, CancellationToken ct = default)
        => RunMutationAsync($"gh api PUT repos/{repoSlug}/notifications",
            BuildMarkRepoNotificationsReadArgs(repoSlug), ct: ct);

    internal static List<string> BuildMarkRepoNotificationsReadArgs(string repoSlug) =>
        ["api", "--method", "PUT", $"repos/{repoSlug}/notifications"];

    /// <summary>Repo settings for the Repo tab. Null = fetch failed.</summary>
    public async Task<RepoSettings?> GetRepoSettingsAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(
            ["repo", "view", repoSlug,
             "--json", "name,description,homepageUrl,repositoryTopics,visibility,isArchived,defaultBranchRef,parent," +
                       "hasIssuesEnabled,hasWikiEnabled,hasProjectsEnabled"],
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
                ParentSlug = parentSlug,
                HasIssues = BoolOrNull(el, "hasIssuesEnabled"),
                HasWiki = BoolOrNull(el, "hasWikiEnabled"),
                HasProjects = BoolOrNull(el, "hasProjectsEnabled")
            };
        }
        catch (Exception ex)
        {
            Log.Warn("gh repo view response unparseable", ex);
            return null;
        }
    }

    /// <summary>
    /// Every label defined on the repo. REST rather than `gh label list`, whose --limit is the
    /// only depth control it has: the labels feed a picker, and a picker missing the label a
    /// reader is looking for reads as a label the repository does not define. Null = fetch failed.
    /// </summary>
    public async Task<List<Label>?> GetLabelsAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(BuildLabelsArgs(repoSlug), ct, PagedReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api labels failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseLabels(run.StdOut);
    }

    internal static List<string> BuildLabelsArgs(string repoSlug) =>
        BuildPagedApiArgs($"repos/{repoSlug}/labels?per_page=100");

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

    /// <summary>
    /// Every milestone (open and closed). REST — gh has no milestone list command. Paged to the
    /// end on the same terms as the labels: these fill pickers, and a picker that stops short
    /// offers no sign that it did. Null = fetch failed.
    /// </summary>
    public async Task<List<Milestone>?> GetMilestonesAsync(string repoSlug, CancellationToken ct = default)
    {
        var run = await RunAsync(BuildMilestonesArgs(repoSlug), ct, PagedReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api milestones failed for {repoSlug}: {run.FirstError}");
            return null;
        }
        return ParseMilestones(run.StdOut);
    }

    internal static List<string> BuildMilestonesArgs(string repoSlug) =>
        BuildPagedApiArgs($"repos/{repoSlug}/milestones?state=all&per_page=100");

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

    /// <summary>Null for an absent or non-boolean flag — distinguishes "unread" from "off".</summary>
    private static bool? BoolOrNull(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : null;

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
        IReadOnlyList<string>? labels = null, string? milestone = null, CancellationToken ct = default)
        => RunMutationAsync($"gh issue create ({repoSlug})",
            BuildCreateIssueArgs(repoSlug, title, body, labels, milestone), ct: ct);

    /// <summary>
    /// <paramref name="milestone"/> is the milestone's title: <c>gh issue create</c> addresses a
    /// milestone by name only, unlike the list read, which takes a number.
    /// </summary>
    internal static List<string> BuildCreateIssueArgs(string repoSlug, string title, string body,
        IReadOnlyList<string>? labels, string? milestone = null)
    {
        // --body always present, even empty: without it gh falls into its interactive
        // prompt, which GH_PROMPT_DISABLED turns into a hard failure.
        var args = new List<string> { "issue", "create", "--repo", repoSlug, "--title", title, "--body", body };
        foreach (var label in labels ?? [])
        {
            args.Add("--label");
            args.Add(label);
        }
        if (!string.IsNullOrWhiteSpace(milestone)) args.AddRange(["--milestone", milestone]);
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
    {
        var what = $"gh pr merge #{number} ({repoSlug})";
        return BuildMergeArgs(repoSlug, number, strategy, deleteBranch) is { } args
            ? RunMutationAsync(what, args, ct: ct)
            : Task.FromResult(Refused(what, $"unknown merge strategy '{strategy}'"));
    }

    /// <summary>Null when <paramref name="strategy"/> is not a gh merge token.</summary>
    internal static List<string>? BuildMergeArgs(string repoSlug, int number, string strategy, bool deleteBranch)
    {
        var flag = strategy switch
        {
            "merge" => "--merge",
            "squash" => "--squash",
            "rebase" => "--rebase",
            _ => null
        };
        if (flag is null) return null;
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
    {
        var what = $"gh pr review #{number} ({repoSlug})";
        return BuildReviewArgs(repoSlug, number, action, body) is { } args
            ? RunMutationAsync(what, args, ct: ct)
            : Task.FromResult(Refused(what, $"unknown review action '{action}'"));
    }

    /// <summary>Null when <paramref name="action"/> is not a gh review token.</summary>
    internal static List<string>? BuildReviewArgs(string repoSlug, int number, string action, string body)
    {
        var flag = action switch
        {
            "approve" => "--approve",
            "requestChanges" or "request-changes" => "--request-changes",
            "comment" => "--comment",
            _ => null
        };
        if (flag is null) return null;
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
        string notes, bool draft = false, bool prerelease = false, string targetSha = "",
        CancellationToken ct = default)
    {
        // Notes travel via --notes-file: a command-line argument caps out (and mangles
        // quoting) long before real release notes do.
        var notesFile = Path.Combine(Path.GetTempPath(), $"pd-release-notes-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(notesFile, notes, ct);
            return await RunMutationAsync($"gh release create {tag}",
                BuildReleaseCreateArgs(tag, title, notesFile, draft, prerelease, targetSha), repoPath, ct: ct);
        }
        finally
        {
            try { File.Delete(notesFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// --target names the commit the tag is created from when the tag is not on the
    /// remote yet; GitHub ignores it when the tag already exists there. Passing the
    /// commit the local tag points at therefore pins the release to that commit in the
    /// unpushed case and changes nothing in the pushed case.
    /// </summary>
    internal static List<string> BuildReleaseCreateArgs(string tag, string title, string notesFile,
        bool draft, bool prerelease, string targetSha = "")
    {
        // --notes-file always present, even for empty notes: without any notes flag gh
        // falls into its interactive prompt, which GH_PROMPT_DISABLED turns into a failure.
        var args = new List<string> { "release", "create", tag, "--title", title, "--notes-file", notesFile };
        if (draft) args.Add("--draft");
        if (prerelease) args.Add("--prerelease");
        if (targetSha.Length > 0) { args.Add("--target"); args.Add(targetSha); }
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

    /// <summary>
    /// Writes one release asset to <paramref name="destinationPath"/>, replacing whatever
    /// is there. gh selects assets by glob and Go's matcher has no escape on Windows, so a
    /// name carrying glob metacharacters cannot be expressed as a pattern that matches
    /// itself: those fall back to fetching the release into a scratch directory and moving
    /// the exact name out of it, and that path refuses any name that is not a single path
    /// component. Both paths end with the asset at the requested path or a failed result.
    /// </summary>
    public async Task<ProcessResult> DownloadReleaseAssetAsync(string repoSlug, string tag, string assetName,
        string destinationPath, CancellationToken ct = default)
    {
        if (!NeedsFullReleaseFetch(assetName))
            return await RunMutationAsync($"gh release download {tag} ({assetName})",
                BuildAssetDownloadArgs(repoSlug, tag, assetName, destinationPath), timeout: LogFetchTimeout, ct: ct);

        if (!IsPlainAssetFileName(assetName))
            return new ProcessResult(1, "", $"{assetName} is not a plain asset file name — not downloading it.",
                TimedOut: false);

        var scratch = Path.Combine(Path.GetTempPath(), $"pd-release-asset-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(scratch);
            var fetch = await RunMutationAsync($"gh release download {tag} (whole release)",
                BuildReleaseDirDownloadArgs(repoSlug, tag, scratch), timeout: LogFetchTimeout, ct: ct);
            if (!fetch.Success) return fetch;

            var fetched = Path.Combine(scratch, assetName);
            if (!File.Exists(fetched))
                return new ProcessResult(1, "", $"{assetName} was not among the release's downloaded assets.", TimedOut: false);

            File.Move(fetched, destinationPath, overwrite: true);
            return fetch;
        }
        catch (Exception ex)
        {
            Log.Warn($"gh release download {tag} ({assetName}) failed for {repoSlug}", ex);
            return new ProcessResult(1, "", ex.Message, TimedOut: false);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// True when the asset name contains a glob metacharacter, which no gh --pattern can
    /// match literally: `[1]` selects the character 1, never the three characters typed.
    /// </summary>
    internal static bool NeedsFullReleaseFetch(string assetName) =>
        assetName.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    /// <summary>
    /// Whether the name is usable as a single path component. The name comes from the
    /// release payload and the glob-fallback path combines it with a scratch directory:
    /// a rooted or traversing name resolves outside that directory, and the move out of
    /// it would relocate an unrelated local file to the reader's chosen destination.
    /// </summary>
    internal static bool IsPlainAssetFileName(string assetName) =>
        assetName.Length > 0 && Path.GetFileName(assetName) == assetName;

    internal static List<string> BuildAssetDownloadArgs(string repoSlug, string tag, string assetName, string destinationPath) =>
        ["release", "download", tag, "--repo", repoSlug, "--pattern", assetName,
         "--output", destinationPath, "--clobber"];

    internal static List<string> BuildReleaseDirDownloadArgs(string repoSlug, string tag, string directory) =>
        ["release", "download", tag, "--repo", repoSlug, "--dir", directory, "--clobber"];

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
    ///
    /// Whether the cap fired travels beside the text rather than only inside it: a caller that
    /// had to find the marker in the log would be reading content to learn a fact about the read,
    /// and a search or a saved copy of a capped log is a partial answer its reader has to be told
    /// about. The marker stays in the text too, so a copy that leaves the app carries the
    /// disclosure with it.
    /// </summary>
    public async Task<WorkflowRunLog?> GetWorkflowRunLogAsync(string repoSlug, long runId,
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
                if (capped) return CappedLog(capture, maxBytes);
            }
            Log.Warn($"gh run view --log {runId} canceled unexpectedly for {repoSlug}");
            return null;
        }

        // The child can exit between the cap firing and the cancel landing — the cap
        // still holds: never hand back the oversized full capture.
        lock (gate)
        {
            if (capped) return CappedLog(capture, maxBytes);
        }

        if (!result.Success)
        {
            Log.Warn($"gh run view --log {runId} failed for {repoSlug}: {result.FirstError}");
            return null;
        }
        // The runner's own capture budget can only cut a log this method's cap did not reach
        // first, which takes a maxBytes above it; the bound disclosed is then the one that
        // actually applied rather than the one that was asked for.
        return result.Truncated
            ? CappedLog(new System.Text.StringBuilder(result.StdOut), ProcessRunner.DefaultCaptureCharBudget)
            : new WorkflowRunLog(result.StdOut, Truncated: false, Cap: maxBytes);
    }

    private static WorkflowRunLog CappedLog(System.Text.StringBuilder capture, int cap) =>
        new(capture.AppendLine(TruncationMarker(cap)).ToString(), Truncated: true, Cap: cap);

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

    /// <summary>
    /// Renames the repository on GitHub. The --repo form leaves every local remote alone: gh
    /// rewrites a clone's remote URL only when it renames the repository it was invoked inside.
    /// </summary>
    public Task<ProcessResult> RenameRepoAsync(string repoSlug, string newName, CancellationToken ct = default)
        => RunMutationAsync($"gh repo rename ({repoSlug} -> {newName})",
            ["repo", "rename", newName, "--repo", repoSlug, "--yes"], ct: ct);

    /// <summary>
    /// True when a rename failed because the owner already holds a repository under the new
    /// name. GitHub answers that with a field error naming the collision, which gh passes
    /// through verbatim; every other failure keeps the server's own text, the only sentence
    /// that says what to do about it.
    /// </summary>
    internal static bool IsRepoNameTaken(string error) =>
        error.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Older gh has no --accept-visibility-change-consequences flag; there the call comes
    /// back as a failed result (unknown flag), never a crash.
    /// </summary>
    public Task<ProcessResult> SetRepoVisibilityAsync(string repoSlug, string visibility, CancellationToken ct = default)
    {
        var what = $"gh repo edit --visibility {visibility} ({repoSlug})";
        return BuildVisibilityArgs(repoSlug, visibility) is { } args
            ? RunMutationAsync(what, args, ct: ct)
            : Task.FromResult(Refused(what, $"unknown visibility '{visibility}'"));
    }

    /// <summary>Null when <paramref name="visibility"/> is not a gh visibility token.</summary>
    internal static List<string>? BuildVisibilityArgs(string repoSlug, string visibility) =>
        visibility is "public" or "private" or "internal"
            ? ["repo", "edit", repoSlug, "--visibility", visibility, "--accept-visibility-change-consequences"]
            : null;

    /// <summary>
    /// Repoints HEAD on the remote. The branch must already exist there; gh reports an
    /// unknown branch as a failed result.
    /// </summary>
    public Task<ProcessResult> SetDefaultBranchAsync(string repoSlug, string branch, CancellationToken ct = default)
    {
        var what = $"gh repo edit --default-branch {branch} ({repoSlug})";
        return BuildDefaultBranchArgs(repoSlug, branch) is { } args
            ? RunMutationAsync(what, args, ct: ct)
            : Task.FromResult(Refused(what, "default branch cannot be blank"));
    }

    /// <summary>
    /// Null for a blank branch: it reaches gh as a flag with a missing argument, which
    /// consumes the next token instead of failing.
    /// </summary>
    internal static List<string>? BuildDefaultBranchArgs(string repoSlug, string branch) =>
        branch.Trim().Length == 0 ? null : ["repo", "edit", repoSlug, "--default-branch", branch];

    /// <summary>Null for a feature means "leave unchanged".</summary>
    public Task<ProcessResult> SetRepoFeaturesAsync(string repoSlug, bool? issues = null, bool? wiki = null,
        bool? projects = null, CancellationToken ct = default)
    {
        var args = BuildRepoFeatureArgs(repoSlug, issues, wiki, projects);
        // A flagless `gh repo edit` is an interactive prompt (hard failure) — skip the spawn.
        return args.Count == 3
            ? Task.FromResult(NoOpSuccess())
            : RunMutationAsync($"gh repo edit features ({repoSlug})", args, ct: ct);
    }

    internal static List<string> BuildRepoFeatureArgs(string repoSlug, bool? issues, bool? wiki, bool? projects)
    {
        var args = new List<string> { "repo", "edit", repoSlug };
        // gh's feature switches are boolean flags: the value has to ride on the same
        // token, because a bare --enable-issues means true and never false.
        if (issues is { } i) args.Add($"--enable-issues={FlagValue(i)}");
        if (wiki is { } w) args.Add($"--enable-wiki={FlagValue(w)}");
        if (projects is { } p) args.Add($"--enable-projects={FlagValue(p)}");
        return args;
    }

    private static string FlagValue(bool value) => value ? "true" : "false";

    /// <summary>
    /// Irreversible on GitHub, and nothing here touches the local clone: the working copy
    /// and its origin remote survive a repository that no longer exists. Callers gate this
    /// behind the danger-zone setting and a typed repo-name confirmation.
    /// </summary>
    public Task<ProcessResult> DeleteRepoAsync(string repoSlug, CancellationToken ct = default)
        => RunMutationAsync($"gh repo delete ({repoSlug})", ["repo", "delete", repoSlug, "--yes"], ct: ct);

    /// <summary>
    /// True when a delete failed for want of the delete_repo scope rather than for want of
    /// rights. gh names the scope and the refresh command in that error and in no other.
    /// </summary>
    internal static bool NeedsDeleteRepoScope(string error) =>
        error.Contains("delete_repo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shown verbatim when a delete fails for the missing scope.</summary>
    public const string DeleteRepoScopeInstructions =
        "The signed-in GitHub CLI lacks the delete_repo scope. Grant it, then retry the delete:\n\n" +
        "gh auth refresh -h github.com -s delete_repo";

    /// <summary>
    /// Launches the delete_repo scope grant interactively in its own console, the same
    /// way sign-in is delegated. Returns the process, or null if gh couldn't be started.
    /// </summary>
    public Process? StartInteractiveDeleteScopeGrant()
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = ResolveGhExe(),
                Arguments = "auth refresh -h github.com -s delete_repo",
                UseShellExecute = true   // give gh a real console for its interactive prompts
            });
        }
        catch (Exception ex)
        {
            Log.Warn("gh auth refresh could not be launched", ex);
            return null;
        }
    }

    public Task<ProcessResult> ArchiveRepoAsync(string repoSlug, CancellationToken ct = default)
        => RunMutationAsync($"gh repo archive ({repoSlug})", ["repo", "archive", repoSlug, "--yes"], ct: ct);

    public Task<ProcessResult> UnarchiveRepoAsync(string repoSlug, CancellationToken ct = default)
        => RunMutationAsync($"gh repo unarchive ({repoSlug})", ["repo", "unarchive", repoSlug, "--yes"], ct: ct);

    /// <summary>
    /// Syncs this clone's copy of the parent's default branch. gh runs the destination side
    /// against the working copy — a fast-forward moves the local branch and, when it is checked
    /// out, the working tree with it — so callers hold the repository lease for the call.
    /// With <paramref name="force"/> the branch is hard-reset onto the parent, discarding every
    /// commit the fork has and the parent does not; without it gh refuses a diverged branch.
    /// </summary>
    public Task<ProcessResult> SyncForkAsync(string repoPath, bool force = false, CancellationToken ct = default)
        => RunMutationAsync($"gh repo sync{(force ? " --force" : "")}", BuildSyncForkArgs(force), repoPath, ct: ct);

    internal static List<string> BuildSyncForkArgs(bool force) =>
        force ? ["repo", "sync", "--force"] : ["repo", "sync"];

    /// <summary>
    /// True when a sync refused because the local branch carries commits the parent does not.
    /// gh names the diverging changes and the --force flag in that refusal and in no other, and
    /// a generic failure line would read as an outage rather than as work a retry would discard.
    /// </summary>
    internal static bool IsForkSyncDiverged(string error) =>
        error.Contains("diverging changes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a sync refused over the working tree rather than over history: gh will not
    /// move a checked-out branch under uncommitted or untracked changes.
    /// </summary>
    internal static bool IsForkSyncDirtyWorkingTree(string error) =>
        error.Contains("uncommitted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ahead/behind of a fork's branch against the same branch on its parent. Null when the
    /// comparison did not answer — an unresolvable branch, a repository outside the parent's
    /// network, an unparseable body, or any gh failure.
    /// </summary>
    public async Task<ForkDivergence?> GetForkDivergenceAsync(string parentSlug, string parentOwner,
        string forkOwner, string branch, CancellationToken ct = default)
    {
        if (BuildForkCompareArgs(parentSlug, parentOwner, forkOwner, branch) is not { } args)
        {
            Log.Warn($"fork divergence not read: {forkOwner}:{branch} against {parentSlug} is incompletely named");
            return null;
        }
        var run = await RunAsync(args, ct, ReadTimeout);
        if (!run.Success || string.IsNullOrWhiteSpace(run.StdOut))
        {
            Log.Warn($"gh api compare failed for {parentSlug}: {run.FirstError}");
            return null;
        }
        return ParseForkDivergence(run.StdOut);
    }

    /// <summary>
    /// Null when any part of the comparison is missing. Both sides carry an owner prefix: an
    /// unqualified head resolves inside the base repository, which compares the parent with
    /// itself and answers zero for every fork.
    /// </summary>
    internal static List<string>? BuildForkCompareArgs(string parentSlug, string parentOwner,
        string forkOwner, string branch) =>
        parentSlug.Length == 0 || parentOwner.Length == 0 || forkOwner.Length == 0 || branch.Length == 0
            ? null
            : ["api", $"repos/{parentSlug}/compare/{parentOwner}:{branch}...{forkOwner}:{branch}"];

    /// <summary>
    /// ahead_by counts commits the fork's branch has and the parent's does not; behind_by counts
    /// the reverse. A response missing either field is not a zero — nothing was measured.
    /// </summary>
    internal static ForkDivergence? ParseForkDivergence(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            if (el.ValueKind != JsonValueKind.Object) return null;
            return Int(el, "ahead_by") is { } ahead && Int(el, "behind_by") is { } behind
                ? new ForkDivergence(ahead, behind)
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn("gh api compare response unparseable", ex);
            return null;
        }
    }

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
    /// A mutation refused before it spawned, in the same failed-result shape every other
    /// refusal on this service uses. A token the argument builder cannot map is a caller
    /// error the enum-bound UI cannot produce, but a throw from a mutation would be the
    /// one failure the callers' result handling does not cover — it would leave the busy
    /// gate's owner unwinding through an exception path instead of a toast.
    /// </summary>
    private static ProcessResult Refused(string what, string reason)
    {
        Log.Warn($"{what} refused: {reason}");
        return new ProcessResult(1, "", reason, TimedOut: false);
    }

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
