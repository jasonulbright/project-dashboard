using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The git the safety rollup runs, against real repositories under the fixture root.
///
/// Every claim here is proved fail-first: the reflog-only walk is shown to report none before the
/// commit is orphaned, and a bundle is shown to verify before it is corrupted. A check that only
/// ever sees the failing state cannot tell a working detector from one that always fires.
/// </summary>
[Collection("app-data-sandbox")]
public class SafetyScannerTests
{
    private readonly ITestOutputHelper _output;

    public SafetyScannerTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static SafetyScanner NewScanner(OperationHistory history, BackupService? backups = null) =>
        new(new GitService(), backups, history);

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("safety-ledger"));

    // ── Reflog-only commits ─────────────────────────────────────────────────

    /// <summary>
    /// A commit no ref reaches but a reflog does is exactly what a backup bundle never captured —
    /// the bundle holds refs. Proved in both directions against one repository.
    /// </summary>
    [Fact]
    public async Task AReflogOnlyCommit_IsCountedOnlyOnceItIsOrphaned()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("reflog-only");
        var history = NewHistory();
        var scanner = NewScanner(history);

        repo.WriteFile("second.txt", "work\n");
        await repo.CommitAllAsync("about to be abandoned");

        var before = await scanner.CountReflogOnlyAsync(repo.Path);
        Assert.Null(before.Error);
        Assert.Equal(0, before.Count);

        var abandoned = await repo.HeadShaAsync();
        await repo.GitAsync("reset", "--hard", "HEAD~1");

        var after = await scanner.CountReflogOnlyAsync(repo.Path);
        Assert.Null(after.Error);
        Assert.Equal(1, after.Count);
        _output.WriteLine($"orphaned {abandoned[..8]}: {before.Count} -> {after.Count}");
    }

    /// <summary>The walk is recorded, so "last checked" survives the session that ran it.</summary>
    [Fact]
    public async Task TheReflogWalk_IsRecordedAgainstTheRepository()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("reflog-record");
        var history = NewHistory();

        await NewScanner(history).CountReflogOnlyAsync(repo.Path);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationCategory.Maintenance, record.Category);
        Assert.Equal("Check for reflog-only commits", record.Label);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
    }

    /// <summary>
    /// A walk git refused is not a repository with no reflog-only commits. The count is meaningless
    /// beside an error and the caller is told so rather than shown a zero.
    /// </summary>
    [Fact]
    public async Task AWalkGitRefused_ReportsAnErrorRatherThanACountOfZero()
    {
        var notARepo = TestEnv.NewDir("reflog-not-a-repo");
        var history = NewHistory();

        var result = await NewScanner(history).CountReflogOnlyAsync(notARepo);

        Assert.NotNull(result.Error);
        Assert.Equal(OperationOutcome.Failed, Assert.Single(history.Tail(notARepo).Records).Outcome);
    }

    // ── Backup verification ─────────────────────────────────────────────────

    /// <summary>
    /// The check is the one a restore runs first, so the row says what the restore button would
    /// say. A bundle that passes today and not tomorrow is the case it exists for, so both are
    /// exercised against one repository.
    /// </summary>
    [Fact]
    public async Task ACorruptedBundle_IsRefusedWhereAnIntactOnePasses()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("verify-bundle");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var backups = new BackupService(new GitService(), settings, NewHistory());
        var handle = await backups.CreateBackupAsync(repo.Path, "fixture");
        var history = NewHistory();
        var scanner = NewScanner(history, backups);

        var intact = await scanner.VerifyBackupsAsync(repo.Path);
        Assert.Null(intact.Error);
        Assert.Equal(1, intact.OnDisk);
        Assert.Equal(1, intact.Checked);
        Assert.Empty(intact.FailedStamps);

        Corrupt(handle.BundlePath);

        var corrupted = await scanner.VerifyBackupsAsync(repo.Path);
        Assert.Equal(1, corrupted.Checked);
        Assert.Equal(handle.UtcStamp, Assert.Single(corrupted.FailedStamps));
        Assert.Empty(corrupted.UnknownStamps);
        _output.WriteLine($"bundle {handle.UtcStamp} verified, then failed after corruption");
    }

    /// <summary>
    /// A verify killed on its timeout answered nothing about the bundle. Counting it as a failure
    /// sends a reader to replace or delete a backup that may be intact, so the unanswered ones are
    /// carried in their own list and never summed into the failures.
    /// </summary>
    [Fact]
    public async Task AVerifyThatTimedOut_IsUnansweredRatherThanFailed()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("verify-timeout");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var handle = await new BackupService(new GitService(), settings, NewHistory())
            .CreateBackupAsync(repo.Path, "fixture");

        var history = NewHistory();
        var timingOut = new BackupService(new TimeOutOnVerifyGitService(), settings, NewHistory());

        var result = await NewScanner(history, timingOut).VerifyBackupsAsync(repo.Path);

        Assert.Equal(1, result.Checked);
        Assert.Empty(result.FailedStamps);
        Assert.Equal(handle.UtcStamp, Assert.Single(result.UnknownStamps));

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationOutcome.Unknown, record.Outcome);
        Assert.Contains("could not be verified", record.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("failed verification", record.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports every <c>bundle verify</c> as killed on its budget. Every other git call runs for
    /// real, so the fixture's listing and its repository are the genuine ones.
    /// </summary>
    private sealed class TimeOutOnVerifyGitService : GitService
    {
        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var listed = args.ToList();
            return listed.Contains("bundle") && listed.Contains("verify")
                ? new ProcessResult(-1, "", "", TimedOut: true)
                : await base.RunAsync(repoPath, listed, environment, ct, timeout);
        }
    }

    /// <summary>
    /// The bound on what the check establishes, measured rather than assumed: git reads the bundle
    /// header and the prerequisites and stops, so a bundle whose packed objects are truncated still
    /// passes. This is why no result is ever reported as the objects being intact — and it is the
    /// same bound the restore itself carries, because the restore runs this same command.
    /// </summary>
    [Fact]
    public async Task ABundleWithATruncatedPack_StillPasses_WhichIsWhyNothingClaimsTheObjectsAreIntact()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("verify-bound");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var backups = new BackupService(new GitService(), settings, NewHistory());
        var handle = await backups.CreateBackupAsync(repo.Path, "fixture");

        var bytes = await File.ReadAllBytesAsync(handle.BundlePath);
        await File.WriteAllBytesAsync(handle.BundlePath, bytes[..(bytes.Length / 2)]);

        var result = await NewScanner(NewHistory(), backups).VerifyBackupsAsync(repo.Path);

        Assert.Empty(result.FailedStamps);
        Assert.Empty(result.UnknownStamps);
        Assert.Contains("not the packed objects", SafetyCopy.BackupCheckLimit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The outcome is the durable half of the answer: "last verified when" is a ledger fact, not
    /// one this session holds and forgets.
    /// </summary>
    [Fact]
    public async Task AFailedVerification_IsRecordedAsAFailureNamingTheBundle()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("verify-record");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var backups = new BackupService(new GitService(), settings, NewHistory());
        var handle = await backups.CreateBackupAsync(repo.Path, "fixture");
        Corrupt(handle.BundlePath);
        var history = NewHistory();

        await NewScanner(history, backups).VerifyBackupsAsync(repo.Path);

        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationCategory.Maintenance, record.Category);
        Assert.Equal(OperationOutcome.Failed, record.Outcome);
        Assert.Contains(handle.UtcStamp, record.Detail, StringComparison.Ordinal);
    }

    /// <summary>A repository with no bundles has nothing to verify, which is not a failure.</summary>
    [Fact]
    public async Task ARepositoryWithNoBundles_ReportsNoneOnDiskRatherThanAFailure()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("verify-empty");
        var settings = new SettingsService();
        settings.Save(new AppSettings());
        var backups = new BackupService(new GitService(), settings, NewHistory());

        var result = await NewScanner(NewHistory(), backups).VerifyBackupsAsync(repo.Path);

        Assert.Null(result.Error);
        Assert.Equal(0, result.OnDisk);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>
    /// With no backup store there is no answer, and the check says so. A zero here would report a
    /// repository as having no backups when nothing looked for any.
    /// </summary>
    [Fact]
    public async Task WithNoBackupStore_VerificationReportsThatNothingWasChecked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("verify-nostore");

        var result = await NewScanner(NewHistory()).VerifyBackupsAsync(repo.Path);

        Assert.NotNull(result.Error);
        Assert.Equal(0, result.Checked);
    }

    // ── Cheap tier ──────────────────────────────────────────────────────────

    /// <summary>
    /// The cheap scan reads every local branch, which is what upgrades divergence beyond the
    /// current branch the card pass already read.
    /// </summary>
    [Fact]
    public async Task TheCheapScan_ReadsEveryLocalBranchAndCountsTheBackups()
    {
        using var origin = await TempRepo.CreateWithCommitAsync("cheap-origin");
        using var bare = await TempRepo.CreateBareFromAsync(origin, "cheap-bare");
        using var clone = await TempRepo.CloneFromAsync(bare, "cheap-clone");
        await clone.GitAsync("switch", "-c", "side");
        clone.WriteFile("side.txt", "s\n");
        await clone.CommitAllAsync("side work");

        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var backups = new BackupService(new GitService(), settings, NewHistory());
        await backups.CreateBackupAsync(clone.Path, "fixture");

        var scan = await NewScanner(NewHistory(), backups).ScanAsync(clone.Path);

        Assert.Null(scan.Error);
        Assert.Equal(1, scan.BackupCount);
        Assert.Contains(scan.Branches, b => b.Name == "main");
        Assert.Contains(scan.Branches, b => b.Name == "side");
    }

    /// <summary>
    /// A ref read git refused is not a repository whose branches all track cleanly. The error is
    /// kept rather than folded into an empty list.
    /// </summary>
    [Fact]
    public async Task ACheapScanGitRefused_KeepsTheError()
    {
        var notARepo = TestEnv.NewDir("cheap-not-a-repo");

        var scan = await NewScanner(NewHistory()).ScanAsync(notARepo);

        Assert.NotNull(scan.Error);
        Assert.Empty(scan.Branches);
    }

    // ── What an object-store reading cannot stand in for ────────────────────

    /// <summary>
    /// Why no expensive answer is keyed on the object store: abandoning a commit produces a
    /// reflog-only commit and leaves the loose and packed counts byte-identical, because the
    /// objects are all still there and only unreferenced. A key that cannot move on the operation
    /// that changes the answer is a key that serves the stale answer for exactly that operation.
    /// </summary>
    [Fact]
    public async Task AbandoningACommit_ChangesTheAnswerAndNotTheObjectCounts()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("store-reading");
        repo.WriteFile("second.txt", "work\n");
        await repo.CommitAllAsync("about to be abandoned");
        var git = new GitService();
        var scanner = NewScanner(NewHistory());

        var before = await git.CountObjectsAsync(repo.Path);
        Assert.Equal(0, (await scanner.CountReflogOnlyAsync(repo.Path)).Count);

        await repo.GitAsync("reset", "--hard", "HEAD~1");

        var after = await git.CountObjectsAsync(repo.Path);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.LooseObjects, after.LooseObjects);
        Assert.Equal(before.PackedObjects, after.PackedObjects);
        Assert.Equal(1, (await scanner.CountReflogOnlyAsync(repo.Path)).Count);
        _output.WriteLine($"counts {before.LooseObjects}/{before.PackedObjects} unchanged; reflog-only 0 -> 1");
    }

    /// <summary>
    /// Clobbers the bundle's signature line, which is the part git reads. Corrupting the pack
    /// instead leaves the bundle passing — see the bound this file measures.
    /// </summary>
    private static void Corrupt(string bundlePath)
    {
        var bytes = File.ReadAllBytes(bundlePath);
        Assert.True(bytes.Length > 64, "fixture bundle is too small to corrupt meaningfully");
        Array.Fill(bytes, (byte)0, 0, 16);
        File.WriteAllBytes(bundlePath, bytes);
    }
}
