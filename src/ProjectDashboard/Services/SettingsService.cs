using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class SettingsService
{
    private static readonly string SettingsDir = AppPaths.LocalDir;

    private static readonly string SettingsPath = AppPaths.SettingsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // Serializes settings-file access across instances: concurrent writers collide
    // on the shared .tmp path, and a read during the swap can throw and fall back
    // to defaults, which the next Save would then persist as a settings wipe.
    // Load takes it too because a corrupt-file read writes the recovered backup.
    private static readonly object FileLock = new();

    private static readonly PublicationOrder Publication = new();

    /// <summary>
    /// Raised after a write that reached disk, carrying the state before and after it.
    /// The single live-apply path: consumers that would otherwise read a value once and
    /// hold it until relaunch re-derive from this instead. Raised outside the file lock,
    /// on the thread that called <see cref="Save"/>.
    ///
    /// A single writer is not required: writes are numbered under the file lock, so a
    /// write overtaken by a newer one is dropped instead of delivering a stale snapshot
    /// after the newer one. Subscribers therefore never regress to superseded state,
    /// whatever thread or scheduling order the writes arrive on.
    /// </summary>
    public event Action<SettingsChange>? Changed;

    public AppSettings Load()
    {
        try
        {
            lock (FileLock)
            {
                // Corrupt-file handling (quarantine + .bak recovery) lives in DurableJsonFile.Read.
                return DurableJsonFile.Read<AppSettings>(SettingsPath, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read settings at {SettingsPath} — using defaults", ex);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persists <paramref name="settings"/>, returning false when the write failed and
    /// the file on disk is unchanged. Failure is never thrown: a throw would reach the
    /// window's Closing handler, where an unhandled exception cancels the close and
    /// leaves the app unclosable.
    /// </summary>
    public bool Save(AppSettings settings)
    {
        AppSettings previous;
        long sequence;
        lock (FileLock)
        {
            try
            {
                // Read under the same lock as the write: a baseline taken outside it can
                // miss a racing writer, and the delta would then report no change.
                previous = Load();
                Directory.CreateDirectory(SettingsDir);
                DurableJsonFile.Write(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
                // Numbered under the lock that orders the writes, so the number and the file
                // agree on which write is newest.
                sequence = Publication.NextSequence();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save settings to {SettingsPath}", ex);
                return false;
            }
        }

        // Fire outside the lock: a subscriber runs arbitrary work (a re-scan starts here)
        // and must not hold every other writer off the settings file while it does. The
        // publication gate replaces the ordering the lock would have given: a write that
        // lost the race to a newer one delivers nothing rather than a stale snapshot.
        if (!Publication.TryPublish(sequence)) return true;

        try { Changed?.Invoke(new SettingsChange(previous, settings)); }
        catch (Exception ex) { Log.Warn("settings-changed subscriber threw", ex); }

        return true;
    }
}

/// <summary>
/// Orders event publication independently of thread scheduling. Each write takes a number
/// under the writer's lock; a write whose number is no longer the highest published is
/// dropped, so a subscriber can never be handed a snapshot older than one it already has.
/// Lock-free, so publishing never blocks another writer.
/// </summary>
internal sealed class PublicationOrder
{
    private long _next;
    private long _published;

    /// <summary>Takes the next number. Call it under the lock that orders the writes.</summary>
    public long NextSequence() => Interlocked.Increment(ref _next);

    /// <summary>True when this write is newer than every write already published.</summary>
    public bool TryPublish(long sequence)
    {
        while (true)
        {
            var published = Volatile.Read(ref _published);
            if (published >= sequence) return false;
            if (Interlocked.CompareExchange(ref _published, sequence, published) == published) return true;
        }
    }
}
