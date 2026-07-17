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

    public async Task<string> GetRepoVisibilityAsync(string repoSlug, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                ["repo", "view", repoSlug, "--json", "visibility", "--jq", ".visibility"], ct);
            var vis = output?.Trim().ToLowerInvariant() ?? "";
            // "unknown" (not "local") when a remote exists but gh couldn't determine visibility —
            // don't conflate a gh failure with a genuinely remote-less repo.
            return vis is "public" or "private" or "internal" ? vis : "unknown";
        }
        catch (Exception ex)
        {
            Log.Warn($"gh repo view failed for {repoSlug}", ex);
            return "unknown";
        }
    }

    public async Task<int> GetOpenIssueCountAsync(string repoSlug, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                ["issue", "list", "--repo", repoSlug, "--state", "open", "--json", "number", "--limit", "100"], ct);

            if (string.IsNullOrWhiteSpace(output))
                return 0;

            var items = JsonSerializer.Deserialize<List<JsonElement>>(output);
            return items?.Count ?? 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"gh issue count failed for {repoSlug} (showing 0)", ex);
            return 0;
        }
    }

    public async Task<int> GetOpenPrCountAsync(string repoSlug, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                ["pr", "list", "--repo", repoSlug, "--state", "open", "--json", "number", "--limit", "100"], ct);

            if (string.IsNullOrWhiteSpace(output))
                return 0;

            var items = JsonSerializer.Deserialize<List<JsonElement>>(output);
            return items?.Count ?? 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"gh pr count failed for {repoSlug} (showing 0)", ex);
            return 0;
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
