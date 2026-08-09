using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace ProjectDashboard.Services;

/// <summary>Outcome of one subprocess run. Non-zero exit is data, not an exception.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>
    /// True when the capture budget stopped the output short: <see cref="StdOut"/> and
    /// <see cref="StdErr"/> then hold a prefix of what the child wrote, not the whole of it.
    /// A parse of a truncated capture is partial by construction and says so to its reader.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Characters the child wrote across both streams, the surplus the budget discarded
    /// included. Zero when the drain was abandoned and neither capture was read.
    /// </summary>
    public long OutputChars { get; init; }

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

    /// <summary>
    /// Characters retained per stream before the capture stops growing. Draining continues past
    /// it — an unread pipe blocks the child — so the budget bounds memory, never the drain. It
    /// is far above any output this app parses and exists so one runaway child cannot exhaust
    /// the process; a caller that knows its own ceiling passes a tighter one.
    /// </summary>
    public const int DefaultCaptureCharBudget = 32 * 1024 * 1024;

    public static Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        int captureCharBudget = DefaultCaptureCharBudget)
        => RunCoreAsync(fileName, arguments, workingDirectory, timeout, environment,
                        onStdOutLine: null, onStdErrLine: null, standardInput: null, ct, captureCharBudget);

    /// <summary>
    /// RunAsync that also writes <paramref name="standardInput"/> to the child's stdin and
    /// closes it (sending EOF). For commands that take a script on stdin — e.g. the single
    /// all-or-nothing `git update-ref --stdin` transaction — where argument passing cannot
    /// express the payload. The input is drained concurrently with stdout/stderr so a child
    /// that echoes while reading cannot deadlock; a write failure is logged, stdin is still
    /// closed, and the child's own exit code reports the outcome. The write is inside the
    /// timeout: a child that never reads stdin blocks the write once the pipe buffer fills,
    /// and that ends as TimedOut with the process tree killed, not as an unbounded wait.
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
                        onStdOutLine: null, onStdErrLine: null, standardInput, ct, DefaultCaptureCharBudget);

    /// <summary>
    /// RunAsync plus live output: each completed line is handed to the matching callback while
    /// the child is still running. Invariants: callbacks run on thread-pool threads in
    /// per-stream order — callers marshal to the UI thread themselves; the result still carries
    /// the full captured stdout/stderr, so streaming is additive, not a replacement; a throwing
    /// callback is caught and logged and draining continues; a slow or stalled callback cannot
    /// back up the pipes or kill the child — lines queue between the pipe reader and the
    /// callback, and the process result is never held hostage to delivery; CR, LF, and CRLF
    /// each terminate a callback line (git progress redraws lines with bare CR); timeout,
    /// cancellation, and kill semantics are identical to RunAsync. The capture is bounded by
    /// <see cref="DefaultCaptureCharBudget"/>, the queue of undelivered lines is not, so
    /// per-line callbacks suit progress- and log-scale output, not bulk data transfer.
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
                        onStdOutLine, onStdErrLine, standardInput: null, ct, DefaultCaptureCharBudget);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onStdOutLine,
        Action<string>? onStdErrLine,
        string? standardInput,
        CancellationToken ct,
        int captureCharBudget)
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
        var (stdOutTask, stdOutDelivery) = BeginDrain(process.StandardOutput, onStdOutLine, fileName, "stdout", captureCharBudget);
        var (stdErrTask, stdErrDelivery) = BeginDrain(process.StandardError, onStdErrLine, fileName, "stderr", captureCharBudget);

        // One budget covers the whole run. It is armed before stdin because a child that never
        // reads fills the pipe buffer and blocks the write: on the caller's token alone that
        // write outlives every timeout the caller set.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        var timedOut = false;

        // Feed stdin while the pipes drain, then close it so the child sees EOF. A write that
        // faults (child already exited) is logged, not thrown: the exit code is the outcome.
        if (standardInput is not null)
        {
            try { await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutCts.Token); }
            catch (OperationCanceledException) { timedOut = true; }
            catch (Exception ex) { Log.Warn($"stdin write failed for {fileName}", ex); }

            // Closing flushes what the writer still holds, and a flush into a pipe nobody reads
            // blocks exactly as the write did. On the timeout path the kill below breaks the
            // pipe first, so the close there fails fast instead.
            if (!timedOut)
                await CloseStdInAsync(process, fileName);
        }

        if (!timedOut)
        {
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) { timedOut = true; }
        }

        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (standardInput is not null)
                await CloseStdInAsync(process, fileName);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* reaped or stuck */ }
        }

        // Kill closes the pipes, so these normally complete promptly — but a descendant that
        // escaped the kill snapshot can keep the handles open. Bound the drain so a runaway
        // grandchild can never wedge a discovery slot. An abandoned drain returns empty
        // stdout/stderr with TimedOut set: neither capture is read on this path, so even a
        // pipe whose read completed reports empty.
        string stdOut = "", stdErr = "";
        var truncated = false;
        long outputChars = 0;
        var drained = false;
        try
        {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(TimeSpan.FromSeconds(timedOut ? 5 : 30));
            stdOut = stdOutTask.Result.Text;
            stdErr = stdErrTask.Result.Text;
            truncated = stdOutTask.Result.Truncated || stdErrTask.Result.Truncated;
            outputChars = stdOutTask.Result.TotalChars + stdErrTask.Result.TotalChars;
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

        return new ProcessResult(timedOut ? -1 : process.ExitCode, stdOut, stdErr, timedOut)
        {
            Truncated = truncated,
            OutputChars = outputChars
        };
    }

    /// <summary>
    /// One stream's capture. <see cref="TotalChars"/> counts everything the child wrote on it,
    /// so a capture the budget cut short is the one whose total outruns its retained text.
    /// </summary>
    private readonly record struct Capture(string Text, long TotalChars)
    {
        public bool Truncated => TotalChars > Text.Length;
    }

    /// <summary>
    /// Closes the child's stdin under a bound. The close flushes, and a descendant that
    /// inherited the read handle can leave that flush blocked with no pipe left to drain it,
    /// so the close never runs inline on the result path. The writer is read out here rather
    /// than inside the task: past the bound the task outlives this method, and the caller
    /// disposes the Process as soon as it returns.
    /// </summary>
    private static async Task CloseStdInAsync(Process process, string fileName)
    {
        var stdIn = process.StandardInput;
        var close = Task.Run(stdIn.Close);
        try
        {
            await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Log.Warn($"Abandoned the stdin close for {fileName} — a descendant process is holding the pipe open");
            Observe(close, $"stdin close for {fileName}");
        }
        catch
        {
            // Already closed, or broken by the kill that precedes this on the timeout path.
        }
    }

    /// <summary>
    /// Keeps an abandoned task's failure from going unobserved. Reading Exception is what marks
    /// it observed; the log entry is what makes it something other than a silent disappearance.
    /// </summary>
    private static void Observe(Task task, string what) =>
        _ = task.ContinueWith(
            faulted => Log.Warn($"{what} faulted after it was abandoned", faulted.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Starts draining one pipe. Without a callback the stream is only captured. With one, the
    /// reader also pushes completed lines into an unbounded queue consumed on a thread-pool task,
    /// so the pipe is drained at full speed no matter how slow the callback is — a subscriber can
    /// never fill the pipe and block the child.
    /// </summary>
    private static (Task<Capture> Capture, Task? Delivery) BeginDrain(
        StreamReader reader, Action<string>? onLine, string fileName, string streamName, int charBudget)
    {
        if (onLine is null)
            return (CaptureAsync(reader, lines: null, charBudget), null);

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

        return (CaptureAsync(reader, lines.Writer, charBudget), delivery);
    }

    private static async Task<Capture> CaptureAsync(StreamReader reader, ChannelWriter<string>? lines, int charBudget)
    {
        var captured = new StringBuilder();
        var lineBuf = new StringBuilder();
        var buffer = new char[4096];
        var sawCr = false;
        long total = 0;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                // Raw characters (line endings included) go to the capture untouched; the
                // line split below is a parallel view, not a transformation. Past the budget
                // the surplus is counted and dropped: reading has to continue either way,
                // because a pipe left unread blocks the child.
                total += read;
                var room = charBudget - captured.Length;
                captured.Append(buffer, 0, Math.Clamp(room, 0, read));

                if (lines is null) continue;
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
            if (lines is not null && lineBuf.Length > 0)
                lines.TryWrite(lineBuf.ToString());
        }
        finally
        {
            // Delivery ends only when the writer completes — including when the read faults
            // after a kill.
            lines?.Complete();
        }
        return new Capture(captured.ToString(), total);
    }

    /// <summary>True if an executable exists at the path, or bare name resolution is being attempted.</summary>
    public static bool LooksInvocable(string fileName) =>
        !Path.IsPathRooted(fileName) || File.Exists(fileName);
}
