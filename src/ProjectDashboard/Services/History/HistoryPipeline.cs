using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProjectDashboard.Services.History;

/// <summary>Failure in one pipeline phase, carrying the child's exit code and stderr tail.</summary>
public sealed class HistoryPipelineException : Exception
{
    public string Phase { get; }
    public int? ExitCode { get; }
    public string StdErrTail { get; }

    public HistoryPipelineException(string phase, string reason, int? exitCode = null, string stdErrTail = "")
        : base($"{phase}: {reason}" + (string.IsNullOrWhiteSpace(stdErrTail) ? "" : $" :: {stdErrTail.Trim()}"))
    {
        Phase = phase;
        ExitCode = exitCode;
        StdErrTail = stdErrTail;
    }
}

public sealed record HistoryProgress(string Phase, long Bytes, long Records);

public sealed class HistoryPipelineOptions
{
    public required string SourceRepository { get; init; }

    /// <summary>Directory for the spool and marks files. Created if absent; contents are not cleaned up here.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Import target. Created via `git init --bare`; a pre-existing non-empty directory is refused.</summary>
    public required string TargetBareRepository { get; init; }

    public required TimeSpan ExportTimeout { get; init; }

    public required TimeSpan ImportTimeout { get; init; }

    public Action<HistoryProgress>? Progress { get; init; }

    /// <summary>Overrides git resolution (known install dirs, then PATH).</summary>
    public string? GitExecutable { get; init; }
}

public sealed class HistoryPipelineResult
{
    public required string SpoolPath { get; init; }
    public required string MarksPath { get; init; }
    public required IReadOnlyList<FastExportRecord> Records { get; init; }
    public required FastExportIndex Index { get; init; }
    public required long BytesSpooled { get; init; }
    public required long RecordsEmitted { get; init; }
}

/// <summary>
/// Round-trip engine: spool `git fast-export` raw to disk, parse and index it (pass A),
/// re-emit the records into `git fast-import` against a fresh bare repo (pass B). With no
/// transforms applied the re-emitted stream is byte-identical to the spool, so the import
/// reproduces every ref hash. The two streaming children are managed directly because
/// their volume is unbounded: stdout is consumed as raw bytes (never line-split, never
/// accumulated in strings), the opposite pipe is drained concurrently so neither side can
/// deadlock on a full pipe, and timeout/cancellation kill the process tree then reap.
/// </summary>
public sealed class HistoryPipeline
{
    private const int CopyBufferSize = 128 * 1024;
    private const long ProgressByteGranularity = 4 * 1024 * 1024;
    private const long ProgressRecordGranularity = 1000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Never prompt for credentials: a windowless app would hang invisibly.</summary>
    private static readonly Dictionary<string, string> GitEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0"
    };

    private static readonly string[] FastExportArgs =
    [
        "fast-export", "--all", "--show-original-ids", "--reencode=yes", "--mark-tags",
        "--signed-tags=strip", "--tag-of-filtered-object=rewrite", "--reference-excluded-parents"
    ];

    private readonly string _gitExe;

    public HistoryPipeline(string? gitExecutable = null) =>
        _gitExe = gitExecutable ?? ResolveGitExecutable();

    /// <summary>Resolve git: known install dirs first (survives a stale PATH), then PATH.</summary>
    public static string ResolveGitExecutable()
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
        return "git";
    }

    public async Task<HistoryPipelineResult> RunAsync(HistoryPipelineOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.WorkingDirectory);
        var spoolPath = Path.Combine(options.WorkingDirectory, "export.spool");
        var marksPath = Path.Combine(options.WorkingDirectory, "import.marks");

        var bytesSpooled = await ExportToSpoolAsync(options.SourceRepository, spoolPath, options, ct);

        List<FastExportRecord> records = [];
        var index = new FastExportIndex();
        await using (var spool = OpenSpoolForRead(spoolPath))
        {
            var reader = new FastExportReader(spool);
            while (reader.ReadRecord() is { } record)
            {
                records.Add(record);
                index.Add(record);
                if (records.Count % ProgressRecordGranularity == 0)
                    options.Progress?.Invoke(new HistoryProgress("parse", reader.Position, records.Count));
            }
        }
        options.Progress?.Invoke(new HistoryProgress("parse", bytesSpooled, records.Count));

        await CreateFreshBareRepoAsync(options.TargetBareRepository, ct);
        var emitted = await ImportAsync(records, spoolPath, marksPath, options, ct);

        return new HistoryPipelineResult
        {
            SpoolPath = spoolPath,
            MarksPath = marksPath,
            Records = records,
            Index = index,
            BytesSpooled = bytesSpooled,
            RecordsEmitted = emitted
        };
    }

    /// <summary>Re-emits records to any destination. Pass B uses this against fast-import's stdin; tests use it for byte-level identity checks.</summary>
    public static async Task<long> EmitAsync(
        IReadOnlyList<FastExportRecord> records, string spoolPath, Stream destination,
        Action<HistoryProgress>? progress = null, CancellationToken ct = default)
    {
        await using var spool = OpenSpoolForRead(spoolPath);
        var writer = new FastExportWriter(destination, spool);
        foreach (var record in records)
        {
            await writer.WriteRecordAsync(record, ct);
            if (writer.RecordsWritten % ProgressRecordGranularity == 0)
                progress?.Invoke(new HistoryProgress("emit", writer.BytesWritten, writer.RecordsWritten));
        }
        await writer.FlushAsync(ct);
        progress?.Invoke(new HistoryProgress("emit", writer.BytesWritten, writer.RecordsWritten));
        return writer.RecordsWritten;
    }

    private static FileStream OpenSpoolForRead(string spoolPath) =>
        new(spoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);

    private async Task<long> ExportToSpoolAsync(string sourceRepo, string spoolPath, HistoryPipelineOptions options, CancellationToken ct)
    {
        using var process = StartGit(FastExportArgs, sourceRepo, "fast-export", redirectStdIn: false);
        var stderrTail = DrainTextAsync(process.StandardError);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.ExportTimeout);

        long total = 0;
        try
        {
            await using (var spool = new FileStream(spoolPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                var buffer = new byte[CopyBufferSize];
                long nextReport = ProgressByteGranularity;
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeoutCts.Token);
                    if (read == 0) break;
                    await spool.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
                    total += read;
                    if (total >= nextReport)
                    {
                        options.Progress?.Invoke(new HistoryProgress("spool", total, 0));
                        nextReport = total + ProgressByteGranularity;
                    }
                }
            }
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process);
            ct.ThrowIfCancellationRequested();
            throw new HistoryPipelineException("fast-export", $"timed out after {options.ExportTimeout}", null, await stderrTail);
        }

        var stderr = await stderrTail;
        if (process.ExitCode != 0)
            throw new HistoryPipelineException("fast-export", "exited non-zero", process.ExitCode, stderr);

        options.Progress?.Invoke(new HistoryProgress("spool", total, 0));
        return total;
    }

    private async Task<long> ImportAsync(
        IReadOnlyList<FastExportRecord> records, string spoolPath, string marksPath,
        HistoryPipelineOptions options, CancellationToken ct)
    {
        // fast-import validates tree entry names against NTFS rules even in a bare target.
        // The source repo already holds these paths, so the import must accept them or a
        // legal repo cannot round-trip on Windows; the override is per-process and the
        // bare target never materializes a working tree, so checkout safety is unaffected.
        using var process = StartGit(
            ["-c", "core.protectNTFS=false", "fast-import", $"--export-marks={marksPath}"],
            options.TargetBareRepository, "fast-import", redirectStdIn: true);

        // Both output pipes drain from the start; stdin feeding must never be the only
        // live pipe or a chatty child blocks against a full buffer.
        var stdoutTail = DrainTextAsync(process.StandardOutput);
        var stderrTail = DrainTextAsync(process.StandardError);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.ImportTimeout);

        long emitted = 0;
        try
        {
            try
            {
                var stdin = process.StandardInput.BaseStream;
                emitted = await EmitAsync(records, spoolPath, stdin, options.Progress, timeoutCts.Token);
            }
            catch (IOException)
            {
                // Broken pipe: fast-import died mid-stream. The real diagnostic is its
                // stderr, reported below after the exit code is known.
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { /* already closed */ }
            }
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process);
            ct.ThrowIfCancellationRequested();
            throw new HistoryPipelineException("fast-import", $"timed out after {options.ImportTimeout}", null, await stderrTail);
        }

        var stderr = await stderrTail;
        await stdoutTail;
        if (process.ExitCode != 0)
            throw new HistoryPipelineException("fast-import", "exited non-zero", process.ExitCode, stderr);
        if (emitted != records.Count)
            throw new HistoryPipelineException("fast-import", $"stream ended early: {emitted} of {records.Count} records written", process.ExitCode, stderr);
        return emitted;
    }

    private async Task CreateFreshBareRepoAsync(string targetPath, CancellationToken ct)
    {
        if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            throw new HistoryPipelineException("init-bare", $"target '{targetPath}' exists and is not empty — the import target must be fresh");
        Directory.CreateDirectory(targetPath);

        var result = await ProcessRunner.RunAsync(
            _gitExe, ["init", "--bare"], targetPath, TimeSpan.FromSeconds(30), GitEnvironment, ct);
        if (!result.Success)
            throw new HistoryPipelineException("init-bare", "git init --bare failed", result.ExitCode, result.StdErr);
    }

    private Process StartGit(IEnumerable<string> args, string workingDirectory, string phase, bool redirectStdIn)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _gitExe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdIn,
            // Raw-byte reads bypass these; they keep the diagnostic text pipes from
            // decoding with the ANSI codepage in a windowless process.
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        foreach (var (key, value) in GitEnvironment)
            process.StartInfo.Environment[key] = value;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new HistoryPipelineException(phase, $"could not start '{_gitExe}': {ex.Message}");
        }
        return process;
    }

    /// <summary>Keeps only the last 16 KiB of a text pipe — enough diagnostic tail without unbounded growth.</summary>
    private static async Task<string> DrainTextAsync(StreamReader reader)
    {
        const int maxTail = 16 * 1024;
        var tail = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                break;
            }
            if (read == 0) break;
            tail.Append(buffer, 0, read);
            if (tail.Length > maxTail)
                tail.Remove(0, tail.Length - maxTail);
        }
        return tail.ToString();
    }

    private static async Task KillAndReapAsync(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* reaped or stuck */ }
    }
}
