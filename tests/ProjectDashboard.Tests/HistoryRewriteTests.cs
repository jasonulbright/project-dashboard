using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

public class RewriteOptionsTests
{
    private static RewriteOptions WithOps(params ContentOp[] ops) => new() { ContentOps = ops };

    [Fact]
    public void ScopeOutsideAllFilesIsRefused()
    {
        var options = new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = [1], Replace = [] }],
            Scope = (RewriteScope)7
        };
        var ex = Assert.Throws<NotSupportedException>(options.Validate);
        Assert.Contains("scope", ex.Message);
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void CommitMessageRewritingIsRefused()
    {
        var options = new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = [1], Replace = [] }],
            ReplaceInCommitMessages = true
        };
        var ex = Assert.Throws<NotSupportedException>(options.Validate);
        Assert.Contains("commit-message", ex.Message);
    }

    [Fact]
    public void EmptyOpListIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => WithOps().Validate());
        Assert.Contains("no content operations", ex.Message);
    }

    [Fact]
    public void EmptyLiteralFindIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => WithOps(new LiteralReplace { Find = [], Replace = [1] }).Validate());
        Assert.Contains("at least one byte", ex.Message);
    }

    [Fact]
    public void MalformedRegexPatternIsRefusedBeforeAnyWork()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => WithOps(new RegexReplace { Pattern = "(", Replacement = "x" }).Validate());
    }

    [Fact]
    public void ValidLiteralAndRegexOpsPass()
    {
        WithOps(
            new LiteralReplace { Find = Encoding.UTF8.GetBytes("secret"), Replace = [] },
            new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }).Validate();
    }
}

public class BlobTransformerTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static BlobTransformer Literal(string find, string replace) =>
        new([new LiteralReplace { Find = Bytes(find), Replace = Bytes(replace) }]);

    [Fact]
    public void OverlappingCandidatesReplaceLeftToRight()
    {
        var result = Literal("aa", "b").Transform(Bytes("aaa"));
        Assert.Equal(TransformClass.Changed, result.Class);
        Assert.Equal(Bytes("ba"), result.Bytes);

        Assert.Equal(Bytes("bb"), Literal("aa", "b").Transform(Bytes("aaaa")).Bytes);
        Assert.Equal(Bytes("aX"), Literal("ab", "X").Transform(Bytes("aab")).Bytes);
    }

    [Fact]
    public void ReplacementContainingTheNeedleDoesNotRescanItsOwnOutput()
    {
        var result = Literal("a", "aa").Transform(Bytes("aa"));
        Assert.Equal(Bytes("aaaa"), result.Bytes);
    }

    [Fact]
    public void EmptyReplacementDeletesEveryMatch()
    {
        var result = Literal("secret", "").Transform(Bytes("a secret and a secret\n"));
        Assert.Equal(Bytes("a  and a \n"), result.Bytes);
    }

    [Fact]
    public void ReplacementLongerThanFindGrowsThePayload()
    {
        var result = Literal("k", "[REDACTED]").Transform(Bytes("k=1\n"));
        Assert.Equal(Bytes("[REDACTED]=1\n"), result.Bytes);
    }

    [Fact]
    public void AbsentNeedleClassifiesUnchangedWithoutAllocating()
    {
        var payload = Bytes("nothing to see\n");
        var result = Literal("secret", "X").Transform(payload);
        Assert.Equal(TransformClass.Unchanged, result.Class);
        Assert.Null(result.Bytes);
        Assert.Null(BlobTransformer.ReplaceLiteral(payload, Bytes("secret"), Bytes("X")));
    }

    [Fact]
    public void SingleByteFindAtBothEndsReplaces()
    {
        Assert.Equal(Bytes("XbX"), Literal("a", "X").Transform(Bytes("aba")).Bytes);
    }

    [Fact]
    public void InvalidUtf8PayloadIsSkippedEvenForLiteralOps()
    {
        byte[] payload = [0x00, 0xFF, .. Bytes("secret"), 0x80, 0xFE];
        var result = Literal("secret", "X").Transform(payload);
        Assert.Equal(TransformClass.BinarySkipped, result.Class);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void IdenticalReplacementClassifiesUnchanged()
    {
        var result = Literal("abc", "abc").Transform(Bytes("xxabcxx"));
        Assert.Equal(TransformClass.Unchanged, result.Class);
    }

    [Fact]
    public void RegexReplacesOverUnicodeContentAndPreservesSurroundingBytes()
    {
        var transformer = new BlobTransformer(
            [new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }]);
        var result = transformer.Transform(Bytes("café token-42 日本 🚀\n"));
        Assert.Equal(TransformClass.Changed, result.Class);
        Assert.Equal(Bytes("café token-X 日本 🚀\n"), result.Bytes);
    }

    [Fact]
    public void RegexWithNoMatchClassifiesUnchanged()
    {
        var transformer = new BlobTransformer(
            [new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }]);
        Assert.Equal(TransformClass.Unchanged, transformer.Transform(Bytes("no tokens here\n")).Class);
    }

    [Fact]
    public void RegexOverPayloadAboveTheLimitIsRefusedLoudly()
    {
        var transformer = new BlobTransformer(
            [new RegexReplace { Pattern = "x", Replacement = "y" }], regexPayloadLimit: 8);
        var ex = Assert.Throws<NotSupportedException>(() => transformer.Transform(Bytes("0123456789")));
        Assert.Contains("regex transform limit", ex.Message);
    }

    [Fact]
    public void LiteralOpsIgnoreThePayloadRegexLimit()
    {
        var transformer = new BlobTransformer(
            [new LiteralReplace { Find = Bytes("5"), Replace = Bytes("V") }], regexPayloadLimit: 8);
        Assert.Equal(Bytes("01234V6789"), transformer.Transform(Bytes("0123456789")).Bytes);
    }

    [Fact]
    public void LiteralOpProducingInvalidUtf8BeforeARegexOpFailsLoudly()
    {
        var transformer = new BlobTransformer(
        [
            new LiteralReplace { Find = Bytes("a"), Replace = [0xFF] },
            new RegexReplace { Pattern = "b", Replacement = "c" }
        ]);
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.Transform(Bytes("ab")));
        Assert.Contains("invalid UTF-8", ex.Message);
    }

    [Fact]
    public void OpsApplyInOrderOverEachOthersOutput()
    {
        var transformer = new BlobTransformer(
        [
            new LiteralReplace { Find = Bytes("secret"), Replace = Bytes("token-9") },
            new RegexReplace { Pattern = "token-[0-9]+", Replacement = "[GONE]" }
        ]);
        Assert.Equal(Bytes("a [GONE] b\n"), transformer.Transform(Bytes("a secret b\n")).Bytes);
    }
}

public class HistoryRewriterTests(ITestOutputHelper output)
{
    private const string Needle = "SECRET-TOKEN-12345";
    private const string Redacted = "[REDACTED-CREDENTIAL-MATERIAL]";

    private static FixtureRepo Fixture(bool bareSource = false) => new(bareSource, prefix: "engine2a-");

    private static RewriteOptions LiteralScrub(string find = Needle, string replace = Redacted) => new()
    {
        ContentOps = [new LiteralReplace
        {
            Find = Encoding.UTF8.GetBytes(find),
            Replace = Encoding.UTF8.GetBytes(replace)
        }]
    };

    private static Task<RewriteReport> RewriteAsync(FixtureRepo f, RewriteOptions rewrite, string? reportPath = null) =>
        new HistoryRewriter(GitGuard.GitExe).RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = f.SourcePath,
            WorkingDirectory = f.WorkDir,
            TargetBareRepository = f.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3),
            Rewrite = rewrite,
            ReportPath = reportPath,
            GitExecutable = GitGuard.GitExe
        });

    /// <summary>git run tolerating non-zero exits — git grep signals "no match" via exit 1.</summary>
    private static (int ExitCode, string StdOut) GitExit(string workingDirectory, params string[] args)
    {
        var result = ProcessRunner.RunAsync(
            GitGuard.GitExe, args, workingDirectory, TimeSpan.FromMinutes(2),
            new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_CONFIG_SYSTEM"] = "NUL",
                ["GIT_CONFIG_NOSYSTEM"] = "1"
            }).GetAwaiter().GetResult();
        Assert.False(result.TimedOut);
        return (result.ExitCode, result.StdOut);
    }

    private static List<string> AllCommits(string repository) =>
        FixtureRepo.RunGit(repository, ["rev-list", "--all"], null, null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

    /// <summary>Greps every given commit for the needle; returns total hits across all of them.</summary>
    private int CountGrepHits(string repository, IReadOnlyList<string> commits, string needle, string label)
    {
        var hits = 0;
        for (var i = 0; i < commits.Count; i += 100)
        {
            var chunk = commits.Skip(i).Take(100).ToList();
            var (exitCode, stdOut) = GitExit(repository,
                ["grep", "-I", "--fixed-strings", "-e", needle, .. chunk]);
            Assert.True(exitCode is 0 or 1, $"git grep exited {exitCode}");
            var chunkHits = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            output.WriteLine($"  {label}: git grep -I -F -e <needle> over {chunk.Count} commit(s) => exit {exitCode}, {chunkHits} hit line(s)");
            hits += chunkHits;
        }
        return hits;
    }

    [Fact]
    public async Task ScrubRemovesNeedleFromEveryRewrittenCommit()
    {
        using var f = Fixture();
        // Needle spread across multiple files, multiple historical versions, a merge
        // commit's tree, and a tag-referenced commit.
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
        f.Write("a.txt", "needle removed at tip\n");
        f.CommitAll("tip cleanup");

        var sourceRefsBefore = f.Git("for-each-ref", "--format=%(refname) %(objectname)");
        var sourceCommits = AllCommits(f.SourcePath);
        var sourceHits = CountGrepHits(f.SourcePath, sourceCommits, Needle, "source");
        Assert.True(sourceHits >= 5, $"fixture must carry the needle across history, found {sourceHits} hits");

        var reportPath = Path.Combine(f.Root, "rewrite-report.json");
        var report = await RewriteAsync(f, LiteralScrub(), reportPath);

        // The source repository's refs are never touched.
        Assert.Equal(sourceRefsBefore, f.Git("for-each-ref", "--format=%(refname) %(objectname)"));

        // Flagship proof: zero grep hits across every commit of the rewritten history.
        var targetCommits = AllCommits(f.TargetPath);
        Assert.Equal(sourceCommits.Count, targetCommits.Count);
        output.WriteLine($"scrub evidence: {sourceHits} needle hit(s) across {sourceCommits.Count} source commits");
        var targetHits = CountGrepHits(f.TargetPath, targetCommits, Needle, "target");
        Assert.Equal(0, targetHits);
        output.WriteLine($"scrub evidence: 0 needle hits across all {targetCommits.Count} rewritten commits");

        // Old→new map: complete, and every rewritten commit differs from its original.
        Assert.Equal(sourceCommits.Count, report.CommitMap.Count);
        foreach (var oid in sourceCommits)
            Assert.Contains(oid, report.CommitMap.Keys);
        var oldHead = f.Git("rev-parse", "main").Trim();
        var newHead = report.CommitMap[oldHead];
        Assert.NotEqual(oldHead, newHead);
        var rewrittenTip = FixtureRepo.RunGit(f.TargetPath, ["show", $"{newHead}:docs/keys.md"], null, null);
        Assert.Contains(Redacted, rewrittenTip);
        Assert.DoesNotContain(Needle, rewrittenTip);

        // Merge topology survives; the annotated tag peels to the rewritten merge commit.
        var mergeOid = f.Git("rev-parse", "v-secret^{commit}").Trim();
        var targetParents = FixtureRepo.RunGit(f.TargetPath,
            ["rev-list", "--parents", "-n", "1", report.CommitMap[mergeOid]], null, null).Trim().Split(' ');
        Assert.Equal(3, targetParents.Length);
        Assert.Equal(report.CommitMap[mergeOid],
            FixtureRepo.RunGit(f.TargetPath, ["rev-parse", "v-secret^{commit}"], null, null).Trim());

        // Report internals: replacement grew the payloads, and the scrub check ran clean.
        Assert.True(report.BlobsChanged >= 4, $"expected several changed blobs, got {report.BlobsChanged}");
        Assert.True(report.BytesDelta > 0, $"a growing replacement must yield a positive delta, got {report.BytesDelta}");
        Assert.Empty(report.BinarySkips);
        // The tip cleaned a.txt but its snapshot still carries docs/keys.md, so every
        // commit in this fixture has a scrubbed snapshot and a changed tree.
        Assert.Equal(sourceCommits.Count, report.CommitsWithChangedTrees.Count);
        Assert.Contains(oldHead, report.CommitsWithChangedTrees);
        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed);
        // A genuine full scrub: every commit grepped, nothing skipped or sampled, so an
        // empty hit list is a real clean bill, not silence.
        Assert.True(scrub.Complete);
        Assert.Equal(Needle, scrub.Needle);
        Assert.Empty(scrub.Hits);
        Assert.Equal(report.CommitMap.Count, scrub.CommitsChecked);

        // The written report round-trips through System.Text.Json.
        var roundTripped = JsonSerializer.Deserialize<RewriteReport>(await File.ReadAllTextAsync(reportPath));
        Assert.NotNull(roundTripped);
        Assert.Equal(report.CommitMap.Count, roundTripped!.CommitMap.Count);
        Assert.Equal(report.BlobsChanged, roundTripped.BlobsChanged);
        Assert.Equal(newHead, roundTripped.CommitMap[oldHead]);
    }

    [Fact]
    public async Task BinaryCarriedNeedleIsReportedNotClean()
    {
        using var f = Fixture();
        byte[] binary = [0x00, 0xFF, .. Encoding.ASCII.GetBytes(Needle), 0x80, 0xFE, 0x00];
        f.WriteBytes("blob.bin", binary);
        f.Write("plain.txt", $"text {Needle}\n");
        f.CommitAll("mixed content");

        var binaryOid = f.Git("rev-parse", "HEAD:blob.bin").Trim();
        var oldHead = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, LiteralScrub());

        var skip = Assert.Single(report.BinarySkips);
        Assert.NotNull(skip.Mark);
        Assert.Equal(binary.Length, skip.Size);
        // The skip names the path so the operator can act.
        Assert.Equal("blob.bin", skip.Path);

        // The transform never corrupts binary content: same blob oid in the rewritten head.
        var newHead = report.CommitMap[oldHead];
        Assert.Equal(binaryOid,
            FixtureRepo.RunGit(f.TargetPath, ["rev-parse", $"{newHead}:blob.bin"], null, null).Trim());

        // The text file in the same tree was still scrubbed.
        var plain = FixtureRepo.RunGit(f.TargetPath, ["show", $"{newHead}:plain.txt"], null, null);
        Assert.Contains(Redacted, plain);
        Assert.DoesNotContain(Needle, plain);

        // The needle survives inside the binary blob. git grep -I cannot see it, but the
        // byte-level fallback must report it as a survivor — never a clean bill.
        var scrub = Assert.Single(report.ScrubChecks);
        Assert.False(scrub.Complete);
        Assert.NotEmpty(scrub.Hits);
        Assert.Contains(scrub.Hits, h => h.Contains("binary-blob"));
        output.WriteLine($"binary-carried needle: mark :{skip.Mark} at {skip.Path}, {skip.Size} bytes; " +
                         $"scrub Complete={scrub.Complete}, hits={scrub.Hits.Count} (survivor reported, not clean)");
    }

    [Fact]
    public async Task NeedleInFilenameIsReportedAsSurviving()
    {
        using var f = Fixture();
        // The needle lives in a path, and paths are never rewritten by this stage.
        f.Write($"config-{Needle}.txt", "no secret in the body\n");
        f.CommitAll("secret in a filename");

        var report = await RewriteAsync(f, LiteralScrub());

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.Contains(scrub.Hits, h => h.StartsWith("path:") && h.Contains(Needle));
        output.WriteLine($"filename needle surfaced as: {scrub.Hits.First(h => h.StartsWith("path:"))}");
    }

    [Fact]
    public async Task DetachedHeadWithUniqueCommitAlignsToRewrittenCommit()
    {
        using var f = Fixture();
        f.Write("a.txt", $"base {Needle}\n");
        f.CommitAll("base");
        f.Git("checkout", "-q", "--detach", "HEAD");
        f.Write("detached.txt", $"detached {Needle}\n");
        f.CommitAll("reachable only from HEAD");
        var oldHead = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, LiteralScrub());

        // Target HEAD is detached and points at the rewritten (not the source) commit.
        Assert.StartsWith("detached ", HistoryTestSupport.DescribeHead(f.TargetPath));
        var targetHead = FixtureRepo.RunGit(f.TargetPath, ["rev-parse", "HEAD"], null, null).Trim();
        Assert.Equal(report.CommitMap[oldHead], targetHead);
        Assert.NotEqual(oldHead, targetHead);
        Assert.DoesNotContain("pd-import", FixtureRepo.RunGit(f.TargetPath, ["for-each-ref"], null, null));

        // The commit target HEAD names carries scrubbed content.
        var shown = FixtureRepo.RunGit(f.TargetPath, ["show", $"{targetHead}:detached.txt"], null, null);
        Assert.Contains(Redacted, shown);
        Assert.DoesNotContain(Needle, shown);
    }

    [Fact]
    public async Task DetachedHeadAtBranchTipAlignsToRewrittenTip()
    {
        using var f = Fixture();
        f.Write("a.txt", $"tip {Needle}\n");
        f.CommitAll("base");
        f.Git("checkout", "-q", "--detach", "HEAD");
        var oldHead = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, LiteralScrub());

        Assert.StartsWith("detached ", HistoryTestSupport.DescribeHead(f.TargetPath));
        var targetHead = FixtureRepo.RunGit(f.TargetPath, ["rev-parse", "HEAD"], null, null).Trim();
        Assert.Equal(report.CommitMap[oldHead], targetHead);
        var shown = FixtureRepo.RunGit(f.TargetPath, ["show", $"{targetHead}:a.txt"], null, null);
        Assert.Contains(Redacted, shown);
        Assert.DoesNotContain(Needle, shown);
    }

    [Fact]
    public async Task RegexRewriteOverUnicodeContent()
    {
        using var f = Fixture();
        f.Write("uni.txt", "café token-42 日本 🚀\n");
        f.CommitAll("v1");
        f.Write("uni.txt", "café token-777 日本 🚀\nzweite Zeile töken\n");
        f.CommitAll("v2");
        var oldHead = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }]
        });

        Assert.Equal(2, report.BlobsChanged);
        var newHead = report.CommitMap[oldHead];
        Assert.Equal("café token-X 日本 🚀\nzweite Zeile töken\n",
            FixtureRepo.RunGit(f.TargetPath, ["show", $"{newHead}:uni.txt"], null, null));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed);
        Assert.Empty(scrub.Hits);
    }

    [Fact]
    public async Task InexpressibleRegexSkipsScrubGrepWithNote()
    {
        using var f = Fixture();
        f.Write("a.txt", "token-42\n");
        f.CommitAll("one");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new RegexReplace { Pattern = @"token-\d+", Replacement = "token-X" }]
        });

        // The transform itself ran; only the grep-based verification is skipped.
        Assert.Equal(1, report.BlobsChanged);
        var scrub = Assert.Single(report.ScrubChecks);
        Assert.False(scrub.Performed);
        Assert.Contains(@"\d", scrub.Note);
    }

    [Fact]
    public async Task ErePatternGitRejectsYieldsReportNotAbortedRun()
    {
        using var f = Fixture();
        f.Write("a.txt", "nothing to match here\n");
        f.CommitAll("one");

        // `SECRET{2` is legal .NET (a literal brace) but git grep -E rejects the interval.
        // The completed rewrite must still return a report, not throw after fsck passed.
        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new RegexReplace { Pattern = "SECRET{2", Replacement = "X" }]
        });

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.False(scrub.Performed);
        Assert.False(scrub.Complete);
        Assert.Contains("git grep", scrub.Note);

        // The target is intact and reportable.
        Assert.NotEmpty(report.CommitMap);
        var head = f.Git("rev-parse", "HEAD").Trim();
        Assert.Equal("nothing to match here\n",
            FixtureRepo.RunGit(f.TargetPath, ["show", $"{report.CommitMap[head]}:a.txt"], null, null));
    }

    [Theory]
    [InlineData("[[:digit:]]")]
    [InlineData("[[.x.]]")]
    [InlineData("[[=a=]]")]
    public async Task PosixBracketExpressionsAreRefusedByTheEreGate(string pattern)
    {
        using var f = Fixture();
        f.Write("a.txt", "plain content 7\n");
        f.CommitAll("one");

        // These are literal bracket contents to .NET but structural to POSIX ERE, so the
        // scrub grep must skip rather than verify the ERE reading of a different edit.
        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new RegexReplace { Pattern = pattern, Replacement = "Z" }]
        });

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.False(scrub.Performed);
        Assert.Contains("POSIX", scrub.Note);
    }

    [Fact]
    public async Task EmptyReplacementDeletesNeedleAcrossHistory()
    {
        using var f = Fixture();
        f.Write("a.txt", $"keep {Needle} keep\n");
        f.CommitAll("v1");
        f.Write("a.txt", $"{Needle}{Needle}\n");
        f.CommitAll("v2");
        var oldHead = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, LiteralScrub(replace: ""));

        Assert.Equal(2, report.BlobsChanged);
        Assert.True(report.BytesDelta < 0, $"deletion must shrink content, got {report.BytesDelta}");
        var newHead = report.CommitMap[oldHead];
        Assert.Equal("\n", FixtureRepo.RunGit(f.TargetPath, ["show", $"{newHead}:a.txt"], null, null));
        Assert.Equal(0, CountGrepHits(f.TargetPath, AllCommits(f.TargetPath), Needle, "target"));
    }

    [Fact]
    public async Task NoOpTransformReproducesIdenticalRefs()
    {
        using var f = Fixture();
        f.Write("a.txt", "nothing secret here\n");
        f.CommitAll("first");
        f.Write("b.txt", "still clean\n");
        f.CommitAll("second");
        f.Git("tag", "-a", "v1", "-m", "clean tag");

        var report = await RewriteAsync(f, LiteralScrub());

        Assert.Equal(0, report.BlobsChanged);
        Assert.Equal(0, report.BytesDelta);
        Assert.Empty(report.BinarySkips);
        Assert.Empty(report.CommitsWithChangedTrees);
        foreach (var (oldOid, newOid) in report.CommitMap)
            Assert.Equal(oldOid, newOid);

        // Untouched content must reproduce the source ref-for-ref, oid-for-oid.
        var verify = await IdentityVerifier.VerifyAsync(
            GitGuard.GitExe, f.SourcePath, f.TargetPath, TimeSpan.FromMinutes(1));
        Assert.True(verify.Success, verify.Describe());
        Assert.Equal(HistoryTestSupport.DescribeHead(f.SourcePath), HistoryTestSupport.DescribeHead(f.TargetPath));
    }

    [Fact]
    public async Task OctopusTopologySurvivesTransform()
    {
        using var f = Fixture();
        f.Write("base.txt", $"base {Needle}\n");
        f.CommitAll("base");
        f.Git("switch", "-q", "-c", "o1");
        f.Write("o1.txt", "o1\n");
        f.CommitAll("on o1");
        f.Git("switch", "-q", "main");
        f.Git("switch", "-q", "-c", "o2");
        f.Write("o2.txt", $"o2 {Needle}\n");
        f.CommitAll("on o2");
        f.Git("switch", "-q", "main");
        f.Write("main2.txt", "m2\n");
        f.CommitAll("diverge");
        f.Git("merge", "-q", "o1", "o2", "-m", "octopus");
        var oldHead = f.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(4, f.Git("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);

        var report = await RewriteAsync(f, LiteralScrub());

        var newHead = report.CommitMap[oldHead];
        var parents = FixtureRepo.RunGit(f.TargetPath, ["rev-list", "--parents", "-n", "1", newHead], null, null)
            .Trim().Split(' ');
        Assert.Equal(4, parents.Length);
        Assert.Equal(0, CountGrepHits(f.TargetPath, AllCommits(f.TargetPath), Needle, "target"));
    }

    [Fact]
    public async Task ThousandCommitRewriteCompletesQuickly()
    {
        using var f = Fixture(bareSource: true);
        var stream = new StringBuilder();
        for (var i = 1; i <= 1000; i++)
        {
            var blobMark = i * 2 - 1;
            var commitMark = i * 2;
            var content = i % 3 == 0 ? $"revision {i} holds {Needle}\n" : $"revision {i} is clean\n";
            stream.Append($"blob\nmark :{blobMark}\ndata {Encoding.UTF8.GetByteCount(content)}\n{content}");
            var message = $"commit {i}\n";
            stream.Append($"commit refs/heads/main\nmark :{commitMark}\n");
            stream.Append($"author Fixture <fixture@example.com> {1700000000 + i} +0000\n");
            stream.Append($"committer Fixture <fixture@example.com> {1700000000 + i} +0000\n");
            stream.Append($"data {message.Length}\n{message}");
            if (i > 1) stream.Append($"from :{(i - 1) * 2}\n");
            stream.Append($"M 100644 :{blobMark} file{i % 20}.txt\n");
            stream.Append('\n');
        }
        f.GitWithStdin(Encoding.UTF8.GetBytes(stream.ToString()), "fast-import", "--quiet");

        var stopwatch = Stopwatch.StartNew();
        var report = await RewriteAsync(f, LiteralScrub());
        stopwatch.Stop();

        Assert.Equal(1000, report.CommitMap.Count);
        Assert.Equal(333, report.BlobsChanged);
        Assert.NotEmpty(report.CommitsWithChangedTrees);
        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed);
        Assert.Empty(scrub.Hits);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(120),
            $"1000-commit rewrite took {stopwatch.Elapsed} — expected well under two minutes");
        output.WriteLine($"1000-commit rewrite: {stopwatch.Elapsed.TotalSeconds:F2}s wall, " +
                         $"{report.BlobsChanged} blobs changed, {report.CommitsWithChangedTrees.Count} trees changed, " +
                         $"scrub over {scrub.CommitsChecked} commits clean");
    }
}
