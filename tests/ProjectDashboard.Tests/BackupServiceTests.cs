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

    /// <summary>Truncates the bundle the moment `git bundle create` returns, so the file exists and the exit code is zero while its contents are unreadable.</summary>
    private sealed class CorruptsBundleAfterCreateGitService : GitService
    {
        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            var result = await base.RunAsync(repoPath, argv, environment, ct, timeout);
            await CorruptOnBundleCreate(argv, ct);
            return result;
        }

        public override async Task<ProcessResult> RunWithInputAsync(
            string repoPath, IEnumerable<string> args, string standardInput,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            var result = await base.RunWithInputAsync(repoPath, argv, standardInput, ct, timeout);
            await CorruptOnBundleCreate(argv, ct);
            return result;
        }

        private static Task CorruptOnBundleCreate(List<string> argv, CancellationToken ct) =>
            argv is ["bundle", "create", var path, ..]
                ? File.WriteAllTextAsync(path, "not a valid git bundle", ct)
                : Task.CompletedTask;
    }

    [Fact]
    public async Task CreateBackup_BundleFailsVerification_ThrowsAndLeavesNoBackupBehind()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = new BackupService(new CorruptsBundleAfterCreateGitService(), new SettingsService());

        var thrown = await Assert.ThrowsAsync<BackupException>(() => service.CreateBackupAsync(repo.Path, "History rewrite"));
        Assert.Contains("verification", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing on disk claims to be a usable backup of this repository.
        Assert.Empty(await service.ListBackupsAsync(repo.Path));
        var dir = SafetyPaths.BackupDirFor(RepoKey.For(repo.Path));
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bundle") : []);
    }

    /// <summary>Deletes a branch in the window between the refs capture and the bundle write, whichever of the two run shapes carries the bundle command.</summary>
    private sealed class DeletesRefBeforeBundleCreateGitService(string repoPath, string branch) : GitService
    {
        private int _fired;

        public override Task<ProcessResult> RunAsync(
            string repo, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            DeleteOnBundleCreate(argv);
            return base.RunAsync(repo, argv, environment, ct, timeout);
        }

        public override Task<ProcessResult> RunWithInputAsync(
            string repo, IEnumerable<string> args, string standardInput,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            DeleteOnBundleCreate(argv);
            return base.RunWithInputAsync(repo, argv, standardInput, ct, timeout);
        }

        private void DeleteOnBundleCreate(List<string> argv)
        {
            if (argv is ["bundle", "create", ..] && Interlocked.Exchange(ref _fired, 1) == 0)
                Git.RunAsync(repoPath, ["branch", "-D", branch]).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task CreateBackup_RefDeletedWhileBundling_StillBundlesTheObjectTheSidecarRecords()
    {
        using var repo = await RailsRepo.CreateAsync();
        await repo.GitAsync("switch", "-q", "-c", "doomed");
        repo.Write("doomed.txt", "only reachable from this branch\n");
        await repo.CommitAllAsync("doomed tip");
        var doomedOid = (await repo.GitAsync("rev-parse", "refs/heads/doomed")).Trim();
        await repo.GitAsync("switch", "-q", "main");

        // refs/heads/doomed is gone by the time the bundle is written, so its tip is reachable
        // from no ref — yet the sidecar still names it and the restore would set a ref to it.
        var service = new BackupService(new DeletesRefBeforeBundleCreateGitService(repo.Path, "doomed"), new SettingsService());
        var handle = await service.CreateBackupAsync(repo.Path);

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RefsSnapshot>(File.ReadAllText(handle.RefsSnapshotPath))!;
        Assert.Contains(snapshot.Refs, r => r.Name == "refs/heads/doomed" && r.ObjectId == doomedOid);

        // The bundle carries that object, so what the sidecar names can actually be restored.
        using var target = await RailsRepo.CreateAsync("bundle-read");
        await target.GitAsync("bundle", "unbundle", handle.BundlePath);
        Assert.Equal(0, (await Git.TryRunAsync(target.Path, "cat-file", "-e", doomedOid)).ExitCode);
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

        var result = await service.RestoreAsync(handle, allowDirty: false);
        Assert.True(result.Success, result.Message);

        Assert.Equal(before, await repo.RefStateAsync());
        // The working tree was reset too, not just the refs.
        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "file.txt")));
    }

    /// <summary>
    /// A default clone carries refs/remotes/origin/HEAD as a symref onto refs/remotes/origin/main.
    /// A reconciliation naming both is rejected whole by `git update-ref --stdin`, so a restore
    /// that reconciled every ref for-each-ref returns refused on essentially every cloned
    /// repository — the layout almost every real one has.
    /// </summary>
    [Fact]
    public async Task Restore_InAClonedRepoWithOriginHead_RestoresLocalRefsAndLeavesTheRemoteOnesAlone()
    {
        using var repo = await RailsRepo.CreateClonedAsync();
        var service = NewService();

        var beforeLocal = (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim();
        var handle = await service.CreateBackupAsync(repo.Path);

        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        await repo.GitAsync("branch", "stray");
        Assert.NotEqual(beforeLocal, (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim());

        var result = await service.RestoreAsync(handle, allowDirty: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(beforeLocal, (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim());
        Assert.DoesNotContain("refs/heads/stray", await repo.RefStateAsync(), StringComparison.Ordinal);

        // The remote-tracking pair is untouched: the symref still resolves, and the restore says
        // what it left alone rather than implying it put those refs back.
        Assert.Equal("refs/remotes/origin/main",
            (await repo.GitAsync("symbolic-ref", "refs/remotes/origin/HEAD")).Trim());
        Assert.Equal(beforeLocal, (await repo.GitAsync("rev-parse", "refs/remotes/origin/main")).Trim());
        Assert.Contains("left as they are", result.Message, StringComparison.Ordinal);
    }

    /// <summary>A symbolic ref outside refs/remotes/ reaches the same rejection, so the exclusion is the symref itself, not the namespace.</summary>
    [Fact]
    public async Task Restore_WithASymbolicBranchAlias_ReconcilesTheRestWithoutTouchingIt()
    {
        using var repo = await RailsRepo.CreateAsync();
        await repo.GitAsync("symbolic-ref", "refs/heads/alias", "refs/heads/main");
        var service = NewService();
        var before = (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim();
        var handle = await service.CreateBackupAsync(repo.Path);

        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");

        var result = await service.RestoreAsync(handle, allowDirty: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(before, (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim());
        Assert.Equal("refs/heads/main", (await repo.GitAsync("symbolic-ref", "refs/heads/alias")).Trim());
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

        var result = await service.RestoreAsync(handle, allowDirty: false);
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

        var result = await service.RestoreAsync(handle, allowDirty: false);

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
        var result = await racing.RestoreAsync(handle, allowDirty: false);

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

    /// <summary>Fails the ref-layout read the restore reconciles against, leaving every other git call alone.</summary>
    private sealed class UnreadableRefLayoutGitService : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            return argv.Contains("for-each-ref")
                ? Task.FromResult(new ProcessResult(128, "", "fatal: could not read refs", TimedOut: false))
                : base.RunAsync(repoPath, argv, environment, ct, timeout);
        }
    }

    [Fact]
    public async Task Restore_CurrentRefLayoutUnreadable_RefusesInsteadOfSkippingEveryDelete()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        // A branch the backup never saw: a restore that read the layout as empty would leave it
        // standing and still report success.
        await repo.GitAsync("branch", "stray");
        var mutated = await repo.RefStateAsync();

        var blind = new BackupService(new UnreadableRefLayoutGitService(), new SettingsService());
        var result = await blind.RestoreAsync(handle, allowDirty: false);

        Assert.False(result.Success);
        Assert.False(result.RefsRestored);
        Assert.Contains("ref layout", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mutated, await repo.RefStateAsync());
        Assert.Contains("refs/heads/stray", await repo.RefStateAsync(), StringComparison.Ordinal);
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

        var result = await service.RestoreAsync(handle, allowDirty: true);

        Assert.True(result.Success, result.Message);
        Assert.True(result.WorktreeWasDirty);
        Assert.Equal(2, result.DiscardedChangeCount);
        // The reset actually ran — the dirty edit is gone.
        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "file.txt")));
    }

    /// <summary>
    /// The reconciliation repoints the branch under an index that still matches the newer history,
    /// so a count read after it sees every old-versus-restored difference as a staged change. A
    /// tree the gate just verified clean must not be reported as having lost uncommitted work.
    /// </summary>
    [Fact]
    public async Task Restore_CleanTreeOverNewerHistory_ReportsNothingDiscarded()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        repo.Write("added.txt", "later work\n");
        await repo.CommitAllAsync("third");

        var result = await service.RestoreAsync(handle, allowDirty: false);

        Assert.True(result.Success, result.Message);
        Assert.False(result.WorktreeWasDirty);
        Assert.Equal(0, result.DiscardedChangeCount);
        // The restore still did its work — the newer history is gone from the tree.
        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "file.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(repo.Path, "added.txt")));
    }

    [Fact]
    public async Task Restore_DirtyWorktreeWithoutAllowDirty_RefusesAndKeepsTheUncommittedWork()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        repo.Write("file.txt", "dirty\n");
        repo.Write("scratch.txt", "unstaged\n");
        await repo.GitAsync("branch", "stray");
        var mutated = await repo.RefStateAsync();

        var result = await service.RestoreAsync(handle, allowDirty: false);

        Assert.False(result.Success);
        Assert.False(result.RefsRestored);
        Assert.Contains("uncommitted change(s)", result.Message, StringComparison.Ordinal);
        // Neither the edits nor the refs were touched.
        Assert.Equal("dirty\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "file.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(repo.Path, "scratch.txt")));
        Assert.Equal(mutated, await repo.RefStateAsync());
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

        Assert.True(await service.DeleteBackupAsync(first));
        Assert.False(File.Exists(first.BundlePath));
        Assert.False(File.Exists(first.RefsSnapshotPath));
        Assert.False(service.BackupFilesRemain(first));
        Assert.Single(await service.ListBackupsAsync(repo.Path));
    }

    /// <summary>
    /// A bundle another process holds open cannot be removed. Removing its refs snapshot anyway
    /// would strip the pair down to bytes no restore can use — and the listing skips a bundle with
    /// no sidecar, so those bytes would also stop being visible. The sidecar goes only after the
    /// bundle does, leaving a failed delete with the backup exactly as it was.
    /// </summary>
    [Fact]
    public async Task DeleteBackup_WhenTheBundleIsHeldOpen_LeavesTheBackupWholeAndSaysSo()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path, "History rewrite");

        using (new FileStream(handle.BundlePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await service.DeleteBackupAsync(handle));
            Assert.True(File.Exists(handle.BundlePath));
            Assert.True(File.Exists(handle.RefsSnapshotPath));
            Assert.True(service.BackupFilesRemain(handle));
            Assert.Single(await service.ListBackupsAsync(repo.Path));
        }

        // Once the hold is released the same delete finishes.
        Assert.True(await service.DeleteBackupAsync(handle));
        Assert.Empty(await service.ListBackupsAsync(repo.Path));
    }

    // ── Verify, standalone ──────────────────────────────────────────────────

    /// <summary>
    /// A backup this service just wrote verified once as it was written; the standalone check has
    /// to reach the same verdict later, since it is the precondition a restore acts on.
    /// </summary>
    [Fact]
    public async Task VerifyBackup_AnIntactBundle_Verifies()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path, "History rewrite");

        var result = await service.VerifyBackupAsync(handle);

        Assert.Equal(BundleVerifyState.Verified, result.State);
        Assert.True(result.Verified);
    }

    /// <summary>Verifying reads; it must not unbundle, move a ref, or touch the working tree.</summary>
    [Fact]
    public async Task VerifyBackup_ChangesNothingInTheRepository()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);
        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        var before = await repo.RefStateAsync();

        Assert.True((await service.VerifyBackupAsync(handle)).Verified);

        Assert.Equal(before, await repo.RefStateAsync());
    }

    [Fact]
    public async Task VerifyBackup_ACorruptBundle_Fails()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);
        File.WriteAllText(handle.BundlePath, "not a valid git bundle");

        var result = await service.VerifyBackupAsync(handle);

        Assert.Equal(BundleVerifyState.Failed, result.State);
        Assert.NotEqual("", result.Detail);
    }

    [Fact]
    public async Task VerifyBackup_AMissingBundle_Fails()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);
        File.Delete(handle.BundlePath);

        var result = await service.VerifyBackupAsync(handle);

        Assert.Equal(BundleVerifyState.Failed, result.State);
        Assert.Contains("missing", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reports whatever `git bundle verify` was going to report as a timeout instead.</summary>
    private sealed class TimesOutOnBundleVerifyGitService : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            return argv is ["bundle", "verify", ..]
                ? Task.FromResult(new ProcessResult(-1, "", "timed out", TimedOut: true))
                : base.RunAsync(repoPath, argv, environment, ct, timeout);
        }
    }

    /// <summary>
    /// A killed check answered nothing. Calling that a corrupt bundle would send a reader to
    /// delete a backup that is intact, so the state stays unknown and the restore still refuses.
    /// </summary>
    [Fact]
    public async Task VerifyBackup_AVerifyThatTimesOut_IsUnknownRatherThanFailed()
    {
        using var repo = await RailsRepo.CreateAsync();
        var handle = await NewService().CreateBackupAsync(repo.Path);
        var service = new BackupService(new TimesOutOnBundleVerifyGitService(), new SettingsService());

        var result = await service.VerifyBackupAsync(handle);

        Assert.Equal(BundleVerifyState.Unknown, result.State);
        Assert.False(result.Verified);

        var before = await repo.RefStateAsync();
        var restore = await service.RestoreAsync(handle, allowDirty: false);
        Assert.False(restore.Success);
        Assert.False(restore.RefsRestored);
        Assert.Equal(before, await repo.RefStateAsync());
    }

    // ── Size ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MeasureBackupBytes_CountsTheBundleAndItsSidecar()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);

        var measured = service.MeasureBackupBytes(handle);

        Assert.NotNull(measured);
        Assert.Equal(
            new FileInfo(handle.BundlePath).Length + new FileInfo(handle.RefsSnapshotPath).Length,
            measured!.Value);
    }

    /// <summary>A deleted backup occupies nothing, which is a total rather than an unreadable one.</summary>
    [Fact]
    public async Task MeasureBackupBytes_AfterDelete_IsZeroNotNull()
    {
        using var repo = await RailsRepo.CreateAsync();
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path);
        await service.DeleteBackupAsync(handle);

        Assert.Equal(0L, service.MeasureBackupBytes(handle));
    }
}
