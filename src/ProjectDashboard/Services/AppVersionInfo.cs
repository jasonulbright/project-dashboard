namespace ProjectDashboard.Services;

/// <summary>
/// This build's version, read from the assembly. The single source: the About line, the
/// update check's comparison, and the request header that names the caller all read it
/// here rather than carrying a copy that can drift from the built artifact.
/// </summary>
public static class AppVersionInfo
{
    private static readonly Version? Assembly =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>
    /// The version comparisons run against. An assembly with no version compares below
    /// every published release, so a build that lost its version reports an update rather
    /// than claiming to be current.
    /// </summary>
    public static Version Current { get; } = Assembly ?? new Version(0, 0, 0, 0);

    /// <summary>The form shown to a reader.</summary>
    public static string Display { get; } = $"v{Assembly?.ToString() ?? "unknown"}";
}
