using System.Text;
using System.Text.Json;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The store under the operation history: what one append writes, what a read is allowed to claim,
/// and the two ways the file can be damaged — a torn line from a concurrent writer, and a line that
/// no longer parses. Neither may cost more than the record it touches.
///
/// Every ledger here is rooted in its own fixture directory, so nothing depends on the shared data
/// directory and these run outside the serialized sandbox collection.
/// </summary>
public class OperationHistoryTests
{
    private readonly ITestOutputHelper _output;

    public OperationHistoryTests(ITestOutputHelper output) => _output = output;

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("ops-ledger"));

    private static string RepoPathFor(string name) => Path.Combine(TestEnv.Root, name);

    [Fact]
    public void ARecord_RoundTripsThroughTheLedger()
    {
        var history = NewHistory();
        var repo = RepoPathFor("round-trip");
        var started = DateTimeOffset.UtcNow.AddSeconds(-4);

        var written = history.Append(OperationRecord.For(
            repo, OperationCategory.Rewrite, "History rewrite", OperationOutcome.Failed,
            "fatal: bad object HEAD", started, backupStamp: "20260809-101112131",
            recovery: new RecoveryNote { Kind = RecoveryKind.RestoreFromBackup, AppliedUtc = started, OfId = "abc" }));

        var read = Assert.Single(history.Tail(repo).Records);
        Assert.Equal(written.Id, read.Id);
        Assert.Equal(OperationCategory.Rewrite, read.Category);
        Assert.Equal(OperationOutcome.Failed, read.Outcome);
        Assert.Equal("History rewrite", read.Label);
        Assert.Equal("fatal: bad object HEAD", read.Detail);
        Assert.Equal("20260809-101112131", read.BackupStamp);
        Assert.Equal(RecoveryKind.RestoreFromBackup, read.Recovery!.Kind);
        Assert.Equal("abc", read.Recovery.OfId);
        Assert.Equal(OperationRecord.CurrentSchema, read.Schema);
        Assert.Equal(RepoKey.For(repo), read.RepoKey);
    }

    /// <summary>
    /// A detail carrying newlines — every verbatim git error does — must not become several lines
    /// in a line-per-record file, or every record after it is unreadable.
    /// </summary>
    [Fact]
    public void AMultiLineDetail_StaysOneLineOnDisk()
    {
        var history = NewHistory();
        var repo = RepoPathFor("multi-line");
        history.Append(OperationRecord.For(repo, OperationCategory.Working, "Commit",
            OperationOutcome.Failed, "error: one\nerror: two\r\nerror: three", DateTimeOffset.UtcNow));
        history.Append(OperationRecord.For(repo, OperationCategory.Working, "Stage all",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow));

        var lines = File.ReadAllLines(LedgerPath(history, repo));
        Assert.Equal(2, lines.Length);

        var records = history.Tail(repo).Records;
        Assert.Equal(2, records.Count);
        Assert.Contains("error: two", records[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADetailLongerThanTheCap_IsClampedRatherThanWritten()
    {
        var history = NewHistory();
        var repo = RepoPathFor("clamped");
        history.Append(OperationRecord.For(repo, OperationCategory.Working, "Commit",
            OperationOutcome.Failed, new string('x', OperationRecord.MaxDetailLength * 3), DateTimeOffset.UtcNow));

        var read = Assert.Single(history.Tail(repo).Records);
        Assert.Equal(OperationRecord.MaxDetailLength + 1, read.Detail.Length);
        Assert.EndsWith("…", read.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Concurrency is the failure this store's append discipline exists for: an interleaved write
    /// leaves a line that parses as neither record, and the tail read would then be short by more
    /// than the one record that lost.
    /// </summary>
    [Fact]
    public void ConcurrentAppends_LeaveNoTornLine()
    {
        var root = TestEnv.NewDir("ops-contended");
        var repo = RepoPathFor("contended");
        const int writers = 8;
        const int each = 40;

        Parallel.For(0, writers, w =>
        {
            var history = new OperationHistory(root);
            for (var i = 0; i < each; i++)
                history.Append(OperationRecord.For(repo, OperationCategory.Working, $"writer {w} op {i}",
                    OperationOutcome.Succeeded, new string('d', 200), DateTimeOffset.UtcNow));
        });

        var page = new OperationHistory(root).Tail(repo, writers * each);
        Assert.Equal(0, page.SkippedLines);
        Assert.Equal(writers * each, page.Records.Count);
        Assert.Equal(writers * each, page.Records.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void AMalformedLine_CostsThatRecordAndNothingElse()
    {
        var history = NewHistory();
        var repo = RepoPathFor("malformed");
        history.Append(OperationRecord.For(repo, OperationCategory.Working, "first",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow));
        File.AppendAllText(LedgerPath(history, repo), "{ this is not json\n");
        history.Append(OperationRecord.For(repo, OperationCategory.Working, "second",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow));

        var page = history.Tail(repo);
        Assert.Equal(1, page.SkippedLines);
        Assert.Equal(["second", "first"], page.Records.Select(r => r.Label));
    }

    /// <summary>
    /// A record written by a later build naming a category this one does not know must not take the
    /// whole line with it: the timestamp, label, and detail are still worth reading.
    /// </summary>
    [Fact]
    public void AnUnknownEnumValue_FallsBackRatherThanDiscardingTheRecord()
    {
        var history = NewHistory();
        var repo = RepoPathFor("future-enum");
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath(history, repo))!);
        File.AppendAllText(LedgerPath(history, repo),
            JsonSerializer.Serialize(new
            {
                id = "future",
                startedUtc = DateTimeOffset.UtcNow,
                endedUtc = DateTimeOffset.UtcNow,
                repoPath = repo,
                repoKey = RepoKey.For(repo),
                category = "Teleportation",
                label = "From a later build",
                outcome = "Ascended",
                detail = "",
                schema = 99
            }) + "\n");

        var read = Assert.Single(history.Tail(repo).Records);
        Assert.Equal("From a later build", read.Label);
        Assert.Equal(OperationCategory.Maintenance, read.Category);
        Assert.Equal(OperationOutcome.Unknown, read.Outcome);
    }

    [Fact]
    public void TheTail_ReturnsTheNewestRecordsAndSaysItIsATail()
    {
        var history = NewHistory();
        var repo = RepoPathFor("tail");
        for (var i = 0; i < 40; i++)
            history.Append(OperationRecord.For(repo, OperationCategory.Working, $"op {i:D2}",
                OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow));

        var page = history.Tail(repo, 5);
        Assert.True(page.Truncated);
        Assert.Equal(["op 39", "op 38", "op 37", "op 36", "op 35"], page.Records.Select(r => r.Label));

        var whole = history.Tail(repo, 500);
        Assert.False(whole.Truncated);
        Assert.Equal(40, whole.Records.Count);
    }

    /// <summary>
    /// Rotation keeps the tail contiguous across the boundary, and the read has to admit that the
    /// generation behind the rotated file is gone rather than presenting the list as complete.
    /// </summary>
    [Fact]
    public void Rotation_KeepsTheTailContiguousAndReportsWhatWasDropped()
    {
        var history = NewHistory();
        var repo = RepoPathFor("rotate");
        var padding = new string('p', 4096);

        var written = 0;
        while (!File.Exists(RotatedPath(history, repo)))
        {
            history.Append(OperationRecord.For(repo, OperationCategory.Working, $"op {written:D5}",
                OperationOutcome.Succeeded, padding, DateTimeOffset.UtcNow));
            written++;
            Assert.True(written < 5000, "the ledger never rotated");
        }
        // One past the roll, so the live file is not empty and the tail must span both files.
        history.Append(OperationRecord.For(repo, OperationCategory.Working, $"op {written:D5}",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow));

        var page = history.Tail(repo, written + 1);
        Assert.True(page.Rotated);
        Assert.Equal($"op {written:D5}", page.Records[0].Label);
        Assert.Equal("op 00000", page.Records[^1].Label);
        Assert.Equal(written + 1, page.Records.Count);
        _output.WriteLine($"rotated after {written} records; tail spans both generations");
    }

    [Fact]
    public void ARepositoryWithNoLedger_ReadsAsNothingRecorded()
    {
        var page = NewHistory().Tail(RepoPathFor("never-touched"));
        Assert.Empty(page.Records);
        Assert.False(page.Truncated);
        Assert.False(page.Rotated);
        Assert.Null(page.ReadError);
        Assert.Null(page.OldestRetainedUtc);
    }

    /// <summary>
    /// The store's whole contract in one line: a write that cannot land is a logged warning, never
    /// an exception a caller has to defend an operation against.
    /// </summary>
    [Fact]
    public void AnUnwritableLedger_DoesNotThrow()
    {
        var root = TestEnv.NewDir("ops-blocked");
        var repo = RepoPathFor("blocked");
        var dir = Path.Combine(root, RepoKey.For(repo));
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        // A FILE where the per-repo directory belongs: CreateDirectory then fails for every append.
        File.WriteAllText(dir, "not a directory");

        var history = new OperationHistory(root);
        var record = history.Append(OperationRecord.For(repo, OperationCategory.Working, "Commit",
            OperationOutcome.Succeeded, "", DateTimeOffset.UtcNow));

        Assert.NotEqual("", record.Id);
        Assert.Empty(history.Tail(repo).Records);
    }

    private static string LedgerPath(OperationHistory history, string repo) =>
        Path.Combine(history.DirectoryFor(RepoKey.For(repo)), OperationHistory.LedgerFileName);

    private static string RotatedPath(OperationHistory history, string repo) =>
        Path.Combine(history.DirectoryFor(RepoKey.For(repo)), OperationHistory.RotatedFileName);
}
