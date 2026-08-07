using System.Text;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// Service-layer coordinator + swap. These touch shared PD_DATA_DIR state (backups and the
/// rewrite journal live under AppPaths), so they join the serialized sandbox collection.
/// </summary>
[Collection("app-data-sandbox")]
public class RewriteCoordinatorTests
{
    private const string Needle = "SECRET-TOKEN-12345";
    private const string Redacted = "[REDACTED-CREDENTIAL-MATERIAL]";

    private readonly ITestOutputHelper _output;

    public RewriteCoordinatorTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static FixtureRepo NewFixture() => new(bareSource: false, prefix: "rewrite-");

    private static RewriteOptions LiteralScrub(string find = Needle, string replace = Redacted) => new()
    {
        ContentOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(find), Replace = Encoding.UTF8.GetBytes(replace) }]
    };

    private static RewriteCoordinator NewCoordinator(SwapService? swap = null)
    {
        var git = new GitService();
        return new RewriteCoordinator(
            new BackupService(git, new SettingsService()),
            new RepoBusyRegistry(),
            git,
            swap ?? new SwapService(git, GitGuard.GitExe),
            gitExecutable: GitGuard.GitExe);
    }

    private static RewriteRequest Request(FixtureRepo f) => new()
    {
        RepoPath = f.SourcePath,
        Options = LiteralScrub(),
        ExportTimeout = TimeSpan.FromMinutes(3),
        ImportTimeout = TimeSpan.FromMinutes(3)
    };

    /// <summary>for-each-ref layout plus HEAD — the canonical "the refs match exactly" signal.</summary>
    private static string RefState(string repo)
    {
        var refs = FixtureRepo.RunGit(repo, ["for-each-ref", "--format=%(objectname) %(refname)"], null, null).Trim();
        return HistoryTestSupport.DescribeHead(repo) + "\n" + refs;
    }

    private static List<string> AllCommits(string repo) =>
        FixtureRepo.RunGit(repo, ["rev-list", "--all"], null, null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

    private static int GrepHits(string repo, IReadOnlyList<string> commits, string needle)
    {
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

    /// <summary>A source repo carrying the needle across branches, a merge, and a tag.</summary>
    private static void SeedSecretHistory(FixtureRepo f)
    {
        f.Write("a.txt", $"line one {Needle}\n");
        f.Write("docs/keys.md", $"key: {Needle}\n");
        f.CommitAll("add secrets");
        f.Write("a.txt", $"line one {Needle}\nline two {Needle}\n");
        f.CommitAll("more secrets");
        f.Git("switch", "-q", "-c", "side");
        f.Write("side.txt", $"side {Needle}\n");
        f.CommitAll("side secret");
        f.Git("switch", "-q", "main");
        f.Write("main.txt", "clean\n");
        f.CommitAll("diverge");
        f.Git("merge", "-q", "--no-ff", "side", "-m", "merge side");
        f.Git("tag", "-a", "v-secret", "-m", "release tag");
    }

    [Fact]
    public async Task ExecuteAsync_ScrubsSourceHistory_WithBackupJournalClearedAndUndoOffered()
    {
        using var f = NewFixture();
        SeedSecretHistory(f);

        var sourceCommits = AllCommits(f.SourcePath);
        var beforeHits = GrepHits(f.SourcePath, sourceCommits, Needle);
        Assert.True(beforeHits >= 5, $"fixture must carry the needle across history, found {beforeHits}");

        var coordinator = NewCoordinator();
        var result = await coordinator.ExecuteAsync(Request(f));

        Assert.True(result.Success, result.FailureReason);
        Assert.NotNull(result.Report);
        Assert.NotNull(result.Undo);
        Assert.NotNull(result.Swap);
        Assert.True(result.Swap!.Success);

        // Flagship proof: the SOURCE repository's history now greps clean.
        var afterHits = GrepHits(f.SourcePath, AllCommits(f.SourcePath), Needle);
        Assert.Equal(0, afterHits);
        _output.WriteLine($"scrub evidence: {beforeHits} needle hit(s) before across {sourceCommits.Count} commits, 0 after in the real repo");

        // The tip's working tree was reset to scrubbed content.
        Assert.Contains(Redacted, await File.ReadAllTextAsync(Path.Combine(f.SourcePath, "docs", "keys.md")));
        Assert.DoesNotContain(Needle, await File.ReadAllTextAsync(Path.Combine(f.SourcePath, "docs", "keys.md")));

        // A verified backup exists, the scrub is honestly complete, and the journal is cleared.
        Assert.NotEmpty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(f.SourcePath));
        var scrub = Assert.Single(result.Report!.ScrubChecks);
        Assert.True(scrub.Complete);
        Assert.Empty(scrub.Hits);
        Assert.Null(await new RewriteJournal().ReadPendingAsync());
    }

    [Fact]
    public async Task Undo_AfterRewrite_ReturnsSourceRefsToPreRewriteStateExactly()
    {
        using var f = NewFixture();
        SeedSecretHistory(f);

        var before = RefState(f.SourcePath);

        var result = await NewCoordinator().ExecuteAsync(Request(f));
        Assert.True(result.Success, result.FailureReason);
        Assert.NotEqual(before, RefState(f.SourcePath));

        var restore = await result.Undo!.RestoreAsync();
        Assert.True(restore.Success, restore.Message);

        // Byte-identical ref layout and HEAD: the undo is exact.
        Assert.Equal(before, RefState(f.SourcePath));
        // And the needle is back — the restore reinstated the original objects.
        Assert.True(GrepHits(f.SourcePath, AllCommits(f.SourcePath), Needle) >= 5);
        _output.WriteLine("undo round-trip: source refs byte-identical to the pre-rewrite snapshot");
    }

    [Fact]
    public async Task ExecuteAsync_DirtyWorkingTree_RefusesAndLeavesSourceUnchanged()
    {
        using var f = NewFixture();
        f.Write("a.txt", $"clean {Needle}\n");
        f.CommitAll("committed");

        var before = RefState(f.SourcePath);
        // An uncommitted edit: the clean-tree gate must refuse before any backup or swap.
        f.Write("a.txt", $"dirty edit {Needle}\n");

        var result = await NewCoordinator().ExecuteAsync(Request(f));

        Assert.False(result.Success);
        Assert.Contains("uncommitted", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, RefState(f.SourcePath));
        // The gate precedes the backup, so none was taken.
        Assert.Empty(await new BackupService(new GitService(), new SettingsService()).ListBackupsAsync(f.SourcePath));
        // The dirty edit is untouched.
        Assert.Contains("dirty edit", await File.ReadAllTextAsync(Path.Combine(f.SourcePath, "a.txt")));
    }

    [Fact]
    public async Task ExecuteAsync_RepoBusy_RefusesWithoutTouchingSource()
    {
        using var f = NewFixture();
        f.Write("a.txt", $"{Needle}\n");
        f.CommitAll("one");

        var before = RefState(f.SourcePath);
        var busy = new RepoBusyRegistry();
        var git = new GitService();
        var coordinator = new RewriteCoordinator(
            new BackupService(git, new SettingsService()), busy, git,
            new SwapService(git, GitGuard.GitExe), gitExecutable: GitGuard.GitExe);

        using (busy.Acquire(f.SourcePath))
        {
            var result = await coordinator.ExecuteAsync(Request(f));
            Assert.False(result.Success);
            Assert.Contains("busy", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, RefState(f.SourcePath));
    }

    [Fact]
    public async Task ExecuteAsync_BinaryCarriedNeedle_SurfacesScrubIncompleteThroughResult()
    {
        using var f = NewFixture();
        byte[] binary = [0x00, 0xFF, .. Encoding.ASCII.GetBytes(Needle), 0x80, 0xFE, 0x00];
        f.WriteBytes("blob.bin", binary);
        f.Write("plain.txt", $"text {Needle}\n");
        f.CommitAll("mixed content");

        var result = await NewCoordinator().ExecuteAsync(Request(f));

        // The text scrubbed cleanly, so the swap still applies — but the report must not claim
        // a clean bill: the binary needle survives where git grep -I cannot see it.
        Assert.True(result.Success, result.FailureReason);
        var scrub = Assert.Single(result.Report!.ScrubChecks);
        Assert.False(scrub.Complete);
        Assert.NotEmpty(scrub.Hits);
        Assert.Contains(scrub.Hits, h => h.Contains("binary-blob"));
        _output.WriteLine($"scrub-incomplete surfaced through coordinator: Complete={scrub.Complete}, hits={scrub.Hits.Count}");
    }

    [Fact]
    public async Task ExecuteAsync_SwapThrowsAfterBackup_LeavesJournalPendingForRecovery()
    {
        using var f = NewFixture();
        f.Write("a.txt", $"{Needle}\n");
        f.CommitAll("one");

        var git = new GitService();
        var coordinator = new RewriteCoordinator(
            new BackupService(git, new SettingsService()), new RepoBusyRegistry(), git,
            new ThrowingSwap(), gitExecutable: GitGuard.GitExe);

        var result = await coordinator.ExecuteAsync(Request(f));

        Assert.False(result.Success);
        Assert.Contains("swap failed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        // The undo is still handed back so a partial op can be reverted.
        Assert.NotNull(result.Undo);

        // The journal survives the crash; the recovery service detects it on the next launch.
        var recovery = new RewriteRecoveryService(new RewriteJournal());
        await recovery.StartAsync(CancellationToken.None);
        Assert.True(recovery.DetectionComplete);
        var pending = Assert.Single(recovery.Pending);
        Assert.Equal(f.SourcePath, pending.RepoPath);
        Assert.Equal("swap", pending.Phase);

        // Clear it so it does not bleed into the next sandbox test.
        await new RewriteJournal().ClearAllAsync();
    }

    private sealed class ThrowingSwap : SwapService
    {
        public ThrowingSwap() : base(new GitService(), GitGuard.GitExe) { }

        public override Task<SwapResult> ApplySwapAsync(string sourceRepo, string tempBareRepo, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated crash mid-swap");
    }

    // ── SwapService pre-flight and atomicity, exercised directly ──────────────

    /// <summary>Builds a bare repo holding one commit on refs/heads/main from a fast-import stream (protectNTFS disabled so illegal names can be planted).</summary>
    private static string CraftBare(string streamBody)
    {
        var bare = Path.Combine(TestEnv.NewDir("crafted"), "crafted.git");
        Directory.CreateDirectory(bare);
        FixtureRepo.RunGit(bare, ["init", "--bare", "-q", "-b", "main"], null, null);
        FixtureRepo.RunGit(bare, ["-c", "core.protectNTFS=false", "fast-import", "--quiet"],
            Encoding.UTF8.GetBytes(streamBody), null);
        return bare;
    }

    [Fact]
    public async Task ApplySwap_RewrittenTreeHasWindowsIllegalName_RefusesBeforeAnyRefMoves()
    {
        using var f = NewFixture();
        f.Write("a.txt", "clean content\n");
        f.CommitAll("one");
        var before = RefState(f.SourcePath);

        // A rewritten tree carrying a reserved DOS device name ("aux") — storable in a bare
        // repo but impossible to check out on Windows. The swap must refuse in pre-flight.
        const string content = "payload\n";
        const string message = "crafted commit\n";
        var stream =
            $"blob\nmark :1\ndata {Encoding.UTF8.GetByteCount(content)}\n{content}" +
            "commit refs/heads/main\nmark :2\n" +
            "author T <t@t> 1700000000 +0000\ncommitter T <t@t> 1700000000 +0000\n" +
            $"data {message.Length}\n{message}" +
            "M 100644 :1 config/aux\n\n";
        var bare = CraftBare(stream);

        var swap = new SwapService(new GitService(), GitGuard.GitExe);
        var result = await swap.ApplySwapAsync(f.SourcePath, bare);

        Assert.False(result.Success);
        Assert.Contains("check out", result.RefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aux", result.RefusalReason, StringComparison.OrdinalIgnoreCase);
        // Not one source ref moved.
        Assert.Equal(before, RefState(f.SourcePath));
        _output.WriteLine($"NTFS pre-flight refusal: {result.RefusalReason}");
    }

    [Fact]
    public async Task ApplySwap_UpdateRefTransactionFails_LeavesEverySourceRefUnchanged()
    {
        using var f = NewFixture();
        f.Write("a.txt", $"{Needle}\n");
        f.CommitAll("one");
        f.Git("branch", "feature");

        // A genuinely rewritten temp bare so the reconciliation actually tries to move main.
        var rewriter = new HistoryRewriter(GitGuard.GitExe);
        await rewriter.RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = f.SourcePath,
            WorkingDirectory = f.WorkDir,
            TargetBareRepository = f.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3),
            Rewrite = LiteralScrub(),
            GitExecutable = GitGuard.GitExe
        });

        var before = RefState(f.SourcePath);

        // Poison the ref update: a pre-existing lock on refs/heads/main makes the update-ref
        // --stdin transaction fail to acquire its lock, so the whole reconciliation aborts.
        var lockPath = Path.Combine(f.SourcePath, ".git", "refs", "heads", "main.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "");
        try
        {
            var swap = new SwapService(new GitService(), GitGuard.GitExe);
            var result = await swap.ApplySwapAsync(f.SourcePath, f.TargetPath);

            Assert.False(result.Success);
            Assert.Contains("reconciliation", result.RefusalReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(lockPath);
        }

        // Every source ref (main and feature) is exactly where it was — the transaction rolled back.
        Assert.Equal(before, RefState(f.SourcePath));
        Assert.True(GrepHits(f.SourcePath, AllCommits(f.SourcePath), Needle) >= 1);
        _output.WriteLine("atomicity: poisoned ref transaction aborted, every source ref unchanged");
    }

    [Fact]
    public async Task PreviewAsync_ReportsWithoutSwapping_ThenExecuteReusesTheBare()
    {
        using var f = NewFixture();
        SeedSecretHistory(f);
        var before = RefState(f.SourcePath);

        var coordinator = NewCoordinator();
        using var preview = await coordinator.PreviewAsync(Request(f));

        // Preview is a pure dry run: the source is untouched, but the report is fully populated.
        Assert.Equal(before, RefState(f.SourcePath));
        Assert.True(preview.Report.BlobsChanged > 0);
        Assert.Single(preview.Report.ScrubChecks);
        Assert.True(Directory.Exists(preview.TempBareRepo));

        // Executing the preview reuses its bare and applies the swap.
        var result = await coordinator.ExecuteAsync(preview);
        Assert.True(result.Success, result.FailureReason);
        Assert.Same(preview.Report, result.Report);
        Assert.Equal(0, GrepHits(f.SourcePath, AllCommits(f.SourcePath), Needle));
    }
}
