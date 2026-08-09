using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// What one read of a repository's ledger returned, with the limits of that read stated rather
/// than implied. <see cref="Records"/> is newest-first and holds at most the requested count.
/// </summary>
/// <param name="Truncated">
/// True when the ledger holds more records than the count asked for, so the list is a tail and
/// not the whole ledger.
/// </param>
/// <param name="Rotated">
/// True when a rotated generation is on disk, so records older than <see cref="OldestRetainedUtc"/>
/// have been dropped.
/// </param>
/// <param name="SkippedLines">Lines that did not parse. One bad line costs that record, never the file.</param>
/// <param name="ReadError">Set when the ledger could not be read at all; the records are then whatever was reached.</param>
public sealed record OperationHistoryPage(
    IReadOnlyList<OperationRecord> Records,
    bool Truncated,
    bool Rotated,
    int SkippedLines,
    string? ReadError,
    DateTimeOffset? OldestRetainedUtc);

/// <summary>
/// The durable record of what this app did to a repository, one append-only JSONL ledger per
/// repository under &lt;LocalDir&gt;\history\&lt;repo-key&gt;.
///
/// Separate from <see cref="RewriteJournal"/> on purpose: the journal answers what is pending now
/// and is rewritten whole on every change, so a completed operation leaves it by design. This
/// answers what happened, including operations that were refused and operations whose backups
/// have since been pruned.
///
/// JSONL rather than one JSON document: an append is a single write of one line, which cannot
/// tear a prior record, where a read-modify-write of a whole document rewrites every record per
/// append and grows quadratically.
///
/// Every write is best effort and swallows to <see cref="Log.Warn"/>. A ledger that can abort an
/// operation is worse than no ledger; the operation's own result is authoritative.
///
/// Nothing here leaves the machine. The records hold repository paths and verbatim git output, and
/// they live under <see cref="AppPaths.LocalDir"/> with the rest of app state.
/// </summary>
public sealed class OperationHistory
{
    /// <summary>Live ledger size at which the file rolls aside, keeping one generation.</summary>
    internal const long RotateAtBytes = 2 * 1024 * 1024;

    /// <summary>Records a read returns unless a caller asks for fewer.</summary>
    public const int DefaultTailCount = 500;

    internal const string LedgerFileName = "ops.jsonl";

    internal const string RotatedFileName = "ops.jsonl.1";

    private const int AppendAttempts = 5;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Serializes appends across every instance in this process. Cross-process writers are
    // serialized by the append handle itself, which denies other writers while it is open.
    private static readonly object FileLock = new();

    /// <summary>
    /// WriteIndented stays off: one record is one line, and an indented document would put a
    /// newline inside a record and split it into unparseable fragments on the next read.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // The ledger is meant to be readable in a text editor, and a record written by a build
        // with a different naming policy still has to read back rather than parse as an empty one.
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new TolerantEnumConverter<OperationCategory>(OperationCategory.Maintenance),
            new TolerantEnumConverter<OperationOutcome>(OperationOutcome.Unknown),
            new TolerantEnumConverter<RecoveryKind>(RecoveryKind.MarkerCleared)
        }
    };

    private readonly string _root;

    public OperationHistory() : this(SafetyPaths.HistoryRoot) { }

    /// <summary>Test seam: point the ledgers at an explicit root.</summary>
    public OperationHistory(string root) => _root = root;

    public string Root => _root;

    public string DirectoryFor(string repoKey) => Path.Combine(_root, repoKey);

    /// <summary>
    /// Appends one record and hands it back so a caller can link a later record to it. The
    /// returned record carries its id whether or not the write landed; a link to a record that
    /// never reached disk simply does not resolve when the ledger is read.
    /// </summary>
    public OperationRecord Append(OperationRecord record)
    {
        try
        {
            var dir = DirectoryFor(record.RepoKey);
            var path = Path.Combine(dir, LedgerFileName);
            var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
            lock (FileLock)
            {
                Directory.CreateDirectory(dir);
                RotateIfOversized(path);
                AppendLine(path, line);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not record an operation for '{record.RepoPath}'", ex);
        }
        return record;
    }

    /// <summary>
    /// The newest <paramref name="count"/> records for a repository, newest first, with the
    /// rotated generation read first so a tail spanning a rotation is still contiguous.
    /// </summary>
    public OperationHistoryPage Tail(string repoPath, int count = DefaultTailCount)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || count < 1)
            return new OperationHistoryPage([], false, false, 0, null, null);

        var dir = DirectoryFor(RepoKey.For(repoPath));
        var rotated = Path.Combine(dir, RotatedFileName);
        var live = Path.Combine(dir, LedgerFileName);

        var hasRotated = SafeExists(rotated);
        var newest = new Queue<OperationRecord>();
        var total = 0;
        var skipped = 0;
        string? error = null;
        DateTimeOffset? oldest = null;

        var files = hasRotated ? new[] { rotated, live } : new[] { live };
        foreach (var file in files)
        {
            if (!SafeExists(file)) continue;
            try
            {
                // Read forward and keep a bounded window: the file is bounded by rotation, and a
                // reverse byte scan buys nothing against that ceiling while being far easier to
                // get wrong on a partially written final line.
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0) continue;
                    var record = TryParse(line);
                    if (record is null) { skipped++; continue; }
                    total++;
                    oldest ??= record.StartedUtc;
                    newest.Enqueue(record);
                    if (newest.Count > count) newest.Dequeue();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"could not read the operation ledger {file}", ex);
                error = ex.Message;
            }
        }

        if (skipped > 0)
            Log.Warn($"{skipped} unreadable line(s) skipped in the operation ledger for '{repoPath}'");

        var records = newest.Reverse().ToList();
        return new OperationHistoryPage(records, total > records.Count, hasRotated, skipped, error, oldest);
    }

    private static OperationRecord? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<OperationRecord>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex)
        {
            Log.Warn($"could not stat the operation ledger {path}", ex);
            return false;
        }
    }

    /// <summary>
    /// Rolls the live ledger aside once it reaches <see cref="RotateAtBytes"/>, keeping exactly one
    /// generation. The generation already on disk is what the overwrite drops, which is why a read
    /// reports the oldest record it still holds rather than presenting its list as complete.
    /// </summary>
    private static void RotateIfOversized(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < RotateAtBytes) return;
        var rotated = Path.Combine(Path.GetDirectoryName(path)!, RotatedFileName);
        File.Move(path, rotated, overwrite: true);
    }

    /// <summary>
    /// Appends one line with the handle denying other writers, so two processes recording against
    /// the same repository serialize instead of interleaving into a torn line. A contended open
    /// is retried; the record is dropped only when every attempt loses.
    /// </summary>
    private static void AppendLine(string path, string line)
    {
        var bytes = Utf8NoBom.GetBytes(line);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) when (attempt < AppendAttempts)
            {
                Thread.Sleep(20);
            }
        }
    }
}

/// <summary>
/// Reads an enum by name and falls back instead of throwing. A record written by a later build
/// carrying a value this one does not know is worth keeping with an honest fallback; a throw would
/// discard the whole line.
/// </summary>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly T _fallback;

    public TolerantEnumConverter(T fallback) => _fallback = fallback;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            return Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : _fallback;

        var text = reader.GetString();
        return text is not null && Enum.TryParse<T>(text, ignoreCase: true, out var parsed) ? parsed : _fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
