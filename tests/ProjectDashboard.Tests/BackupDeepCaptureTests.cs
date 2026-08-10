using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The two backup tiers, asserted in both directions against the same fixture: what a standard
/// capture provably does not hold, and what a deep one provably does. A test that only proved the
/// deep tier captures the extra objects would pass just as well if the standard tier had been
/// capturing them all along, and the gap the deep tier exists to close would go unmeasured.
///
/// The objects in question are a commit left behind by an amend, which only HEAD's reflog reaches,
/// and stash entries below the newest — `git stash push` keeps its stack as refs/stash's own
/// reflog, so stash@{1} and below are reflog entries rather than refs. Presence is read from a
/// scratch repository the bundle is unbundled into, never from the repository the backup was taken
/// from, where every one of them is present regardless of what the bundle received.
/// </summary>
[Collection("app-data-sandbox")]
public class BackupDeepCaptureTests
{
    public BackupDeepCaptureTests() => TestSandbox.ResetDataDir();

    private static BackupService NewService() => new(new GitService(), new SettingsService());

    /// <summary>What a fixture repository holds that no ref reaches, plus the one stash that is a ref.</summary>
    private sealed record DeepFixture(string PreAmend, string Stash0, string Stash1, string Stash2)
    {
        /// <summary>The three a standard capture leaves out.</summary>
        public string[] Unreferenced => [PreAmend, Stash1, Stash2];
    }

    /// <summary>
    /// A repository holding one reflog-only commit and a three-deep stash stack. The stash pushes
    /// are ordered so stash@{0} is the newest, which is the one — and the only one — `--all` reaches.
    /// </summary>
    private static async Task<DeepFixture> SeedDeepAsync(RailsRepo repo)
    {
        repo.Write("file.txt", "two\n");
        await repo.CommitAllAsync("second");
        var preAmend = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.GitAsync("commit", "--amend", "-m", "second, amended");

        for (var n = 1; n <= 3; n++)
        {
            repo.Write("file.txt", $"dirty {n}\n");
            await repo.GitAsync("stash", "push", "-m", $"stash {n}");
        }

        return new DeepFixture(
            preAmend,
            (await repo.GitAsync("rev-parse", "stash@{0}")).Trim(),
            (await repo.GitAsync("rev-parse", "stash@{1}")).Trim(),
            (await repo.GitAsync("rev-parse", "stash@{2}")).Trim());
    }

    private static async Task<bool> HasObjectAsync(string repoPath, string oid) =>
        (await new GitService().RunAsync(repoPath, ["cat-file", "-e", oid])).Success;

    /// <summary>
    /// An empty repository with one bundle unpacked into it — the only place a question about what
    /// a bundle carries is about the bundle rather than about the repository it was written from,
    /// where every one of these objects is present regardless.
    /// </summary>
    private static async Task<string> UnbundledIntoScratchAsync(string bundlePath)
    {
        var scratch = TestEnv.NewDir("deep-scratch");
        await Git.RunAsync(scratch, "init", "-b", "main");
        var unbundle = await new GitService().RunAsync(scratch, ["bundle", "unbundle", bundlePath]);
        Assert.True(unbundle.Success, unbundle.FirstError);
        return scratch;
    }

    [Fact]
    public async Task AStandardCapture_HoldsTheNewestStashAndNothingOnlyAReflogReaches()
    {
        using var repo = await RailsRepo.CreateAsync("deep-standard");
        var fixture = await SeedDeepAsync(repo);

        var handle = await NewService().CreateBackupAsync(repo.Path, "History rewrite", deep: false);
        var scratch = await UnbundledIntoScratchAsync(handle.BundlePath);

        Assert.True(await HasObjectAsync(scratch, fixture.Stash0),
            "the top refs/stash entry is a ref, so --all reaches it");
        foreach (var oid in fixture.Unreferenced)
            Assert.False(await HasObjectAsync(scratch, oid),
                $"{oid} is reachable from a reflog alone and a standard capture does not hold it");
    }

    [Fact]
    public async Task ADeepCapture_HoldsEveryReflogOnlyCommitAndEveryStashEntry()
    {
        using var repo = await RailsRepo.CreateAsync("deep-on");
        var fixture = await SeedDeepAsync(repo);

        var handle = await NewService().CreateBackupAsync(repo.Path, "History rewrite", deep: true);
        var scratch = await UnbundledIntoScratchAsync(handle.BundlePath);

        Assert.True(await HasObjectAsync(scratch, fixture.Stash0));
        foreach (var oid in fixture.Unreferenced)
            Assert.True(await HasObjectAsync(scratch, oid),
                $"{oid} is what the deep tier exists to carry");
    }

    /// <summary>
    /// The whole ancestry, not only the tips: a stash entry whose index commit did not come with it
    /// is an object id `git show` cannot render, which is the one use the preserved objects have.
    /// </summary>
    [Fact]
    public async Task ADeepCapture_HoldsWhatAPreservedStashEntryNeedsToBeReadable()
    {
        using var repo = await RailsRepo.CreateAsync("deep-closure");
        var fixture = await SeedDeepAsync(repo);
        var indexCommit = (await repo.GitAsync("rev-parse", "stash@{2}^2")).Trim();

        var handle = await NewService().CreateBackupAsync(repo.Path, "History rewrite", deep: true);
        var scratch = await UnbundledIntoScratchAsync(handle.BundlePath);

        Assert.True(await HasObjectAsync(scratch, indexCommit));
        var show = await new GitService().RunAsync(scratch, ["show", "--stat", "--oneline", fixture.Stash2]);
        Assert.True(show.Success, show.FirstError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACaptureRecordsItsTierInTheSidecar(bool deep)
    {
        using var repo = await RailsRepo.CreateAsync("deep-sidecar");
        await SeedDeepAsync(repo);

        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path, "History rewrite", deep);
        var details = service.ReadDetails(handle);

        Assert.NotNull(details);
        Assert.Equal(deep, details!.DeepCapture);
        if (!deep)
        {
            Assert.Equal(0, details.DeepObjectCount);
            return;
        }

        // At least the three the fixture seeds — the pre-amend commit and the two older stash
        // entries. Not an exact figure: `git stash push` also writes an index commit, and three
        // pushes inside one second produce one identical object where three pushes across a second
        // boundary produce more, so an equality here would fail on the clock rather than on a
        // defect. Which objects the capture holds is asserted against the bundle itself above.
        Assert.True(details.DeepObjectCount >= 3,
            $"expected at least the 3 seeded reflog-only objects, got {details.DeepObjectCount}");
    }


    /// <summary>
    /// A caller with no opinion follows the saved setting rather than a default of its own, and
    /// reads it fresh: the coordinators take their backups through this same path, so a setting
    /// changed mid-session must reach them without a relaunch.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACaptureThatNamesNoTier_FollowsTheSavedSetting(bool configured)
    {
        using var repo = await RailsRepo.CreateAsync("deep-setting");
        await SeedDeepAsync(repo);
        new SettingsService().Save(new AppSettings { DeepBackupCapture = configured });

        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path, "History rewrite");

        Assert.Equal(configured, service.ReadDetails(handle)!.DeepCapture);
    }

    [Fact]
    public async Task AnExplicitTier_OverridesTheSavedSettingForThatCaptureOnly()
    {
        using var repo = await RailsRepo.CreateAsync("deep-override");
        var fixture = await SeedDeepAsync(repo);
        new SettingsService().Save(new AppSettings { DeepBackupCapture = false, BackupRetentionCount = 10 });

        var service = NewService();
        var forced = await service.CreateBackupAsync(repo.Path, "Manual backup", deep: true);
        var following = await service.CreateBackupAsync(repo.Path, "History rewrite");

        Assert.True(service.ReadDetails(forced)!.DeepCapture);
        Assert.False(service.ReadDetails(following)!.DeepCapture);
        Assert.True(await HasObjectAsync(await UnbundledIntoScratchAsync(forced.BundlePath), fixture.Stash2));
        Assert.False(await HasObjectAsync(await UnbundledIntoScratchAsync(following.BundlePath), fixture.Stash2));
    }

    /// <summary>Fails the reflog walk and nothing else, so the refusal is attributable to that read.</summary>
    private sealed class FailsTheReflogWalkGitService : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList();
            return argv is ["rev-list", "--reflog", ..]
                ? Task.FromResult(new ProcessResult(1, "", "fatal: bad object HEAD@{3}", false))
                : base.RunAsync(repoPath, argv, environment, ct, timeout);
        }
    }

    /// <summary>
    /// A deep capture whose reflog walk fails refuses rather than writing a standard bundle. The
    /// sidecar is the only record of a backup's tier, and one narrowed silently would be read months
    /// later as covering history it never received.
    /// </summary>
    [Fact]
    public async Task ADeepCaptureWhoseReflogWalkFails_RefusesAndLeavesNoBackupBehind()
    {
        using var repo = await RailsRepo.CreateAsync("deep-walk-fails");
        await SeedDeepAsync(repo);
        var service = new BackupService(new FailsTheReflogWalkGitService(), new SettingsService());

        var thrown = await Assert.ThrowsAsync<BackupException>(
            () => service.CreateBackupAsync(repo.Path, "History rewrite", deep: true));

        Assert.Contains("reflogs", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await service.ListBackupsAsync(repo.Path));
        var dir = SafetyPaths.BackupDirFor(RepoKey.For(repo.Path));
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bundle") : []);
    }

    /// <summary>
    /// The extra objects a deep capture carries change what the bundle holds and nothing about
    /// which refs a restore ends up with. Both tiers are restored from the same starting state and
    /// asserted to land on the identical ref layout — the guard against the scope boundary quietly
    /// widening into a stash-stack replay.
    /// </summary>
    [Fact]
    public async Task ADeepRestore_ReconcilesExactlyTheRefsAStandardOneDoes()
    {
        var states = new List<string>();
        foreach (var deep in new[] { false, true })
        {
            using var repo = await RailsRepo.CreateAsync($"deep-restore-{deep}");
            await SeedDeepAsync(repo);
            var service = NewService();
            var handle = await service.CreateBackupAsync(repo.Path, "History rewrite", deep);
            var before = await repo.RefStateAsync();

            repo.Write("file.txt", "after the backup\n");
            await repo.CommitAllAsync("post-backup");
            await repo.GitAsync("branch", "added-after");
            Assert.NotEqual(before, await repo.RefStateAsync());

            var restore = await service.RestoreAsync(handle, allowDirty: false);

            Assert.True(restore.Success, restore.Message);
            Assert.Equal(before, await repo.RefStateAsync());
            states.Add(await repo.RefStateAsync());
        }

        // Both fixtures were seeded identically, so a difference here is the tier changing what a
        // restore reconciles rather than the two repositories differing.
        Assert.Equal(
            states[0].Split('\n').Select(l => l.Split(' ').Last()).ToArray(),
            states[1].Split('\n').Select(l => l.Split(' ').Last()).ToArray());
    }

    /// <summary>
    /// The restore is a ref reconciliation, so the preserved objects come back as objects and
    /// nothing rebuilds the stash stack from them. A deep restore that repopulated refs/stash's
    /// reflog would be the scope boundary the setting's helper text denies.
    /// </summary>
    [Fact]
    public async Task ADeepRestore_PutsTheObjectsBackWithoutRebuildingTheStashStack()
    {
        using var repo = await RailsRepo.CreateAsync("deep-no-replay");
        var fixture = await SeedDeepAsync(repo);
        var service = NewService();
        var handle = await service.CreateBackupAsync(repo.Path, "History rewrite", deep: true);

        // Drops every stash entry, leaving refs/stash absent and its reflog gone with it.
        await repo.GitAsync("stash", "clear");
        Assert.False((await new GitService()
            .RunAsync(repo.Path, ["rev-parse", "--verify", "-q", "refs/stash"])).Success);

        var restore = await service.RestoreAsync(handle, allowDirty: false);
        Assert.True(restore.Success, restore.Message);

        // refs/stash is a ref the snapshot recorded, so it comes back; the entries below it are
        // objects the bundle carried, and no stack is rebuilt from them.
        Assert.Equal(fixture.Stash0, (await repo.GitAsync("rev-parse", "refs/stash")).Trim());
        Assert.True(await HasObjectAsync(repo.Path, fixture.Stash2));
        var stack = await new GitService().RunAsync(repo.Path, ["reflog", "show", "--format=%H", "refs/stash"]);
        Assert.DoesNotContain(fixture.Stash2, stack.StdOut, StringComparison.Ordinal);
    }
}
