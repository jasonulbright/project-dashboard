using System.Diagnostics;
using System.Text;
using ProjectDashboard.Services;
using ProjectDashboard.Services.History;
using ProjectDashboard.ViewModels.Pages;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

public class PathGlobTests
{
    [Theory]
    // literal
    [InlineData("a.txt", "a.txt", true)]
    [InlineData("a.txt", "b.txt", false)]
    // '*' does not cross '/'
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "dir/a.txt", false)]
    [InlineData("src/*.cs", "src/x.cs", true)]
    [InlineData("src/*.cs", "src/sub/x.cs", false)]
    // '?' single non-separator
    [InlineData("a?.txt", "ab.txt", true)]
    [InlineData("a?.txt", "a/b.txt", false)]
    // '**' crosses '/'
    [InlineData("src/**", "src/a.cs", true)]
    [InlineData("src/**", "src/a/b/c.cs", true)]
    [InlineData("src/**", "other/a.cs", false)]
    [InlineData("**/x.cs", "x.cs", true)]
    [InlineData("**/x.cs", "a/b/x.cs", true)]
    [InlineData("**/*.md", "docs/readme.md", true)]
    [InlineData("src/**/*.cs", "src/a/b.cs", true)]
    [InlineData("src/**/*.cs", "src/b.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b.txt", false)]
    // trailing slash = subtree
    [InlineData("docs/", "docs/a.md", true)]
    [InlineData("docs/", "docs/a/b.md", true)]
    [InlineData("docs/", "docsx/a.md", false)]
    // leading slash anchored, backslash normalized
    [InlineData("/a.txt", "a.txt", true)]
    [InlineData("src/*.cs", "src\\x.cs", true)]
    // unsupported metacharacters are literal, never operators
    [InlineData("a[b].txt", "a[b].txt", true)]
    [InlineData("a[b].txt", "ab.txt", false)]
    public void MatchesSubset(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new PathGlob(pattern).IsMatch(path));

    [Theory]
    [InlineData("a.txt", ":(glob)a.txt")]
    [InlineData("src/**/*.cs", ":(glob)src/**/*.cs")]
    [InlineData("docs/", ":(glob)docs/**")]
    [InlineData("/a.txt", ":(glob)a.txt")]
    [InlineData("src\\x.cs", ":(glob)src/x.cs")]
    // Brackets are literal to PathGlob, so the pathspec must spell them as the
    // single-character classes wildmatch reads back as those characters.
    [InlineData("a[b].txt", ":(glob)a[[]b[]].txt")]
    [InlineData("**/a[1].txt", ":(glob)**/a[[]1[]].txt")]
    public void TranslatesToAnEquivalentGitPathspec(string pattern, string expected) =>
        Assert.Equal(expected, PathGlob.ToGitPathspec(pattern));

    [Theory]
    [InlineData("a**b")]
    [InlineData("**b")]
    [InlineData("src/**b/c")]
    public void DoubleStarThatIsNotAWholeSegmentHasNoPathspec(string pattern) =>
        Assert.Null(PathGlob.ToGitPathspec(pattern));

    [Fact]
    public void AnchoredAtBothEnds()
    {
        Assert.False(new PathGlob("a.txt").IsMatch("xa.txt"));
        Assert.False(new PathGlob("a.txt").IsMatch("a.txtx"));
    }

    [Fact]
    public void DoubleStarAloneMatchesEverything()
    {
        Assert.True(new PathGlob("**").IsMatch("a/b/c.txt"));
        Assert.True(new PathGlob("**").IsMatch("x"));
    }

    // A newline is a legal byte in a git path, so the anchors must be \A/\z and the
    // wildcards must span it.
    [Fact]
    public void TrailingNewlineIsNotSwallowedByTheEndAnchor()
    {
        Assert.False(new PathGlob("secret.txt").IsMatch("secret.txt\n"));
        Assert.False(new PathGlob("*.txt").IsMatch("secret.txt\n"));
        Assert.True(new PathGlob("secret.txt\n").IsMatch("secret.txt\n"));
    }

    [Fact]
    public void WildcardsSpanAnEmbeddedNewline()
    {
        Assert.True(new PathGlob("**").IsMatch("a\nb"));
        Assert.True(new PathGlob("**").IsMatch("dir/a\nb.txt"));
        // '*' and '**' agree: neither stops at a newline, only at '/'.
        Assert.True(new PathGlob("a*").IsMatch("a\nb"));
        Assert.True(new PathGlob("**/*.txt").IsMatch("dir/a\nb.txt"));
        Assert.False(new PathGlob("*").IsMatch("dir/a\nb"));
    }

    [Fact]
    public void ScopesAgreeWithTheMatcherOnNewlinePaths()
    {
        Assert.True(new GlobScope { Patterns = ["**"] }.Matches("a\nb.txt"));
        Assert.False(new GlobScope { Patterns = ["secret.txt"] }.Matches("secret.txt\n"));
        Assert.True(new ExplicitPathsScope { Paths = ["a\nb.txt"] }.Matches("a\nb.txt"));
        Assert.False(new ExplicitPathsScope { Paths = ["a.txt"] }.Matches("a.txt\n"));
    }

    /// <summary>The scrub filters collected paths through <see cref="FileScope.Matches"/>; a path it drops is a blind spot.</summary>
    [Fact]
    public void ScrubPathFilterKeepsNewlinePathsInScope()
    {
        FileScope scope = new GlobScope { Patterns = ["**"] };
        string[] collected = ["clean.txt", "dir/a\nb.txt", "deep/dir/c\nd.bin"];
        Assert.Equal(collected, collected.Where(p => scope.Matches(p)).ToArray());
    }

    [Fact]
    public void ExplicitPathScopeMatchesExactAndSubtree()
    {
        var scope = new ExplicitPathsScope { Paths = ["dir", "top.txt"] };
        Assert.True(scope.Matches("top.txt"));
        Assert.True(scope.Matches("dir/a.txt"));
        Assert.True(scope.Matches("dir/sub/b.txt"));
        Assert.False(scope.Matches("dirx/a.txt"));
        Assert.False(scope.Matches("other.txt"));
    }

    [Fact]
    public void GlobScopeMatchesAny()
    {
        var scope = new GlobScope { Patterns = ["*.md", "src/**"] };
        Assert.True(scope.Matches("readme.md"));
        Assert.True(scope.Matches("src/deep/x.cs"));
        Assert.False(scope.Matches("bin/x.dll"));
    }
}

public class IdentityHeaderTests
{
    private static byte[] Line(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void RewritesNameAndEmailPreservingTimestamp()
    {
        var ok = IdentityHeader.TryRewrite(
            Line("author Old Name <old@x.com> 1700000000 +0000"),
            [new IdentityMapping { OldEmail = "old@x.com", NewName = "New Name", NewEmail = "new@y.com" }],
            out var rewritten);
        Assert.True(ok);
        Assert.Equal("author New Name <new@y.com> 1700000000 +0000", Encoding.UTF8.GetString(rewritten));
    }

    [Fact]
    public void PreservesUnicodeNames()
    {
        var ok = IdentityHeader.TryRewrite(
            Line("committer Öld Nämé <o@x> 10 +0100"),
            [new IdentityMapping { OldName = "Öld Nämé", NewName = "Nëw Ñame" }],
            out var rewritten);
        Assert.True(ok);
        Assert.Equal("committer Nëw Ñame <o@x> 10 +0100", Encoding.UTF8.GetString(rewritten));
    }

    [Fact]
    public void NonMatchingLeavesUnchanged()
    {
        Assert.False(IdentityHeader.TryRewrite(
            Line("author Keep <keep@x> 1 +0000"),
            [new IdentityMapping { OldEmail = "other@x", NewEmail = "z@x" }],
            out _));
    }

    [Fact]
    public void NonIdentityHeaderIgnored()
    {
        Assert.False(IdentityHeader.TryRewrite(
            Line("original-oid abc123"),
            [new IdentityMapping { OldName = "x", NewName = "y" }],
            out _));
    }
}

public class HistoryScopedRewriterTests(ITestOutputHelper output)
{
    private const string Needle = "SECRET-TOKEN-12345";
    private const string Redacted = "[REDACTED-CREDENTIAL-MATERIAL]";

    private static FixtureRepo Fixture(bool bareSource = false) => new(bareSource, prefix: "engine2b-");

    /// <summary>
    /// A scoped rewrite run, verified by fsck, the content that came out, and the report's own
    /// scrub checks. The identity proofs in <see cref="HistoryTestSupport.RoundTripAsync"/> — the
    /// byte-identical re-emit and the ref-for-ref object-id comparison — do not hold here: a
    /// rewrite changes payload bytes, so the emitted stream differs from the spool and every
    /// commit downstream of a changed blob gets a new object id. They hold only where a scoped
    /// run provably changed nothing.
    /// </summary>
    private static Task<RewriteReport> RewriteAsync(FixtureRepo f, RewriteOptions rewrite, long ceiling = HistoryRewriter.DefaultChangedPayloadCeiling) =>
        new HistoryRewriter(GitGuard.GitExe, changedPayloadCeiling: ceiling).RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = f.SourcePath,
            WorkingDirectory = f.WorkDir,
            TargetBareRepository = f.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3),
            Rewrite = rewrite,
            GitExecutable = GitGuard.GitExe
        });

    private static RewriteOptions Literal(string find, string replace, FileScope? files = null, CommitScope? commits = null) => new()
    {
        ContentOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(find), Replace = Encoding.UTF8.GetBytes(replace) }],
        FileScope = files ?? new AllFilesScope(),
        CommitScope = commits ?? new AllHistoryScope()
    };

    private static string Show(FixtureRepo f, string spec) => FixtureRepo.RunGit(f.TargetPath, ["show", spec], null, null);

    private static bool ObjectReachable(string repo, string oid) =>
        FixtureRepo.RunGit(repo, ["rev-list", "--objects", "--all"], null, null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(l => l.StartsWith(oid, StringComparison.Ordinal));

    // ---- Glob / explicit-path scoping restricts the rewrite ------------------------------

    [Fact]
    public async Task GlobScopeRewritesOnlyMatchingPaths()
    {
        using var f = Fixture();
        f.Write("src/app.cs", $"code {Needle}\n");
        f.Write("docs/readme.md", $"doc {Needle}\n");
        f.CommitAll("mixed");
        var head = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new GlobScope { Patterns = ["src/**"] }));
        var newHead = report.CommitMap[head];

        Assert.Contains(Redacted, Show(f, $"{newHead}:src/app.cs"));
        // The out-of-scope doc keeps the needle — and the scrub reports it honestly, not as a fail.
        Assert.Contains(Needle, Show(f, $"{newHead}:docs/readme.md"));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed);
        Assert.True(scrub.WithinScopeOnly);
        // Coverage of the scope is total; the scope flag alone is what withholds the
        // everywhere claim.
        Assert.True(scrub.Complete);
        Assert.Empty(scrub.Hits); // in-scope src/** is clean
        Assert.Contains("within scope", scrub.Note);
        output.WriteLine($"glob scope: src/app.cs scrubbed, docs/readme.md survivor retained; scrub WithinScopeOnly={scrub.WithinScopeOnly}, Complete={scrub.Complete}");
    }

    [Fact]
    public async Task BracketedGlobScopesTheSameSetInThePreviewAndTheScrub()
    {
        using var f = Fixture();
        f.Write("a[b].txt", $"bracketed {Needle}\n");
        f.Write("ab.txt", $"unbracketed {Needle}\n");
        f.CommitAll("bracket pair");
        var head = f.Git("rev-parse", "HEAD").Trim();

        // The glob names the bracketed path literally. Read as a git character class it would
        // select ab.txt instead, so the scrub would grep a path the rewrite never touched and
        // report its needle as a survivor.
        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new GlobScope { Patterns = ["a[b].txt"] }));
        var newHead = report.CommitMap[head];

        Assert.Contains(Redacted, Show(f, $"{newHead}:a[b].txt"));
        Assert.Contains(Needle, Show(f, $"{newHead}:ab.txt"));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed, scrub.Note);
        Assert.True(scrub.Complete, scrub.Note);
        Assert.Empty(scrub.Hits);
    }

    [Fact]
    public async Task AGlobNamingADirectoryWithoutASlashScopesNothingAndReportsNoSurvivors()
    {
        using var f = Fixture();
        f.Write("docs/keys.md", $"doc {Needle}\n");
        f.CommitAll("docs only");
        var head = f.Git("rev-parse", "HEAD").Trim();

        // "docs" names the directory itself, which is not a path; the rewrite therefore scopes
        // no file. git's pathspec prefix-matches the directory, so the scrub grep sees
        // docs/keys.md — a path this run was never allowed to touch.
        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new GlobScope { Patterns = ["docs"] }));
        var newHead = report.CommitMap[head];

        Assert.Equal(0, report.BlobsChanged);
        Assert.Contains(Needle, Show(f, $"{newHead}:docs/keys.md"));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed, scrub.Note);
        Assert.Empty(scrub.Hits);
        Assert.True(scrub.WithinScopeOnly);
        Assert.Contains("within scope", scrub.Note);
        output.WriteLine($"glob 'docs' scoped 0 blobs; scrub hits={scrub.Hits.Count} WithinScopeOnly={scrub.WithinScopeOnly}");
    }

    [Fact]
    public async Task ExplicitPathWithABracketScopesItselfAndNothingElse()
    {
        using var f = Fixture();
        f.Write("a[b].txt", $"bracketed {Needle}\n");
        f.Write("ab.txt", $"unbracketed {Needle}\n");
        f.CommitAll("bracket pair");
        var head = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new ExplicitPathsScope { Paths = ["a[b].txt"] }));
        var newHead = report.CommitMap[head];

        Assert.Contains(Redacted, Show(f, $"{newHead}:a[b].txt"));
        Assert.Contains(Needle, Show(f, $"{newHead}:ab.txt"));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.Performed, scrub.Note);
        Assert.Empty(scrub.Hits);
    }

    [Fact]
    public async Task GlobWithNoPathspecTranslationSkipsTheGrepInsteadOfNarrowingItWrongly()
    {
        using var f = Fixture();
        f.Write("axxb.txt", $"in scope {Needle}\n");
        f.CommitAll("one");

        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new GlobScope { Patterns = ["a**b.txt"] }));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.False(scrub.Performed);
        Assert.False(scrub.Complete);
        Assert.Contains("cannot express", scrub.Note);
    }

    [Fact]
    public async Task ExplicitPathScopeRewritesOnlyListedPath()
    {
        using var f = Fixture();
        f.Write("keep.txt", $"keep {Needle}\n");
        f.Write("target.txt", $"target {Needle}\n");
        f.CommitAll("two files");
        var head = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new ExplicitPathsScope { Paths = ["target.txt"] }));
        var newHead = report.CommitMap[head];

        Assert.Contains(Redacted, Show(f, $"{newHead}:target.txt"));
        Assert.Contains(Needle, Show(f, $"{newHead}:keep.txt"));
        Assert.True(report.BlobsChanged >= 1);
    }

    // ---- Commit-range scoping ------------------------------------------------------------

    [Fact]
    public async Task CommitRangeScopeRewritesOnlyInRangeCommits()
    {
        using var f = Fixture();
        f.Write("a.txt", $"v1 {Needle}\n");
        f.CommitAll("c1");
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("a.txt", $"v2 {Needle}\n");
        f.CommitAll("c2");
        f.Write("a.txt", $"v3 {Needle}\n");
        f.CommitAll("c3");
        var c3 = f.Git("rev-parse", "HEAD").Trim();

        // Range c1..c3 selects c2 and c3, leaving c1 out of scope.
        var report = await RewriteAsync(f, Literal(Needle, Redacted, commits: new CommitRangeScope { FromRef = c1, ToRef = c3 }));

        Assert.Equal(2, report.InScopeCommitCount);
        Assert.Contains(Redacted, Show(f, $"{report.CommitMap[c3]}:a.txt"));
        // c1 is out of scope: its version keeps the needle.
        Assert.Contains(Needle, Show(f, $"{report.CommitMap[c1]}:a.txt"));

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.WithinScopeOnly);
        Assert.True(scrub.Complete);
        Assert.Empty(scrub.Hits);
        output.WriteLine($"commit-range scope: c2,c3 scrubbed; c1 survivor retained honestly " +
                         $"(WithinScopeOnly={scrub.WithinScopeOnly}, Complete={scrub.Complete})");
    }

    /// <summary>
    /// A scope narrows what the check answers for; it does not excuse gaps inside that scope.
    /// A regex op has no byte-level fallback, so an in-scope payload the transform skipped is
    /// genuinely unsearched — the check must report incomplete coverage even though the run
    /// was scoped, or the scope flag alone would render it as a cleaned scope.
    /// </summary>
    [Fact]
    public async Task AScopedRunWithASkippedPayloadReportsIncompleteCoverageNotACleanedScope()
    {
        using var f = Fixture();
        f.WriteBytes("src/big.txt", Encoding.ASCII.GetBytes(new string('a', 40) + "\n"));
        f.Write("src/small.txt", "token-4\n");
        f.Write("docs/out.txt", "token-9\n");
        f.CommitAll("mixed sizes");

        // 32-byte regex payload limit: src/big.txt (41 bytes) is skipped before it is read.
        var report = await new HistoryRewriter(GitGuard.GitExe, regexPayloadLimit: 32).RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = f.SourcePath,
            WorkingDirectory = f.WorkDir,
            TargetBareRepository = f.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3),
            Rewrite = new RewriteOptions
            {
                ContentOps = [new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }],
                FileScope = new GlobScope { Patterns = ["src/**"] }
            },
            GitExecutable = GitGuard.GitExe
        });

        Assert.Contains("regex transform limit", Assert.Single(report.BinarySkips).Reason);

        var check = Assert.Single(report.ScrubChecks);
        Assert.True(check.Performed);
        Assert.True(check.WithinScopeOnly);
        Assert.False(check.Complete);
        Assert.Empty(check.Hits);

        var line = RewriteScrubVerdict.Describe(check, report.BinarySkips);
        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.False(line.ClaimsClean);
        output.WriteLine($"scoped run with a skipped payload: WithinScopeOnly={check.WithinScopeOnly}, " +
                         $"Complete={check.Complete}, verdict={line.Verdict}");
    }

    /// <summary>
    /// The scoped pass gates the regex transform the same way, and owes the same scan: an
    /// in-scope payload it declined to rewrite still reaches the import carrying its needle.
    /// </summary>
    [Fact]
    public async Task AnOverLimitInScopeBlobCarryingALiteralNeedle_IsReportedAsAByteSurvivor()
    {
        using var f = Fixture();
        f.WriteBytes("src/big.txt", Encoding.ASCII.GetBytes(new string('a', 40) + Needle + "\n"));
        f.Write("docs/out.txt", $"out of scope {Needle}\n");
        f.CommitAll("over limit in scope");

        var report = await new HistoryRewriter(GitGuard.GitExe, regexPayloadLimit: 32).RunAsync(new HistoryRewriteRequest
        {
            SourceRepository = f.SourcePath,
            WorkingDirectory = f.WorkDir,
            TargetBareRepository = f.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3),
            Rewrite = new RewriteOptions
            {
                ContentOps =
                [
                    new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) },
                    new RegexReplace { Pattern = "never-matches-anything", Replacement = "z" }
                ],
                FileScope = new GlobScope { Patterns = ["src/**"] }
            },
            GitExecutable = GitGuard.GitExe
        });

        Assert.Contains("regex transform limit", Assert.Single(report.BinarySkips).Reason);

        var literalCheck = report.ScrubChecks.Single(c => c.Kind == "literal");
        Assert.Contains(literalCheck.Hits, h => h.Contains("carry the needle", StringComparison.Ordinal));
        Assert.False(literalCheck.Complete);
        output.WriteLine($"scoped over-limit literal survivor reported: {string.Join(" | ", literalCheck.Hits)}");
    }

    [Fact]
    public async Task CommitScopedNoteReportsInheritedOutOfScopeChanges()
    {
        using var f = Fixture();
        f.Write("a.txt", $"v1 {Needle}\n");
        f.CommitAll("c1 introduces");
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("b.txt", "unrelated\n");
        f.CommitAll("c2 touches something else");
        var c2 = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, Literal(Needle, Redacted, commits: new ExplicitCommitsScope { Commits = [c1] }));

        // c2 never re-touches a.txt, so its snapshot inherits the rewritten blob.
        Assert.Contains(Redacted, Show(f, $"{report.CommitMap[c2]}:a.txt"));
        Assert.Contains(c2, report.CommitsWithChangedTrees);
        Assert.Equal(1, report.OutOfScopeCommitsWithChangedTrees);

        var scrub = Assert.Single(report.ScrubChecks);
        Assert.True(scrub.WithinScopeOnly);
        Assert.NotNull(scrub.Note);
        Assert.DoesNotContain("outside the scope are intentionally retained", scrub.Note);
        Assert.Contains("1 out-of-scope commit(s) have a changed tree", scrub.Note);
        output.WriteLine($"commit-scoped note: {scrub.Note}");
    }

    // ---- Shared-blob split (the correctness core) ----------------------------------------

    [Fact]
    public async Task SharedBlobSplitByPathLeavesOutOfScopePathUntouched()
    {
        using var f = Fixture();
        // Identical content at two paths dedupes to one blob object.
        f.Write("in.txt", $"{Needle}\n");
        f.Write("out.txt", $"{Needle}\n");
        f.CommitAll("shared blob");
        var head = f.Git("rev-parse", "HEAD").Trim();
        var sourceOutBlob = f.Git("rev-parse", "HEAD:out.txt").Trim();
        var sourceInBlob = f.Git("rev-parse", "HEAD:in.txt").Trim();
        Assert.Equal(sourceInBlob, sourceOutBlob); // proves the blob is shared

        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new ExplicitPathsScope { Paths = ["in.txt"] }));
        var newHead = report.CommitMap[head];

        // In-scope path rewritten; out-of-scope path's blob hash unchanged.
        Assert.Contains(Redacted, Show(f, $"{newHead}:in.txt"));
        Assert.DoesNotContain(Needle, Show(f, $"{newHead}:in.txt"));
        var targetOutBlob = FixtureRepo.RunGit(f.TargetPath, ["rev-parse", $"{newHead}:out.txt"], null, null).Trim();
        Assert.Equal(sourceOutBlob, targetOutBlob);
        Assert.Equal(1, report.BlobsSplit);
        output.WriteLine($"shared-blob split by path: out.txt blob {sourceOutBlob[..10]} UNCHANGED, in.txt rewritten; BlobsSplit={report.BlobsSplit}");
    }

    [Fact]
    public async Task SharedBlobSplitByCommitLeavesOutOfScopeCommitUntouched()
    {
        using var f = Fixture();
        f.Write("a.txt", $"{Needle}\n");
        f.CommitAll("c1 introduces");
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("a.txt", "unrelated\n");
        f.CommitAll("c2 changes");
        var c2 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("a.txt", $"{Needle}\n"); // identical to c1's version -> same blob mark
        f.CommitAll("c3 restores");
        var c3 = f.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(f.Git("rev-parse", $"{c1}:a.txt").Trim(), f.Git("rev-parse", $"{c3}:a.txt").Trim());

        // Only c3 is in scope; c1 references the same blob but is out of scope.
        var report = await RewriteAsync(f, Literal(Needle, Redacted, commits: new CommitRangeScope { FromRef = c2, ToRef = c3 }));

        Assert.Contains(Redacted, Show(f, $"{report.CommitMap[c3]}:a.txt"));
        Assert.Contains(Needle, Show(f, $"{report.CommitMap[c1]}:a.txt"));
        Assert.Equal(1, report.BlobsSplit);
        output.WriteLine($"shared-blob split by commit: c1 kept needle, c3 rewritten; BlobsSplit={report.BlobsSplit}");
    }

    // ---- Purge ---------------------------------------------------------------------------

    [Fact]
    public async Task PurgeRemovesPathAndPrunesEmptyCommit()
    {
        using var f = Fixture();
        f.Write("keep.txt", "keep\n");
        f.CommitAll("c1 base");
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        var keepBlob = f.Git("rev-parse", "HEAD:keep.txt").Trim();
        f.Write("secret.txt", $"{Needle}\n");
        f.CommitAll("c2 adds secret only");
        var secretBlob = f.Git("rev-parse", "HEAD:secret.txt").Trim();
        f.Write("keep.txt", "keep two\n");
        f.CommitAll("c3 edits keep");
        var c3 = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            Purge = new PurgeSpec { Paths = new ExplicitPathsScope { Paths = ["secret.txt"] } }
        });

        // The file is gone from every rewritten commit.
        var newHead = report.CommitMap[c3];
        var tree = FixtureRepo.RunGit(f.TargetPath, ["ls-tree", "-r", "--name-only", newHead], null, null);
        Assert.DoesNotContain("secret.txt", tree);
        // The purged object is unreferenced in the target.
        Assert.False(ObjectReachable(f.TargetPath, secretBlob));
        // The empty commit c2 was pruned; c1 and c3 survive and c3 rewired onto c1.
        Assert.Equal(1, report.CommitsPruned);
        Assert.Equal(1, report.FileCommandsRemoved);
        var targetCommits = FixtureRepo.RunGit(f.TargetPath, ["rev-list", "--count", "--all"], null, null).Trim();
        Assert.Equal("2", targetCommits);
        // Unrelated content is byte-identical.
        Assert.Equal(keepBlob, FixtureRepo.RunGit(f.TargetPath, ["rev-parse", $"{report.CommitMap[c1]}:keep.txt"], null, null).Trim());
        output.WriteLine($"purge: secret.txt gone, object {secretBlob[..10]} unreferenced, c2 pruned; target has {targetCommits} commits");
    }

    [Fact]
    public async Task PurgeKeepsCommitsItDidNotEmpty()
    {
        using var f = Fixture();
        f.Write("keep.txt", "keep\n");
        f.CommitAll("c1 base");
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        f.Git("commit", "-q", "--allow-empty", "-m", "ci trigger");
        var marker = f.Git("rev-parse", "HEAD").Trim();
        f.Write("junk.txt", $"{Needle}\n");
        f.CommitAll("c3 adds junk only");
        var c3 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("keep.txt", "keep two\n");
        f.CommitAll("c4 edits keep");
        var c4 = f.Git("rev-parse", "HEAD").Trim();
        Assert.Equal("4", f.Git("rev-list", "--count", "--all").Trim());

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            Purge = new PurgeSpec { Paths = new ExplicitPathsScope { Paths = ["junk.txt"] } }
        });

        // Only c3 — the commit the purge emptied — is pruned.
        Assert.Equal(1, report.CommitsPruned);
        var after = FixtureRepo.RunGit(f.TargetPath, ["rev-list", "--count", "--all"], null, null).Trim();
        Assert.Equal("3", after);

        // The pre-existing empty commit survives as itself, not folded into its parent.
        var newMarker = report.CommitMap[marker];
        Assert.NotEqual(report.CommitMap[c1], newMarker);
        Assert.Equal("ci trigger",
            FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%s", newMarker], null, null).Trim());
        Assert.Equal(report.CommitMap[c1],
            FixtureRepo.RunGit(f.TargetPath, ["rev-parse", newMarker + "^"], null, null).Trim());

        // The commit the purge emptied is gone, and its oid resolves to its parent.
        Assert.Equal(newMarker, report.CommitMap[c3]);
        var tree = FixtureRepo.RunGit(f.TargetPath, ["ls-tree", "-r", "--name-only", report.CommitMap[c4]], null, null);
        Assert.DoesNotContain("junk.txt", tree);
        output.WriteLine($"purge prune gate: before=4 after={after} pruned={report.CommitsPruned}; " +
                         $"'ci trigger' survives as {newMarker[..10]}, purged c3 maps to it");
    }

    [Fact]
    public async Task PurgePreservesMergeTopology()
    {
        using var f = Fixture();
        f.Write("base.txt", "base\n");
        f.CommitAll("base");
        f.Git("switch", "-q", "-c", "side");
        f.Write("side.txt", "side\n");
        f.CommitAll("side");
        f.Git("switch", "-q", "main");
        f.Write("junk.txt", $"{Needle}\n");
        f.CommitAll("main adds junk");
        f.Git("merge", "-q", "--no-ff", "side", "-m", "merge side");
        var mergeOid = f.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(3, f.Git("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            Purge = new PurgeSpec { Paths = new ExplicitPathsScope { Paths = ["junk.txt"] } }
        });

        var newMerge = report.CommitMap[mergeOid];
        var parents = FixtureRepo.RunGit(f.TargetPath, ["rev-list", "--parents", "-n", "1", newMerge], null, null).Trim().Split(' ');
        Assert.Equal(3, parents.Length); // merge still has two parents
        Assert.False(ObjectReachable(f.TargetPath, f.Git("rev-parse", $"{mergeOid}^:junk.txt").Trim()));
    }

    [Fact]
    public async Task PurgeKeepsAMergedBranchTipAtItsOwnCommit()
    {
        using var f = Fixture();
        f.Write("base.txt", "base\n");
        f.CommitAll("base");
        var baseOid = f.Git("rev-parse", "HEAD").Trim();
        f.Git("switch", "-q", "-c", "feature");
        f.Write("junk.txt", $"{Needle}\n");
        f.CommitAll("feature adds junk only");
        var featureTip = f.Git("rev-parse", "HEAD").Trim();
        f.Git("switch", "-q", "main");
        f.Write("m.txt", "m\n");
        f.CommitAll("main advances");
        f.Git("merge", "-q", "--no-ff", "feature", "-m", "merge feature");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            Purge = new PurgeSpec { Paths = new ExplicitPathsScope { Paths = ["junk.txt"] } }
        });

        var targetFeature = FixtureRepo.RunGit(f.TargetPath, ["rev-parse", "refs/heads/feature"], null, null).Trim();
        // The tip is empty after the purge but still establishes refs/heads/feature; rewinding
        // it onto "base" would drop a commit nobody asked to remove.
        Assert.Equal(report.CommitMap[featureTip], targetFeature);
        Assert.NotEqual(report.CommitMap[baseOid], targetFeature);
        Assert.Equal("feature adds junk only",
            FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%s", targetFeature], null, null).Trim());
        Assert.Equal(0, report.CommitsPruned);
        Assert.False(ObjectReachable(f.TargetPath, f.Git("rev-parse", $"{featureTip}:junk.txt").Trim()));
        output.WriteLine($"merged tip: refs/heads/feature -> {targetFeature[..10]} (its own rewritten tip), " +
                         $"not base {report.CommitMap[baseOid][..10]}");
    }

    [Fact]
    public async Task PurgeBySizeTargetsLargestBlobs()
    {
        using var f = Fixture();
        f.WriteBytes("big.bin", Encoding.ASCII.GetBytes(new string('x', 5000)));
        f.Write("small.txt", "tiny\n");
        f.CommitAll("mixed sizes");
        var head = f.Git("rev-parse", "HEAD").Trim();
        var bigBlob = f.Git("rev-parse", "HEAD:big.bin").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            Purge = new PurgeSpec { MinBlobSize = 1000 }
        });

        var newHead = report.CommitMap[head];
        Assert.False(ObjectReachable(f.TargetPath, bigBlob));
        Assert.Equal("tiny\n", Show(f, $"{newHead}:small.txt"));
        output.WriteLine($"size purge: big.bin ({bigBlob[..10]}) removed, small.txt kept");
    }

    [Fact]
    public async Task SizePurgeSeesASplitBlobAtItsNewMark()
    {
        using var f = Fixture();
        // Identical oversized content at two paths dedupes to one blob; scoping the content
        // op to in.txt splits it, so in.txt's M line points at a mark minted after parse.
        var payload = new string('x', 2000) + Needle + "\n";
        f.Write("in.txt", payload);
        f.Write("out.txt", payload);
        f.CommitAll("shared oversized blob");
        var head = f.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(f.Git("rev-parse", "HEAD:in.txt").Trim(), f.Git("rev-parse", "HEAD:out.txt").Trim());

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }],
            FileScope = new ExplicitPathsScope { Paths = ["in.txt"] },
            Purge = new PurgeSpec { MinBlobSize = 1000 }
        });

        Assert.Equal(1, report.BlobsSplit);
        // The same rule reaches both paths: the split mark is not exempt from the size purge.
        Assert.Equal(2, report.FileCommandsRemoved);
        var tree = FixtureRepo.RunGit(f.TargetPath, ["ls-tree", "-r", "--name-only", report.CommitMap[head]], null, null);
        Assert.DoesNotContain("in.txt", tree);
        Assert.DoesNotContain("out.txt", tree);
        output.WriteLine($"size purge over a split: BlobsSplit={report.BlobsSplit}, both 2019-byte paths purged");
    }

    [Fact]
    public async Task SizePurgeMeasuresThePostTransformPayload()
    {
        using var f = Fixture();
        var run = new string('Z', 1200);
        f.Write("shrink.txt", "PAD" + run + "\n");
        f.WriteBytes("big.bin", Encoding.ASCII.GetBytes(new string('q', 5000)));
        f.CommitAll("one shrinking blob, one staying big");
        var head = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(run), Replace = Encoding.UTF8.GetBytes("z") }],
            Purge = new PurgeSpec { MinBlobSize = 1000 }
        });

        var newHead = report.CommitMap[head];
        // 1204 bytes before the op, 5 after: the threshold measures what the import receives.
        Assert.Equal("PADz\n", Show(f, $"{newHead}:shrink.txt"));
        var tree = FixtureRepo.RunGit(f.TargetPath, ["ls-tree", "-r", "--name-only", newHead], null, null);
        Assert.DoesNotContain("big.bin", tree);
        Assert.Equal(1, report.FileCommandsRemoved);
        output.WriteLine("size purge on post-transform size: shrink.txt (1204->5) kept, big.bin (5000) purged");
    }

    // ---- Message rewrite -----------------------------------------------------------------

    [Fact]
    public async Task MessageRewriteLiteralAndRegexOverCommitsAndTags()
    {
        using var f = Fixture();
        f.Write("a.txt", "body\n");
        f.CommitAll($"commit mentions {Needle} and ticket-42");
        f.Git("tag", "-a", "rel", "-m", $"release {Needle}");
        var head = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps =
            [
                new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) },
                new RegexReplace { Pattern = "ticket-[0-9]+", Replacement = "ticket-X" }
            ]
        });

        var msg = FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%B", report.CommitMap[head]], null, null);
        Assert.Contains(Redacted, msg);
        Assert.Contains("ticket-X", msg);
        Assert.DoesNotContain(Needle, msg);
        var tagMsg = FixtureRepo.RunGit(f.TargetPath, ["for-each-ref", "refs/tags/rel", "--format=%(contents)"], null, null);
        Assert.Contains(Redacted, tagMsg);
        Assert.True(report.MessagesChanged >= 2);

        foreach (var scrub in report.ScrubChecks)
        {
            Assert.True(scrub.Performed);
            Assert.True(scrub.Complete);
            Assert.Empty(scrub.Hits);
        }
        output.WriteLine($"message rewrite: {report.MessagesChanged} messages changed; commit+tag scrubbed clean");
    }

    [Fact]
    public async Task MessageAndIdentityOpsHonourTheCommitScope()
    {
        using var f = Fixture();
        var oldEnv = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Old Name",
            ["GIT_AUTHOR_EMAIL"] = "old@example.com",
            ["GIT_COMMITTER_NAME"] = "Old Name",
            ["GIT_COMMITTER_EMAIL"] = "old@example.com"
        };
        f.Write("a.txt", "one\n");
        f.CommitAll($"c1 keeps {Needle}", oldEnv);
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("b.txt", "two\n");
        f.CommitAll($"c2 leaks {Needle}", oldEnv);
        var c2 = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }],
            IdentityMappings = [new IdentityMapping { OldEmail = "old@example.com", NewName = "New Person", NewEmail = "new@example.com" }],
            CommitScope = new ExplicitCommitsScope { Commits = [c2] }
        });

        // Exactly the named commit is rewritten; the report's scope line is what happened.
        var scopedMessage = FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%B", report.CommitMap[c2]], null, null);
        Assert.Contains(Redacted, scopedMessage);
        var untouchedMessage = FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%B", report.CommitMap[c1]], null, null);
        Assert.Contains(Needle, untouchedMessage);
        Assert.Equal(1, report.MessagesChanged);

        Assert.Equal("New Person|new@example.com",
            FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%an|%ae", report.CommitMap[c2]], null, null).Trim());
        Assert.Equal("Old Name|old@example.com",
            FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%an|%ae", report.CommitMap[c1]], null, null).Trim());

        // The scrub reports scoped verification, never a global clean bill.
        foreach (var check in report.ScrubChecks)
        {
            Assert.True(check.WithinScopeOnly);
            Assert.True(check.Complete);
            Assert.Empty(check.Hits);
        }
        Assert.Contains($"commits [{c2}]", report.ScopeDescription);
        Assert.Contains("messages/identities:", report.ScopeDescription);
        output.WriteLine($"commit-scoped metadata: {report.MessagesChanged} message(s) and " +
                         $"{report.IdentitiesRewritten} identity header(s) rewritten, c1 untouched; " +
                         $"scope='{report.ScopeDescription}'");
    }

    /// <summary>
    /// A tag message is arbitrary bytes. When the message scrub reads tags through a
    /// delimited format, a message carrying that delimiter deletes or truncates its own
    /// record, and a surviving needle is then reported as a clean bill.
    /// </summary>
    [Theory]
    [InlineData("no separator", -1)]
    [InlineData("unit separator", 0x1f)]
    [InlineData("record separator", 0x1e)]
    public async Task ATagMessageCarryingAScrubSeparatorStillReportsItsSurvivor(string label, int separator)
    {
        using var f = Fixture();
        f.Write("a.txt", "body\n");
        f.CommitAll("clean commit");

        // Invalid UTF-8 makes the message transform skip it, so the needle genuinely
        // survives in the target and the scrub is obliged to say so.
        // The separator precedes the needle: a format that splits on it truncates the record
        // exactly where the needle would have been.
        var message = new List<byte>(Encoding.ASCII.GetBytes("release "));
        if (separator >= 0) message.Add((byte)separator);
        message.AddRange(Encoding.ASCII.GetBytes($" {Needle} "));
        message.AddRange([0xFF, 0xFE, (byte)'\n']);
        var messagePath = Path.Combine(f.Root, "tag-message.bin");
        File.WriteAllBytes(messagePath, message.ToArray());
        f.Git("tag", "-a", "rel", "-F", messagePath, "--cleanup=verbatim");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }]
        });

        var targetTag = FixtureRepo.RunGit(f.TargetPath, ["for-each-ref", "refs/tags/rel", "--format=%(contents)"], null, null);
        Assert.Contains(Needle, targetTag);

        var check = Assert.Single(report.ScrubChecks);
        Assert.NotEmpty(check.Hits);
        output.WriteLine($"{label}: needleSurvivesInTarget=True scrubHits={check.Hits.Count} Complete={check.Complete}");
    }

    /// <summary>
    /// A message op that could not run must leave a signal. Without one the report shows no
    /// change and no skip, and the check reads as having covered the message.
    /// The fixture leaks through a tag because <c>git commit</c> re-encodes a non-UTF-8 message
    /// at write time, so no message authored by this fixture's commits stays undecodable. The
    /// commit branch is live all the same — see
    /// <see cref="AnImportedCommitMessageThatIsNotUtf8IsReportedAsASkip"/>.
    /// </summary>
    [Fact]
    public async Task AMessageTheTransformCannotDecodeIsReportedAsASkip()
    {
        using var f = Fixture();
        f.Write("a.txt", "body\n");
        f.CommitAll("clean commit");

        var tagMessagePath = Path.Combine(f.Root, "tag-message.bin");
        File.WriteAllBytes(tagMessagePath, [.. Encoding.ASCII.GetBytes($"tag {Needle} "), 0xFF, 0xFE, (byte)'\n']);
        f.Git("tag", "-a", "rel", "-F", tagMessagePath, "--cleanup=verbatim");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }]
        });

        var skip = Assert.Single(report.BinarySkips);
        Assert.Equal("message is not valid UTF-8", skip.Reason);
        Assert.Equal(0, report.MessagesChanged);

        var check = Assert.Single(report.ScrubChecks);
        Assert.False(check.Complete);
        Assert.Contains("1 message(s) are not valid UTF-8", check.Note);
        output.WriteLine($"undecodable tag message: skip reported ({skip.Size} bytes), Complete={check.Complete}; note='{check.Note}'");
    }

    /// <summary>
    /// <c>fast-export --reencode=yes</c> re-encodes only what it can decode, so a commit message
    /// that is not valid UTF-8 reaches the transform raw. History written by fast-import,
    /// git-svn, git-p4, or a raw object writer carries such messages, so the commit branch of
    /// the undecodable-message skip is reachable and owes the same incomplete check a tag does.
    /// </summary>
    [Fact]
    public async Task AnImportedCommitMessageThatIsNotUtf8IsReportedAsASkip()
    {
        using var f = Fixture(bareSource: true);
        var message = new List<byte>(Encoding.ASCII.GetBytes($"commit {Needle} "));
        message.AddRange([0xFF, 0xFE, (byte)'\n']);
        var feed = new List<byte>(Encoding.ASCII.GetBytes(
            "blob\nmark :1\ndata 5\nbody\n\n" +
            "commit refs/heads/main\nmark :2\n" +
            "author Fixture <fixture@example.com> 1700000000 +0000\n" +
            "committer Fixture <fixture@example.com> 1700000000 +0000\n" +
            $"data {message.Count}\n"));
        feed.AddRange(message);
        feed.AddRange(Encoding.ASCII.GetBytes("M 100644 :1 a.txt\n\n"));
        f.GitWithStdin([.. feed], "fast-import", "--quiet");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }]
        });

        var skip = Assert.Single(report.BinarySkips);
        Assert.Equal("message is not valid UTF-8", skip.Reason);
        Assert.Equal(message.Count, skip.Size);
        Assert.Equal(0, report.MessagesChanged);
        Assert.Contains(Needle,
            FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%B", "refs/heads/main"], null, null));

        var check = Assert.Single(report.ScrubChecks);
        Assert.NotEmpty(check.Hits);
        Assert.False(check.Complete);
        Assert.Contains("1 message(s) are not valid UTF-8", check.Note);
        output.WriteLine($"undecodable imported commit message: skip reported ({skip.Size} bytes), " +
                         $"needle survives in target, scrubHits={check.Hits.Count}, Complete={check.Complete}");
    }

    /// <summary>
    /// A pruned commit's map entry points at the survivor its children were rewired onto.
    /// When that survivor is out of scope, reading it on the pruned commit's behalf reports a
    /// hit on a message the run was never allowed to touch.
    /// </summary>
    [Fact]
    public async Task APrunedInScopeCommitDoesNotDragAnOutOfScopeMessageIntoTheScrub()
    {
        using var f = Fixture();
        f.Write("keep.txt", "keep\n");
        f.CommitAll($"c1 leaks {Needle}");
        var c1 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("junk.txt", "junk\n");
        f.CommitAll("c2 adds junk only");
        var c2 = f.Git("rev-parse", "HEAD").Trim();
        f.Write("keep.txt", "keep two\n");
        f.CommitAll("c3 edits keep");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }],
            CommitScope = new ExplicitCommitsScope { Commits = [c2] },
            Purge = new PurgeSpec { Paths = new ExplicitPathsScope { Paths = ["junk.txt"] } }
        });

        // The purge emptied the one in-scope commit, so its oid now resolves to out-of-scope c1.
        Assert.Equal(1, report.CommitsPruned);
        Assert.Equal(report.CommitMap[c1], report.CommitMap[c2]);
        Assert.Contains(Needle,
            FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%B", report.CommitMap[c1]], null, null));

        var check = Assert.Single(report.ScrubChecks);
        Assert.Empty(check.Hits);
        Assert.True(check.WithinScopeOnly);
        Assert.True(check.Complete);
        Assert.Contains("out-of-scope messages are untouched and unchecked", check.Note);
        output.WriteLine($"pruned in-scope commit: c2 maps to out-of-scope c1, scrub hits={check.Hits.Count}; note='{check.Note}'");
    }

    /// <summary>
    /// The tag corpus feeds both metadata checks. A tag read the scrub cannot trust — a
    /// failed enumeration, or a record that does not parse — must degrade those checks to a
    /// note; returning an empty tag list would let them report coverage of tags nothing
    /// looked at.
    /// </summary>
    [Fact]
    public async Task ATagRecordTheScrubCannotParseDegradesTheMetadataChecks()
    {
        using var f = Fixture(bareSource: true);
        // A tagger name holding the field separator gives for-each-ref a six-field record.
        // fast-import accepts the ident and fsck --strict passes the object, so the scrub is
        // the only layer that can react to it.
        var feed =
            "blob\nmark :1\ndata 5\nbody\n\n" +
            "commit refs/heads/main\nmark :2\n" +
            "author Fixture <fixture@example.com> 1700000000 +0000\n" +
            "committer Fixture <fixture@example.com> 1700000000 +0000\n" +
            "data 3\nc1\nM 100644 :1 a.txt\n\n" +
            "tag weird\nfrom :2\ntagger Fix\x1fture <fixture@example.com> 1700000000 +0000\ndata 5\nmsg1\n\n";
        f.GitWithStdin(Encoding.ASCII.GetBytes(feed), "fast-import", "--quiet");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }],
            IdentityMappings = [new IdentityMapping { OldEmail = "fixture@example.com", NewEmail = "new@example.com" }]
        });

        Assert.Equal(2, report.ScrubChecks.Count);
        foreach (var check in report.ScrubChecks)
        {
            Assert.False(check.Performed);
            Assert.False(check.Complete);
            Assert.Empty(check.Hits);
            Assert.Contains("could not read the target", check.Note);
            Assert.Contains("cannot parse", check.Note);
        }
        output.WriteLine($"unparseable tag record: both metadata checks degraded; note='{report.ScrubChecks[0].Note}'");
    }

    // ---- Identity rewrite ----------------------------------------------------------------

    [Fact]
    public async Task IdentityRewriteMapsAuthorCommitterAndTagger()
    {
        using var f = Fixture();
        var oldEnv = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Öld Nämé",
            ["GIT_AUTHOR_EMAIL"] = "old@example.com",
            ["GIT_COMMITTER_NAME"] = "Öld Nämé",
            ["GIT_COMMITTER_EMAIL"] = "old@example.com"
        };
        f.Write("a.txt", "content\n");
        f.CommitAll("commit", oldEnv);
        f.Git(oldEnv, "tag", "-a", "rel", "-m", "tag message");
        var head = f.Git("rev-parse", "HEAD").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            IdentityMappings =
            [
                new IdentityMapping { OldEmail = "old@example.com", NewName = "New Person", NewEmail = "new@example.com" }
            ]
        });

        var newHead = report.CommitMap[head];
        var idLine = FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%an|%ae|%cn|%ce", newHead], null, null).Trim();
        Assert.Equal("New Person|new@example.com|New Person|new@example.com", idLine);
        var tagger = FixtureRepo.RunGit(f.TargetPath, ["for-each-ref", "refs/tags/rel", "--format=%(taggername)|%(taggeremail)"], null, null).Trim();
        Assert.Equal("New Person|<new@example.com>", tagger);
        Assert.True(report.IdentitiesRewritten >= 3);

        var idScrub = Assert.Single(report.ScrubChecks, c => c.Kind == "identity");
        Assert.True(idScrub.Complete);
        Assert.Empty(idScrub.Hits);
        output.WriteLine($"identity rewrite: author+committer+tagger remapped ({report.IdentitiesRewritten} headers); identity scrub clean");
    }

    /// <summary>
    /// A stream carrying an ident whose bytes are not UTF-8: the rewriter and the read-back
    /// both decode through UTF-8, so both see the same replacement character and a mapping
    /// typed from the rendered name matches neither. Nothing is rewritten and nothing
    /// survives the search, which is the shape of a clean bill — so the header the op could
    /// not read must be reported as a gap, or the run signs off on an identity it never
    /// matched.
    /// </summary>
    [Fact]
    public async Task AnIdentHeaderThatIsNotUtf8IsReportedAsAGapNotAsClean()
    {
        using var f = Fixture(bareSource: true);
        f.GitWithStdin(NonUtf8AuthorFeed(), "fast-import", "--quiet");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            IdentityMappings = [new IdentityMapping { OldName = "José", NewName = "Anon" }]
        });

        var check = Assert.Single(report.ScrubChecks);
        var author = FixtureRepo.RunGit(f.TargetPath, ["log", "-1", "--format=%an", "refs/heads/main"], null, null);
        output.WriteLine($"non-UTF-8 ident: rewritten={report.IdentitiesRewritten} complete={check.Complete} " +
                         $"withinScopeOnly={check.WithinScopeOnly} hits={check.Hits.Count} " +
                         $"verdict={RewriteScrubVerdict.For(check)}; target author still '{author.Trim()}'");

        Assert.Equal("identity", check.Kind);
        Assert.Equal(0, report.IdentitiesRewritten);
        Assert.Empty(check.Hits);
        Assert.False(check.Complete);
        Assert.Equal(ScrubVerdict.NotVerified, RewriteScrubVerdict.For(check));
        Assert.Contains(report.BinarySkips, s => s.Reason == ScopedRewriteOutcome.IdentityNotUtf8);
    }

    /// <summary>
    /// The same gap under a commit scope. "Clean within scope" is a claim about the commits
    /// the caller named, and the unreadable ident sits inside one of them.
    /// </summary>
    [Fact]
    public async Task AScopedIdentityRunOverANonUtf8IdentIsNotCleanWithinScope()
    {
        using var f = Fixture(bareSource: true);
        f.GitWithStdin(NonUtf8AuthorFeed(), "fast-import", "--quiet");
        var head = f.Git("rev-parse", "refs/heads/main").Trim();

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            IdentityMappings = [new IdentityMapping { OldName = "José", NewName = "Anon" }],
            CommitScope = new ExplicitCommitsScope { Commits = [head] }
        });

        var check = Assert.Single(report.ScrubChecks);
        output.WriteLine($"scoped non-UTF-8 ident: complete={check.Complete} withinScopeOnly={check.WithinScopeOnly} " +
                         $"verdict={RewriteScrubVerdict.For(check)}");

        Assert.True(check.WithinScopeOnly);
        Assert.False(check.Complete);
        Assert.NotEqual(ScrubVerdict.CleanWithinScope, RewriteScrubVerdict.For(check));
        Assert.Equal(ScrubVerdict.NotVerified, RewriteScrubVerdict.For(check));
    }

    /// <summary>
    /// An ident carrying the byte the identity read-back splits fields on. Dropping the record
    /// it mangles would leave that identity out of the corpus entirely, so a mapping would find
    /// no survivor in a history the check never read while still reporting itself complete. The
    /// check degrades to a note instead, as it does for a tag record it cannot parse.
    /// </summary>
    [Fact]
    public async Task AnIdentityRecordTheReadBackCannotParseDegradesTheCheckToANote()
    {
        using var f = Fixture(bareSource: true);
        f.GitWithStdin(SeparatorInIdentFeed(), "fast-import", "--quiet");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [],
            IdentityMappings = [new IdentityMapping { OldName = "Nobody", NewName = "Anon" }]
        });

        var check = Assert.Single(report.ScrubChecks);
        output.WriteLine($"unparseable ident record: performed={check.Performed} complete={check.Complete} " +
                         $"verdict={RewriteScrubVerdict.For(check)}; note={check.Note}");

        Assert.Equal("identity", check.Kind);
        Assert.False(check.Performed);
        Assert.False(check.Complete);
        Assert.Empty(check.Hits);
        Assert.Contains("cannot parse", check.Note);
        Assert.Equal(ScrubVerdict.NotVerified, RewriteScrubVerdict.For(check));
    }

    /// <summary>
    /// Content, message, and identity ops in one run over a commit whose blob, message, and
    /// ident are all unreadable. Each check may name only the gaps its own op left: a message
    /// and an ident skip both carry a null mark, so a check that partitions on the mark reports
    /// the other family's gap as its own and a content check sweeps up all three.
    /// </summary>
    [Fact]
    public async Task EachCheckNamesOnlyTheUnreadablePayloadsItsOwnOpLeftBehind()
    {
        using var f = Fixture(bareSource: true);
        f.GitWithStdin(CombinedNonUtf8Feed(), "fast-import", "--quiet");

        var report = await RewriteAsync(f, new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes(Needle), Replace = Encoding.UTF8.GetBytes(Redacted) }],
            MessageOps = [new LiteralReplace { Find = Encoding.UTF8.GetBytes("ticket-9182"), Replace = Encoding.UTF8.GetBytes("redacted") }],
            IdentityMappings = [new IdentityMapping { OldName = "José", NewName = "Anon" }]
        });

        output.WriteLine("skips: " + string.Join(" | ",
            report.BinarySkips.Select(s => $"mark={s.Mark?.ToString() ?? "-"} path={s.Path ?? "-"} reason={s.Reason}")));
        Assert.Equal(3, report.BinarySkips.Count);

        var content = Assert.Single(RewriteScrubVerdict.SkipsFor("literal", report.BinarySkips));
        var message = Assert.Single(RewriteScrubVerdict.SkipsFor("message-literal", report.BinarySkips));
        var identity = Assert.Single(RewriteScrubVerdict.SkipsFor("identity", report.BinarySkips));

        Assert.Equal("bin.dat", content.Path);
        Assert.Equal(ScopedRewriteOutcome.MessageNotUtf8, message.Reason);
        Assert.Equal(ScopedRewriteOutcome.IdentityNotUtf8, identity.Reason);

        // The rendered rows carry the same partition: no check names another family's payload.
        foreach (var check in report.ScrubChecks)
        {
            var detail = RewriteScrubVerdict.Describe(check, report.BinarySkips).Detail;
            var expected = RewriteScrubVerdict.SkipsFor(check.Kind, report.BinarySkips);
            foreach (var skip in report.BinarySkips)
            {
                var location = RewriteScrubVerdict.SkipLocation(skip);
                if (expected.Contains(skip))
                    Assert.Contains(location, detail);
                else
                    Assert.DoesNotContain(location, detail);
            }
        }
    }

    /// <summary>
    /// One commit whose blob, message, and ident are each Latin-1 bytes with no encoding header,
    /// so all three ops meet a payload they cannot decode in a single run. Latin-1 encodes the
    /// whole feed because every other byte in it is ASCII.
    /// </summary>
    private static byte[] CombinedNonUtf8Feed()
    {
        const string blob = "binary payload é\n";
        const string message = "classified é\n";
        return Encoding.Latin1.GetBytes(
            $"blob\nmark :1\ndata {blob.Length}\n{blob}\n" +
            "commit refs/heads/main\nmark :2\n" +
            "author José <old@example.com> 1700000000 +0000\n" +
            "committer Fixture <fixture@example.com> 1700000000 +0000\n" +
            $"data {message.Length}\n{message}" +
            "M 100644 :1 bin.dat\n\n");
    }

    /// <summary>One commit whose author name carries a 0x1f byte — the separator the identity read-back splits on.</summary>
    private static byte[] SeparatorInIdentFeed() => Encoding.ASCII.GetBytes(
        "blob\nmark :1\ndata 5\nbody\n\n" +
        "commit refs/heads/main\nmark :2\n" +
        "author Sep" + (char)0x1f + "Arated <old@example.com> 1700000000 +0000\n" +
        "committer Fixture <fixture@example.com> 1700000000 +0000\n" +
        "data 3\nc1\nM 100644 :1 a.txt\n\n");

    /// <summary>
    /// One commit whose author name is <c>Jos\xE9</c> — Latin-1 bytes with no encoding header,
    /// which fast-import accepts and fsck --strict passes, so nothing below the scrub can react
    /// to it. Latin-1 encodes the whole feed because every other byte in it is ASCII.
    /// </summary>
    private static byte[] NonUtf8AuthorFeed() => Encoding.Latin1.GetBytes(
        "blob\nmark :1\ndata 5\nbody\n\n" +
        "commit refs/heads/main\nmark :2\n" +
        "author José <old@example.com> 1700000000 +0000\n" +
        "committer Fixture <fixture@example.com> 1700000000 +0000\n" +
        "data 3\nc1\nM 100644 :1 a.txt\n\n");

    // ---- No-op scoped rewrite is byte-identical ------------------------------------------

    [Fact]
    public async Task NoOpScopedRewriteReproducesIdenticalRefs()
    {
        using var f = Fixture();
        f.Write("src/a.cs", "clean code\n");
        f.CommitAll("c1");
        f.Write("docs/x.md", "clean doc\n");
        f.CommitAll("c2");
        f.Git("tag", "-a", "v1", "-m", "clean tag");

        // Scoped to src/** but the needle is absent, so nothing changes.
        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new GlobScope { Patterns = ["src/**"] }));

        Assert.Equal(0, report.BlobsChanged);
        Assert.Equal(0, report.BlobsSplit);
        foreach (var (oldOid, newOid) in report.CommitMap)
            Assert.Equal(oldOid, newOid);

        var verify = await IdentityVerifier.VerifyAsync(GitGuard.GitExe, f.SourcePath, f.TargetPath, TimeSpan.FromMinutes(1));
        Assert.True(verify.Success, verify.Describe());
        Assert.Equal(HistoryTestSupport.DescribeHead(f.SourcePath), HistoryTestSupport.DescribeHead(f.TargetPath));
    }

    // ---- Perf: scoped rewrite of a 1000-commit history -----------------------------------

    [Fact]
    public async Task ThousandCommitScopedRewriteCompletesQuickly()
    {
        using var f = Fixture(bareSource: true);
        SyntheticHistory.Import(f, SyntheticHistory.BuildStream(1000, Needle, directoryCount: 4));

        var stopwatch = Stopwatch.StartNew();
        var report = await RewriteAsync(f, Literal(Needle, Redacted, files: new GlobScope { Patterns = ["dir0/**"] }));
        stopwatch.Stop();

        Assert.Equal(1000, report.CommitMap.Count);
        Assert.True(report.BlobsChanged > 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(120),
            $"scoped 1000-commit rewrite took {stopwatch.Elapsed}");
        output.WriteLine($"scoped 1000-commit rewrite: {stopwatch.Elapsed.TotalSeconds:F2}s wall, " +
                         $"{report.BlobsChanged} blobs changed in dir0/**, {report.BlobsSplit} splits");
    }
}
