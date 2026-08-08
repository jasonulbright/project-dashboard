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

    /// <summary>
    /// Runs between parse and import. Mutations to the parsed records are what the
    /// import receives, so with a transform installed the emitted stream is no longer
    /// byte-identical to the spool and the target's object ids follow the mutations.
    /// A throw here aborts the run before the target bare is created.
    /// </summary>
    public Func<ParsedExport, CancellationToken, Task>? TransformAsync { get; init; }
}

/// <summary>
/// Parsed stream handed to a transform. Spool slices in the records stay valid only while
/// <see cref="SpoolPath"/> exists. <see cref="Records"/> is mutable: a transform may edit
/// records in place, insert freshly minted blobs, or drop pruned commits — the same list
/// object is what the import pass re-emits.
/// </summary>
public sealed record ParsedExport(List<FastExportRecord> Records, FastExportIndex Index, string SpoolPath);

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

    /// <summary>The application-wide non-interactive git environment; see <see cref="GitService.NonInteractiveEnvironment"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> GitEnvironment = GitService.NonInteractiveEnvironment;

    private static readonly string[] FastExportArgs =
    [
        // Global flag, before the subcommand: without it fast-export follows
        // refs/replace/* and substitutes replacement objects into the walk, so the
        // import reproduces the replacement history instead of the original commits.
        "--no-replace-objects",
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
        await RefuseNestedTagsAsync(options.SourceRepository, ct);
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

        if (options.TransformAsync is { } transform)
            await transform(new ParsedExport(records, index, spoolPath), ct);

        await CreateFreshBareRepoAsync(options.TargetBareRepository, ct);
        var (importFeed, scratchHeadRef) = BuildImportFeed(records);
        long emitted;
        try
        {
            emitted = await ImportAsync(importFeed, spoolPath, marksPath, options, ct);
        }
        catch when (scratchHeadRef is not null)
        {
            // fast-import can exit non-zero after its refs are already written (a failed
            // marks dump, for one), so a failed import can still leave the scratch ref in
            // the target. Best-effort delete: the import failure is the diagnostic that
            // must propagate, and cancellation must not suppress the cleanup.
            try
            {
                await ProcessRunner.RunAsync(_gitExe, ["update-ref", "-d", scratchHeadRef],
                    options.TargetBareRepository, TimeSpan.FromSeconds(30), GitEnvironment, CancellationToken.None);
            }
            catch { /* target may be absent or unusable */ }
            throw;
        }
        if (scratchHeadRef is not null)
            await RunGitCheckedAsync(options.TargetBareRepository, ["update-ref", "-d", scratchHeadRef], "align-head", ct);
        await AlignTargetHeadAsync(options, index, marksPath, ct);

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

    /// <summary>
    /// The stream fed to fast-import must not update a ref literally named HEAD: the
    /// fresh bare target holds a default HEAD symref, so fast-import dereferences it and
    /// creates or moves whichever branch init.defaultBranch names. A detached source HEAD
    /// exports as `reset HEAD` (tip already on a branch) or as `commit HEAD` records
    /// (commits reachable only from HEAD). Resets carry no objects and are dropped —
    /// HEAD is aligned explicitly after import; commits are re-addressed to a scratch
    /// ref deleted after import. The parsed record list is not mutated, so re-emission
    /// stays byte-identical to the spool.
    /// </summary>
    private static (IReadOnlyList<FastExportRecord> Feed, string? ScratchHeadRef) BuildImportFeed(
        IReadOnlyList<FastExportRecord> records)
    {
        var scratch = "refs/pd-import/head";
        while (records.Any(r => r is CommitRecord c && c.RefName == scratch
                             || r is ResetRecord s && s.RefName == scratch))
            scratch += "-x";

        string? used = null;
        var feed = new List<FastExportRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            switch (records[i])
            {
                case ResetRecord { RefName: "HEAD" }:
                    // The separator after the dropped reset must go with it: a blank
                    // line where fast-import expects a command is a fatal parse error.
                    if (i + 1 < records.Count && records[i + 1] is BlankRecord)
                        i++;
                    break;
                case CommitRecord { RefName: "HEAD" } commit:
                    used = scratch;
                    feed.Add(CloneUnderRef(commit, scratch));
                    break;
                default:
                    feed.Add(records[i]);
                    break;
            }
        }
        return (feed, used);
    }

    private static CommitRecord CloneUnderRef(CommitRecord commit, string refName)
    {
        var clone = new CommitRecord
        {
            ByteOffset = commit.ByteOffset,
            RefNameBytes = Encoding.ASCII.GetBytes(refName),
            Mark = commit.Mark,
            OriginalOid = commit.OriginalOid,
            Message = commit.Message
        };
        clone.HeaderLines.AddRange(commit.HeaderLines);
        clone.Parents.AddRange(commit.Parents);
        clone.FileCommands.AddRange(commit.FileCommands);
        return clone;
    }

    /// <summary>
    /// Points target HEAD where source HEAD points. The import feed carries no HEAD
    /// record (see <see cref="BuildImportFeed"/>), so without this the target keeps
    /// whatever symref `git init` chose — possibly naming a branch that does not exist.
    /// A detached HEAD names a commit oid; under a content rewrite that source oid does
    /// not exist in the target, so it must be mapped through original-oid → mark → import
    /// oid before it is written, or target HEAD would point at unscrubbed (or absent)
    /// content.
    /// </summary>
    private async Task AlignTargetHeadAsync(
        HistoryPipelineOptions options, FastExportIndex index, string marksPath, CancellationToken ct)
    {
        var symref = await ProcessRunner.RunAsync(
            _gitExe, ["symbolic-ref", "-q", "HEAD"], options.SourceRepository,
            TimeSpan.FromSeconds(30), GitEnvironment, ct);
        if (symref.Success)
        {
            await RunGitCheckedAsync(options.TargetBareRepository,
                ["symbolic-ref", "HEAD", symref.StdOut.Trim()], "align-head", ct);
            return;
        }

        // Detached: --no-deref writes HEAD itself; a plain update-ref would follow the
        // target's default symref and recreate the phantom branch.
        var sourceOid = (await RunGitCheckedAsync(options.SourceRepository,
            ["rev-parse", "--verify", "HEAD"], "align-head", ct)).StdOut.Trim();
        var targetOid = MapSourceOidToTarget(sourceOid, index, marksPath);
        await RunGitCheckedAsync(options.TargetBareRepository,
            ["update-ref", "--no-deref", "HEAD", targetOid], "align-head", ct);
    }

    /// <summary>Resolves a source commit oid to its imported oid via original-oid → mark → marks file. Fails loudly when the source oid is not in the exported history.</summary>
    private static string MapSourceOidToTarget(string sourceOid, FastExportIndex index, string marksPath)
    {
        var entry = index.CommitsInOrder.FirstOrDefault(c =>
            string.Equals(c.OriginalOid, sourceOid, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new HistoryPipelineException(
                "align-head", $"detached HEAD commit {sourceOid} is absent from the exported history — cannot align target HEAD");

        foreach (var raw in File.ReadLines(marksPath))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] != ':') continue;
            var sp = line.IndexOf(' ');
            if (sp > 1 && long.TryParse(line.AsSpan(1, sp - 1), out var mark) && mark == entry.Mark)
                return line[(sp + 1)..];
        }
        throw new HistoryPipelineException(
            "align-head", $"detached HEAD commit {sourceOid} (mark :{entry.Mark}) is missing from the import marks file");
    }

    private async Task<ProcessResult> RunGitCheckedAsync(
        string repository, string[] args, string phase, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            _gitExe, args, repository, TimeSpan.FromSeconds(30), GitEnvironment, ct);
        if (!result.Success)
            throw new HistoryPipelineException(
                phase, $"git {string.Join(' ', args)} failed in '{repository}'", result.ExitCode, result.StdErr);
        return result;
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

    /// <summary>
    /// Refuses a source holding a tag object the export cannot re-emit faithfully, before any
    /// export work, so the refusal names the ref instead of surfacing later as a corrupt target
    /// or a verification ref difference. Two shapes qualify, and every ref is scanned for both —
    /// not only refs/tags, because a tag object is reachable from any ref and fast-export walks
    /// every one of them:
    ///
    /// A tag object pointing at another tag object. fast-export re-emits the inner tag under the
    /// outer ref's name, so both land on one ref and the nesting is lost. Detection reads the tag
    /// object's own `type` header via %(type); %(*objecttype) is unusable because it peels
    /// recursively to the final non-tag object. Tags of blobs or trees round-trip and pass.
    ///
    /// A tag object whose embedded name header is not the name fast-export will emit — the ref
    /// name minus `refs/tags/`, or the whole ref name outside refs/tags. The re-imported tag
    /// object then carries a different name than the source's and hashes differently, so the ref
    /// no longer resolves to the object it did. A second ref pointing at an existing tag object
    /// is the ordinary way to reach this.
    /// </summary>
    private async Task RefuseNestedTagsAsync(string sourceRepository, CancellationToken ct)
    {
        // Global --no-replace-objects, matching the export flags: %(type) reads the tag
        // object's content, and with a replace ref active it reads the replacement's
        // content instead, so a nested tag can masquerade as tag→commit and slip past
        // this check while the export still walks the original nested tag.
        var result = await RunGitCheckedAsync(sourceRepository,
            ["--no-replace-objects", "for-each-ref", "--format=%(refname)%1f%(objecttype)%1f%(type)%1f%(tag)"], "preflight", ct);

        var refusals = new List<string>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            // A tag name is arbitrary bytes and may hold the separator, so it is the trailing
            // remainder rather than one field; the three ahead of it cannot contain either
            // the separator or a newline.
            var fields = line.Split('\x1f');
            if (fields.Length < 4)
                throw new HistoryPipelineException(
                    "preflight", $"for-each-ref emitted a ref record the pre-flight cannot parse: '{line}'");
            var (refName, objectType, pointeeType) = (fields[0], fields[1], fields[2]);
            var tagName = string.Join('\x1f', fields[3..]);
            if (objectType != "tag") continue;

            if (pointeeType == "tag")
            {
                refusals.Add($"{refName} is a nested tag: it points at another tag object");
                continue;
            }

            var emitted = refName.StartsWith("refs/tags/", StringComparison.Ordinal) ? refName["refs/tags/".Length..] : refName;
            if (!string.Equals(tagName, emitted, StringComparison.Ordinal))
                refusals.Add($"{refName} holds a tag object named '{tagName}', which the export would re-emit as '{emitted}'");
        }
        if (refusals.Count > 0)
            throw new HistoryPipelineException(
                "preflight", $"these tags cannot round-trip through fast-export — {string.Join("; ", refusals)}");
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
