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
///
/// A portable location that refuses writes reverts to the default layout and sets
/// <see cref="StartupNotice"/>. Keeping the portable root there would make every
/// state write fail with no trail, because log.txt lives in that same directory.
/// </summary>
public static class AppPaths
{
    /// <summary>Selects portable mode when present in the application directory.</summary>
    internal const string PortableMarkerFileName = "portable.marker";

    /// <summary>Portable state directory, created beside the executable on first write.</summary>
    internal const string PortableDataDirName = "data";

    /// <summary>
    /// A unified root of null means the default split layout. A non-null notice must
    /// reach the user: it reports state landing somewhere other than the portable folder.
    /// </summary>
    internal readonly record struct RootDecision(string? Root, string? Notice);

    private static readonly RootDecision Decision = ResolveRoot(
        Environment.GetEnvironmentVariable("PD_DATA_DIR"),
        AppContext.BaseDirectory,
        DirectoryAcceptsWrites);

    /// <summary>Machine-local state: settings, cache, log.</summary>
    public static string LocalDir { get; } = Decision.Root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectDashboard");

    /// <summary>Durable user data: the manifest index.</summary>
    public static string RoamingDir { get; } = Decision.Root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProjectDashboard");

    /// <summary>One-time startup message about the resolved layout, or null when there is nothing to report.</summary>
    public static string? StartupNotice { get; } = Decision.Notice;

    public static string SettingsFile => Path.Combine(LocalDir, "settings.json");
    public static string DiscoveryCacheFile => Path.Combine(LocalDir, "discovery-cache.json");
    public static string LogFile => Path.Combine(LocalDir, "log.txt");
    public static string ManifestIndexFile => Path.Combine(RoamingDir, "manifests.json");

    /// <summary>
    /// Picks the directory holding both LocalDir and RoamingDir. PD_DATA_DIR outranks
    /// the marker so a sandboxed run stays sandboxed even when launched from a portable
    /// directory, and is taken unprobed so a sandbox target can be created lazily. A
    /// directory named like the marker does not select portable mode — only a file does.
    /// The portable root is probed once here; probing on every write would multiply the
    /// cost and could still race an ACL change.
    /// </summary>
    internal static RootDecision ResolveRoot(
        string? dataDirOverride, string appDirectory, Func<string, bool> acceptsWrites)
    {
        if (dataDirOverride is { Length: > 0 })
            return new RootDecision(Path.GetFullPath(dataDirOverride), null);

        if (!File.Exists(Path.Combine(appDirectory, PortableMarkerFileName)))
            return new RootDecision(null, null);

        var dataDir = Path.GetFullPath(Path.Combine(appDirectory, PortableDataDirName));

        // Before the data directory exists, creating it is the write that must succeed,
        // so the application directory is what gets probed.
        var probed = Directory.Exists(dataDir) ? dataDir : Path.GetFullPath(appDirectory);

        return acceptsWrites(probed)
            ? new RootDecision(dataDir, null)
            : new RootDecision(null, PortableFallbackNotice(probed));
    }

    internal static string PortableFallbackNotice(string directory) =>
        $"Project Dashboard cannot write to {directory}, so portable mode is off for this session. "
        + "Settings, project metadata, and the log are being kept in your user profile instead. "
        + "Move the portable folder to a writable location to store them beside the executable again.";

    /// <summary>Creates and removes a probe file; never throws, since a failed probe is the answer.</summary>
    internal static bool DirectoryAcceptsWrites(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using var stream = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
