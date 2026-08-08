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

    /// <summary>
    /// Raised after a write that reached disk, carrying the state before and after it.
    /// The single live-apply path: consumers that would otherwise read a value once and
    /// hold it until relaunch re-derive from this instead. Raised outside the file lock,
    /// on the thread that called <see cref="Save"/>.
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
        lock (FileLock)
        {
            try
            {
                // Read under the same lock as the write: a baseline taken outside it can
                // miss a racing writer, and the delta would then report no change.
                previous = Load();
                Directory.CreateDirectory(SettingsDir);
                DurableJsonFile.Write(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save settings to {SettingsPath}", ex);
                return false;
            }
        }

        // Fire outside the lock: a subscriber runs arbitrary work (a re-scan starts here)
        // and must not hold every other writer off the settings file while it does.
        try { Changed?.Invoke(new SettingsChange(previous, settings)); }
        catch (Exception ex) { Log.Warn("settings-changed subscriber threw", ex); }

        return true;
    }
}
