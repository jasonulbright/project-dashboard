using System.Security.AccessControl;
using System.Security.Principal;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

public class AppPathsPortableTests
{
    // The literal name, not the constant: the build script writes this file into the
    // portable archive, so renaming the constant alone silently breaks portable mode.
    private const string MarkerFileName = "portable.marker";

    private static readonly Func<string, bool> Writable = _ => true;
    private static readonly Func<string, bool> Unwritable = _ => false;

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

        var decision = AppPaths.ResolveRoot(null, appDir, Writable);

        Assert.Equal(Path.Combine(appDir, "data"), decision.Root);
        Assert.Null(decision.Notice);
    }

    [Fact]
    public void Marker_Absent_LeavesTheDefaultLayout()
    {
        var appDir = TestEnv.NewDir("installed-app");

        Assert.Null(AppPaths.ResolveRoot(null, appDir, Writable).Root);
        Assert.Null(AppPaths.ResolveRoot("", appDir, Writable).Root);
    }

    [Fact]
    public void Marker_Absent_IsNotProbed()
    {
        var appDir = TestEnv.NewDir("installed-app");

        var decision = AppPaths.ResolveRoot(null, appDir, Unwritable);

        Assert.Null(decision.Root);
        Assert.Null(decision.Notice);
    }

    [Fact]
    public void Marker_AsDirectory_DoesNotSelectPortableMode()
    {
        var appDir = TestEnv.NewDir("marker-dir-app");
        Directory.CreateDirectory(Path.Combine(appDir, MarkerFileName));

        Assert.Null(AppPaths.ResolveRoot(null, appDir, Writable).Root);
    }

    [Fact]
    public void EnvironmentOverride_OutranksTheMarker()
    {
        var appDir = AppDirWithMarker();
        var sandbox = TestEnv.NewDir("override");

        Assert.Equal(sandbox, AppPaths.ResolveRoot(sandbox, appDir, Unwritable).Root);
    }

    [Fact]
    public void EnvironmentOverride_IsResolvedToAnAbsolutePath()
    {
        Assert.Equal(
            Path.GetFullPath("relative-data"),
            AppPaths.ResolveRoot("relative-data", TestEnv.NewDir("installed-app"), Writable).Root);
    }

    [Fact]
    public void AppDirectoryWithTrailingSeparator_ResolvesTheSamePath()
    {
        var appDir = AppDirWithMarker();

        Assert.Equal(
            AppPaths.ResolveRoot(null, appDir, Writable).Root,
            AppPaths.ResolveRoot(null, appDir + Path.DirectorySeparatorChar, Writable).Root);
    }

    [Fact]
    public void UnwritablePortableRoot_FallsBackToTheDefaultLayout()
    {
        var decision = AppPaths.ResolveRoot(null, AppDirWithMarker(), Unwritable);

        Assert.Null(decision.Root);
    }

    [Fact]
    public void UnwritablePortableRoot_NamesTheDirectoryAndTheFallback()
    {
        var appDir = AppDirWithMarker();

        var notice = AppPaths.ResolveRoot(null, appDir, Unwritable).Notice;

        Assert.NotNull(notice);
        Assert.Contains(appDir, notice);
        Assert.Contains("cannot write", notice);
        Assert.Contains("user profile", notice);
    }

    [Fact]
    public void MissingDataDir_ProbesTheApplicationDirectory()
    {
        var appDir = AppDirWithMarker();
        var probed = new List<string>();

        AppPaths.ResolveRoot(null, appDir, d => { probed.Add(d); return true; });

        Assert.Equal([appDir], probed);
    }

    [Fact]
    public void ExistingDataDir_IsProbedItself()
    {
        var appDir = AppDirWithMarker();
        var dataDir = Path.Combine(appDir, "data");
        Directory.CreateDirectory(dataDir);
        var probed = new List<string>();

        AppPaths.ResolveRoot(null, appDir, d => { probed.Add(d); return true; });

        Assert.Equal([dataDir], probed);
    }

    [Fact]
    public void WriteProbe_AcceptsAWritableDirectoryAndLeavesNothingBehind()
    {
        var dir = TestEnv.NewDir("writable");

        Assert.True(AppPaths.DirectoryAcceptsWrites(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public void WriteProbe_RejectsAMissingDirectory()
    {
        Assert.False(AppPaths.DirectoryAcceptsWrites(
            Path.Combine(TestEnv.NewDir("absent"), "no-such-subdirectory")));
    }

    [Fact]
    public void WriteProbe_RejectsADirectoryDeniedByAcl()
    {
        var dir = TestEnv.NewDir("denied");
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.Write,
            AccessControlType.Deny);

        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            Assert.False(AppPaths.DirectoryAcceptsWrites(dir));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public void PortableRootDeniedByAcl_FallsBackAndNotifies()
    {
        var appDir = AppDirWithMarker();
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.Write,
            AccessControlType.Deny);

        var info = new DirectoryInfo(appDir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            var decision = AppPaths.ResolveRoot(null, appDir, AppPaths.DirectoryAcceptsWrites);

            Assert.Null(decision.Root);
            Assert.Contains(appDir, decision.Notice);
            Assert.False(Directory.Exists(Path.Combine(appDir, "data")));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }
}
