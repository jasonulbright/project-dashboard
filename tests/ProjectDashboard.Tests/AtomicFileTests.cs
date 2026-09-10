using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

public class AtomicFileTests
{
    [Fact]
    public async Task ASiblingTemporaryFile_IsNeverOverwrittenOrDeleted()
    {
        var path = Path.Combine(TestEnv.NewDir("atomic-sibling"), "export.csv");
        await File.WriteAllTextAsync(path + ".tmp", "another application's file");

        await AtomicFile.WriteAllTextAsync(path, "complete export");

        Assert.Equal("complete export", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(path + ".tmp"));
        Assert.Equal("another application's file", await File.ReadAllTextAsync(path + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentSaves_EachPublishAWholeFile()
    {
        var directory = TestEnv.NewDir("atomic-concurrent");
        var path = Path.Combine(directory, "export.csv");
        var contents = Enumerable.Range(0, 12).Select(i => new string((char)('a' + i), 1024 * 1024)).ToArray();

        await Task.WhenAll(contents.Select(text => AtomicFile.WriteAllTextAsync(path, text)));

        Assert.Contains(await File.ReadAllTextAsync(path), contents);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CancelledSave_PreservesTheDestination()
    {
        var directory = TestEnv.NewDir("atomic-cancel");
        var path = Path.Combine(directory, "export.csv");
        await File.WriteAllTextAsync(path, "keep");
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAllTextAsync(path, "replace", stop.Token));

        Assert.Equal("keep", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory));
    }
}
