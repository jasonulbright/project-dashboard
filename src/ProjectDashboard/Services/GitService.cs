using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class GitService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Environment for every git call: never prompt for credentials (a windowless app
    /// would hang invisibly), never take optional index locks during reads.
    /// </summary>
    private static readonly Dictionary<string, string> GitEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0"
    };

    /// <summary>
    /// True when the directory is a git checkout. A primary checkout has a .git
    /// DIRECTORY; a linked worktree or submodule has a .git FILE — accept both.
    /// </summary>
    public static bool IsGitRepo(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    public async Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        var status = new GitStatus();

        // Dirty / modified / untracked — THE critical signal. `status --porcelain` works even on a
        // repo with no commits yet, so only a real failure here means "status unknown".
        try
        {
            var porcelain = await RunGitAsync(repoPath, ["status", "--porcelain"], ct);
            if (!string.IsNullOrWhiteSpace(porcelain))
            {
                status.IsDirty = true;
                foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 2) continue;
                    if (line.StartsWith("??"))
                        status.UntrackedCount++;
                    else
                        status.ModifiedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            // git missing / stale PATH / broken repo — must NOT masquerade as a clean repo.
            status.HasError = true;
            Log.Warn($"git status failed for {repoPath}", ex);
            return status;
        }

        // Everything below is best-effort metadata. A fresh repo with no commits/tags/remote/upstream
        // is normal and must not blank out the dirty signal above.
        try { status.Branch = (await RunGitAsync(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"], ct)).Trim(); }
        catch { /* no commits yet */ }

        try { status.LatestTag = (await RunGitAsync(repoPath, ["describe", "--tags", "--abbrev=0"], ct)).Trim(); }
        catch { /* no tags */ }

        try
        {
            var logLine = await RunGitAsync(repoPath, ["log", "-1", "--format=%aI|%s"], ct);
            if (!string.IsNullOrWhiteSpace(logLine))
            {
                var parts = logLine.Trim().Split('|', 2);
                if (parts.Length >= 1 && DateTimeOffset.TryParse(parts[0], out var date))
                    status.LastCommitDate = date;
                if (parts.Length >= 2)
                    status.LastCommitMessage = parts[1];
            }
        }
        catch { /* no commits yet */ }

        try { status.RemoteUrl = (await RunGitAsync(repoPath, ["config", "--get", "remote.origin.url"], ct)).Trim(); }
        catch { /* no remote */ }

        try
        {
            // "<behind>\t<ahead>" relative to upstream, in one call.
            var counts = await RunGitAsync(repoPath, ["rev-list", "--left-right", "--count", "@{u}...HEAD"], ct);
            var parts = counts.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                if (int.TryParse(parts[0], out var behind)) status.BehindBy = behind;
                if (int.TryParse(parts[1], out var ahead)) status.AheadBy = ahead;
            }
        }
        catch { /* no upstream */ }

        return status;
    }

    public async Task<List<GitCommit>> GetRecentCommitsAsync(string repoPath, int count = 20, CancellationToken ct = default)
    {
        var commits = new List<GitCommit>();

        try
        {
            var output = await RunGitAsync(repoPath, ["log", $"--format=%h|%an|%aI|%s", "-n", count.ToString()], ct);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 4);
                if (parts.Length < 4) continue;

                commits.Add(new GitCommit
                {
                    ShortHash = parts[0],
                    Author = parts[1],
                    Date = DateTimeOffset.TryParse(parts[2], out var d) ? d : default,
                    Message = parts[3]
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"git log failed for {repoPath}", ex);
        }

        return commits;
    }

    /// <summary>Init + stage + first commit for New Project. Returns null on success, else a short error.</summary>
    public async Task<string?> InitWithFirstCommitAsync(string repoPath, string commitMessage, CancellationToken ct = default)
    {
        var init = await RunAsync(repoPath, ["init"], ct);
        if (!init.Success) return $"git init failed: {init.FirstError}";

        var add = await RunAsync(repoPath, ["add", "-A"], ct);
        if (!add.Success) return $"git add failed: {add.FirstError}";

        var commit = await RunAsync(repoPath, ["commit", "-m", commitMessage], ct);
        if (!commit.Success) return $"git commit failed: {commit.FirstError}";

        return null;
    }

    /// <summary>Structured run for callers that need exit codes and stderr (no throw on failure).</summary>
    public async Task<ProcessResult> RunAsync(string repoPath, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        // core.quotepath=false: unicode paths arrive as UTF-8, not octal escapes.
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);
        return await ProcessRunner.RunAsync(ResolveGitExe(), full, repoPath, timeout ?? Timeout, GitEnvironment, ct);
    }

    /// <summary>String-result run that throws on non-zero exit (legacy shape for simple reads).</summary>
    private async Task<string> RunGitAsync(string workingDir, IEnumerable<string> args, CancellationToken ct)
    {
        var result = await RunAsync(workingDir, args, ct);
        if (result.TimedOut)
            throw new TimeoutException($"git timed out in {workingDir}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git failed ({result.ExitCode}): {result.FirstError}");
        return result.StdOut;
    }

    /// <summary>Resolve git: known install dirs first (survives a stale Start-Menu PATH), then PATH.</summary>
    private static string ResolveGitExe()
    {
        string[] known =
        [
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)", "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("LocalAppData") ?? "", "Programs", "Git", "cmd", "git.exe"),
        ];
        foreach (var p in known)
            if (p.Length > 0 && File.Exists(p)) return p;
        return "git"; // last resort: PATH
    }
}
