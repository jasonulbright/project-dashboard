using System.Text;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The cancellation contract, exercised against real repositories: a rewrite is freely
/// cancellable while it is scratch work, and stops being cancellable the moment the swap can
/// begin moving the source's own refs. The boundary is a single line in
/// <see cref="SwapService.ApplySwapAsync"/>, and the phase report made on that line is the hook
/// these tests cancel from — so what is proved is the boundary itself, not a delay near it.
///
/// These touch shared PD_DATA_DIR state (backups and the journal live under AppPaths), so they
/// join the serialized sandbox collection.
/// </summary>
[Collection("app-data-sandbox")]
public class RewriteCancellationTests
{
    private const string Needle = "SECRET-TOKEN-12345";
    private const string Redacted = "[REDACTED-CREDENTIAL-MATERIAL]";

    private readonly ITestOutputHelper _output;

    public RewriteCancellationTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static RewriteOptions LiteralScrub() => new()
    {
        ContentOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }]
    };

    private static RewriteRequest Request(FixtureRepo f) => new()
    {
        RepoPath = f.SourcePath,
        Options = LiteralScrub(),
        ExportTimeout = TimeSpan.FromMinutes(3),
        ImportTimeout = TimeSpan.FromMinutes(3)
    };

    private static RewriteCoordinator NewCoordinator(SwapService? swap = null, RepoBusyRegistry? busy = null)
    {
        var git = new GitService();
        return new RewriteCoordinator(
            new BackupService(git, new SettingsService()),
            busy ?? new RepoBusyRegistry(),
            git,
            swap ?? new SwapService(git, GitGuard.GitExe),
            gitExecutable: GitGuard.GitExe);
    }

    private static string RefState(string repo)
    {
        var refs = FixtureRepo.RunGit(repo, ["for-each-ref", "--format=%(objectname) %(refname)"], null, null).Trim();
        return HistoryTestSupport.DescribeHead(repo) + "\n" + refs;
    }

    private static List<string> AllCommits(string repo) =>
        FixtureRepo.RunGit(repo, ["rev-list", "--all"], null, null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

    private static int GrepHits(string repo, string needle)
    {
        var commits = AllCommits(repo);
        var hits = 0;
        for (var i = 0; i < commits.Count; i += 100)
        {
            var chunk = commits.Skip(i).Take(100).ToList();
            var result = ProcessRunner.RunAsync(
                GitGuard.GitExe, ["grep", "-I", "--fixed-strings", "-e", needle, .. chunk], repo,
                TimeSpan.FromMinutes(2),
                new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" }).GetAwaiter().GetResult();
            Assert.True(result.ExitCode is 0 or 1, $"git grep exited {result.ExitCode}: {result.StdErr}");
            hits += result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        return hits;
    }

    private static void SeedSecretHistory(FixtureRepo f)
    {
        f.Write("a.txt", $"line one {Needle}\n");
        f.Write("docs/keys.md", $"key: {Needle}\n");
        f.CommitAll("add secrets");
        f.Git("switch", "-q", "-c", "side");
        f.Write("side.txt", $"side {Needle}\n");
        f.CommitAll("side secret");
        f.Git("switch", "-q", "main");
        f.Write("main.txt", "clean\n");
        f.CommitAll("diverge");
        f.Git("tag", "-a", "v-secret", "-m", "release tag");
    }

    /// <summary>Rewrites the fixture into its own target bare, ready for a direct swap.</summary>
    private static async Task RewriteIntoTargetAsync(FixtureRepo f) =>
        await new HistoryRewriter(GitGuard.GitExe).RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = f.SourcePath,
            WorkingDirectory = f.WorkDir,
            TargetBareRepository = f.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3),
            Rewrite = LiteralScrub(),
            GitExecutable = GitGuard.GitExe
        }, CancellationToken.None);

    /// <summary>Cancels the run at the phase it is told about, which is the boundary under test.</summary>
    private sealed class CancelOnPhase(CancellationTokenSource source, RewritePhase at) : IProgress<RewritePhase>
    {
        public int Reports { get; private set; }

        public void Report(RewritePhase value)
        {
            Reports++;
            if (value == at) source.Cancel();
        }
    }

    // ── The point of no return holds ─────────────────────────────────────────

    /// <summary>
    /// The load-bearing refusal. `git update-ref --stdin` commits by renaming lock files one at
    /// a time, so a kill part-way through leaves some refs moved and others not. Cancelling at
    /// the exact instant the transaction may begin must therefore be ignored: the swap runs to
    /// completion and the repository ends in the all-or-nothing state it promised.
    /// </summary>
    [Fact]
    public async Task ApplySwap_CancelledAtThePointOfNoReturn_IgnoresItAndAppliesTheWholeSwap()
    {
        using var f = new FixtureRepo(bareSource: false, prefix: "cancel-swap-");
        SeedSecretHistory(f);
        await RewriteIntoTargetAsync(f);

        var before = RefState(f.SourcePath);
        Assert.True(GrepHits(f.SourcePath, Needle) >= 3);

        using var cts = new CancellationTokenSource();
        var phase = new CancelOnPhase(cts, RewritePhase.Applying);
        var swap = new SwapService(new GitService(), GitGuard.GitExe);

        var result = await swap.ApplySwapAsync(f.SourcePath, f.TargetPath, phase, cts.Token);

        Assert.True(cts.IsCancellationRequested, "the phase hook must have fired at the boundary");
        Assert.Equal(1, phase.Reports);
        Assert.True(result.Success, result.RefusalReason);

        // Every ref moved, HEAD moved, and the working tree was reset — nothing was left half done.
        Assert.NotEqual(before, RefState(f.SourcePath));
        Assert.Equal(0, GrepHits(f.SourcePath, Needle));
        Assert.Contains(Redacted, await File.ReadAllTextAsync(Path.Combine(f.SourcePath, "docs", "keys.md")));
        Assert.Equal(
            FixtureRepo.RunGit(f.TargetPath, ["rev-parse", "refs/heads/main"], null, null).Trim(),
            FixtureRepo.RunGit(f.SourcePath, ["rev-parse", "refs/heads/main"], null, null).Trim());
        Assert.Equal(
            FixtureRepo.RunGit(f.TargetPath, ["rev-parse", "refs/heads/side"], null, null).Trim(),
            FixtureRepo.RunGit(f.SourcePath, ["rev-parse", "refs/heads/side"], null, null).Trim());
        _output.WriteLine("cancel at the point of no return was refused: every ref reconciled and the tree reset");
    }

    /// <summary>Before that boundary there is nothing to protect, so the cancellation is honoured and no ref moves.</summary>
    [Fact]
    public async Task ApplySwap_CancelledBeforeTheRefTransaction_ThrowsAndMovesNoRef()
    {
        using var f = new FixtureRepo(bareSource: false, prefix: "cancel-early-");
        SeedSecretHistory(f);
        await RewriteIntoTargetAsync(f);

        var before = RefState(f.SourcePath);
        var hitsBefore = GrepHits(f.SourcePath, Needle);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var phase = new CancelOnPhase(cts, RewritePhase.Applying);
        var swap = new SwapService(new GitService(), GitGuard.GitExe);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => swap.ApplySwapAsync(f.SourcePath, f.TargetPath, phase, cts.Token));

        // The boundary was never reached, so no phase was reported and nothing moved.
        Assert.Equal(0, phase.Reports);
        Assert.Equal(before, RefState(f.SourcePath));
        Assert.Equal(hitsBefore, GrepHits(f.SourcePath, Needle));
    }

    // ── The coordinator's cancelled outcome ──────────────────────────────────

    /// <summary>
    /// A cancellation observed after the journal entry was written is not an interruption: the
    /// swap refused to start, so nothing needs recovering. Leaving the entry would raise a
    /// crash-recovery prompt at the next launch over a repository nothing touched.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledBeforeTheSwap_ReportsCancelledAndClearsTheJournal()
    {
        using var f = new FixtureRepo(bareSource: false, prefix: "cancel-exec-");
        SeedSecretHistory(f);

        var before = RefState(f.SourcePath);
        var hitsBefore = GrepHits(f.SourcePath, Needle);

        using var cts = new CancellationTokenSource();
        var coordinator = NewCoordinator(new CancellingSwap(cts));

        var result = await coordinator.ExecuteAsync(Request(f), cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Null(result.FailureReason);
        // Nothing was applied, so there is nothing to undo — offering a restore would offer to
        // restore the state the repository is already in.
        Assert.Null(result.Undo);

        Assert.Equal(before, RefState(f.SourcePath));
        Assert.Equal(hitsBefore, GrepHits(f.SourcePath, Needle));
        Assert.Null(await new RewriteJournal().ReadPendingAsync(f.SourcePath));

        // The backup the run took is still on disk, so the Backups surface can still reach it.
        Assert.NotEmpty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(f.SourcePath));
        _output.WriteLine("cancelled before the swap: refs untouched, journal clear, backup retained");
    }

    /// <summary>A cancelled run leaves the repository free for the next one — the lease is not stranded.</summary>
    [Fact]
    public async Task ExecuteAsync_Cancelled_ReleasesTheRepositoryLease()
    {
        using var f = new FixtureRepo(bareSource: false, prefix: "cancel-lease-");
        SeedSecretHistory(f);

        var busy = new RepoBusyRegistry();
        using var cts = new CancellationTokenSource();

        var result = await NewCoordinator(new CancellingSwap(cts), busy).ExecuteAsync(Request(f), cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(busy.IsBusy(f.SourcePath));

        // And a fresh run against the same repository is accepted rather than refused as busy.
        var second = await NewCoordinator(busy: busy).ExecuteAsync(Request(f));
        Assert.True(second.Success, second.FailureReason);
        Assert.Equal(0, GrepHits(f.SourcePath, Needle));
    }

    /// <summary>
    /// The whole pipeline, cancelled at the boundary rather than before it: the swap ignores the
    /// request, so the run reports success and the journal is cleared as a completed rewrite.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledAtThePointOfNoReturn_CompletesAndReportsSuccess()
    {
        using var f = new FixtureRepo(bareSource: false, prefix: "cancel-late-");
        SeedSecretHistory(f);

        using var cts = new CancellationTokenSource();
        var phase = new CancelOnPhase(cts, RewritePhase.Applying);

        var result = await NewCoordinator().ExecuteAsync(Request(f), cts.Token, phase);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(result.Success, result.FailureReason);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Undo);
        Assert.Equal(0, GrepHits(f.SourcePath, Needle));
        Assert.Null(await new RewriteJournal().ReadPendingAsync(f.SourcePath));
    }

    /// <summary>
    /// A swap that cancels the run's own token at its entry, so the cancellation lands after the
    /// backup and the journal entry but before any ref could move — the window the coordinator's
    /// cancelled outcome exists for.
    /// </summary>
    private sealed class CancellingSwap(CancellationTokenSource source)
        : SwapService(new GitService(), GitGuard.GitExe)
    {
        public override Task<SwapResult> ApplySwapAsync(
            string sourceRepo, string tempBareRepo, IProgress<RewritePhase>? phase = null, CancellationToken ct = default)
        {
            source.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SwapResult.Refused("unreachable: the token was cancelled above"));
        }
    }
}
