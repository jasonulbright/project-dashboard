using System.Diagnostics;
using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class GitService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public Task<bool> IsGitRepoAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(Directory.Exists(Path.Combine(path, ".git")));
    }

    public async Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        var status = new GitStatus();

        try
        {
            // Branch
            var branchOutput = await RunGitAsync(repoPath, "rev-parse --abbrev-ref HEAD", ct);
            status.Branch = branchOutput.Trim();

            // Dirty / modified / untracked
            var porcelain = await RunGitAsync(repoPath, "status --porcelain", ct);
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

            // Latest tag
            try
            {
                var tag = await RunGitAsync(repoPath, "describe --tags --abbrev=0", ct);
                status.LatestTag = tag.Trim();
            }
            catch { /* no tags */ }

            // Last commit
            var logLine = await RunGitAsync(repoPath, "log -1 --format=%aI|%s", ct);
            if (!string.IsNullOrWhiteSpace(logLine))
            {
                var parts = logLine.Trim().Split('|', 2);
                if (parts.Length >= 1 && DateTimeOffset.TryParse(parts[0], out var date))
                    status.LastCommitDate = date;
                if (parts.Length >= 2)
                    status.LastCommitMessage = parts[1];
            }

            // Remote URL
            try
            {
                var remote = await RunGitAsync(repoPath, "config --get remote.origin.url", ct);
                status.RemoteUrl = remote.Trim();
            }
            catch { /* no remote */ }

            // Ahead count
            try
            {
                var ahead = await RunGitAsync(repoPath, "rev-list --count @{u}..HEAD", ct);
                if (int.TryParse(ahead.Trim(), out var count))
                    status.AheadBy = count;
            }
            catch { /* no upstream */ }
        }
        catch
        {
            // Return whatever we have so far
        }

        return status;
    }

    public async Task<List<GitCommit>> GetRecentCommitsAsync(string repoPath, int count = 20, CancellationToken ct = default)
    {
        var commits = new List<GitCommit>();

        try
        {
            var output = await RunGitAsync(repoPath, $"log --format=%h|%an|%aI|%s -n {count}", ct);
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
        catch
        {
            // Return empty list on error
        }

        return commits;
    }

    private static async Task<string> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);

        // Race between process exit and timeout
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(Timeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {arguments} timed out");
        }

        var output = await outputTask;

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        }

        return output;
    }
}
