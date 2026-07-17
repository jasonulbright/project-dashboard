using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProjectDashboard.Services;

/// <summary>Outcome of one subprocess run. Non-zero exit is data, not an exception.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>First non-empty stderr line, else stdout line — for compact UI/log messages.</summary>
    public string FirstError
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
            foreach (var line in source.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t;
            }
            return TimedOut ? "timed out" : $"exit code {ExitCode}";
        }
    }
}

/// <summary>
/// The one place a child process is spawned. Guarantees that hung or chatty children
/// cannot hang us: both pipes are drained concurrently from the start (a full, unread
/// stderr pipe blocks the child forever), output decodes as UTF-8 regardless of the
/// system codepage (git/gh emit UTF-8; a windowless WPF app otherwise decodes with the
/// ANSI codepage — verified mojibake), arguments pass via ArgumentList (no quoting bugs),
/// and timeout/cancellation kill the whole process tree then reap the reads.
/// </summary>
public static class ProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment)
                process.StartInfo.Environment[key] = value;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Executable unresolvable / working dir gone. Return a failed result instead of
            // throwing — one unlaunchable repo must not fault a whole parallel discovery.
            Log.Warn($"could not start {fileName}", ex);
            return new ProcessResult(-1, "", ex.Message, TimedOut: false);
        }

        // Drain both pipes from the start — never let either fill.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* reaped or stuck */ }
        }

        // Kill closes the pipes, so these normally complete promptly — but a descendant that
        // escaped the kill snapshot can keep the handles open. Bound the drain so a runaway
        // grandchild can never wedge a discovery slot; whatever was read so far is returned.
        string stdOut = "", stdErr = "";
        try
        {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(TimeSpan.FromSeconds(timedOut ? 5 : 30));
            stdOut = stdOutTask.Result;
            stdErr = stdErrTask.Result;
        }
        catch (TimeoutException)
        {
            timedOut = true;
            Log.Warn($"Abandoned pipe drain for {fileName} — a descendant process is holding the output handles");
        }

        // Distinguish caller cancellation from a genuine timeout.
        ct.ThrowIfCancellationRequested();

        return new ProcessResult(timedOut ? -1 : process.ExitCode, stdOut, stdErr, timedOut);
    }

    /// <summary>True if an executable exists at the path, or bare name resolution is being attempted.</summary>
    public static bool LooksInvocable(string fileName) =>
        !Path.IsPathRooted(fileName) || File.Exists(fileName);
}
