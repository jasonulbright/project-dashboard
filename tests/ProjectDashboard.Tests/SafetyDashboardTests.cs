using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The safety rollup over a real fixture portfolio: what it reports without being asked, what it
/// refuses to claim, what one explicit check changes, and where a row's single action leads.
///
/// Discovery, settings and the recovery journal all live under AppPaths, so these join the
/// serialized collection.
/// </summary>
[Collection("app-data-sandbox")]
public class SafetyDashboardTests
{
    private readonly ITestOutputHelper _output;

    public SafetyDashboardTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    /// <summary>Records every git invocation, so a check promoted into the free tier is caught.</summary>
    private sealed class CountingGitService : GitService
    {
        public List<string> Invocations { get; } = [];

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var listed = args.ToList();
            var result = await base.RunAsync(repoPath, listed, environment, ct, timeout);
            lock (Invocations) Invocations.Add(string.Join(' ', listed));
            return result;
        }
    }

    // ── The free tier ───────────────────────────────────────────────────────

    /// <summary>
    /// The free tier is paid on every dashboard refresh across the whole portfolio. A check quietly
    /// promoted into it would cost a git process per repository per refresh and nothing else would
    /// notice, so the zero is pinned.
    /// </summary>
    [Fact]
    public async Task TheFreeTier_SpawnsNoGitProcess()
    {
        var host = await NewHostAsync("free-cost", repos: 2);
        var counting = new CountingGitService();
        var safety = host.NewSafety(counting);

        safety.Rebuild();

        Assert.Empty(counting.Invocations);
        Assert.NotEqual("", safety.RollupText);
        _output.WriteLine(safety.RollupText);
    }

    /// <summary>
    /// Every free signal is on the page from the moment it opens, and the header states that the
    /// checks nobody ran have not run.
    /// </summary>
    [Fact]
    public async Task OnOpening_TheFreeSignalsAreRenderedAndTheUncheckedTiersAreNamed()
    {
        var host = await NewHostAsync("free-signals", repos: 1);
        var safety = host.NewSafety();

        Assert.Contains("Free checks only", safety.TierText, StringComparison.Ordinal);
        AssertGroup(safety, "Interrupted operations");
        AssertGroup(safety, "Repositories with no remote");
        AssertGroup(safety, "Backups");
        AssertGroup(safety, "Reflog-only commits");
        Assert.Contains(safety.Rows, r => r.IsGroup && r.Title == "Backups"
            && r.Line.Contains("Not checked", StringComparison.Ordinal));
    }

    /// <summary>A repository with no remote exists on one machine only, and the row offers Remotes.</summary>
    [Fact]
    public async Task ARepositoryWithNoRemote_IsAFindingThatOpensRemotes()
    {
        var host = await NewHostAsync("no-remote", repos: 1);
        var safety = host.NewSafety();

        var row = Assert.Single(safety.Rows,
            r => r.IsFinding && r.Line.Contains("No remote configured", StringComparison.Ordinal));
        Assert.Equal(SafetyAction.OpenRemotes, row.Action);
        Assert.Equal(SafetySeverity.WorthALook, row.Severity);
    }

    /// <summary>
    /// Dirty is already a dashboard chip. The rollup reports it from the same project list and the
    /// same predicate, so the two surfaces cannot disagree about how many repositories are dirty.
    /// </summary>
    [Fact]
    public async Task TheUncommittedWorkGroup_CountsWhatTheDashboardChipCounts()
    {
        var host = await NewHostAsync("dirty-agrees", repos: 2);
        await File.WriteAllTextAsync(Path.Combine(host.Repos[0], "wip.txt"), "work\n");
        await host.Dashboard.ForceRefreshCommand.ExecuteAsync(null);
        var safety = host.NewSafety();

        var rows = safety.Rows.Count(r => r.IsFinding
            && r.Line.Contains("uncommitted change", StringComparison.Ordinal));

        Assert.Equal(1, host.Dashboard.DirtyCount);
        Assert.Equal(host.Dashboard.DirtyCount, rows);
        Assert.Contains(safety.Rows, r => r.IsGroup && r.Title == "Uncommitted work"
            && r.Line.Contains("Dirty chip", StringComparison.Ordinal));
    }

    /// <summary>A branch both ahead of and behind its upstream is the state no fast-forward resolves.</summary>
    [Fact]
    public async Task ADivergedCurrentBranch_IsReportedWithoutAnyExtraRead()
    {
        var host = await NewHostAsync("diverged", repos: 0);
        await host.AddDivergedCloneAsync("worker");
        await host.Dashboard.ForceRefreshCommand.ExecuteAsync(null);
        var counting = new CountingGitService();
        var safety = host.NewSafety(counting);

        var row = Assert.Single(safety.Rows,
            r => r.IsFinding && r.Line.Contains("diverged", StringComparison.Ordinal));
        Assert.Equal(SafetyAction.OpenBranches, row.Action);
        Assert.Empty(counting.Invocations);
    }

    /// <summary>
    /// The journal is read at startup and its entries are the loud signal. An empty result carries
    /// the caveat that an unreadable journal reports nothing pending.
    /// </summary>
    [Fact]
    public async Task AnInterruptedOperation_IsTheLoudestFindingAndOffersRecovery()
    {
        var host = await NewHostAsync("interrupted", repos: 1);
        var journal = new RewriteJournal(Path.Combine(TestEnv.NewDir("interrupted-journal"), "journal.json"));
        await journal.BeginAsync(new RewriteJournalEntry
        {
            RepoPath = host.Repos[0],
            Phase = "swap",
            UtcStamp = "20260808-101112000",
        });
        var recovery = new RewriteRecoveryService(journal, host.History);
        await recovery.StartAsync(CancellationToken.None);

        var safety = host.NewSafety(recovery: recovery);

        var row = Assert.Single(safety.Rows,
            r => r.IsFinding && r.Line.Contains("Interrupted", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SafetySeverity.NeedsAttention, row.Severity);
        Assert.Equal(SafetyAction.OpenRecoveryBackups, row.Action);
        Assert.Equal("Recover…", row.ActionLabel);
    }

    [Fact]
    public async Task WithNothingInterrupted_TheGroupStillCarriesTheCaveat()
    {
        var host = await NewHostAsync("interrupted-none", repos: 1);
        var recovery = new RewriteRecoveryService(
            new RewriteJournal(Path.Combine(TestEnv.NewDir("empty-journal"), "journal.json")), host.History);
        await recovery.StartAsync(CancellationToken.None);

        var safety = host.NewSafety(recovery: recovery);

        var group = Assert.Single(safety.Rows, r => r.IsGroup && r.Title == "Interrupted operations");
        Assert.Contains("not proof", group.Line, StringComparison.Ordinal);
    }

    // ── The expensive tier is never claimed before it runs ──────────────────

    /// <summary>
    /// A repository nobody asked about reads as not checked. A blank row here, or a zero, would be
    /// a measurement the page never took.
    /// </summary>
    [Fact]
    public async Task BeforeAnyDeepCheck_EveryRepositoryReadsAsNotChecked()
    {
        var host = await NewHostAsync("not-checked", repos: 2);
        var safety = host.NewSafety();

        var rows = safety.Rows
            .SkipWhile(r => !(r.IsGroup && r.Title == "Reflog-only commits"))
            .Skip(1)
            .TakeWhile(r => r.IsFinding)
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(SafetyCopy.NotChecked, r.Line);
            Assert.Equal("Check", r.ActionLabel);
            Assert.Equal(SafetyAction.CheckReflogOnly, r.Action);
        });
    }

    /// <summary>One repository's walk answers for that repository and leaves the others unmeasured.</summary>
    [Fact]
    public async Task OneRepositorysReflogWalk_AnswersOnlyForThatRepository()
    {
        var host = await NewHostAsync("one-walk", repos: 2);
        await Git.RunAsync(host.Repos[0], "commit", "--allow-empty", "-m", "abandoned");
        await Git.RunAsync(host.Repos[0], "reset", "--hard", "HEAD~1");
        var safety = host.NewSafety();

        var target = ReflogRowFor(safety, Path.GetFileName(host.Repos[0]));
        await safety.RunRowActionCommand.ExecuteAsync(target);

        var checkedRow = ReflogRowFor(safety, Path.GetFileName(host.Repos[0]));
        Assert.Contains("1 reflog-only commit", checkedRow.Line, StringComparison.Ordinal);
        Assert.Equal(SafetyAction.OpenReflog, checkedRow.Action);

        var untouched = ReflogRowFor(safety, Path.GetFileName(host.Repos[1]));
        Assert.Equal(SafetyCopy.NotChecked, untouched.Line);

        var group = Assert.Single(safety.Rows, r => r.IsGroup && r.Title == "Reflog-only commits");
        Assert.Contains("Checked on 1 of 2", group.Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repository the walk could not read reports that, never a count of none. A zero here would
    /// tell a reader that a damaged repository has nothing living only in a reflog.
    /// </summary>
    [Fact]
    public async Task ARepositoryTheWalkCouldNotMeasure_DoesNotReadAsHavingNone()
    {
        var host = await NewHostAsync("walk-broken", repos: 1);
        var safety = host.NewSafety();
        var row = ReflogRowFor(safety, Path.GetFileName(host.Repos[0]));

        Corrupt(host.Repos[0]);
        await safety.RunRowActionCommand.ExecuteAsync(row);

        var measured = ReflogRowFor(safety, Path.GetFileName(host.Repos[0]));
        Assert.Equal("Could not be measured", measured.Line);
        Assert.NotEqual("", measured.Detail);
        Assert.Contains("could not be completed", safety.StatusText, StringComparison.Ordinal);
    }

    /// <summary>Leaves the directory in place with a HEAD git refuses to resolve, so every read fails.</summary>
    private static void Corrupt(string repoPath)
    {
        var gitDir = Path.Combine(repoPath, ".git");
        Directory.Delete(Path.Combine(gitDir, "refs"), recursive: true);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "not a ref\n");
    }

    // ── The expensive tier answers the repository as it is now ──────────────

    /// <summary>
    /// A reset that abandons a commit changes no object count — the objects are all still there,
    /// only unreferenced — while it is exactly the operation that produces a reflog-only commit.
    /// A second run must walk again and report 1, not re-serve the 0 the first run found.
    /// </summary>
    [Fact]
    public async Task ASecondCheckAll_AfterAResetAbandonsACommit_ReportsTheReflogOnlyCommit()
    {
        var host = await NewHostAsync("checkall-reset", repos: 1);
        var repo = host.Repos[0];
        await File.WriteAllTextAsync(Path.Combine(repo, "second.txt"), "work\n");
        await Git.RunAsync(repo, "add", "-A");
        await Git.RunAsync(repo, "commit", "-m", "about to be abandoned");
        var safety = host.NewSafety();

        await safety.CheckAllCommand.ExecuteAsync(null);
        Assert.Contains("No reflog-only commit",
            ReflogRowFor(safety, Path.GetFileName(repo)).Line, StringComparison.Ordinal);

        var before = await ObjectCountsAsync(repo);
        await Git.RunAsync(repo, "reset", "--hard", "HEAD~1");
        // The premise the stale answer rode on: nothing about the object store moved.
        Assert.Equal(before, await ObjectCountsAsync(repo));

        await safety.CheckAllCommand.ExecuteAsync(null);

        Assert.Contains("1 reflog-only commit",
            ReflogRowFor(safety, Path.GetFileName(repo)).Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backup bundle is written under the app's own folder, so taking one moves nothing in the
    /// repository. A second run must verify what is on disk now: a bundle that appeared since the
    /// first run and cannot be verified has to be reported, not covered by the first run's pass.
    /// </summary>
    [Fact]
    public async Task ASecondCheckAll_VerifiesABundleThatAppearedSinceTheFirst()
    {
        var host = await NewHostAsync("checkall-bundle", repos: 1);
        var backups = new BackupService(new GitService(), host.Settings, host.History);
        await backups.CreateBackupAsync(host.Repos[0], "first");
        var safety = host.NewSafety(backups: backups);

        await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);
        await safety.CheckAllCommand.ExecuteAsync(null);
        Assert.Contains("1 backup(s) verified on", BackupRow(safety).Line, StringComparison.Ordinal);

        var before = await ObjectCountsAsync(host.Repos[0]);
        var second = await backups.CreateBackupAsync(host.Repos[0], "second");
        CorruptBundle(second.BundlePath);
        // The premise the stale answer rode on: nothing about the object store moved.
        Assert.Equal(before, await ObjectCountsAsync(host.Repos[0]));

        await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);
        await safety.CheckAllCommand.ExecuteAsync(null);

        var row = BackupRow(safety);
        Assert.Contains("1 failed verification", row.Line, StringComparison.Ordinal);
        Assert.Contains(second.UtcStamp, row.Detail, StringComparison.Ordinal);
    }

    private static SafetyRow BackupRow(SafetyViewModel safety) =>
        Assert.Single(safety.Rows, r => r.Action == SafetyAction.VerifyBackups);

    /// <summary>Clobbers the bundle's signature line, which is the part git reads.</summary>
    private static void CorruptBundle(string bundlePath)
    {
        var bytes = File.ReadAllBytes(bundlePath);
        Array.Fill(bytes, (byte)0, 0, 16);
        File.WriteAllBytes(bundlePath, bytes);
    }

    /// <summary>The loose and packed counts, which is all an object-store reading amounts to.</summary>
    private static async Task<string> ObjectCountsAsync(string repoPath)
    {
        var counts = await new GitService().CountObjectsAsync(repoPath);
        Assert.NotNull(counts);
        return $"{counts.LooseObjects}/{counts.PackedObjects}";
    }

    // ── The cheap tier ──────────────────────────────────────────────────────

    /// <summary>
    /// The check is read-only and takes no lease, so a repository another operation holds is
    /// skipped — and the count says how many, because a count that silently excludes reports a
    /// smaller portfolio than there is.
    /// </summary>
    [Fact]
    public async Task ABusyRepository_IsSkippedByTheCheapCheckAndCounted()
    {
        var host = await NewHostAsync("busy-skip", repos: 2);
        var safety = host.NewSafety();

        Assert.True(host.Busy.TryAcquire(host.Repos[0], out var lease));
        using (lease) await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);

        Assert.Contains("1 skipped (busy)", safety.StatusText, StringComparison.Ordinal);
        Assert.Contains("1 skipped (busy)", safety.TierText, StringComparison.Ordinal);
        Assert.Contains("Branches and backups checked", safety.TierText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A per-repository check refuses on a busy repository rather than reading refs a swap is
    /// mid-way through moving, and says which repository it refused for.
    /// </summary>
    [Fact]
    public async Task ABusyRepository_RefusesItsOwnDeepCheck()
    {
        var host = await NewHostAsync("busy-refuse", repos: 1);
        var safety = host.NewSafety();
        var row = ReflogRowFor(safety, Path.GetFileName(host.Repos[0]));

        Assert.True(host.Busy.TryAcquire(host.Repos[0], out var lease));
        using (lease) await safety.RunRowActionCommand.ExecuteAsync(row);

        Assert.Contains(SafetyCopy.RepoBusyRefusal, safety.StatusText, StringComparison.Ordinal);
        Assert.Equal(SafetyCopy.NotChecked, ReflogRowFor(safety, Path.GetFileName(host.Repos[0])).Line);
    }

    /// <summary>
    /// The cheap tier lists what is on disk; it never verifies one. A repository with bundles reads
    /// as having them and as having none of them verified.
    /// </summary>
    [Fact]
    public async Task TheCheapCheck_ListsBackupsWithoutClaimingTheyVerify()
    {
        var host = await NewHostAsync("backup-listing", repos: 1);
        var backups = new BackupService(new GitService(), host.Settings, host.History);
        await backups.CreateBackupAsync(host.Repos[0], "fixture");
        var safety = host.NewSafety(backups: backups);

        await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);

        var row = Assert.Single(safety.Rows,
            r => r.IsFinding && r.Line.Contains("backup(s) on disk", StringComparison.Ordinal));
        Assert.Contains("none verified", row.Line, StringComparison.Ordinal);
        Assert.Equal("Verify", row.ActionLabel);
    }

    /// <summary>
    /// Verifying bundles is the expensive tier, run per repository, and its answer carries the
    /// moment it was taken — a result shown without its age reads as current. The bound on what
    /// verification establishes travels with it, on the heading and on the row.
    /// </summary>
    [Fact]
    public async Task VerifyingOneRepositorysBackups_StampsTheAnswerAndStatesItsBound()
    {
        var host = await NewHostAsync("backup-verify", repos: 1);
        var backups = new BackupService(new GitService(), host.Settings, host.History);
        await backups.CreateBackupAsync(host.Repos[0], "fixture");
        var safety = host.NewSafety(backups: backups);
        await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);

        var row = Assert.Single(safety.Rows, r => r.Action == SafetyAction.VerifyBackups);
        await safety.RunRowActionCommand.ExecuteAsync(row);

        var verified = Assert.Single(safety.Rows,
            r => r.IsFinding && r.Line.Contains("backup(s) verified on", StringComparison.Ordinal));
        Assert.Contains("1 backup(s) verified on", verified.Line, StringComparison.Ordinal);
        Assert.Contains(SafetyCopy.BackupCheckLimit, verified.Detail, StringComparison.Ordinal);

        var heading = Assert.Single(safety.Rows, r => r.IsGroup && r.Title == "Backups");
        Assert.Contains(SafetyCopy.BackupCheckLimit, heading.Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verify that never answered leaves the row saying so. Ranking it beside a bundle found bad
    /// would send a reader to replace a backup that may be intact.
    /// </summary>
    [Fact]
    public async Task ABackupTheVerifierCouldNotAnswerFor_ReadsAsUnansweredNotFailed()
    {
        var host = await NewHostAsync("backup-unknown", repos: 1);
        var settings = host.Settings;
        await new BackupService(new GitService(), settings, host.History)
            .CreateBackupAsync(host.Repos[0], "fixture");
        var timingOut = new BackupService(new TimeOutOnVerifyGitService(), settings, host.History);
        var safety = host.NewSafety(backups: timingOut);
        await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);

        var row = Assert.Single(safety.Rows, r => r.Action == SafetyAction.VerifyBackups);
        await safety.RunRowActionCommand.ExecuteAsync(row);

        var answered = Assert.Single(safety.Rows,
            r => r.IsFinding && r.Line.Contains("could not be verified", StringComparison.Ordinal));
        Assert.DoesNotContain("failed verification", answered.Line, StringComparison.Ordinal);
        Assert.Equal(SafetySeverity.WorthALook, answered.Severity);
    }

    /// <summary>Reports every bundle verify as killed on its budget; every other git call is real.</summary>
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

    // ── Where a row leads ───────────────────────────────────────────────────

    /// <summary>
    /// Every action is a link into a surface that carries its own gates. The rollup restores
    /// nothing, deletes nothing, and rewrites nothing.
    /// </summary>
    [Fact]
    public async Task ARowsAction_NavigatesToTheSurfaceThatCarriesItsGates()
    {
        var host = await NewHostAsync("routing", repos: 1);
        await File.WriteAllTextAsync(Path.Combine(host.Repos[0], "wip.txt"), "work\n");
        await host.Dashboard.ForceRefreshCommand.ExecuteAsync(null);
        var safety = host.NewSafety();
        // The backup row exists once the cheap tier has listed what is on disk; before that the
        // group states it has not been checked rather than rendering a row per repository.
        await safety.CheckBranchesAndBackupsCommand.ExecuteAsync(null);

        DetailTab? tab = null;
        DetailOverlay? overlay = null;
        safety.NavigateToProjectTabRequested += (_, t) => tab = t;
        safety.NavigateToProjectOverlayRequested += (_, o) => overlay = o;

        await safety.RunRowActionCommand.ExecuteAsync(
            Assert.Single(safety.Rows, r => r.Action == SafetyAction.OpenChanges));
        Assert.Equal(DetailTab.Changes, tab);

        await safety.RunRowActionCommand.ExecuteAsync(
            Assert.Single(safety.Rows, r => r.Action == SafetyAction.OpenRemotes));
        Assert.Equal(DetailTab.Internals, tab);

        await safety.RunRowActionCommand.ExecuteAsync(
            Assert.Single(safety.Rows, r => r.Action == SafetyAction.OpenBackups));
        Assert.Equal(DetailOverlay.Backups, overlay);
    }

    /// <summary>A group heading is a heading, not a control: it offers nothing to press.</summary>
    [Fact]
    public async Task AGroupHeading_OffersNoAction()
    {
        var host = await NewHostAsync("headings", repos: 1);
        var safety = host.NewSafety();

        Assert.All(safety.Rows.Where(r => r.IsGroup), r =>
        {
            Assert.False(r.HasAction);
            Assert.Equal(SafetyAction.None, r.Action);
        });
    }

    /// <summary>
    /// Every row is announced by a name composed on the model, and each part carries its own
    /// separator so an absent value never leaves punctuation with nothing after it.
    /// </summary>
    [Fact]
    public async Task EveryRow_IsAnnouncedWithoutDanglingSeparators()
    {
        var host = await NewHostAsync("naming", repos: 1);
        var safety = host.NewSafety();

        Assert.All(safety.Rows, r =>
        {
            Assert.NotEqual("", r.AccessibleName);
            Assert.DoesNotContain(", ,", r.AccessibleName, StringComparison.Ordinal);
            Assert.False(r.AccessibleName.EndsWith(',') || r.AccessibleName.EndsWith(", ", StringComparison.Ordinal),
                $"name ends on a separator: '{r.AccessibleName}'");
        });
    }

    // ── Fixture plumbing ────────────────────────────────────────────────────

    private static void AssertGroup(SafetyViewModel safety, string title) =>
        Assert.Contains(safety.Rows, r => r.IsGroup && r.Title == title);

    /// <summary>The reflog-only row for one repository, by the directory name the row carries.</summary>
    private static SafetyRow ReflogRowFor(SafetyViewModel safety, string directoryName) =>
        safety.Rows
            .SkipWhile(r => !(r.IsGroup && r.Title == "Reflog-only commits"))
            .Skip(1)
            .TakeWhile(r => r.IsFinding)
            .Single(r => r.Title == directoryName);

    private sealed class SafetyHost
    {
        public required string Root { get; init; }
        public required List<string> Repos { get; init; }
        public required DashboardViewModel Dashboard { get; init; }
        public required ProjectDiscoveryService Discovery { get; init; }
        public required RepoBusyRegistry Busy { get; init; }
        public required OperationHistory History { get; init; }
        public required SettingsService Settings { get; init; }

        public SafetyViewModel NewSafety(
            GitService? git = null, BackupService? backups = null, RewriteRecoveryService? recovery = null) =>
            new(Dashboard, Busy, Settings, git ?? new GitService(), backups, recovery, History, Discovery,
                // No Application in the test host, so the default post target has no dispatcher and
                // would drop every rebuild the recovery service and the project list ask for.
                uiPost: callback => callback());

        /// <summary>
        /// A clone whose current branch is both ahead of and behind its upstream: one commit pushed,
        /// rewound locally, and replaced by a different one.
        /// </summary>
        public async Task AddDivergedCloneAsync(string name)
        {
            using var seed = await TempRepo.CreateWithCommitAsync(name + "-seed");
            using var bare = await TempRepo.CreateBareFromAsync(seed, name + "-bare");

            var clone = Path.Combine(Root, name);
            await Git.RunAsync(Root, "clone", bare.FileUrl, name);
            await File.WriteAllTextAsync(Path.Combine(clone, "pushed.txt"), "pushed\n");
            await Git.RunAsync(clone, "add", "-A");
            await Git.RunAsync(clone, "commit", "-m", "pushed work");
            await Git.RunAsync(clone, "push");
            await Git.RunAsync(clone, "reset", "--hard", "HEAD~1");
            await File.WriteAllTextAsync(Path.Combine(clone, "local.txt"), "local\n");
            await Git.RunAsync(clone, "add", "-A");
            await Git.RunAsync(clone, "commit", "-m", "local work");
            Repos.Add(clone);
        }
    }

    private static async Task<SafetyHost> NewHostAsync(string prefix, int repos)
    {
        var root = TestEnv.NewDir(prefix);
        var paths = new List<string>();
        for (var i = 0; i < repos; i++)
        {
            var path = Path.Combine(root, $"repo{i}");
            Directory.CreateDirectory(path);
            await Git.RunAsync(path, "init", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "one\n");
            await Git.RunAsync(path, "add", "-A");
            await Git.RunAsync(path, "commit", "-m", "initial");
            paths.Add(path);
        }

        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
            RefreshIntervalSeconds = 7200,
            BackupRetentionCount = 5,
        });

        var gitHub = new GitHubService(settings);
        var discovery = new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore());
        var busy = new RepoBusyRegistry();
        var history = new OperationHistory(TestEnv.NewDir(prefix + "-ledger"));
        var dashboard = new DashboardViewModel(
            discovery, navigationService: null!, settings, gitHub, new GitService(),
            new ProjectWatcherService(), busy, uiPost: callback => callback(), history: history);
        await dashboard.LoadProjectsCommand.ExecutionTask!;

        return new SafetyHost
        {
            Root = root,
            Repos = paths,
            Dashboard = dashboard,
            Discovery = discovery,
            Busy = busy,
            History = history,
            Settings = settings,
        };
    }
}
