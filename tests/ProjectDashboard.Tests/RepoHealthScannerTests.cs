using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Health;
using ProjectDashboard.Services.Safety;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The git the health page runs, against real repositories under the fixture root.
///
/// Every claim is proved fail-first: connectivity is shown to pass before an object is removed,
/// signing is shown to read as off before it is switched on, and a bundle is shown to verify before
/// it is corrupted. A check that only ever sees the failing state cannot tell a working detector
/// from one that always fires.
/// </summary>
[Collection("app-data-sandbox")]
public class RepoHealthScannerTests
{
    private readonly ITestOutputHelper _output;

    public RepoHealthScannerTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("health-ledger"));

    private static RepoHealthScanner NewScanner(OperationHistory? history = null, BackupService? backups = null) =>
        new(new GitService(), backups, history ?? NewHistory());

    private static HealthCheck Check(IReadOnlyList<HealthCheck> checks, string id) =>
        Assert.Single(checks, c => c.Id == id);

    /// <summary>
    /// Cancels once the hooks-path configuration has been read, putting the cancellation exactly
    /// where a page-leave lands between a check's git call and its directory walk.
    /// </summary>
    private sealed class CancelAfterConfigGitService(CancellationTokenSource cts) : GitService
    {
        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            var result = await base.RunAsync(repoPath, list, environment, ct, timeout);
            if (list.Contains("core.hooksPath")) await cts.CancelAsync();
            return result;
        }
    }

    /// <summary>
    /// Answers the object-store walk as a run the budget killed: no line delivered, and a result
    /// carrying the timeout. A large repository on slow storage produces exactly this, and no
    /// fixture can be made big enough to produce it on demand.
    /// </summary>
    private sealed class TimedOutWalkGitService : GitService
    {
        public override Task<ProcessResult> RunStreamingAsync(
            string repoPath, IEnumerable<string> args, Action<string> onStdOutLine,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            return list.Contains("cat-file")
                ? Task.FromResult(new ProcessResult(-1, "", "", TimedOut: true))
                : base.RunStreamingAsync(repoPath, list, onStdOutLine, ct, timeout);
        }
    }

    // ── git version ─────────────────────────────────────────────────────────

    /// <summary>
    /// Only the token after the literal `version` is read. The other dotted-numeric tokens on a
    /// distributor's line belong to an install path or a bundled tool, and a decision made from one
    /// is made from a number that has nothing to do with git.
    /// </summary>
    [Theory]
    [InlineData("git version 2.45.1.windows.1", "2.45.1.windows.1")]
    [InlineData("git version 2.39.5", "2.39.5")]
    [InlineData("git version 2.51.0\n", "2.51.0")]
    [InlineData("git 2.45.1", null)]
    [InlineData("", null)]
    public void TheVersionToken_IsTheOneAfterTheWordVersion(string line, string? expected)
    {
        Assert.Equal(expected, GitVersion.TokenFrom(line));
    }

    [Theory]
    [InlineData("git version 2.45.1.windows.1", 2, 45)]
    [InlineData("git version 3.0", 3, 0)]
    public void TheVersionMajorAndMinor_IgnoreEverythingPastTheMinor(string line, int major, int minor)
    {
        Assert.Equal((major, minor), GitVersion.MajorMinorFrom(line));
    }

    [Theory]
    [InlineData("git version next")]
    [InlineData("git version 2")]
    [InlineData("nothing here")]
    public void AnUnreadableVersion_IsNullRatherThanAGuess(string line)
    {
        Assert.Null(GitVersion.MajorMinorFrom(line));
    }

    [Fact]
    public async Task TheVersionRow_NamesTheGitTheApplicationRuns()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-version");

        var check = Check(await NewScanner().QuickAsync(repo.Path), HealthCheckId.GitVersion);

        Assert.Equal(HealthState.Ok, check.State);
        Assert.StartsWith("git ", check.Summary, StringComparison.Ordinal);
        _output.WriteLine(check.Summary);
    }

    // ── Lock files ──────────────────────────────────────────────────────────

    /// <summary>
    /// Only index.lock was ever visible to this application. A held HEAD.lock blocks ref writes
    /// exactly as an index.lock blocks index writes, so the scan covers the git directory rather
    /// than one filename — proved by planting a lock that is not index.lock.
    /// </summary>
    [Fact]
    public async Task ALockThatIsNotTheIndexLock_IsFoundWhereNoneWasBefore()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-locks");
        var scanner = NewScanner();

        var clean = Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Locks);
        Assert.Equal(HealthState.Ok, clean.State);

        var planted = Path.Combine(repo.Path, ".git", "HEAD.lock");
        await File.WriteAllTextAsync(planted, "");

        var found = Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Locks);
        Assert.NotEqual(HealthState.Ok, found.State);
        Assert.Contains("HEAD.lock", found.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lock a live git process is holding right now looks the same on disk as an abandoned one.
    /// The age split is what separates them, and a fresh lock is reported without the word that
    /// would send a reader to delete it.
    /// </summary>
    [Fact]
    public async Task AFreshLock_IsReportedWithoutBeingCalledAbandoned()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-fresh-lock");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".git", "index.lock"), "");
        var gitDir = Path.Combine(repo.Path, ".git");

        var fresh = await NewScanner().LocksAsync(gitDir, DateTime.UtcNow);
        Assert.Equal(HealthState.Warn, fresh.State);
        Assert.DoesNotContain("looks abandoned", fresh.Detail, StringComparison.Ordinal);

        var later = await NewScanner().LocksAsync(gitDir, DateTime.UtcNow + TimeSpan.FromHours(1));
        Assert.Equal(HealthState.Bad, later.State);
        Assert.Contains("looks abandoned", later.Detail, StringComparison.Ordinal);
    }

    /// <summary>Nothing on this tab deletes a lock; the row says so where a reader would otherwise assume it had.</summary>
    [Fact]
    public async Task TheLockRow_SaysNothingHereRemovesALock()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-lock-copy");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".git", "config.lock"), "");

        var check = await NewScanner().LocksAsync(Path.Combine(repo.Path, ".git"), DateTime.UtcNow);

        Assert.Contains(HealthCopy.LocksAreReportedNotRemoved, check.Detail, StringComparison.Ordinal);
    }

    /// <summary>A git directory nothing could resolve is unknown, never a repository with no locks.</summary>
    [Fact]
    public async Task AnUnresolvableGitDirectory_IsUnknownRatherThanClean()
    {
        var check = await NewScanner().LocksAsync(null, DateTime.UtcNow);

        Assert.Equal(HealthState.Unknown, check.State);
    }

    // ── Signing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Signing is reported as configuration and labelled as configuration. Nothing here verifies a
    /// signature, so the row must not read as evidence that any commit carries one.
    /// </summary>
    [Fact]
    public async Task SigningIsReportedAsConfiguration_AndSaysSo()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-signing");
        var scanner = NewScanner();

        var off = Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Signing);
        Assert.Equal(HealthState.Ok, off.State);
        Assert.Contains("Neither", off.Summary, StringComparison.Ordinal);
        Assert.Contains(HealthCopy.SigningIsConfigurationOnly, off.Detail, StringComparison.Ordinal);

        await repo.GitAsync("config", "commit.gpgsign", "true");

        var on = Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Signing);
        Assert.Equal(HealthState.Warn, on.State);
        Assert.Contains("no signing key", on.Summary, StringComparison.Ordinal);
    }

    /// <summary>A configured key turns the warning off; the row is then a plain statement of configuration.</summary>
    [Fact]
    public async Task SigningWithAKeyConfigured_IsNotWarnedAbout()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-signing-key");
        await repo.GitAsync("config", "commit.gpgsign", "true");
        await repo.GitAsync("config", "user.signingkey", "ABCD1234");

        var check = Check(await NewScanner().QuickAsync(repo.Path), HealthCheckId.Signing);

        Assert.Equal(HealthState.Ok, check.State);
        Assert.Contains("Commits are configured to be signed", check.Summary, StringComparison.Ordinal);
    }

    // ── Hooks ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HooksAreListed_AndTheSamplesAreNotCountedAsInstalled()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-hooks");
        var scanner = NewScanner();

        var none = Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Hooks);
        Assert.Equal(HealthState.Ok, none.State);
        Assert.Equal("No hook is installed.", none.Summary);

        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\n");

        var installed = Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Hooks);
        Assert.Equal(HealthState.Ok, installed.State);
        Assert.Contains("pre-commit", installed.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hooks directory outside the repository is a legitimate setup and also the shape a
    /// repository carries when something else has redirected what runs on its commits. Either way
    /// the reader is told where, rather than shown a hook list with no location.
    /// </summary>
    [Fact]
    public async Task AHooksPathOutsideTheRepository_IsReportedWithItsPath()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-hooks-outside");
        var elsewhere = TestEnv.NewDir("health-hooks-elsewhere");
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "pre-push"), "#!/bin/sh\n");
        await repo.GitAsync("config", "core.hooksPath", elsewhere.Replace('\\', '/'));

        var check = Check(await NewScanner().QuickAsync(repo.Path), HealthCheckId.Hooks);

        Assert.Equal(HealthState.Warn, check.State);
        Assert.Contains("outside this repository", check.Summary, StringComparison.Ordinal);
        Assert.Contains("pre-push", check.Summary, StringComparison.Ordinal);
    }

    // ── LFS, remotes, object store ──────────────────────────────────────────

    /// <summary>
    /// A repository with no LFS rule is not applicable rather than clear. The distinction is the
    /// whole point of the state: nothing was measured because there was nothing to measure.
    /// </summary>
    [Fact]
    public async Task ARepositoryWithNoLfsRule_IsNotApplicableRatherThanOk()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-no-lfs");

        var check = Check(await NewScanner().QuickAsync(repo.Path), HealthCheckId.Lfs);

        Assert.Equal(HealthState.NotApplicable, check.State);
    }

    /// <summary>
    /// With an LFS rule present the row stops being not-applicable and becomes an answer about the
    /// filter — which of the two answers depends on whether git-lfs is on this machine, and that is
    /// exactly what the row reports.
    /// </summary>
    [Fact]
    public async Task AnLfsRule_TurnsTheRowIntoAnAnswerAboutTheFilter()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-lfs");
        repo.WriteFile(".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n");

        var check = Check(await NewScanner().QuickAsync(repo.Path), HealthCheckId.Lfs);

        Assert.NotEqual(HealthState.NotApplicable, check.State);
        Assert.Contains(check.State, new[] { HealthState.Ok, HealthState.Bad });
        _output.WriteLine($"{check.State}: {check.Summary}");
    }

    [Fact]
    public async Task ARepositoryWithNoRemote_IsNotApplicableForBothRemoteRows()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-no-remote");
        var scanner = NewScanner();

        Assert.Equal(HealthState.NotApplicable,
            Check(await scanner.QuickAsync(repo.Path), HealthCheckId.Remotes).State);
        Assert.Equal(HealthState.NotApplicable,
            (await scanner.CheckReachabilityAsync(repo.Path)).State);
    }

    [Fact]
    public async Task TheObjectStoreRow_SaysItEstablishesNothingAboutIntegrity()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-objects");

        var check = Check(await NewScanner().QuickAsync(repo.Path), HealthCheckId.ObjectStore);

        Assert.Equal(HealthState.Ok, check.State);
        Assert.Contains(HealthCopy.SizeIsNotIntegrity, check.Detail, StringComparison.Ordinal);
    }

    // ── Deep tier: fsck ─────────────────────────────────────────────────────

    /// <summary>
    /// The failure that actually breaks a repository is a missing object, and it is what
    /// `--connectivity-only` is for. Proved in both directions against one repository: the pass is
    /// shown before the object is removed, so a detector that always fired would fail here.
    /// </summary>
    [Fact]
    public async Task AMissingObject_IsFoundByConnectivityWhereACleanRepositoryPasses()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-fsck");
        var scanner = NewScanner();

        var clean = await scanner.CheckConnectivityAsync(repo.Path);
        Assert.Equal(HealthState.Ok, clean.State);
        Assert.Equal(HealthCopy.ConnectivityClean, clean.Summary);

        var blob = (await repo.GitAsync("rev-parse", "HEAD:file.txt")).Trim();
        var loose = Path.Combine(repo.Path, ".git", "objects", blob[..2], blob[2..]);
        // git writes loose objects read-only, which is what makes them immutable rather than undeletable.
        File.SetAttributes(loose, FileAttributes.Normal);
        File.Delete(loose);

        var broken = await scanner.CheckConnectivityAsync(repo.Path);
        Assert.Equal(HealthState.Bad, broken.State);
        _output.WriteLine($"removed {blob[..8]}: {clean.State} -> {broken.State}");
    }

    /// <summary>
    /// A clean connectivity pass reports what it read, and the object contents it did not. Read as
    /// "healthy" it would stand in for the check nobody ran.
    /// </summary>
    [Fact]
    public async Task ACleanConnectivityPass_NeverClaimsTheObjectsWereRead()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-fsck-copy");

        var check = await NewScanner().CheckConnectivityAsync(repo.Path);

        Assert.Contains("object contents not verified", check.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("healthy", check.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The strict read is its own press and its own answer; nothing escalates into it.</summary>
    [Fact]
    public async Task TheStrictCheck_IsSeparateFromConnectivityAndReportsItsOwnLimit()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-strict");

        var check = await NewScanner().CheckStrictAsync(repo.Path);

        Assert.Equal(HealthState.Ok, check.State);
        Assert.Equal(HealthCopy.StrictClean, check.Summary);
    }

    /// <summary>
    /// Every deep check lands on the repository's operation ledger as a Maintenance record, which
    /// is what makes "when was this last checked" a durable fact rather than one this session holds.
    /// </summary>
    [Fact]
    public async Task EveryDeepCheck_IsRecordedAgainstTheRepository()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-record");
        var history = NewHistory();
        var scanner = NewScanner(history);

        await scanner.CheckConnectivityAsync(repo.Path);
        await scanner.CheckStrictAsync(repo.Path);
        await scanner.CheckReachabilityAsync(repo.Path);
        await scanner.CheckLargeObjectsAsync(repo.Path);

        var records = history.Tail(repo.Path).Records;
        Assert.Equal(4, records.Count);
        Assert.All(records, r => Assert.Equal(OperationCategory.Maintenance, r.Category));
        Assert.Contains(records, r => r.Label == "Check object connectivity");
        Assert.Contains(records, r => r.Label == "Check every object");
        Assert.Contains(records, r => r.Label == "Check remote reachability");
        Assert.Contains(records, r => r.Label == "List the largest objects");
    }

    // ── Deep tier: remote reachability ──────────────────────────────────────

    /// <summary>
    /// `ls-remote` had no caller anywhere in this application before. A reachable file:// remote
    /// answers and an absent one does not, and the row reports git's own words rather than choosing
    /// between "gone" and "unreachable from here".
    /// </summary>
    [Fact]
    public async Task AnUnreachableRemote_IsReportedWithoutBeingDiagnosed()
    {
        using var source = await TempRepo.CreateWithCommitAsync("health-remote-source");
        using var origin = await TempRepo.CreateBareFromAsync(source, "health-remote-origin");
        using var clone = await TempRepo.CloneFromAsync(origin, "health-remote-clone");
        var scanner = NewScanner();

        var reachable = await scanner.CheckReachabilityAsync(clone.Path);
        Assert.Equal(HealthState.Ok, reachable.State);

        await clone.GitAsync("remote", "set-url", "origin",
            new Uri(Path.Combine(TestEnv.Root, "health-remote-absent.git")).AbsoluteUri);

        var unreachable = await scanner.CheckReachabilityAsync(clone.Path);
        Assert.Equal(HealthState.Warn, unreachable.State);
        Assert.Contains(HealthCopy.ReachabilityIsNotDiagnosis, unreachable.Detail, StringComparison.Ordinal);
        _output.WriteLine(unreachable.Detail);
    }

    // ── Deep tier: large objects ────────────────────────────────────────────

    /// <summary>
    /// The join between object sizes and object names runs in this process — never a shell pipe,
    /// which would replace one command's exit status with the other's. What is asserted is the
    /// result of that join: the biggest blob, ranked first, carrying the path git names it by.
    /// </summary>
    [Fact]
    public async Task TheLargestObject_IsRankedFirstAndCarriesItsPath()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-large");
        repo.WriteFile("big.bin", new string('x', 300_000));
        repo.WriteFile("small.txt", "tiny\n");
        await repo.CommitAllAsync("add a large blob");

        var (check, scan) = await NewScanner().CheckLargeObjectsAsync(repo.Path);

        Assert.Null(scan.Error);
        Assert.Equal(HealthState.Ok, check.State);
        var largest = scan.Objects[0];
        Assert.Equal("big.bin", largest.Path);
        Assert.Equal(300_000, largest.Bytes);
        Assert.True(scan.Objects.Count <= 10, "the ranking holds a fixed number of entries, never the store");
    }

    /// <summary>
    /// An empty ranking means two different things and they must not be worded alike. A complete
    /// pass over a repository with no blob establishes that it has none; a pass the budget killed
    /// before it reached one establishes nothing, and reporting it as "holds no blob" would be a
    /// confident verdict about a walk that never finished.
    ///
    /// Proved in both directions against one repository: the complete pass is shown first, so a
    /// build that reported Unknown for everything would fail here.
    /// </summary>
    [Fact]
    public async Task AWalkThatWasCutShort_IsUnknownWhereACompletePassSaysThereIsNoBlob()
    {
        using var repo = TempRepo.CreateEmptyDir("health-blobless");
        await repo.GitAsync("init", "-b", "main");

        var (complete, completeScan) = await NewScanner().CheckLargeObjectsAsync(repo.Path);
        Assert.False(completeScan.Partial);
        Assert.Empty(completeScan.Objects);
        Assert.Equal(HealthState.Ok, complete.State);
        Assert.Equal("This repository holds no blob.", complete.Summary);

        var scanner = new RepoHealthScanner(new TimedOutWalkGitService(), null, NewHistory());
        var (cutShort, cutShortScan) = await scanner.CheckLargeObjectsAsync(repo.Path);

        Assert.True(cutShortScan.Partial);
        Assert.Empty(cutShortScan.Objects);
        Assert.Equal(HealthState.Unknown, cutShort.State);
        Assert.DoesNotContain("holds no blob", cutShort.Summary, StringComparison.Ordinal);
        Assert.Contains(HealthCopy.LargeObjectsPartial, cutShort.Detail, StringComparison.Ordinal);
    }

    /// <summary>A walk nothing finished is recorded as an unknown outcome, not a successful one.</summary>
    [Fact]
    public async Task AWalkThatWasCutShort_IsRecordedAsUnknown()
    {
        using var repo = TempRepo.CreateEmptyDir("health-blobless-record");
        await repo.GitAsync("init", "-b", "main");
        var history = NewHistory();

        await new RepoHealthScanner(new TimedOutWalkGitService(), null, history)
            .CheckLargeObjectsAsync(repo.Path);

        Assert.Equal(OperationOutcome.Unknown, Assert.Single(history.Tail(repo.Path).Records).Outcome);
    }

    // ── Bounded filesystem walks ────────────────────────────────────────────

    /// <summary>
    /// A directory enumeration answers to no token of its own. Both bounds are proved on the shared
    /// helper the two walks run through, because a walk that outlives the page contradicts the
    /// cancellation every other check on this surface honours.
    /// </summary>
    [Fact]
    public async Task ABoundedWalk_StopsOnItsBudget()
    {
        using var blocked = new ManualResetEventSlim(false);

        await Assert.ThrowsAsync<TimeoutException>(() => RepoHealthScanner.Bounded(
            () => { blocked.Wait(TimeSpan.FromSeconds(30)); return 0; },
            TimeSpan.FromMilliseconds(50), CancellationToken.None));

        blocked.Set();
    }

    [Fact]
    public async Task ABoundedWalk_StopsOnCancellation()
    {
        using var blocked = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var running = RepoHealthScanner.Bounded(
            () => { blocked.Wait(TimeSpan.FromSeconds(30)); return 0; },
            TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        blocked.Set();
    }

    /// <summary>
    /// A cancelled scan is Not run, never "no lock file is present": it looked at part of a
    /// directory, and neither an absence nor a count is a fact about the repository.
    /// </summary>
    [Fact]
    public async Task ACancelledLockScan_IsNotRunRatherThanClean()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-locks-cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var check = await NewScanner().LocksAsync(Path.Combine(repo.Path, ".git"), DateTime.UtcNow, cts.Token);

        Assert.Equal(HealthState.NotRun, check.State);
        Assert.DoesNotContain("No lock file", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>A scan the budget stopped is unknown, and the row names the bound it hit.</summary>
    [Fact]
    public async Task ALockScanThatOutranItsBudget_IsUnknownAndNamesTheBound()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-locks-budget");
        var refs = Path.Combine(repo.Path, ".git", "refs", "heads");
        Directory.CreateDirectory(refs);
        for (var i = 0; i < 200; i++)
            await File.WriteAllTextAsync(Path.Combine(refs, $"branch{i}.lock"), "");

        var check = await NewScanner().LocksAsync(
            Path.Combine(repo.Path, ".git"), DateTime.UtcNow, CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(HealthState.Unknown, check.State);
        Assert.Contains("still being read after", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelled between the configuration read and the directory walk, which is the only moment
    /// that exercises the walk's own arm: a token already cancelled stops the config read first,
    /// and that cancellation propagates rather than producing a row at all.
    /// </summary>
    [Fact]
    public async Task ACancelledHookScan_IsNotRunRatherThanNoHooks()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-hooks-cancel");
        using var cts = new CancellationTokenSource();
        var scanner = new RepoHealthScanner(new CancelAfterConfigGitService(cts), null, NewHistory());

        var check = await scanner.HooksAsync(repo.Path, Path.Combine(repo.Path, ".git"), cts.Token);

        Assert.Equal(HealthState.NotRun, check.State);
        Assert.DoesNotContain("No hook is installed", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHookScanThatOutranItsBudget_IsUnknownAndNamesTheBound()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-hooks-budget");
        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        for (var i = 0; i < 200; i++)
            await File.WriteAllTextAsync(Path.Combine(hooks, $"hook{i}"), "#!/bin/sh\n");

        var check = await NewScanner().HooksAsync(
            repo.Path, Path.Combine(repo.Path, ".git"), CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(HealthState.Unknown, check.State);
        Assert.Contains("still being read after", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>The ranking names blobs; a commit or a tree is not a file a purge could remove.</summary>
    [Fact]
    public async Task TheRanking_HoldsOnlyBlobs()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-large-blobs");
        repo.WriteFile("payload.bin", new string('y', 50_000));
        await repo.CommitAllAsync("add a payload");

        var (_, scan) = await NewScanner().CheckLargeObjectsAsync(repo.Path);

        var head = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        Assert.DoesNotContain(scan.Objects, o => o.Sha == head);
    }

    // ── Deep tier: backup verification ──────────────────────────────────────

    /// <summary>
    /// The health page verifies through the shared verifier, so one bundle is not worded two ways
    /// across the rollup, the Backups browser, and here. Proved in both directions on one bundle.
    /// </summary>
    [Fact]
    public async Task ACorruptedBundle_TurnsTheBackupRowBadWhereAnIntactOnePasses()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-verify");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var history = NewHistory();
        var backups = new BackupService(new GitService(), settings, history);
        var handle = await backups.CreateBackupAsync(repo.Path, "fixture");
        var scanner = NewScanner(history, backups);

        var (intact, intactResult) = await scanner.CheckBackupsAsync(repo.Path);
        Assert.Equal(HealthState.Ok, intact.State);
        Assert.Empty(intactResult.FailedStamps);

        await File.WriteAllTextAsync(handle.BundlePath, "not a bundle");

        var (corrupted, corruptedResult) = await scanner.CheckBackupsAsync(repo.Path);
        Assert.Equal(HealthState.Bad, corrupted.State);
        Assert.Equal(handle.UtcStamp, Assert.Single(corruptedResult.FailedStamps));
    }

    /// <summary>
    /// A repository with no bundle is worth a look, not clear: there is nothing this application
    /// could put back, and a row reading OK would say the opposite.
    /// </summary>
    [Fact]
    public async Task ARepositoryWithNoBundle_IsWorthALookRatherThanOk()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-no-backup");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var backups = new BackupService(new GitService(), settings, NewHistory());

        var quick = Check(await NewScanner(backups: backups).QuickAsync(repo.Path), HealthCheckId.Backups);

        Assert.Equal(HealthState.Warn, quick.State);
        Assert.Equal("No backup on disk.", quick.Summary);
    }

    // ── The unrun tier ──────────────────────────────────────────────────────

    /// <summary>
    /// Every deep row exists before anyone presses it, and every one of them is Not run. A page
    /// that rendered only what it measured would read as though the rest had nothing to report.
    /// </summary>
    [Fact]
    public void TheDeepRows_StartAtNotRunRatherThanAbsent()
    {
        var rows = RepoHealthScanner.DeepNotRun();

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(HealthState.NotRun, r.State));
        Assert.All(rows, r => Assert.Equal(HealthTier.Deep, r.Tier));
        Assert.Equal(HealthCopy.ConnectivityNotRun,
            rows.Single(r => r.Id == HealthCheckId.Connectivity).Summary);
    }

    /// <summary>The quick tier never produces a deep row, so no cheap answer can stand in for an expensive one.</summary>
    [Fact]
    public async Task TheQuickTier_ProducesNoDeepAnswer()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-tiers");

        var checks = await NewScanner().QuickAsync(repo.Path);

        Assert.All(checks, c => Assert.Equal(HealthTier.Quick, c.Tier));
        Assert.DoesNotContain(checks, c => c.Id == HealthCheckId.Connectivity);
    }
}
