using System.IO;
using System.Text.Json;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// One in-flight history rewrite, written before the swap begins and cleared after
/// it succeeds. Serialized DTO, so plain get/set.
/// </summary>
public sealed class RewriteJournalEntry
{
    public string RepoPath { get; set; } = "";

    /// <summary>The backup taken before the op — the restore target if recovery is needed.</summary>
    public BackupHandle? BackupHandle { get; set; }

    /// <summary>Coarse progress marker (e.g. "backup", "swap") for diagnostics after a crash.</summary>
    public string Phase { get; set; } = "";

    public string UtcStamp { get; set; } = "";
}

/// <summary>
/// Crash-visible marker for a history rewrite. <see cref="BeginAsync"/> writes
/// the journal before a swap starts; <see cref="CompleteAsync"/> clears it after
/// success. A process that dies mid-swap leaves the journal on disk, which
/// <see cref="ReadPendingAsync"/> finds on the next launch. One entry at a time:
/// rewrites are serialized behind <see cref="RepoBusyRegistry"/>, so a second Begin
/// overwriting the first would signal a caller-ordering bug, not normal operation.
/// </summary>
public sealed class RewriteJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public RewriteJournal() : this(SafetyPaths.JournalFile) { }

    /// <summary>Test seam: point the journal at an explicit path.</summary>
    public RewriteJournal(string path) => _path = path;

    public string Path => _path;

    public Task BeginAsync(RewriteJournalEntry entry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        DurableJsonFile.Write(_path, JsonSerializer.Serialize(entry, JsonOptions));
        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken ct = default)
    {
        TryDelete(_path);
        // The .bak is a prior journal version; leaving it would resurface as a phantom
        // pending op on a later crash-recovery read that falls back to backup.
        TryDelete(_path + ".bak");
        return Task.CompletedTask;
    }

    public Task<RewriteJournalEntry?> ReadPendingAsync(CancellationToken ct = default)
    {
        try { return Task.FromResult(DurableJsonFile.Read<RewriteJournalEntry>(_path, JsonOptions)); }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read rewrite journal {_path}", ex);
            return Task.FromResult<RewriteJournalEntry?>(null);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Could not clear rewrite journal {path}", ex); }
    }
}
