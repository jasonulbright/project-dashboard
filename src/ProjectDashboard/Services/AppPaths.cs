using System.IO;

namespace ProjectDashboard.Services;

/// <summary>
/// Single source of truth for where app state lives.
///
/// Default layout:
///   %LOCALAPPDATA%\ProjectDashboard  → settings.json, discovery-cache.json, log.txt (machine-local)
///   %APPDATA%\ProjectDashboard       → manifests.json (durable user data, roams)
///
/// If the PD_DATA_DIR environment variable is set, ALL state lives under that
/// directory instead — used for portable installs and for exercising the app
/// against disposable data without touching the real profile.
/// </summary>
public static class AppPaths
{
    private static readonly string? Override =
        Environment.GetEnvironmentVariable("PD_DATA_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : null;

    /// <summary>Machine-local state: settings, cache, log.</summary>
    public static string LocalDir { get; } = Override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectDashboard");

    /// <summary>Durable user data: the manifest index.</summary>
    public static string RoamingDir { get; } = Override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProjectDashboard");

    public static string SettingsFile => Path.Combine(LocalDir, "settings.json");
    public static string DiscoveryCacheFile => Path.Combine(LocalDir, "discovery-cache.json");
    public static string LogFile => Path.Combine(LocalDir, "log.txt");
    public static string ManifestIndexFile => Path.Combine(RoamingDir, "manifests.json");
}
