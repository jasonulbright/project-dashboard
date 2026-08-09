using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using Xunit;

namespace ProjectDashboard.Tests;

[Collection("app-data-sandbox")]
public class BackupServiceTests
{
    public BackupServiceTests() => TestSandbox.ResetDataDir();

    private static BackupService NewService() => new(new GitService(), new SettingsService());

    [Fact]
    public async Task CreateBackup_BundleExistsAndVerifies_SnapshotMatchesForEachRef()
    {
        using var repo = await RailsRepo.CreateAsync();
        await repo.GitAsync("branch", "feature");
        await repo.GitAsync("tag", "v1");

        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        Assert.True(File.Exists(handle.BundlePath), "bundle file should exist");
        Assert.True(File.Exists(handle.RefsSnapshotPath), "refs sidecar should exist");

        // The bundle must independently verify against the repo.
        var verify = await new GitService().RunAsync(repo.Path, ["bundle", "verify", handle.BundlePath]);
        Assert.True(verify.Success, verify.FirstError);

        // The snapshot must record exactly the refs for-each-ref reports.
        var expected = (await repo.GitAsync("for-each-ref", "--format=%(objectname) %(refname)")).Trim()
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(x => x).ToArray();

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RefsSnapshot>(File.ReadAllText(handle.RefsSnapshotPath))!;
        var actual = snapshot.Refs.Select(r => $"{r.ObjectId} {r.Name}").OrderBy(x => x).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal("refs/heads/main", snapshot.HeadRef);
        Assert.Equal((await repo.GitAsync("rev-parse", "HEAD")).Trim(), snapshot.HeadObjectId);
    }

    [Fact]
    public async Task Restore_AfterMutation_ReturnsRefsToSnapshotExactly()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();

        var before = await repo.RefStateAsync();
        var handle = await service.CreateBackupAsync(repo.Path);

        // Mutate: extra commits on main plus a whole new branch the backup never saw.
        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        repo.Write("file.txt", "three\n");
        await repo.CommitAllAsync("third");
        await repo.GitAsync("branch", "stray");
        Assert.NotEqual(before, await repo.RefStateAsync());

        var result = await service.RestoreAsync(handle);
        Assert.True(result.Success, result.Message);

        Assert.Equal(before, await repo.RefStateAsync());
        // The working tree was reset too, not just the refs.
        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "file.txt")));
    }

    [Fact]
    public async Task Restore_BundleFailsVerification_RefusesWithoutMutating()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        // Advance the repo, then corrupt the bundle. Restore must refuse and leave the
        // advanced state untouched — never a partial restore.
        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        var mutated = await repo.RefStateAsync();

        File.WriteAllText(handle.BundlePath, "not a valid git bundle");

        var result = await service.RestoreAsync(handle);
        Assert.False(result.Success);
        Assert.Contains("verification", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mutated, await repo.RefStateAsync());
    }

    [Fact]
    public async Task Restore_ReconciliationTransactionFails_LeavesEveryRefUnchanged()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        // Advance the repo and add a stray branch the backup never saw.
        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        await repo.GitAsync("branch", "stray");
        var mutated = await repo.RefStateAsync();

        // Poison the snapshot: add a ref pointing at an object that does not exist. The
        // update-ref --stdin transaction must reject the whole script, so the stray branch is
        // NOT deleted and main is NOT rewound — all-or-nothing.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RefsSnapshot>(
            File.ReadAllText(handle.RefsSnapshotPath))!;
        snapshot.Refs.Add(new RefEntry
        {
            Name = "refs/heads/phantom",
            ObjectId = "0123456789abcdef0123456789abcdef01234567"
        });
        File.WriteAllText(handle.RefsSnapshotPath,
            System.Text.Json.JsonSerializer.Serialize(snapshot,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var result = await service.RestoreAsync(handle);

        Assert.False(result.Success);
        Assert.Contains("reconciliation", result.Message, StringComparison.OrdinalIgnoreCase);
        // Not one ref moved — the transaction rolled back entirely, and the phantom ref the
        // failed script named was never created.
        Assert.Equal(mutated, await repo.RefStateAsync());
        Assert.DoesNotContain("refs/heads/phantom", await repo.RefStateAsync());
    }

    /// <summary>Moves a branch immediately before the restore's ref transaction runs — after the restore has read the layout it reconciles against.</summary>
    private sealed class MovesRefBeforeRefTransaction(string repoPath, string branch, string target) : GitService
    {
        private int _fired;

        public override Task<ProcessResult> RunWithInputAsync(
            string repo, IEnumerable<string> args, string standardInput,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                Git.RunAsync(repoPath, ["branch", "-f", branch, target]).GetAwaiter().GetResult();
            return base.RunWithInputAsync(repo, args, standardInput, ct, timeout);
        }
    }

    [Fact]
    public async Task Restore_RefMovedAfterTheLayoutWasRead_RefusesInsteadOfOverwritingIt()
    {
        using var repo = await RailsRepo.CreateAsync();
        await repo.GitAsync("branch", "feature");
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        var mutated = await repo.RefStateAsync();

        // refs/heads/feature moves to main's tip after the restore read the current layout, so
        // the value its reconciliation was built against is stale by the time it commits.
        var racing = new BackupService(new MovesRefBeforeRefTransaction(repo.Path, "feature", "main"), new SettingsService());
        var result = await racing.RestoreAsync(handle);

        Assert.False(result.Success);
        Assert.Contains("reconciliation", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.RefsRestored);
        // Not one ref was rewound, and the external move stands.
        var after = await repo.RefStateAsync();
        Assert.NotEqual(mutated, after);
        Assert.Equal(
            (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim(),
            (await repo.GitAsync("rev-parse", "refs/heads/feature")).Trim());
    }

    [Fact]
    public async Task Restore_DirtyWorktree_ReportsDiscardedChangeCount()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        // A tracked edit plus a brand-new untracked file: two porcelain lines the reset discards.
        repo.Write("file.txt", "dirty\n");
        repo.Write("scratch.txt", "unstaged\n");

        var result = await service.RestoreAsync(handle);

        Assert.True(result.Success, result.Message);
        Assert.True(result.WorktreeWasDirty);
        Assert.Equal(2, result.DiscardedChangeCount);
        // The reset actually ran — the dirty edit is gone.
        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "file.txt")));
    }

    [Fact]
    public async Task Retention_PrunesToConfiguredCount_KeepingNewest()
    {
        using var repo = await RailsRepo.CreateAsync();
        new SettingsService().Save(new AppSettings { BackupRetentionCount = 3 });
        var service = NewService();

        var handles = new List<BackupHandle>();
        for (var i = 0; i < 5; i++)
            handles.Add(await service.CreateBackupAsync(repo.Path));

        var listed = await service.ListBackupsAsync(repo.Path);
        Assert.Equal(3, listed.Count);

        // The three newest (by stamp) survive; the two oldest are gone from disk.
        var survivors = handles.OrderByDescending(h => h.UtcStamp, StringComparer.Ordinal).Take(3)
            .Select(h => h.UtcStamp).OrderBy(x => x).ToArray();
        Assert.Equal(survivors, listed.Select(h => h.UtcStamp).OrderBy(x => x).ToArray());
        foreach (var pruned in handles.OrderByDescending(h => h.UtcStamp, StringComparer.Ordinal).Skip(3))
            Assert.False(File.Exists(pruned.BundlePath), $"{pruned.UtcStamp} bundle should be pruned");
    }

    [Fact]
    public async Task Retention_ChangedMidSession_AppliesToTheNextPrune()
    {
        using var repo = await RailsRepo.CreateAsync();
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 10 });
        var service = NewService();

        for (var i = 0; i < 4; i++)
            await service.CreateBackupAsync(repo.Path);
        Assert.Equal(4, (await service.ListBackupsAsync(repo.Path)).Count);

        // The service holds no cached count: the next prune reads the new value, with no
        // relaunch between the settings write and the backup that enforces it.
        settings.Save(new AppSettings { BackupRetentionCount = 2 });
        await service.CreateBackupAsync(repo.Path);

        Assert.Equal(2, (await service.ListBackupsAsync(repo.Path)).Count);
    }

    [Fact]
    public async Task ListBackups_NewestFirst_AndDeleteRemovesBoth()
    {
        using var repo = await RailsRepo.CreateAsync();
        new SettingsService().Save(new AppSettings { BackupRetentionCount = 10 });
        var service = NewService();

        var first = await service.CreateBackupAsync(repo.Path);
        var second = await service.CreateBackupAsync(repo.Path);

        var listed = await service.ListBackupsAsync(repo.Path);
        Assert.Equal(2, listed.Count);
        Assert.Equal(second.UtcStamp, listed[0].UtcStamp); // newest first

        await service.DeleteBackupAsync(first);
        Assert.False(File.Exists(first.BundlePath));
        Assert.False(File.Exists(first.RefsSnapshotPath));
        Assert.Single(await service.ListBackupsAsync(repo.Path));
    }
}
