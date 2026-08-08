using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

public class AppPathsPortableTests
{
    // The literal name, not the constant: the build script writes this file into the
    // portable archive, so renaming the constant alone silently breaks portable mode.
    private const string MarkerFileName = "portable.marker";

    private static string AppDirWithMarker()
    {
        var dir = TestEnv.NewDir("portable-app");
        File.WriteAllText(Path.Combine(dir, MarkerFileName), "");
        return dir;
    }

    [Fact]
    public void Marker_Present_PutsStateBesideTheExecutable()
    {
        var appDir = AppDirWithMarker();

        var root = AppPaths.ResolveUnifiedRoot(null, appDir);

        Assert.Equal(Path.Combine(appDir, "data"), root);
    }

    [Fact]
    public void Marker_Absent_LeavesTheDefaultLayout()
    {
        var appDir = TestEnv.NewDir("installed-app");

        Assert.Null(AppPaths.ResolveUnifiedRoot(null, appDir));
        Assert.Null(AppPaths.ResolveUnifiedRoot("", appDir));
    }

    [Fact]
    public void Marker_AsDirectory_DoesNotSelectPortableMode()
    {
        var appDir = TestEnv.NewDir("marker-dir-app");
        Directory.CreateDirectory(Path.Combine(appDir, MarkerFileName));

        Assert.Null(AppPaths.ResolveUnifiedRoot(null, appDir));
    }

    [Fact]
    public void EnvironmentOverride_OutranksTheMarker()
    {
        var appDir = AppDirWithMarker();
        var sandbox = TestEnv.NewDir("override");

        Assert.Equal(sandbox, AppPaths.ResolveUnifiedRoot(sandbox, appDir));
    }

    [Fact]
    public void EnvironmentOverride_IsResolvedToAnAbsolutePath()
    {
        Assert.Equal(
            Path.GetFullPath("relative-data"),
            AppPaths.ResolveUnifiedRoot("relative-data", TestEnv.NewDir("installed-app")));
    }

    [Fact]
    public void AppDirectoryWithTrailingSeparator_ResolvesTheSamePath()
    {
        var appDir = AppDirWithMarker();

        Assert.Equal(
            AppPaths.ResolveUnifiedRoot(null, appDir),
            AppPaths.ResolveUnifiedRoot(null, appDir + Path.DirectorySeparatorChar));
    }
}
