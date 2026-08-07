using ProjectDashboard.Services.Safety;
using Xunit;

namespace ProjectDashboard.Tests;

public class RewriteJournalTests
{
    private static string TempJournalPath() =>
        System.IO.Path.Combine(TestEnv.NewDir("journal"), "rewrite-journal.json");

    [Fact]
    public async Task Begin_LeavesEntry_ReadableByANewInstance()
    {
        // A crash between Begin and Complete leaves the journal; a fresh instance
        // (the next launch) must read it back intact.
        var path = TempJournalPath();
        var entry = new RewriteJournalEntry
        {
            RepoPath = @"C:\projects\demo",
            Phase = "swap",
            UtcStamp = "20260807-120000000",
            BackupHandle = new BackupHandle { RepoPath = @"C:\projects\demo", UtcStamp = "20260807-115900000", BundlePath = "b.bundle" }
        };

        await new RewriteJournal(path).BeginAsync(entry);

        var pending = await new RewriteJournal(path).ReadPendingAsync();
        Assert.NotNull(pending);
        Assert.Equal(@"C:\projects\demo", pending!.RepoPath);
        Assert.Equal("swap", pending.Phase);
        Assert.Equal("b.bundle", pending.BackupHandle!.BundlePath);
    }

    [Fact]
    public async Task Complete_ClearsJournal_NoPendingAfterward()
    {
        var path = TempJournalPath();
        var journal = new RewriteJournal(path);
        await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\demo", Phase = "backup" });

        await journal.CompleteAsync();

        Assert.Null(await new RewriteJournal(path).ReadPendingAsync());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task ReadPending_NoJournal_ReturnsNull()
    {
        Assert.Null(await new RewriteJournal(TempJournalPath()).ReadPendingAsync());
    }

    [Fact]
    public async Task RecoveryService_Startup_DetectsPendingWithoutRestoring()
    {
        var path = TempJournalPath();
        var journal = new RewriteJournal(path);
        await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\demo", Phase = "swap" });

        var service = new RewriteRecoveryService(journal);
        RewriteJournalEntry? raised = null;
        service.PendingDetected += e => raised = e;

        await service.StartAsync(CancellationToken.None);

        Assert.True(service.DetectionComplete);
        Assert.NotNull(service.Pending);
        Assert.Equal(@"C:\projects\demo", service.Pending!.RepoPath);
        Assert.Same(service.Pending, raised);
        // Detection must not clear the journal — the entry is held for a restore prompt.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RecoveryService_Startup_CleanShutdown_NothingPending()
    {
        var service = new RewriteRecoveryService(new RewriteJournal(TempJournalPath()));
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.DetectionComplete);
        Assert.Null(service.Pending);
    }
}
