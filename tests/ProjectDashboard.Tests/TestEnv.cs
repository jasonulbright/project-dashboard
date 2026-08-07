using System.Runtime.CompilerServices;

namespace ProjectDashboard.Tests;

/// <summary>
/// Process-wide sandbox for the test run. PD_DATA_DIR redirects all app state
/// (log, settings, cache) away from the real profile, and GIT_CONFIG_GLOBAL +
/// GIT_CONFIG_NOSYSTEM pin git to a private config so machine settings
/// (default branch, autocrlf, commit signing) cannot change test outcomes.
/// Both must be set before any test code touches AppPaths or spawns git;
/// the module initializer runs before any test in this assembly.
/// </summary>
internal static class TestEnv
{
    /// <summary>Per-run fixture root under %TEMP%\pd-fixtures; removed on process exit.</summary>
    internal static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        Root = Path.Combine(Path.GetTempPath(), "pd-fixtures",
            "tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);

        Environment.SetEnvironmentVariable("PD_DATA_DIR", Path.Combine(Root, "app-data"));

        var gitConfig = Path.Combine(Root, "gitconfig");
        File.WriteAllText(gitConfig, """
            [user]
                name = Project Dashboard Tests
                email = tests@projectdashboard.invalid
            [init]
                defaultBranch = main
            [commit]
                gpgsign = false
            [tag]
                gpgsign = false
            [core]
                autocrlf = false
            [protocol "file"]
                allow = always
            """);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteTree(Root);
    }

    /// <summary>Fresh empty directory under the fixture root.</summary>
    internal static string NewDir(string prefix)
    {
        var dir = Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Best-effort recursive delete. Clears the read-only bit git sets on object
    /// files (Directory.Delete fails on read-only entries), retries once for
    /// transient handle locks, and never throws — a file still held by a
    /// straggling process must not fail a test that already passed.
    /// </summary>
    internal static void TryDeleteTree(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
    }
}
