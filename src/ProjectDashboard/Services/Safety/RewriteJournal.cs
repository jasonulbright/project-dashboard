using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// The journal's on-disk shape: one entry per repository, keyed by <see cref="RepoKey"/>.
/// The four legacy fields are the single-entry shape this file used to have; they are read so a
/// journal left pending by an older build is folded into <see cref="Entries"/> rather than lost,
/// and never written back.
/// </summary>
internal sealed class RewriteJournalFile
{
    public Dictionary<string, RewriteJournalEntry> Entries { get; set; } = new(StringComparer.Ordinal);

    public string? RepoPath { get; set; }

    public BackupHandle? BackupHandle { get; set; }

    public string? Phase { get; set; }

    public string? UtcStamp { get; set; }
}

/// <summary>
/// Crash-visible marker for history rewrites. <see cref="BeginAsync"/> writes an entry before a
/// rewrite starts; <see cref="CompleteAsync(string,CancellationToken)"/> removes that repository's
/// entry afterwards. A process that dies mid-rewrite leaves its entry on disk, which
/// <see cref="ReadAllPendingAsync"/> finds on the next launch.
///
/// Entries are keyed per repository: <see cref="RepoBusyRegistry"/> serializes operations against
/// one repository but not across several, and a failure leaving an entry pending is an ordinary
/// outcome, so a single slot would let one repository's success delete another's marker and
/// orphan its backup.
/// </summary>
public sealed class RewriteJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Serializes the whole read-modify-write across instances. Two repositories rewriting at
    // once otherwise interleave read and write, so one Begin drops the other's entry and
    // orphans its backup, and both writers collide on the shared .tmp, which throws out of
    // Begin after a backup was taken and out of Complete after the operation succeeded.
    // Every file path is held synchronously under the lock; nothing here awaits.
    private static readonly object FileLock = new();

    private readonly string _path;

    public RewriteJournal() : this(SafetyPaths.JournalFile) { }

    /// <summary>Test seam: point the journal at an explicit path.</summary>
    public RewriteJournal(string path) => _path = path;

    public string Path => _path;

    public Task BeginAsync(RewriteJournalEntry entry, CancellationToken ct = default)
    {
        lock (FileLock)
        {
            var file = ReadFile();
            file.Entries[KeyFor(entry.RepoPath)] = entry;
            WriteFile(file);
        }
        return Task.CompletedTask;
    }

    /// <summary>Clears one repository's entry. Other repositories' pending entries survive.</summary>
    public Task CompleteAsync(string repoPath, CancellationToken ct = default)
    {
        lock (FileLock)
        {
            var file = ReadFile();
            if (!file.Entries.Remove(KeyFor(repoPath))) return Task.CompletedTask;

            if (file.Entries.Count == 0) DeleteFile();
            else WriteFile(file);
        }
        return Task.CompletedTask;
    }

    /// <summary>Drops every entry. For a caller that has decided nothing on disk is worth recovering.</summary>
    public Task ClearAllAsync(CancellationToken ct = default)
    {
        lock (FileLock) DeleteFile();
        return Task.CompletedTask;
    }

    /// <summary>Every interrupted operation on disk, in no particular order.</summary>
    public Task<IReadOnlyList<RewriteJournalEntry>> ReadAllPendingAsync(CancellationToken ct = default)
    {
        // A read takes the lock too: a read racing the swap can fail, and recovering a corrupt
        // file writes the recovered content back.
        lock (FileLock)
            return Task.FromResult<IReadOnlyList<RewriteJournalEntry>>(ReadFile().Entries.Values.ToList());
    }

    /// <summary>That repository's interrupted operation, or null when it has none.</summary>
    public Task<RewriteJournalEntry?> ReadPendingAsync(string repoPath, CancellationToken ct = default)
    {
        lock (FileLock)
            return Task.FromResult(ReadFile().Entries.TryGetValue(KeyFor(repoPath), out var entry) ? entry : null);
    }

    /// <summary>One pending entry, for a caller that only needs to know whether anything is pending.</summary>
    public Task<RewriteJournalEntry?> ReadPendingAsync(CancellationToken ct = default)
    {
        lock (FileLock)
            return Task.FromResult(ReadFile().Entries.Values.FirstOrDefault());
    }

    /// <summary>
    /// A path that no longer resolves (a legacy entry written with none) still has to key
    /// somewhere, or the whole file would be unreadable because of one bad row.
    /// </summary>
    private static string KeyFor(string? repoPath) =>
        string.IsNullOrWhiteSpace(repoPath) ? "unknown" : RepoKey.For(repoPath);

    private RewriteJournalFile ReadFile()
    {
        RewriteJournalFile? file;
        try { file = DurableJsonFile.Read<RewriteJournalFile>(_path, JsonOptions); }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read rewrite journal {_path}", ex);
            return new RewriteJournalFile();
        }

        if (file is null) return new RewriteJournalFile();

        if (file.RepoPath is not null && file.Entries.Count == 0)
        {
            var legacy = new RewriteJournalEntry
            {
                RepoPath = file.RepoPath,
                BackupHandle = file.BackupHandle,
                Phase = file.Phase ?? "",
                UtcStamp = file.UtcStamp ?? ""
            };
            file.Entries[KeyFor(legacy.RepoPath)] = legacy;
        }
        file.RepoPath = null;
        file.BackupHandle = null;
        file.Phase = null;
        file.UtcStamp = null;
        return file;
    }

    /// <summary>
    /// No .bak: a retained prior version names entries that have since completed, and a
    /// crash-recovery read that falls back to it both surfaces them as pending and writes them
    /// back as the live file, so the phantom persists. The .tmp swap still keeps the live file
    /// from ever being torn. The journal answers only what is pending now.
    /// </summary>
    private void WriteFile(RewriteJournalFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        DurableJsonFile.Write(_path, JsonSerializer.Serialize(file, JsonOptions), keepBackup: false);
    }

    private void DeleteFile()
    {
        TryDelete(_path);
        // A .bak from a build that retained one still resurfaces as a phantom pending op.
        TryDelete(_path + ".bak");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Could not clear rewrite journal {path}", ex); }
    }
}
