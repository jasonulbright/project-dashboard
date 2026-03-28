using System.Diagnostics;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class GitHubService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var exitCode = await RunGhExitCodeAsync("auth status", ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<GitHubIssue>> GetIssuesAsync(string repoSlug, string state = "open", CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                $"issue list --repo {repoSlug} --state {state} --json number,title,state,createdAt --limit 50", ct);

            if (string.IsNullOrWhiteSpace(output))
                return [];

            var issues = JsonSerializer.Deserialize<List<GitHubIssue>>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return issues ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<int> GetOpenIssueCountAsync(string repoSlug, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGhAsync(
                $"issue list --repo {repoSlug} --state open --json number --limit 100", ct);

            if (string.IsNullOrWhiteSpace(output))
                return 0;

            var items = JsonSerializer.Deserialize<List<JsonElement>>(output);
            return items?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<string> RunGhAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(Timeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"gh {arguments} timed out");
        }

        return await outputTask;
    }

    private static async Task<int> RunGhExitCodeAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(Timeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return -1;
        }

        return process.ExitCode;
    }
}
