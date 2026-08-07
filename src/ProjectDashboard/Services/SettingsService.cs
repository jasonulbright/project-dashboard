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

    public AppSettings Load()
    {
        try
        {
            // Corrupt-file handling (quarantine + .bak recovery) lives in DurableJsonFile.Read.
            return DurableJsonFile.Read<AppSettings>(SettingsPath, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read settings at {SettingsPath} — using defaults", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        DurableJsonFile.Write(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
