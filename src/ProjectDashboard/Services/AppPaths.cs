using System.IO;

namespace ProjectDashboard.Services;

/// <summary>
/// Single source of truth for where app state lives.
///
/// Default layout:
///   %LOCALAPPDATA%\ProjectDashboard  → settings.json, discovery-cache.json, log.txt (machine-local)
///   %APPDATA%\ProjectDashboard       → manifests.json (durable user data, roams)
///
/// Two conditions collapse both into a single directory instead: the PD_DATA_DIR
/// environment variable, and a portable marker file sitting beside the executable.
/// PD_DATA_DIR is used for exercising the app against disposable data without
/// touching the real profile; the marker ships only in the portable archive, so an
/// installed copy always takes the default layout.
/// </summary>
public static class AppPaths
{
    /// <summary>Selects portable mode when present in the application directory.</summary>
    internal const string PortableMarkerFileName = "portable.marker";

    /// <summary>Portable state directory, created beside the executable on first write.</summary>
    internal const string PortableDataDirName = "data";

    private static readonly string? UnifiedRoot = ResolveUnifiedRoot(
        Environment.GetEnvironmentVariable("PD_DATA_DIR"), AppContext.BaseDirectory);

    /// <summary>Machine-local state: settings, cache, log.</summary>
    public static string LocalDir { get; } = UnifiedRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectDashboard");

    /// <summary>Durable user data: the manifest index.</summary>
    public static string RoamingDir { get; } = UnifiedRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProjectDashboard");

    public static string SettingsFile => Path.Combine(LocalDir, "settings.json");
    public static string DiscoveryCacheFile => Path.Combine(LocalDir, "discovery-cache.json");
    public static string LogFile => Path.Combine(LocalDir, "log.txt");
    public static string ManifestIndexFile => Path.Combine(RoamingDir, "manifests.json");

    /// <summary>
    /// The directory holding both LocalDir and RoamingDir, or null for the default
    /// split layout. PD_DATA_DIR outranks the marker so a sandboxed run stays
    /// sandboxed even when launched from a portable directory. A directory named
    /// like the marker does not select portable mode — only a file does.
    /// </summary>
    internal static string? ResolveUnifiedRoot(string? dataDirOverride, string appDirectory)
    {
        if (dataDirOverride is { Length: > 0 })
            return Path.GetFullPath(dataDirOverride);

        return File.Exists(Path.Combine(appDirectory, PortableMarkerFileName))
            ? Path.GetFullPath(Path.Combine(appDirectory, PortableDataDirName))
            : null;
    }
}
