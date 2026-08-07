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

        await journal.CompleteAsync(@"C:\projects\demo");

        Assert.Null(await new RewriteJournal(path).ReadPendingAsync());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task Begin_TwoRepositories_BothPendingAndCompletingOneKeepsTheOther()
    {
        // One repository's success must not delete another's marker: that would orphan the
        // second repository's backup with nothing left on disk pointing at it.
        var path = TempJournalPath();
        var journal = new RewriteJournal(path);
        await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\alpha", Phase = "rebase" });
        await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\beta", Phase = "swap" });

        Assert.Equal(2, (await journal.ReadAllPendingAsync()).Count);

        await journal.CompleteAsync(@"C:\projects\beta");

        var remaining = Assert.Single(await new RewriteJournal(path).ReadAllPendingAsync());
        Assert.Equal(@"C:\projects\alpha", remaining.RepoPath);
        Assert.Equal("rebase", remaining.Phase);
        Assert.Null(await journal.ReadPendingAsync(@"C:\projects\beta"));
        Assert.NotNull(await journal.ReadPendingAsync(@"C:\projects\alpha"));
    }

    [Fact]
    public async Task Begin_TwoRepositoriesConcurrently_KeepsBothEntries()
    {
        // Two repositories rewrite at once: RepoBusyRegistry serializes within one repository,
        // never across several. Unserialized read-modify-write both loses an entry and collides
        // on the shared .tmp, which throws out of Begin after a backup was already taken.
        for (var round = 0; round < 25; round++)
        {
            var path = TempJournalPath();
            var journal = new RewriteJournal(path);
            using var gate = new ManualResetEventSlim(false);

            var alpha = Task.Run(() =>
            {
                gate.Wait();
                return journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\alpha", Phase = "rebase" });
            });
            var beta = Task.Run(() =>
            {
                gate.Wait();
                return journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\beta", Phase = "swap" });
            });

            gate.Set();
            await Task.WhenAll(alpha, beta);

            var pending = await new RewriteJournal(path).ReadAllPendingAsync();
            Assert.Equal(2, pending.Count);
            Assert.Contains(pending, e => e.RepoPath == @"C:\projects\alpha");
            Assert.Contains(pending, e => e.RepoPath == @"C:\projects\beta");
        }
    }

    [Fact]
    public async Task Complete_RacingABeginForAnotherRepository_LeavesExactlyTheBegunEntry()
    {
        // Either interleaving is legitimate; both must end with alpha cleared and beta pending.
        // A lost update leaves alpha resurrected or beta missing, and the .tmp collision throws
        // out of Complete, which would strand a finished operation as permanently pending.
        for (var round = 0; round < 25; round++)
        {
            var path = TempJournalPath();
            var journal = new RewriteJournal(path);
            await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\alpha", Phase = "rebase" });
            using var gate = new ManualResetEventSlim(false);

            var complete = Task.Run(() => { gate.Wait(); return journal.CompleteAsync(@"C:\projects\alpha"); });
            var begin = Task.Run(() =>
            {
                gate.Wait();
                return journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\beta", Phase = "swap" });
            });

            gate.Set();
            await Task.WhenAll(complete, begin);

            var pending = await new RewriteJournal(path).ReadAllPendingAsync();
            var remaining = Assert.Single(pending);
            Assert.Equal(@"C:\projects\beta", remaining.RepoPath);
        }
    }

    [Fact]
    public async Task RecoveryService_Startup_SurfacesEveryPendingRepository()
    {
        var path = TempJournalPath();
        var journal = new RewriteJournal(path);
        await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\alpha", Phase = "rebase" });
        await journal.BeginAsync(new RewriteJournalEntry { RepoPath = @"C:\projects\beta", Phase = "swap" });

        var service = new RewriteRecoveryService(journal);
        var raised = new List<RewriteJournalEntry>();
        service.PendingDetected += e => raised.Add(e);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, service.Pending.Count);
        Assert.Equal(2, raised.Count);
        Assert.Contains(service.Pending, e => e.RepoPath == @"C:\projects\alpha");
        Assert.Contains(service.Pending, e => e.RepoPath == @"C:\projects\beta");
    }

    [Fact]
    public async Task ReadPending_SingleEntryJournalFromAnOlderBuild_IsStillRecovered()
    {
        // The file used to hold one entry at the top level. A pending marker in that shape is
        // precisely what must not be dropped by an upgrade.
        var path = TempJournalPath();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "RepoPath": "C:\\projects\\legacy",
              "BackupHandle": { "RepoPath": "C:\\projects\\legacy", "UtcStamp": "20260807-115900000", "BundlePath": "b.bundle" },
              "Phase": "swap",
              "UtcStamp": "20260807-120000000"
            }
            """);

        var journal = new RewriteJournal(path);
        var pending = Assert.Single(await journal.ReadAllPendingAsync());
        Assert.Equal(@"C:\projects\legacy", pending.RepoPath);
        Assert.Equal("swap", pending.Phase);
        Assert.Equal("b.bundle", pending.BackupHandle!.BundlePath);

        // And it is addressable by repo path, so completing that repo clears it.
        Assert.NotNull(await journal.ReadPendingAsync(@"C:\projects\legacy"));
        await journal.CompleteAsync(@"C:\projects\legacy");
        Assert.Empty(await journal.ReadAllPendingAsync());
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
        var pending = Assert.Single(service.Pending);
        Assert.Equal(@"C:\projects\demo", pending.RepoPath);
        Assert.Same(pending, raised);
        // Detection must not clear the journal — the entry is held for a restore prompt.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RecoveryService_Startup_CorruptJournal_DegradesGracefully()
    {
        // A journal.json of garbage bytes (a torn write, disk corruption) must not throw out
        // of startup: DurableJsonFile quarantines it and the read returns null, so the app
        // launches with nothing pending rather than crashing before the window shows.
        var path = TempJournalPath();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "}{ not json at all \0\0\0");

        var service = new RewriteRecoveryService(new RewriteJournal(path));

        await service.StartAsync(CancellationToken.None); // must not throw

        Assert.True(service.DetectionComplete);
        Assert.Empty(service.Pending);
        // The corrupt file is quarantined, not left live to re-break the next launch.
        Assert.False(File.Exists(path));
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Assert.NotEmpty(Directory.GetFiles(dir, "rewrite-journal.json.corrupt-*"));
    }

    [Fact]
    public async Task RecoveryService_Startup_CleanShutdown_NothingPending()
    {
        var service = new RewriteRecoveryService(new RewriteJournal(TempJournalPath()));
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.DetectionComplete);
        Assert.Empty(service.Pending);
    }
}
