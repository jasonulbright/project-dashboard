using System.Text;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Surgery;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The recovery marker is cleared AFTER the operation has already changed the repository, so a
/// throw from that write is a bookkeeping failure, not an operation failure. Left to propagate it
/// escapes both coordinators — neither catches around the clear — and takes the ledger write with
/// it, so the one operation that most needs a record (a repo-mutating rewrite or rebase) leaves
/// none at all.
///
/// The journal file is made read-only mid-operation, between the entry being written and the clear:
/// the atomic swap inside the durable write replaces the live file, which a read-only destination
/// refuses. Backups and the journal live under AppPaths, so these join the serialized collection.
/// </summary>
[Collection("app-data-sandbox")]
public class OperationHistoryJournalFailureTests
{
    private readonly ITestOutputHelper _output;

    public OperationHistoryJournalFailureTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("ops-journal-ledger"));

    /// <summary>
    /// A second repository's entry, so clearing the one under test rewrites the file rather than
    /// deleting it — deletion is already best-effort, and the rewrite is the write that can throw.
    /// </summary>
    private static async Task<RewriteJournal> SeededJournalAsync(string path)
    {
        var journal = new RewriteJournal(path);
        await journal.BeginAsync(new RewriteJournalEntry
        {
            RepoPath = Path.Combine(TestEnv.Root, "some-other-repository"),
            Phase = "swap",
            UtcStamp = "20260101-000000000"
        });
        return journal;
    }

    private static void Unseal(string path)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public async Task ASurgeryWhoseMarkerClearThrows_StillSucceedsAndIsStillRecorded()
    {
        using var repo = await RailsRepo.CreateAsync("ops-journal-surgery");
        var history = NewHistory();
        var journalPath = Path.Combine(TestEnv.NewDir("ops-journal-file"), "rewrite-journal.json");
        var journal = await SeededJournalAsync(journalPath);

        var git = new SealJournalOnResetGitService(journalPath);
        var coordinator = new SurgeryCoordinator(
            new BackupService(git, new SettingsService(), history), new RepoBusyRegistry(), git,
            journal: journal, history: history);

        SurgeryResult result;
        try { result = await coordinator.ResetAsync(repo.Path, "HEAD", ResetMode.Soft); }
        finally { Unseal(journalPath); }

        Assert.True(git.Sealed, "the journal was never made unwritable, so the clear never failed");
        Assert.True(result.Success, result.FailureReason);
        var record = Assert.Single(history.Tail(repo.Path).Records);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
        Assert.Equal(OperationCategory.Surgery, record.Category);
        _output.WriteLine($"recorded despite the failed clear: {record.Label} · {record.Outcome}");
    }

    [Fact]
    public async Task ARewriteWhoseMarkerClearThrows_StillSucceedsAndIsStillRecorded()
    {
        using var fixture = new FixtureRepo(bareSource: false, prefix: "ops-journal-rewrite-");
        fixture.Write("a.txt", "SECRET-TOKEN-12345\n");
        fixture.CommitAll("one");

        var history = NewHistory();
        var journalPath = Path.Combine(TestEnv.NewDir("ops-journal-file"), "rewrite-journal.json");
        var journal = await SeededJournalAsync(journalPath);

        var git = new GitService();
        var swap = new SealJournalOnSwap(journalPath);
        var coordinator = new RewriteCoordinator(
            new BackupService(git, new SettingsService(), history), new RepoBusyRegistry(), git, swap,
            journal, gitExecutable: GitGuard.GitExe, history: history);

        RewriteExecutionResult result;
        try
        {
            result = await coordinator.ExecuteAsync(new RewriteRequest
            {
                RepoPath = fixture.SourcePath,
                Options = new RewriteOptions
                {
                    ContentOps =
                    [
                        new LiteralReplace
                        {
                            Find = Encoding.UTF8.GetBytes("SECRET-TOKEN-12345"),
                            Replace = Encoding.UTF8.GetBytes("[REDACTED]")
                        }
                    ]
                },
                ExportTimeout = TimeSpan.FromMinutes(3),
                ImportTimeout = TimeSpan.FromMinutes(3)
            });
        }
        finally { Unseal(journalPath); }

        Assert.True(swap.Sealed, "the journal was never made unwritable, so the clear never failed");
        Assert.True(result.Success, result.FailureReason);
        var record = Assert.Single(history.Tail(fixture.SourcePath).Records);
        Assert.Equal(OperationOutcome.Succeeded, record.Outcome);
        Assert.Equal(OperationCategory.Rewrite, record.Category);
    }

    /// <summary>Makes the journal unwritable at the reset, which runs after its entry was written.</summary>
    private sealed class SealJournalOnResetGitService : GitService
    {
        private readonly string _journalPath;

        public SealJournalOnResetGitService(string journalPath) => _journalPath = journalPath;

        public bool Sealed { get; private set; }

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args as IReadOnlyList<string> ?? args.ToList();
            if (!Sealed && list.Contains("reset") && File.Exists(_journalPath))
            {
                File.SetAttributes(_journalPath, FileAttributes.ReadOnly);
                Sealed = true;
            }
            return base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    /// <summary>
    /// Reports a landed swap without touching the repository, and seals the journal on the way —
    /// the clear that follows is the write under test.
    /// </summary>
    private sealed class SealJournalOnSwap : SwapService
    {
        private readonly string _journalPath;

        public SealJournalOnSwap(string journalPath) : base(new GitService()) => _journalPath = journalPath;

        public bool Sealed { get; private set; }

        public override Task<SwapResult> ApplySwapAsync(
            string sourceRepo, string tempBareRepo, IProgress<RewritePhase>? phase = null,
            CancellationToken ct = default)
        {
            if (File.Exists(_journalPath))
            {
                File.SetAttributes(_journalPath, FileAttributes.ReadOnly);
                Sealed = true;
            }
            return Task.FromResult(new SwapResult(true, null, [], null, null));
        }
    }
}
