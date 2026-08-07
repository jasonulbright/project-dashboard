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
    /// so this clears the manifest index as well. log.txt is exempt: it is not
    /// state the sandboxed tests read, and tests OUTSIDE this serialized
    /// collection append to it concurrently (File.AppendAllText opens without
    /// FileShare.Delete), so deleting it here races them into IOException.
    /// </summary>
    internal static void ResetDataDir()
    {
        Directory.CreateDirectory(AppPaths.LocalDir);
        foreach (var file in Directory.GetFiles(AppPaths.LocalDir))
        {
            if (string.Equals(file, AppPaths.LogFile, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A short-lived handle (antivirus scan, indexer, straggling
                // reader) makes the first delete transiently fail; one settled
                // retry, then the failure surfaces as the test's own.
                Thread.Sleep(100);
                File.Delete(file);
            }
        }
    }
}

[CollectionDefinition("app-data-sandbox")]
public sealed class AppDataSandboxCollection
{
}
