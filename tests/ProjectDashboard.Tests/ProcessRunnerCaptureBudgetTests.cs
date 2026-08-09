using System;
using System.IO;
using System.Threading.Tasks;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// A child's output is captured into memory, so a runaway child is a memory bound the app has
/// to own. The budget caps what is retained without ever stopping the drain — a pipe left
/// unread blocks the child — and the result says the capture is a prefix rather than passing
/// silently partial content to a parser.
/// </summary>
public sealed class ProcessRunnerCaptureBudgetTests
{
    static ProcessRunnerCaptureBudgetTests()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PD_DATA_DIR")))
            Environment.SetEnvironmentVariable(
                "PD_DATA_DIR", Path.Combine(TestEnv.Root, "app-data"));
    }

    private const string Pwsh = "powershell.exe";

    private static string[] Ps(string script) => new[] { "-NoProfile", "-Command", script };

    [Fact]
    public async Task OutputPastTheBudget_StopsAtItAndIsReportedTruncated()
    {
        // 256 KB against a 64 KB budget. The tail marker proves the child ran to completion:
        // the surplus was read and dropped, not left to fill the pipe and block it.
        var result = await ProcessRunner.RunAsync(
            Pwsh,
            Ps("$chunk = 'o' * 8192; 1..32 | ForEach-Object { [Console]::Out.Write($chunk) }; [Console]::Out.Write('TAIL')"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            ct: default,
            captureCharBudget: 64 * 1024);

        Assert.True(result.Success, result.FirstError);
        Assert.False(result.TimedOut);
        Assert.True(result.Truncated);
        Assert.Equal(64 * 1024, result.StdOut.Length);
        Assert.DoesNotContain("TAIL", result.StdOut);
        Assert.True(result.OutputChars >= 256 * 1024 + 4, $"counted only {result.OutputChars}");
    }

    [Fact]
    public async Task StdErrPastTheBudget_IsBoundedOnItsOwnStream()
    {
        var result = await ProcessRunner.RunAsync(
            Pwsh,
            Ps("$chunk = 'e' * 8192; 1..32 | ForEach-Object { [Console]::Error.Write($chunk) }"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            ct: default,
            captureCharBudget: 64 * 1024);

        Assert.True(result.Truncated);
        Assert.Equal(64 * 1024, result.StdErr.Length);
        Assert.Equal("", result.StdOut);
    }

    [Fact]
    public async Task OutputUnderTheBudget_IsWholeAndNotFlagged()
    {
        var result = await ProcessRunner.RunAsync(
            Pwsh,
            Ps("[Console]::Out.Write('short output')"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            ct: default,
            captureCharBudget: 64 * 1024);

        Assert.True(result.Success, result.FirstError);
        Assert.False(result.Truncated);
        Assert.Equal("short output", result.StdOut);
        Assert.Equal("short output".Length, result.OutputChars);
    }

    [Fact]
    public async Task TheDefaultBudget_LeavesOrdinaryOutputUntouched()
    {
        var result = await ProcessRunner.RunAsync(
            Pwsh,
            Ps("$chunk = 'd' * 8192; 1..64 | ForEach-Object { [Console]::Out.Write($chunk) }"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60));

        Assert.True(result.Success, result.FirstError);
        Assert.False(result.Truncated);
        Assert.Equal(512 * 1024, result.StdOut.Length);
    }
}
