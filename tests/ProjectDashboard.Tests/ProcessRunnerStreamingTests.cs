using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

public sealed class ProcessRunnerStreamingTests
{
    static ProcessRunnerStreamingTests()
    {
        // Log writes route into a disposable sandbox; without it, launch-failure and
        // callback-failure paths append to the real profile log. The fallback lives
        // under the per-run fixture root so process-exit cleanup removes it.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PD_DATA_DIR")))
            Environment.SetEnvironmentVariable(
                "PD_DATA_DIR", Path.Combine(TestEnv.Root, "app-data"));
    }

    private const string Pwsh = "powershell.exe";

    private static string[] Ps(string script) => new[] { "-NoProfile", "-Command", script };

    [Fact]
    public async Task InterleavedStreams_DeliverLinesInPerStreamOrder_AndStillCaptureEverything()
    {
        var outLines = new ConcurrentQueue<string>();
        var errLines = new ConcurrentQueue<string>();

        var result = await ProcessRunner.RunStreamingAsync(
            Pwsh,
            Ps("1..40 | ForEach-Object { [Console]::Out.WriteLine(\"out $_\"); [Console]::Error.WriteLine(\"err $_\") }"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            onStdOutLine: outLines.Enqueue,
            onStdErrLine: errLines.Enqueue);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => $"out {i}").ToArray(), outLines.ToArray());
        Assert.Equal(Enumerable.Range(1, 40).Select(i => $"err {i}").ToArray(), errLines.ToArray());

        // Streaming is additive: the result still carries the full capture.
        Assert.Contains("out 1", result.StdOut);
        Assert.Contains("out 40", result.StdOut);
        Assert.Contains("err 1", result.StdErr);
        Assert.Contains("err 40", result.StdErr);
    }

    [Fact]
    public async Task BareCrSplitCrlfAndUnterminatedTail_ScanAsLines_CaptureStaysRaw()
    {
        var received = new ConcurrentQueue<string>();

        var result = await ProcessRunner.RunStreamingAsync(
            Pwsh,
            Ps("[Console]::Out.Write(\"p1`rp2`rp3`r`ndone`ntail\")"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            onStdOutLine: received.Enqueue,
            onStdErrLine: null);

        Assert.True(result.Success, result.FirstError);

        // A bare CR terminates a line, the LF of a CRLF pair does not fire a second
        // empty line, and the unterminated tail still flushes at end of stream.
        Assert.Equal(new[] { "p1", "p2", "p3", "done", "tail" }, received.ToArray());

        // The capture is the raw stream, line endings untouched.
        Assert.Equal("p1\rp2\rp3\r\ndone\ntail", result.StdOut);
    }

    [Fact]
    public async Task LargeStdErrFlood_CompletesWithoutTimeout()
    {
        // A full, unread stderr pipe blocks the child forever; 256 KB exceeds any pipe buffer.
        var lineCount = 0;

        var result = await ProcessRunner.RunStreamingAsync(
            Pwsh,
            Ps("$chunk = 'e' * 8192; 1..32 | ForEach-Object { [Console]::Error.WriteLine($chunk) }"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            onStdOutLine: null,
            onStdErrLine: _ => Interlocked.Increment(ref lineCount));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StdErr.Length >= 256 * 1024, $"captured only {result.StdErr.Length} chars");
        Assert.Equal(32, lineCount);
    }

    [Fact]
    public async Task Timeout_KillsMidStream_ReturnsTimedOutWithPartialOutput()
    {
        var received = new ConcurrentQueue<string>();
        var sw = Stopwatch.StartNew();

        var result = await ProcessRunner.RunStreamingAsync(
            Pwsh,
            Ps("[Console]::Out.WriteLine('early'); [Console]::Out.Flush(); Start-Sleep -Seconds 120; [Console]::Out.WriteLine('late')"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(10),
            environment: null,
            onStdOutLine: received.Enqueue,
            onStdErrLine: null);

        sw.Stop();
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.Contains("early", result.StdOut);
        Assert.DoesNotContain("late", result.StdOut);
        Assert.Contains("early", received);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}; the child outlived the kill");
    }

    [Fact]
    public async Task Cancellation_MidStream_KillsChildAndThrows()
    {
        using var cts = new CancellationTokenSource();
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();

        var task = ProcessRunner.RunStreamingAsync(
            Pwsh,
            Ps("[Console]::Out.WriteLine('spinning'); [Console]::Out.Flush(); Start-Sleep -Seconds 120"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(120),
            environment: null,
            onStdOutLine: _ => firstLine.TrySetResult(),
            onStdErrLine: null,
            ct: cts.Token);

        // The callback fires while the child is still sleeping — live delivery, not
        // end-of-run replay. Cancelling only after it proves cancellation mid-stream.
        await firstLine.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}; the child outlived the cancel");
    }

    [Fact]
    public async Task MissingExecutable_ReturnsFailedResultWithoutThrowingOrStreaming()
    {
        var callbackFired = false;

        var result = await ProcessRunner.RunStreamingAsync(
            "definitely-not-a-real-tool-8f3a.exe",
            new[] { "--version" },
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(10),
            environment: null,
            onStdOutLine: _ => callbackFired = true,
            onStdErrLine: _ => callbackFired = true);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotEqual("", result.StdErr);
        Assert.False(callbackFired);
    }

    [Fact]
    public async Task ThrowingCallback_DoesNotPreventCompletionOrCapture()
    {
        var result = await ProcessRunner.RunStreamingAsync(
            Pwsh,
            Ps("1..100 | ForEach-Object { [Console]::Out.WriteLine(\"line $_\") }"),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(60),
            environment: null,
            onStdOutLine: _ => throw new InvalidOperationException("subscriber fault"),
            onStdErrLine: null);

        Assert.True(result.Success, result.FirstError);
        Assert.False(result.TimedOut);
        Assert.Contains("line 1", result.StdOut);
        Assert.Contains("line 100", result.StdOut);
    }
}
