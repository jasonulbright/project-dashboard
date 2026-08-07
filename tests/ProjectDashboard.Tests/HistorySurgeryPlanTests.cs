using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The pure reorder-plan builder: moves and marks in, the ordered sha list the driver
/// receives out. No repository is touched here — everything the planner decides is decided
/// before a gated operation exists.
/// </summary>
public class HistorySurgeryPlanTests
{
    /// <summary>Four commits, oldest first, with recognisable 40-character shas.</summary>
    private static List<PlannedCommit> Range(params string[] names) =>
        names.Select(n => new PlannedCommit { Sha = Sha(n), Subject = n }).ToList();

    private static string Sha(string name) => name.PadRight(40, '0');

    private static List<string> Order(List<PlannedCommit> commits) => commits.Select(c => c.Sha).ToList();

    // ── moves ──────────────────────────────────────────────────────────────

    [Fact]
    public void MoveUp_SwapsWithThePrecedingCommit()
    {
        var commits = Range("a", "b", "c");
        Assert.True(HistoryPlan.MoveUp(commits, 2));
        Assert.Equal([Sha("a"), Sha("c"), Sha("b")], Order(commits));
    }

    [Fact]
    public void MoveUp_OnTheOldestCommit_DoesNothing()
    {
        var commits = Range("a", "b", "c");
        Assert.False(HistoryPlan.MoveUp(commits, 0));
        Assert.Equal([Sha("a"), Sha("b"), Sha("c")], Order(commits));
    }

    [Fact]
    public void MoveDown_OnTheNewestCommit_DoesNothing()
    {
        var commits = Range("a", "b", "c");
        Assert.False(HistoryPlan.MoveDown(commits, 2));
        Assert.Equal([Sha("a"), Sha("b"), Sha("c")], Order(commits));
    }

    [Fact]
    public void MoveDown_SwapsWithTheFollowingCommit()
    {
        var commits = Range("a", "b", "c");
        Assert.True(HistoryPlan.MoveDown(commits, 0));
        Assert.Equal([Sha("b"), Sha("a"), Sha("c")], Order(commits));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void Moves_OutOfRangeIndex_AreRefusedWithoutChangingTheList(int index)
    {
        var commits = Range("a", "b", "c");
        Assert.False(HistoryPlan.MoveUp(commits, index));
        Assert.False(HistoryPlan.MoveDown(commits, index));
        Assert.Equal([Sha("a"), Sha("b"), Sha("c")], Order(commits));
    }

    [Fact]
    public void MoveUp_ThenMoveDown_ReturnsTheOriginalOrder()
    {
        var commits = Range("a", "b", "c", "d");
        var original = Order(commits);
        HistoryPlan.MoveUp(commits, 3);
        HistoryPlan.MoveDown(commits, 2);
        Assert.Equal(original, Order(commits));
        Assert.False(HistoryPlan.Resolve(commits, original).IsValid);
    }

    // ── marks are mutually exclusive ───────────────────────────────────────

    [Fact]
    public void MarkingDrop_ClearsTheSquashMarkAndViceVersa()
    {
        var commit = new PlannedCommit { Sha = Sha("a"), Subject = "a" };

        commit.SquashIntoPrevious = true;
        commit.Drop = true;
        Assert.False(commit.SquashIntoPrevious);
        Assert.Equal("drop", commit.MarkLabel);

        commit.SquashIntoPrevious = true;
        Assert.False(commit.Drop);
        Assert.Equal("squash", commit.MarkLabel);

        commit.SquashIntoPrevious = false;
        Assert.Equal("pick", commit.MarkLabel);
    }

    // ── resolution ─────────────────────────────────────────────────────────

    [Fact]
    public void UntouchedPlan_ResolvesToNothingToApply()
    {
        var commits = Range("a", "b", "c");
        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Equal(HistoryPlanKind.None, resolution.Kind);
        Assert.Contains("Nothing to apply", resolution.Refusal);
    }

    [Fact]
    public void MovesOnly_ResolveToAReorderListingEveryCommitInTheNewOrder()
    {
        var commits = Range("a", "b", "c");
        var original = Order(commits);
        HistoryPlan.MoveUp(commits, 2);

        var resolution = HistoryPlan.Resolve(commits, original);

        Assert.True(resolution.IsValid);
        Assert.Equal(HistoryPlanKind.Reorder, resolution.Kind);
        Assert.Equal([Sha("a"), Sha("c"), Sha("b")], resolution.Shas);
    }

    [Fact]
    public void DropsOnly_ResolveToJustTheDroppedCommits()
    {
        var commits = Range("a", "b", "c", "d");
        commits[1].Drop = true;
        commits[3].Drop = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.Equal(HistoryPlanKind.Drop, resolution.Kind);
        Assert.Equal([Sha("b"), Sha("d")], resolution.Shas);
    }

    [Fact]
    public void SquashMark_IncludesTheCommitItFoldsInto()
    {
        var commits = Range("a", "b", "c");
        commits[2].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.Equal(HistoryPlanKind.Squash, resolution.Kind);
        Assert.Equal([Sha("b"), Sha("c")], resolution.Shas);
    }

    [Fact]
    public void ConsecutiveSquashMarks_ShareOneAnchor()
    {
        var commits = Range("a", "b", "c", "d");
        commits[2].SquashIntoPrevious = true;
        commits[3].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.Equal(HistoryPlanKind.Squash, resolution.Kind);
        Assert.Equal([Sha("b"), Sha("c"), Sha("d")], resolution.Shas);
    }

    [Fact]
    public void SquashRunsSeparatedByAGap_AreRefusedAsTwoGroups()
    {
        var commits = Range("a", "b", "c", "d", "e");
        commits[1].SquashIntoPrevious = true;
        commits[4].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Contains("folds 2 separate groups", resolution.Refusal);
        Assert.Contains("fold one, then plan the next", resolution.Refusal);
    }

    [Fact]
    public void AdjacentSquashRuns_AreRefusedRatherThanCollapsedIntoOneFold()
    {
        // {a,b} and {c,d} form one contiguous sha list, which a driver reads as a single fold
        // set anchored on "a" — two previewed commits, one produced. Counting runs is what
        // separates this from the consecutive marks below, which really are one group.
        var commits = Range("a", "b", "c", "d");
        commits[1].SquashIntoPrevious = true;
        commits[3].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Equal(HistoryPlanKind.None, resolution.Kind);
        Assert.Contains("folds 2 separate groups", resolution.Refusal);
        Assert.Equal(2, HistoryPlan.Preview(commits).Count);
    }

    [Fact]
    public void DroppingEveryCommit_IsRefusedWithTheResetAdvice()
    {
        var commits = Range("a", "b");
        commits[0].Drop = true;
        commits[1].Drop = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Equal(HistoryPlanKind.None, resolution.Kind);
        Assert.Contains("empty the branch", resolution.Refusal);
        Assert.Contains("use a reset to the commit before the range", resolution.Refusal);
    }

    [Fact]
    public void SquashOnTheOldestCommit_IsRefusedBecauseNothingPrecedesIt()
    {
        var commits = Range("a", "b");
        commits[0].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Contains("nothing before it", resolution.Refusal);
    }

    [Theory]
    [InlineData(true, true, false, "a reorder and a drop")]
    [InlineData(true, false, true, "a reorder and a squash")]
    [InlineData(false, true, true, "a drop and a squash")]
    [InlineData(true, true, true, "a reorder and a drop and a squash")]
    public void MixedKinds_AreRefusedNamingWhatWasMixed(bool move, bool drop, bool squash, string expected)
    {
        var commits = Range("a", "b", "c", "d");
        var original = Order(commits);
        if (move) HistoryPlan.MoveUp(commits, 3);
        if (drop) commits.First(c => c.Sha == Sha("a")).Drop = true;
        if (squash) commits.First(c => c.Sha == Sha("c")).SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, original);

        Assert.False(resolution.IsValid);
        Assert.Equal(HistoryPlanKind.None, resolution.Kind);
        Assert.Contains(expected, resolution.Refusal);
        Assert.Contains("apply one, then plan the next", resolution.Refusal);
    }

    [Fact]
    public void PlanBuiltFromADifferentRange_IsRefusedRatherThanHandedToTheDriver()
    {
        var commits = Range("a", "b", "c");
        var otherRange = new List<string> { Sha("a"), Sha("b"), Sha("z") };

        var resolution = HistoryPlan.Resolve(commits, otherRange);

        Assert.False(resolution.IsValid);
        Assert.Contains("reload the history", resolution.Refusal);
    }

    [Fact]
    public void ShortenedPlan_IsRefusedRatherThanReadAsADrop()
    {
        var commits = Range("a", "b", "c");
        var original = Order(commits);
        commits.RemoveAt(1);

        Assert.Contains("reload the history", HistoryPlan.Resolve(commits, original).Refusal);
    }

    [Fact]
    public void EmptyRange_ResolvesToNothingToApply()
    {
        Assert.Contains("Nothing to apply", HistoryPlan.Resolve([], []).Refusal);
    }

    // ── preview ────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_ShowsTheOrderedResultWithDropsGoneAndFoldsMerged()
    {
        var commits = Range("alpha", "beta", "gamma", "delta");
        commits[1].Drop = true;
        commits[3].SquashIntoPrevious = true;

        Assert.Equal(
        [
            $"{Sha("alpha")[..8]}  alpha",
            $"{Sha("gamma")[..8]}  gamma + delta"
        ], HistoryPlan.Preview(commits));
    }

    [Fact]
    public void Preview_FollowsMoves()
    {
        var commits = Range("alpha", "beta");
        HistoryPlan.MoveUp(commits, 1);

        Assert.Equal(
        [
            $"{Sha("beta")[..8]}  beta",
            $"{Sha("alpha")[..8]}  alpha"
        ], HistoryPlan.Preview(commits));
    }

    [Fact]
    public void Preview_ASquashMarkWithNothingKeptBeforeItStartsItsOwnLine()
    {
        // The oldest commit is dropped, so the mark below it has no line to merge into;
        // the preview must still describe a commit rather than losing it.
        var commits = Range("alpha", "beta");
        commits[0].Drop = true;
        commits[1].SquashIntoPrevious = true;

        Assert.Equal([$"{Sha("beta")[..8]}  beta"], HistoryPlan.Preview(commits));
    }

    [Fact]
    public void Preview_EveryCommitDropped_IsEmpty()
    {
        var commits = Range("alpha", "beta");
        commits[0].Drop = true;
        commits[1].Drop = true;

        Assert.Empty(HistoryPlan.Preview(commits));
    }
}
