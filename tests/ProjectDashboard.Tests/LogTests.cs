using System.Runtime.CompilerServices;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// The log is the only record of a failure the app swallowed on purpose. A summary line alone
/// names what failed and not where, which is not enough to act on; and a file that only ever
/// grows is a session-length disk leak on a dashboard left open for days.
/// </summary>
public class LogTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner() => throw new InvalidOperationException("inner boom");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOuter()
    {
        try
        {
            ThrowInner();
        }
        catch (Exception ex)
        {
            throw new ApplicationException("outer boom", ex);
        }
    }

    [Fact]
    public void ALoggedException_CarriesItsStackTraceAndItsInnerChain()
    {
        var offset = File.Exists(AppPaths.LogFile) ? new FileInfo(AppPaths.LogFile).Length : 0;
        var context = $"log-detail-{Guid.NewGuid():N}";

        try
        {
            ThrowOuter();
        }
        catch (Exception ex)
        {
            Log.Error(context, ex);
        }

        var text = ReadLogFrom(offset);

        // The summary line the surfaces already grep for stays a single line.
        Assert.Contains($"[ERROR] {context} :: ApplicationException: outer boom", text);
        // The chain and the frames it came from are what a report was missing.
        Assert.Contains("---> System.InvalidOperationException: inner boom", text);
        Assert.Contains(nameof(ThrowInner), text);
        Assert.Contains(nameof(ThrowOuter), text);
    }

    [Fact]
    public void ALoggedContextWithoutAnException_StaysOneLine()
    {
        var offset = File.Exists(AppPaths.LogFile) ? new FileInfo(AppPaths.LogFile).Length : 0;
        var context = $"log-plain-{Guid.NewGuid():N}";

        Log.Warn(context);

        Assert.Contains($"[WARN] {context}", ReadLogFrom(offset));
    }

    [Fact]
    public void AFileUnderTheLimit_IsLeftWhereItIs()
    {
        var path = NewLogPath("roll-under");
        File.WriteAllText(path, "short");

        Log.RollIfOversized(path, maxBytes: 1024, keep: 3);

        Assert.True(File.Exists(path));
        Assert.Equal("short", File.ReadAllText(path));
        Assert.False(File.Exists(Rolled(path, 1)));
    }

    [Fact]
    public void AFileAtTheLimit_MovesAsideSoTheNextWriteStartsEmpty()
    {
        var path = NewLogPath("roll-at");
        File.WriteAllText(path, new string('x', 1024));

        Log.RollIfOversized(path, maxBytes: 1024, keep: 3);

        Assert.False(File.Exists(path));
        Assert.Equal(1024, new FileInfo(Rolled(path, 1)).Length);
    }

    [Fact]
    public void RepeatedRolls_KeepOnlyTheAllowedGenerations()
    {
        var path = NewLogPath("roll-generations");

        foreach (var generation in new[] { "one", "two", "three", "four" })
        {
            File.WriteAllText(path, generation.PadRight(1024, '.'));
            Log.RollIfOversized(path, maxBytes: 1024, keep: 3);
        }

        // Three files in all, and the live path is free for the next write: the two newest
        // generations behind it, with everything older discarded.
        Assert.False(File.Exists(path));
        Assert.StartsWith("four", File.ReadAllText(Rolled(path, 1)));
        Assert.StartsWith("three", File.ReadAllText(Rolled(path, 2)));
        Assert.False(File.Exists(Rolled(path, 3)));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    [Fact]
    public void AKeepCountWithNoRoomForAGeneration_DiscardsTheFile()
    {
        var path = NewLogPath("roll-keep-one");
        File.WriteAllText(path, new string('x', 2048));

        Log.RollIfOversized(path, maxBytes: 1024, keep: 1);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Rolled(path, 1)));
    }

    [Fact]
    public void AMissingFile_RollsToNothingWithoutThrowing()
    {
        var path = NewLogPath("roll-missing");

        Log.RollIfOversized(path, maxBytes: 1024, keep: 3);

        Assert.False(File.Exists(path));
    }

    private static string NewLogPath(string prefix) =>
        Path.Combine(TestEnv.NewDir(prefix), "log.txt");

    private static string Rolled(string path, int generation) =>
        Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.{generation}{Path.GetExtension(path)}");

    private static string ReadLogFrom(long offset)
    {
        using var stream = new FileStream(
            AppPaths.LogFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
