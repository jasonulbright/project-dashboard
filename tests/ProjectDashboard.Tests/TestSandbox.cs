using System.IO;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// Every test that reads or writes the shared state files (settings.json,
/// manifests.json, discovery-cache.json) must join the "app-data-sandbox"
/// collection: the files are process-wide singletons, so those tests have to
/// run serially.
/// </summary>
internal static class TestSandbox
{
    /// <summary>
    /// Deletes every state file (not directories) so a test starts from an
    /// empty data dir. LocalDir == RoamingDir under the PD_DATA_DIR override,
    /// so this clears the manifest index as well.
    /// </summary>
    internal static void ResetDataDir()
    {
        Directory.CreateDirectory(AppPaths.LocalDir);
        foreach (var file in Directory.GetFiles(AppPaths.LocalDir))
            File.Delete(file);
    }
}

[CollectionDefinition("app-data-sandbox")]
public sealed class AppDataSandboxCollection
{
}
