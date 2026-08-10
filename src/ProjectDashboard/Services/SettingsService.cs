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
    /// Raise order matches write order only while the writes share a thread. Across
    /// threads the numbering drops a write that a newer one has already published by the
    /// time it reaches the gate; a raise that passed the gate before the newer write
    /// published still runs, so a subscriber can still be handed the older snapshot last.
    /// </summary>
    public event Action<SettingsChange>? Changed;

    public AppSettings Load()
    {
        try
        {
            lock (FileLock)
            {
                // Corrupt-file handling (quarantine + .bak recovery) lives in DurableJsonFile.Read.
                return Migrated(DurableJsonFile.Read<AppSettings>(SettingsPath, JsonOptions) ?? new AppSettings());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read settings at {SettingsPath} — using defaults", ex);
            return Migrated(new AppSettings());
        }
    }

    /// <summary>
    /// Brings a file written before the root list up to it, in memory only. A load that wrote
    /// back would turn every read into a disk write and race the writer's own lock; the next
    /// save persists the shape.
    /// </summary>
    private static AppSettings Migrated(AppSettings settings)
    {
        ProjectRootSettings.Migrate(settings);
        Taxonomy.EnsureSeeded(settings);
        return settings;
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
                // Settled against what is on disk: a caller that built an AppSettings by hand
                // carries no root list, and one that load-mutated the singular root or its
                // exclusions means that edit.
                ProjectRootSettings.Reconcile(settings, previous);
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
        // and must not hold every other writer off the settings file while it does. A write
        // already superseded when it reaches the gate delivers nothing rather than a stale
        // snapshot.
        if (!Publication.TryPublish(sequence)) return true;

        try { Changed?.Invoke(new SettingsChange(previous, settings)); }
        catch (Exception ex) { Log.Warn("settings-changed subscriber threw", ex); }

        return true;
    }
}

/// <summary>
/// Drops a settings write that a newer one has already published. Each write takes a number
/// under the writer's lock; a write descheduled between taking its number and reaching the
/// gate delivers nothing instead of an out-of-date snapshot. The gate covers a write that
/// arrives after a newer publication, not one already past it — a raise in flight when a
/// newer write publishes still completes. Lock-free, so publishing never blocks a writer.
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
