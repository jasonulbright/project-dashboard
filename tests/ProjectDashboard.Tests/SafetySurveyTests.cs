using ProjectDashboard.Models;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The free tier and the rollup's own wording. Both are pure, which is the point of them: the
/// claims a safety page makes are the feature, and every one of them is assertable here without a
/// repository on disk, a dashboard, or a git process.
/// </summary>
public class SafetySurveyTests
{
    private static ProjectInfo Repo(string name, Action<GitStatus>? configure = null)
    {
        var status = new GitStatus { Branch = "main", RemoteUrl = "https://example.invalid/x.git" };
        configure?.Invoke(status);
        return new ProjectInfo
        {
            DirectoryName = name,
            DisplayName = name,
            FullPath = @"C:\projects\" + name,
            GitStatus = status,
        };
    }

    // ── Divergence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Divergence is both sides at once. A branch only ahead or only behind fast-forwards, and
    /// reporting it here would put every repository with unpushed work on a safety page.
    /// </summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, 3, false)]
    [InlineData(1, 1, true)]
    public void Divergence_IsBothSidesAtOnce(int ahead, int behind, bool diverged) =>
        Assert.Equal(diverged, SafetySurvey.IsDiverged(ahead, behind));

    [Fact]
    public void ADivergedCurrentBranch_IsAFindingThatOpensBranches()
    {
        var finding = Assert.Single(SafetySurvey.DivergedCurrentBranch(
            [Repo("worker", s => { s.AheadBy = 2; s.BehindBy = 3; })]));

        Assert.Equal(SafetySignal.DivergedBranch, finding.Signal);
        Assert.Equal(SafetyAction.OpenBranches, finding.Action);
        Assert.Contains("main", finding.Headline, StringComparison.Ordinal);
        Assert.Contains("2 ahead, 3 behind", finding.Detail, StringComparison.Ordinal);
    }

    /// <summary>A branch whose upstream is gone has no upstream to have diverged from.</summary>
    [Fact]
    public void AllBranchDivergence_IgnoresBranchesWithNoLiveUpstream()
    {
        var project = Repo("worker");
        var branches = new List<BranchInfo>
        {
            new() { Name = "main", Upstream = "origin/main", Ahead = 1, Behind = 1 },
            new() { Name = "orphan", Upstream = "", Ahead = 4, Behind = 4 },
            new() { Name = "stale", Upstream = "origin/stale", UpstreamGone = true, Ahead = 2, Behind = 2 },
        };

        var finding = Assert.Single(SafetySurvey.DivergedBranches(project, branches));
        Assert.Contains("main", finding.Headline, StringComparison.Ordinal);
    }

    // ── Repositories that cannot be described ───────────────────────────────

    /// <summary>
    /// A repository git could not read is its own finding, and is excluded from every other free
    /// check — a status that was never read cannot say a repository has a remote or is clean.
    /// </summary>
    [Fact]
    public void ARepositoryGitCouldNotRead_IsItsOwnFindingAndNotCountedAsClean()
    {
        var broken = Repo("broken", s => { s.HasError = true; s.RemoteUrl = ""; s.AheadBy = 1; s.BehindBy = 1; });

        Assert.Single(SafetySurvey.StatusUnreadable([broken]));
        Assert.Empty(SafetySurvey.NoRemote([broken]));
        Assert.Empty(SafetySurvey.DivergedCurrentBranch([broken]));
    }

    [Fact]
    public void ARemoteOnlyCard_IsNotACheckableRepository()
    {
        var cloud = new ProjectInfo { DirectoryName = "cloud", IsRemoteOnly = true, RemoteSlug = "owner/cloud" };
        Assert.Empty(SafetySurvey.Checkable([cloud]));
        Assert.Empty(SafetySurvey.NoRemote([cloud]));
    }

    // ── Interrupted operations ──────────────────────────────────────────────

    [Fact]
    public void AnInterruptedOperation_NamesItsBackupAndOffersRecovery()
    {
        var finding = Assert.Single(SafetySurvey.Interrupted(
            [new InterruptedOperation(@"C:\projects\worker", "swap", "20260808-101112000", "20260808-101000000", "")]));

        Assert.Equal(SafetySeverity.NeedsAttention, finding.Severity);
        Assert.Equal(SafetyAction.OpenRecoveryBackups, finding.Action);
        Assert.Equal("worker", finding.RepoName);
        Assert.Contains("swap", finding.Headline, StringComparison.Ordinal);
        Assert.Contains("20260808-101000000", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("Nothing has been restored", finding.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A journal entry that names no backup must not read as one whose backup is intact: what a
    /// restore would put back is the thing the reader is deciding on.
    /// </summary>
    [Fact]
    public void AnInterruptedOperationWithNoBackup_SaysWhatIsUnknown()
    {
        var finding = Assert.Single(SafetySurvey.Interrupted(
            [new InterruptedOperation(@"C:\projects\worker", "", "", null, "")]));

        Assert.Contains("names no backup", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("unrecorded phase", finding.Headline, StringComparison.Ordinal);
    }

    /// <summary>The ledger's own label outranks one reconstructed from the journal's phase alone.</summary>
    [Fact]
    public void ARecordedInterruption_IsNamedByItsLedgerLabel()
    {
        var finding = Assert.Single(SafetySurvey.Interrupted(
            [new InterruptedOperation(@"C:\projects\worker", "swap", "", null, "Interrupted history operation (swap)")]));

        Assert.Equal("Interrupted history operation (swap)", finding.Headline);
    }

    // ── Project data age ────────────────────────────────────────────────────

    /// <summary>An age nothing recorded is unknown, never zero.</summary>
    [Fact]
    public void ProjectDataWithNoRecordedScan_ReportsTheAgeAsUnknown()
    {
        var finding = Assert.Single(SafetySurvey.StaleProjectData(null, 7200, DateTimeOffset.Now));
        Assert.Contains("unknown", finding.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SafetyAction.Rescan, finding.Action);
    }

    [Fact]
    public void ProjectDataInsideTheRefreshInterval_IsNoFinding()
    {
        var now = DateTimeOffset.Now;
        Assert.Empty(SafetySurvey.StaleProjectData(now.AddSeconds(-60), 7200, now));
    }

    [Fact]
    public void ProjectDataOlderThanTheRefreshInterval_IsAFinding()
    {
        var now = DateTimeOffset.Now;
        var finding = Assert.Single(SafetySurvey.StaleProjectData(now.AddSeconds(-7201), 7200, now));
        Assert.Equal(SafetySeverity.Informational, finding.Severity);
        Assert.Equal("", finding.RepoPath);
    }

    // ── Rollup composition ──────────────────────────────────────────────────

    /// <summary>
    /// A repository is counted once, at its worst finding. Counting it in every band it has a
    /// finding in would report more repositories than the portfolio holds.
    /// </summary>
    [Fact]
    public void TheRollup_CountsEachRepositoryAtItsWorstFinding()
    {
        var repos = new List<ProjectInfo> { Repo("a"), Repo("b"), Repo("c") };
        var findings = new List<SafetyFinding>
        {
            new(SafetySignal.InterruptedOperation, SafetySeverity.NeedsAttention, repos[0].FullPath, "a", "", "", SafetyAction.None, ""),
            new(SafetySignal.UncommittedWork, SafetySeverity.Informational, repos[0].FullPath, "a", "", "", SafetyAction.None, ""),
            new(SafetySignal.NoRemote, SafetySeverity.WorthALook, repos[1].FullPath, "b", "", "", SafetyAction.None, ""),
        };

        Assert.Equal("1 need attention · 1 worth a look · 1 with nothing found",
            SafetyViewModel.ComposeRollup(repos, findings));
    }

    /// <summary>
    /// An informational finding still means something was found, so that repository is not one the
    /// checks passed over in silence.
    /// </summary>
    [Fact]
    public void TheRollup_DoesNotCountAnInformationalFindingAsNothingFound()
    {
        var repos = new List<ProjectInfo> { Repo("a") };
        var findings = new List<SafetyFinding>
        {
            new(SafetySignal.UncommittedWork, SafetySeverity.Informational, repos[0].FullPath, "a", "", "", SafetyAction.None, ""),
        };

        Assert.Equal("0 need attention · 0 worth a look · 0 with nothing found",
            SafetyViewModel.ComposeRollup(repos, findings));
    }

    /// <summary>A portfolio-level finding belongs to no repository and must not consume one of the counts.</summary>
    [Fact]
    public void TheRollup_IgnoresAFindingThatBelongsToNoRepository()
    {
        var repos = new List<ProjectInfo> { Repo("a") };
        var findings = new List<SafetyFinding>
        {
            new(SafetySignal.StaleProjectData, SafetySeverity.Informational, "", "", "", "", SafetyAction.Rescan, ""),
        };

        Assert.Equal("0 need attention · 0 worth a look · 1 with nothing found",
            SafetyViewModel.ComposeRollup(repos, findings));
    }

    // ── Honest copy ─────────────────────────────────────────────────────────

    /// <summary>A count that excluded nothing says nothing; one that excluded repositories names how many.</summary>
    [Theory]
    [InlineData(0, "")]
    [InlineData(3, " 3 skipped (busy).")]
    public void SkippedRepositories_AreNamedOrAbsent(int skipped, string expected) =>
        Assert.Equal(expected, SafetyCopy.Skipped(skipped));

    /// <summary>Four backup conditions, four sentences. None of them is silence.</summary>
    [Fact]
    public void BackupState_SeparatesAllFourConditions()
    {
        var when = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("No backup on disk.", SafetyCopy.BackupState(0, 0, null));
        Assert.Contains("none checked", SafetyCopy.BackupState(4, 0, null), StringComparison.Ordinal);
        Assert.Contains("refused by a restore", SafetyCopy.BackupState(4, 1, when), StringComparison.Ordinal);
        Assert.Contains("passed the restore's own check", SafetyCopy.BackupState(4, 0, when), StringComparison.Ordinal);
    }

    /// <summary>
    /// The backup check claims the restore's gate, never the objects. `git bundle verify` reads the
    /// header and prerequisites and stops, so a passing bundle is one a restore would accept and
    /// not one whose packed objects have been read.
    /// </summary>
    [Fact]
    public void TheBackupCheck_NeverClaimsThePackedObjectsWereRead()
    {
        Assert.Contains("not the packed objects", SafetyCopy.BackupCheckLimit, StringComparison.Ordinal);
        foreach (var state in new[]
                 {
                     SafetyCopy.BackupState(0, 0, null),
                     SafetyCopy.BackupState(4, 0, null),
                     SafetyCopy.BackupState(4, 1, DateTimeOffset.Now),
                     SafetyCopy.BackupState(4, 0, DateTimeOffset.Now),
                 })
            Assert.DoesNotContain("verified", state, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The header names the tiers that have not run. Without it, an absence of findings from a check
    /// nobody asked for reads as a clean bill of health.
    /// </summary>
    [Fact]
    public void TheTierLine_NamesWhatHasNotRun()
    {
        var untouched = SafetyCopy.TiersRun(SafetyTierState.NotRun, 0, 0, 30);
        Assert.Contains("Free checks only", untouched, StringComparison.Ordinal);
        Assert.Contains("0 of 30", untouched, StringComparison.Ordinal);

        var partial = SafetyCopy.TiersRun(SafetyTierState.Ran, 2, 1, 30);
        Assert.Contains("Branches and backups checked", partial, StringComparison.Ordinal);
        Assert.Contains("Backups checked on 2 of 30", partial, StringComparison.Ordinal);
        Assert.Contains("reflog-only commits checked on 1 of 30", partial, StringComparison.Ordinal);
    }

    /// <summary>
    /// The journal keeps no second copy, so an unreadable one reports nothing pending. Every place
    /// that reports zero interrupted operations carries that caveat.
    /// </summary>
    [Fact]
    public void TheInterruptedCaveat_SaysAnEmptyResultIsNotProof() =>
        Assert.Contains("not proof", SafetyCopy.InterruptedCaveat, StringComparison.Ordinal);
}
