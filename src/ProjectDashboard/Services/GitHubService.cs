using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class GitHubService(SettingsService settingsService)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
                    if (!data.TryGetProperty($"r{i}", out var repo)) continue;
                    if (repo.ValueKind == JsonValueKind.Null)
                    {
                        // Alias errored (repo missing / no access): known-not-found.
                        results[chunk[i]] = new RepoRemoteData("unknown", null, null, Found: false);
                        continue;
                    }
                    var vis = repo.TryGetProperty("visibility", out var v) ? v.GetString()?.ToLowerInvariant() ?? "unknown" : "unknown";
                    int? issues = repo.TryGetProperty("issues", out var iss) && iss.TryGetProperty("totalCount", out var ic) ? ic.GetInt32() : null;
                    int? prs = repo.TryGetProperty("pullRequests", out var pr) && pr.TryGetProperty("totalCount", out var pc) ? pc.GetInt32() : null;
                    results[chunk[i]] = new RepoRemoteData(vis, issues, prs, Found: true);
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
                ["issue", "list", "--repo", repoSlug, "--state", state, "--json", "number,title,state,createdAt", "--limit", "50"], ct);

            if (string.IsNullOrWhiteSpace(output))
                return [];

            var issues = JsonSerializer.Deserialize<List<GitHubIssue>>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return issues ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"gh issue list failed for {repoSlug} (showing 0 issues)", ex);
            return [];
        }
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
