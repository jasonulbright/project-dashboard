using System.IO;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Locations for the rails' persisted state, all under AppPaths.LocalDir so a
/// PD_DATA_DIR override redirects them with the rest of app state and nothing is
/// ever written inside a target repository.
/// </summary>
internal static class SafetyPaths
{
    /// <summary>Root of the per-repo backup directories: &lt;LocalDir&gt;\backups\&lt;repo-key&gt;.</summary>
    public static string BackupsRoot => Path.Combine(AppPaths.LocalDir, "backups");

    public static string BackupDirFor(string repoKey) => Path.Combine(BackupsRoot, repoKey);

    /// <summary>The single pending-rewrite journal; presence at startup means an interrupted op.</summary>
    public static string JournalFile => Path.Combine(AppPaths.LocalDir, "rewrite-journal.json");
}
