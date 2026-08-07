using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// Points PD_DATA_DIR at a per-run temp sandbox before anything touches
/// AppPaths, whose directories freeze at type initialization. Every test that
/// reads or writes the shared state files (settings.json, manifests.json,
/// discovery-cache.json) must join the "app-data-sandbox" collection: the files
/// are process-wide singletons, so those tests have to run serially.
/// </summary>
internal static class TestSandbox
{
    internal static readonly string Root =
        Path.Combine(Path.GetTempPath(), "pd-fixtures", "tests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("PD_DATA_DIR", Root);

    /// <summary>Deletes every state file (not directories) so a test starts from an empty data dir.</summary>
    internal static void ResetDataDir()
    {
        Directory.CreateDirectory(Root);
        foreach (var file in Directory.GetFiles(Root))
            File.Delete(file);
    }
}

[CollectionDefinition("app-data-sandbox")]
public sealed class AppDataSandboxCollection
{
}
