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

    public void Save(AppSettings settings)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                DurableJsonFile.Write(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex)
            {
                // A throw here reaches the window's Closing handler, where an unhandled
                // exception cancels the close and leaves the app unclosable.
                Log.Error($"Failed to save settings to {SettingsPath}", ex);
            }
        }
    }
}
