using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

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

    public static Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
        => RunCoreAsync(fileName, arguments, workingDirectory, timeout, environment,
                        onStdOutLine: null, onStdErrLine: null, standardInput: null, ct);

    /// <summary>
    /// RunAsync that also writes <paramref name="standardInput"/> to the child's stdin and
    /// closes it (sending EOF). For commands that take a script on stdin — e.g. the single
    /// all-or-nothing `git update-ref --stdin` transaction — where argument passing cannot
    /// express the payload. The input is drained concurrently with stdout/stderr so a child
    /// that echoes while reading cannot deadlock; a write failure is logged, stdin is still
    /// closed, and the child's own exit code reports the outcome.
    /// </summary>
    public static Task<ProcessResult> RunWithInputAsync(
        string fileName,
        IEnumerable<string> arguments,
        string standardInput,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
        => RunCoreAsync(fileName, arguments, workingDirectory, timeout, environment,
                        onStdOutLine: null, onStdErrLine: null, standardInput, ct);

    /// <summary>
    /// RunAsync plus live output: each completed line is handed to the matching callback while
    /// the child is still running. Invariants: callbacks run on thread-pool threads in
    /// per-stream order — callers marshal to the UI thread themselves; the result still carries
    /// the full captured stdout/stderr, so streaming is additive, not a replacement; a throwing
    /// callback is caught and logged and draining continues; a slow or stalled callback cannot
    /// back up the pipes or kill the child — lines queue between the pipe reader and the
    /// callback, and the process result is never held hostage to delivery; CR, LF, and CRLF
    /// each terminate a callback line (git progress redraws lines with bare CR); timeout,
    /// cancellation, and kill semantics are identical to RunAsync. Memory is O(total output):
    /// the full capture accrues alongside any queued undelivered lines, so per-line callbacks
    /// suit progress- and log-scale output, not bulk data transfer.
    /// </summary>
    public static Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onStdOutLine,
        Action<string>? onStdErrLine,
        CancellationToken ct = default)
        => RunCoreAsync(fileName, args, workingDirectory, timeout, environment,
                        onStdOutLine, onStdErrLine, standardInput: null, ct);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onStdOutLine,
        Action<string>? onStdErrLine,
        string? standardInput,
        CancellationToken ct)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (standardInput is not null)
            process.StartInfo.StandardInputEncoding = Utf8NoBom;
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
        var (stdOutTask, stdOutDelivery) = BeginDrain(process.StandardOutput, onStdOutLine, fileName, "stdout");
        var (stdErrTask, stdErrDelivery) = BeginDrain(process.StandardError, onStdErrLine, fileName, "stderr");

        // Feed stdin while the pipes drain, then close it so the child sees EOF. A write that
        // faults (child already exited) is logged, not thrown: the exit code is the outcome.
        if (standardInput is not null)
        {
            try { await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Log.Warn($"stdin write failed for {fileName}", ex); }
            finally { try { process.StandardInput.Close(); } catch { /* already closed */ } }
        }

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
        // grandchild can never wedge a discovery slot. An abandoned drain returns empty
        // stdout/stderr with TimedOut set: neither capture is read on this path, so even a
        // pipe whose read completed reports empty.
        string stdOut = "", stdErr = "";
        var drained = false;
        try
        {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(TimeSpan.FromSeconds(timedOut ? 5 : 30));
            stdOut = stdOutTask.Result;
            stdErr = stdErrTask.Result;
            drained = true;
        }
        catch (TimeoutException)
        {
            timedOut = true;
            Log.Warn($"Abandoned pipe drain for {fileName} — a descendant process is holding the output handles" +
                     (stdOutDelivery is not null || stdErrDelivery is not null
                         ? "; queued line delivery cannot complete until it exits"
                         : ""));
        }

        // Line delivery is decoupled from the pipes; a bounded wait lets callers normally
        // observe every line before the result, while a stalled callback cannot hold the
        // result hostage — its remaining lines deliver in the background. The wait only
        // runs after a completed drain: the line channels complete when the pipes close,
        // so with the drain abandoned the wait can never succeed and a timeout here would
        // misattribute the pipe stall to the callback.
        if (drained && (stdOutDelivery is not null || stdErrDelivery is not null))
        {
            try
            {
                await Task.WhenAll(stdOutDelivery ?? Task.CompletedTask, stdErrDelivery ?? Task.CompletedTask)
                          .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log.Warn($"Line callback stalled for {fileName}; remaining lines deliver in the background");
            }
        }

        // Distinguish caller cancellation from a genuine timeout.
        ct.ThrowIfCancellationRequested();

        return new ProcessResult(timedOut ? -1 : process.ExitCode, stdOut, stdErr, timedOut);
    }

    /// <summary>
    /// Starts draining one pipe. Without a callback this is a plain read-to-end. With one, the
    /// reader pushes completed lines into an unbounded queue consumed on a thread-pool task, so
    /// the pipe is drained at full speed no matter how slow the callback is — a subscriber can
    /// never fill the pipe and block the child.
    /// </summary>
    private static (Task<string> Capture, Task? Delivery) BeginDrain(
        StreamReader reader, Action<string>? onLine, string fileName, string streamName)
    {
        if (onLine is null)
            return (reader.ReadToEndAsync(CancellationToken.None), null);

        var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var delivery = Task.Run(async () =>
        {
            var logged = false;
            await foreach (var line in lines.Reader.ReadAllAsync(CancellationToken.None))
            {
                try
                {
                    onLine(line);
                }
                catch (Exception ex)
                {
                    // Delivery outlives a faulty subscriber. Only the first exception per
                    // stream is logged — a callback that throws on every line of a large
                    // drain otherwise floods the log.
                    if (!logged)
                    {
                        logged = true;
                        Log.Warn($"{streamName} line callback threw for {fileName}", ex);
                    }
                }
            }
        });

        return (CaptureAndStreamAsync(reader, lines.Writer), delivery);
    }

    private static async Task<string> CaptureAndStreamAsync(StreamReader reader, ChannelWriter<string> lines)
    {
        var captured = new StringBuilder();
        var lineBuf = new StringBuilder();
        var buffer = new char[4096];
        var sawCr = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                // Raw characters (line endings included) go to the capture untouched; the
                // line split below is a parallel view, not a transformation.
                captured.Append(buffer, 0, read);
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        if (sawCr) { sawCr = false; continue; }
                        lines.TryWrite(lineBuf.ToString());
                        lineBuf.Clear();
                    }
                    else if (c == '\r')
                    {
                        // A bare CR terminates a callback line: git progress redraws with
                        // CR-only updates, and holding those until LF defeats live progress.
                        lines.TryWrite(lineBuf.ToString());
                        lineBuf.Clear();
                        sawCr = true;
                    }
                    else
                    {
                        sawCr = false;
                        lineBuf.Append(c);
                    }
                }
            }
            if (lineBuf.Length > 0)
                lines.TryWrite(lineBuf.ToString());
        }
        finally
        {
            // Delivery ends only when the writer completes — including when the read faults
            // after a kill.
            lines.Complete();
        }
        return captured.ToString();
    }

    /// <summary>True if an executable exists at the path, or bare name resolution is being attempted.</summary>
    public static bool LooksInvocable(string fileName) =>
        !Path.IsPathRooted(fileName) || File.Exists(fileName);
}
