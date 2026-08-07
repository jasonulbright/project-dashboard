using ProjectDashboard.Services.History;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The scrub-honesty mapping, exhaustively. Every combination of hits, Performed, Complete,
/// and WithinScopeOnly is asserted, because the whole feature's credibility rests on the one
/// combination that reads as silence and must never read as success: no hits with incomplete
/// coverage.
/// </summary>
public class RewriteScrubVerdictTests
{
    private static ScrubCheckResult Check(
        int hits, bool performed, bool complete, bool withinScopeOnly,
        string kind = "literal", string? note = null, int commitsChecked = 7) =>
        new()
        {
            Kind = kind,
            Needle = "SECRET",
            Performed = performed,
            Complete = complete,
            WithinScopeOnly = withinScopeOnly,
            CommitsChecked = commitsChecked,
            Hits = Enumerable.Range(0, hits).Select(i => $"deadbeef:a{i}.txt:SECRET").ToList(),
            Note = note,
        };

    private static RewriteReport Report(params ScrubCheckResult[] checks) => new()
    {
        SourceRepository = @"C:\repo",
        TargetBareRepository = @"C:\temp\target.git",
        CommitCount = 7,
        BlobsChanged = 3,
        BytesDelta = -12,
        BinarySkips = [],
        CommitMap = new Dictionary<string, string> { ["aaa"] = "bbb" },
        CommitsWithChangedTrees = ["aaa"],
        FsckOutput = "",
        ScrubChecks = checks,
    };

    public static TheoryData<bool, bool, bool> CoverageFlags()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var performed in new[] { true, false })
            foreach (var complete in new[] { true, false })
                foreach (var scoped in new[] { true, false })
                    data.Add(performed, complete, scoped);
        return data;
    }

    [Theory]
    [MemberData(nameof(CoverageFlags))]
    public void HitsAlwaysOutrankEveryCoverageFlag(bool performed, bool complete, bool scoped)
    {
        var line = RewriteScrubVerdict.Describe(Check(2, performed, complete, scoped), []);

        Assert.Equal(ScrubVerdict.OccurrencesRemain, line.Verdict);
        Assert.Equal(RewriteScrubVerdict.RemainLabel, line.Label);
        Assert.False(line.ClaimsClean);
        Assert.True(line.IsProblem);
        Assert.Contains("still present", line.Headline);
        // The surviving locations are named, not merely counted.
        Assert.Contains("a0.txt", line.Detail);
        Assert.Contains("a1.txt", line.Detail);
    }

    [Theory]
    [MemberData(nameof(CoverageFlags))]
    public void NoVerdictOtherThanVerifiedCleanEverClaimsClean(bool performed, bool complete, bool scoped)
    {
        foreach (var hits in new[] { 0, 3 })
        {
            var line = RewriteScrubVerdict.Describe(Check(hits, performed, complete, scoped), []);
            var earnsClean = hits == 0 && performed && complete && !scoped;
            Assert.Equal(earnsClean, line.ClaimsClean);
            Assert.Equal(earnsClean, line.Verdict == ScrubVerdict.VerifiedClean);
        }
    }

    [Fact]
    public void NoHitsAndComplete_IsTheOnlyCleanBill()
    {
        var line = RewriteScrubVerdict.Describe(Check(0, performed: true, complete: true, withinScopeOnly: false), []);

        Assert.Equal(ScrubVerdict.VerifiedClean, line.Verdict);
        Assert.Equal(RewriteScrubVerdict.CleanLabel, line.Label);
        Assert.Contains("is gone", line.Headline);
        Assert.Contains("7 commit(s)", line.Headline);
    }

    /// <summary>
    /// The count is carried only by a commit-scoped run, so the fully-clean unscoped message
    /// and identity rewrites — the normal case — are exactly the ones that report zero.
    /// </summary>
    [Fact]
    public void ACleanBillWithNoCommitCount_DoesNotPrintZeroCommitsChecked()
    {
        var line = RewriteScrubVerdict.Describe(
            Check(0, performed: true, complete: true, withinScopeOnly: false,
                kind: "message-literal", commitsChecked: 0), []);

        Assert.Equal(ScrubVerdict.VerifiedClean, line.Verdict);
        Assert.DoesNotContain("0 commit(s)", line.Headline);
        Assert.Contains("is gone", line.Headline);
        Assert.Contains("across the rewritten history", line.Headline);
    }

    /// <summary>The dangerous case: an empty hit list from a check that did not cover everything.</summary>
    [Fact]
    public void NoHitsAndIncomplete_ReadsAsNotVerifiedNeverAsClean()
    {
        var check = Check(0, performed: true, complete: false, withinScopeOnly: false,
            note: "1 blob skipped as binary");
        var skips = new List<BinarySkip> { new(Mark: 42, Size: 900, Path: "assets/logo.png", Reason: "not valid UTF-8") };

        var line = RewriteScrubVerdict.Describe(check, skips);

        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.Equal(RewriteScrubVerdict.NotVerifiedLabel, line.Label);
        Assert.False(line.ClaimsClean);
        Assert.Contains("NOT proof", line.Headline);
        Assert.DoesNotContain("is gone", line.Headline);
        Assert.DoesNotContain("Verified clean", line.Headline);
        // The reason and the skipped path are both named.
        Assert.Contains("1 blob skipped as binary", line.Detail);
        Assert.Contains("assets/logo.png", line.Detail);
        Assert.Contains("not valid UTF-8", line.Detail);
    }

    public static TheoryData<bool, bool> CompleteAndScopedFlags()
    {
        var data = new TheoryData<bool, bool>();
        foreach (var complete in new[] { true, false })
            foreach (var scoped in new[] { true, false })
                data.Add(complete, scoped);
        return data;
    }

    /// <summary>
    /// A check that never ran is silence under every other flag, including the scope flag.
    /// WithinScopeOnly is set from the requested scope alone, so a scoped run whose grep was
    /// rejected, timed out, or exited abnormally arrives here with the scope flag set and no
    /// search behind it — the one shape that must never render as a cleaned scope.
    /// </summary>
    [Theory]
    [MemberData(nameof(CompleteAndScopedFlags))]
    public void AnUnperformedCheckIsNotVerified_WhateverTheOtherFlagsSay(bool complete, bool scoped)
    {
        var line = RewriteScrubVerdict.Describe(
            Check(0, performed: false, complete: complete, withinScopeOnly: scoped), []);

        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.Equal(RewriteScrubVerdict.NotVerifiedLabel, line.Label);
        Assert.False(line.ClaimsClean);
        Assert.True(line.IsProblem);
        Assert.Contains("NOT proof", line.Headline);
        Assert.DoesNotContain("was cleaned within the selected scope", line.Headline);
        Assert.Contains("could not run", line.Detail);
    }

    /// <summary>The same gap through the summary line, where a scoped-clean verdict renders amber rather than red.</summary>
    [Fact]
    public void Overall_AnUnperformedScopedCheck_IsNotVerifiedNotCleanWithinScope()
    {
        var line = RewriteScrubVerdict.Overall(
            Report(Check(0, performed: false, complete: true, withinScopeOnly: true)));

        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.False(line.ClaimsClean);
        Assert.True(line.IsProblem);
    }

    [Fact]
    public void NotPerformed_IsNeverCleanEvenWhenTheRunMarkedItComplete()
    {
        var check = Check(0, performed: false, complete: true, withinScopeOnly: false,
            note: "git grep rejected the pattern");

        var line = RewriteScrubVerdict.Describe(check, []);

        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.Contains("could not run", line.Detail);
        Assert.Contains("git grep rejected the pattern", line.Detail);
    }

    [Fact]
    public void WithinScopeOnly_SaysScopeAndRefusesTheEverywhereClaim()
    {
        var line = RewriteScrubVerdict.Describe(
            Check(0, performed: true, complete: false, withinScopeOnly: true, note: "checked 3 of 40 commits"), []);

        Assert.Equal(ScrubVerdict.CleanWithinScope, line.Verdict);
        Assert.Equal(RewriteScrubVerdict.WithinScopeLabel, line.Label);
        Assert.False(line.ClaimsClean);
        Assert.Contains("within the selected scope", line.Headline);
        Assert.Contains("left by design", line.Headline);
        Assert.Contains("not a claim that the repository is clean everywhere", line.Headline);
        Assert.Contains("checked 3 of 40 commits", line.Detail);
    }

    [Fact]
    public void TreeCheckNamesOnlyBlobSkips_MessageCheckOnlyMessageSkips()
    {
        List<BinarySkip> skips =
        [
            new(Mark: 7, Size: 100, Path: "bin/tool.exe", Reason: "not valid UTF-8"),
            new(Mark: null, Size: 40, Path: null, Reason: "message is not valid UTF-8"),
        ];

        var tree = RewriteScrubVerdict.Describe(Check(0, true, false, false, kind: "literal"), skips);
        var message = RewriteScrubVerdict.Describe(Check(0, true, false, false, kind: "message-literal"), skips);

        Assert.Contains("bin/tool.exe", tree.Detail);
        Assert.DoesNotContain("commit or tag message", tree.Detail);
        Assert.Contains("commit or tag message", message.Detail);
        Assert.DoesNotContain("bin/tool.exe", message.Detail);
    }

    [Fact]
    public void Overall_TakesTheWorstCheckNotTheBest()
    {
        var report = Report(
            Check(0, true, true, false),                       // verified clean
            Check(0, true, false, false),                      // not verified
            Check(0, true, false, true));                      // within scope only

        var line = RewriteScrubVerdict.Overall(report);

        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.False(line.ClaimsClean);
    }

    [Fact]
    public void Overall_WithNoChecks_IsUnverifiedNotSuccess()
    {
        var line = RewriteScrubVerdict.Overall(Report());

        Assert.Equal(ScrubVerdict.NotVerified, line.Verdict);
        Assert.False(line.ClaimsClean);
        Assert.Contains("nothing here proves any content was removed", line.Headline);
    }

    [Fact]
    public void Overall_AllCleanChecks_IsTheOnlyPathToACleanSummary()
    {
        var line = RewriteScrubVerdict.Overall(Report(Check(0, true, true, false), Check(0, true, true, false)));

        Assert.Equal(ScrubVerdict.VerifiedClean, line.Verdict);
        Assert.True(line.ClaimsClean);
        Assert.Contains("All 2 check(s) verified clean", line.Headline);
    }

    [Fact]
    public void RowsReadAsSentences_NotAsRecordSyntax()
    {
        var line = RewriteScrubVerdict.Describe(Check(0, true, true, false), []);
        Assert.StartsWith(RewriteScrubVerdict.CleanLabel + ". ", line.ToString());
        Assert.DoesNotContain("Verdict =", line.ToString());

        Assert.Equal("Commits rewritten: 12", new RewriteFact("Commits rewritten", "12").ToString());
    }

    // ── Pre-flight refusals ──────────────────────────────────────────────────

    [Theory]
    [InlineData("repository is busy with another operation: C:\\repo", "Wait for it to finish")]
    [InlineData("working tree has 2 uncommitted change(s) — refusing the rewrite (stash or commit first): a.txt, b.txt", "Commit or stash")]
    [InlineData("preflight: nested tags are unsupported — refs/tags/outer point(s) at another tag object", "cannot round-trip")]
    [InlineData("rewritten path 'config/aux' can never check out on Windows: reserved device name", "Windows cannot check out")]
    [InlineData("backup failed — no rewrite attempted: disk full", "no rewrite was attempted")]
    [InlineData("rewrite target failed fsck — refusing the swap: broken link", "integrity check")]
    public void DescribeRefusal_AddsGuidanceAndKeepsTheRawReason(string reason, string expectedGuidance)
    {
        var text = RewriteScrubVerdict.DescribeRefusal(reason);

        Assert.Contains(expectedGuidance, text);
        Assert.Contains(reason, text); // the file list, tag name, or path survives verbatim
    }

    [Fact]
    public void DescribeRefusal_UnrecognisedReason_IsShownAsItIs()
    {
        Assert.Equal("something unusual happened", RewriteScrubVerdict.DescribeRefusal("something unusual happened"));
    }

    [Fact]
    public void DescribeRefusal_NoReason_StillSaysNothingChanged()
    {
        Assert.Contains("Nothing was changed", RewriteScrubVerdict.DescribeRefusal(null));
    }

    // ── Report facts ─────────────────────────────────────────────────────────

    [Fact]
    public void Facts_NameTheOutOfScopeSpillAndSignTheByteDelta()
    {
        var report = new RewriteReport
        {
            SourceRepository = @"C:\repo",
            TargetBareRepository = @"C:\temp\t.git",
            CommitCount = 40,
            BlobsChanged = 5,
            BytesDelta = -2048,
            BinarySkips = [new BinarySkip(1, 10, "a.bin", "not valid UTF-8")],
            CommitMap = new Dictionary<string, string> { ["a"] = "b", ["c"] = "d" },
            CommitsWithChangedTrees = ["a", "c"],
            FsckOutput = "",
            ScrubChecks = [],
            ScopeDescription = "files: globs [src/**]; commits: all history",
            InScopeCommitCount = 12,
            OutOfScopeCommitsWithChangedTrees = 9,
        };

        var facts = RewriteReportFacts.For(report);

        Assert.Contains(facts, f => f.Label == "Scope" && f.Value == "files: globs [src/**]; commits: all history");
        Assert.Contains(facts, f => f.Label.Contains("outside the selected scope") && f.Value == "9");
        Assert.Contains(facts, f => f.Label == "Size change" && f.Value == "-2,048 bytes");
        Assert.Contains(RewriteReportFacts.SkipLines(report), l => l.Contains("a.bin") && l.Contains("not valid UTF-8"));
    }
}
