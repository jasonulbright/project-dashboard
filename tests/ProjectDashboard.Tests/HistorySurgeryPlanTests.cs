using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The pure history planner: moves and marks in, the one combined todo the driver receives out.
/// No repository is touched here — everything the planner decides is decided before a gated
/// operation exists, including every combination it refuses.
/// </summary>
public class HistorySurgeryPlanTests
{
    /// <summary>Commits, oldest first, with recognisable 40-character shas.</summary>
    private static List<PlannedCommit> Range(params string[] names) =>
        names.Select(n => new PlannedCommit { Sha = Sha(n), Subject = n }).ToList();

    private static string Sha(string name) => name.PadRight(40, '0');

    private static List<string> Order(List<PlannedCommit> commits) => commits.Select(c => c.Sha).ToList();

    /// <summary>The todo as "action subject" pairs, which is what the commands actually assert.</summary>
    private static List<string> Steps(RebaseTodo todo, IReadOnlyList<PlannedCommit> planned)
    {
        var subjects = planned.ToDictionary(p => p.Sha, p => p.Subject, StringComparer.OrdinalIgnoreCase);
        return todo.Steps.Select(s => $"{s.Action.ToString().ToLowerInvariant()} {subjects[s.Sha]}").ToList();
    }

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

    [Fact]
    public void AReword_ClearsTheDropAndSquashMarks_AndIsClearedByThem()
    {
        // A dropped commit's message is never written and a folded one's is discarded by the
        // fold, so a plan may never carry a reword beside either mark.
        var commit = new PlannedCommit { Sha = Sha("a"), Subject = "a" };

        commit.Drop = true;
        commit.NewMessage = "a rewritten subject";
        Assert.False(commit.Drop);
        Assert.Equal("reword", commit.MarkLabel);
        Assert.Equal("a rewritten subject", commit.EffectiveSubject);

        commit.SquashIntoPrevious = true;
        Assert.Null(commit.NewMessage);
        Assert.Equal("a", commit.EffectiveSubject);

        commit.NewMessage = "another subject";
        Assert.False(commit.SquashIntoPrevious);
        commit.Drop = true;
        Assert.Null(commit.NewMessage);
        Assert.Equal("drop", commit.MarkLabel);
    }

    [Fact]
    public void ARewordsSubject_IsItsFirstLine()
    {
        var commit = new PlannedCommit { Sha = Sha("a"), Subject = "a" };
        commit.NewMessage = "the subject\n\nthe body, which no list line shows";

        Assert.Equal("the subject", commit.EffectiveSubject);
        Assert.Equal($"{Sha("a")[..8]}  the subject", Assert.Single(HistoryPlan.Preview([commit])));
    }

    // ── resolution ─────────────────────────────────────────────────────────

    [Fact]
    public void UntouchedPlan_ResolvesToNothingToApply()
    {
        var commits = Range("a", "b", "c");
        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Null(resolution.Todo);
        Assert.Contains("Nothing to apply", resolution.Refusal);
        // The history is unchanged, not absent: the preview still describes what is there.
        Assert.Equal(3, resolution.Preview.Count);
        Assert.Equal("no change", resolution.Scope.Summary);
    }

    [Fact]
    public void MovesOnly_ResolveToATodoListingEveryCommitInTheNewOrder()
    {
        var commits = Range("a", "b", "c");
        var original = Order(commits);
        HistoryPlan.MoveUp(commits, 2);

        var resolution = HistoryPlan.Resolve(commits, original);

        Assert.True(resolution.IsValid);
        Assert.Equal(["pick a", "pick c", "pick b"], Steps(resolution.Todo!, commits));
        Assert.Equal("order changed", resolution.Scope.Summary);
    }

    [Fact]
    public void DropsOnly_ResolveToATodoThatStillListsThem()
    {
        var commits = Range("a", "b", "c", "d");
        commits[1].Drop = true;
        commits[3].Drop = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        // Every commit is listed, dropped ones included: a todo that named only the survivors
        // could not say whether a missing commit was dropped or lost.
        Assert.Equal(["pick a", "drop b", "pick c", "drop d"], Steps(resolution.Todo!, commits));
        Assert.Equal("2 commit(s) dropped", resolution.Scope.Summary);
        Assert.Equal(2, resolution.Preview.Count);
    }

    [Fact]
    public void SquashMark_FoldsIntoTheCommitAboveIt()
    {
        var commits = Range("a", "b", "c");
        commits[2].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.Equal(["pick a", "pick b", "fixup c"], Steps(resolution.Todo!, commits));
        Assert.Equal("1 commit(s) squashed", resolution.Scope.Summary);
        Assert.Equal([$"{Sha("a")[..8]}  a", $"{Sha("b")[..8]}  b + c"], resolution.Preview);
    }

    [Fact]
    public void ConsecutiveSquashMarks_ShareOneAnchor()
    {
        var commits = Range("a", "b", "c", "d");
        commits[2].SquashIntoPrevious = true;
        commits[3].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.Equal(["pick a", "pick b", "fixup c", "fixup d"], Steps(resolution.Todo!, commits));
        Assert.Equal([$"{Sha("a")[..8]}  a", $"{Sha("b")[..8]}  b + c + d"], resolution.Preview);
    }

    [Fact]
    public void SquashRunsSeparatedByAGap_EachFoldIntoTheirOwnAnchor()
    {
        var commits = Range("a", "b", "c", "d", "e");
        commits[1].SquashIntoPrevious = true;
        commits[4].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.True(resolution.IsValid);
        Assert.Equal(["pick a", "fixup b", "pick c", "pick d", "fixup e"], Steps(resolution.Todo!, commits));
        Assert.Equal(
            [$"{Sha("a")[..8]}  a + b", $"{Sha("c")[..8]}  c", $"{Sha("d")[..8]}  d + e"], resolution.Preview);
    }

    [Fact]
    public void AdjacentSquashRuns_StayTwoCommits_RatherThanCollapsingIntoOneFold()
    {
        // {a,b} and {c,d} were previously handed to a driver as one sha list, which it read as a
        // single fold set anchored on "a" — two previewed commits, one produced. Positions in one
        // todo cannot say that: each fixup folds into the pick above it and nothing else.
        var commits = Range("a", "b", "c", "d");
        commits[1].SquashIntoPrevious = true;
        commits[3].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.True(resolution.IsValid);
        Assert.Equal(["pick a", "fixup b", "pick c", "fixup d"], Steps(resolution.Todo!, commits));
        Assert.Equal([$"{Sha("a")[..8]}  a + b", $"{Sha("c")[..8]}  c + d"], resolution.Preview);
    }

    [Fact]
    public void ARewordedCommit_CarriesItsMessageOnItsOwnStep()
    {
        var commits = Range("a", "b");
        commits[1].NewMessage = "b, said differently";

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.Equal("1 commit(s) reworded", resolution.Scope.Summary);
        Assert.Equal("b, said differently", resolution.Todo!.Steps[1].NewMessage);
        Assert.Null(resolution.Todo.Steps[0].NewMessage);
        Assert.Equal($"{Sha("b")[..8]}  b, said differently", resolution.Preview[1]);
    }

    [Fact]
    public void AllFourKindsTogether_ResolveToOneTodoAndOneHonestSummary()
    {
        var commits = Range("a", "b", "c", "d", "e");
        var original = Order(commits);
        HistoryPlan.MoveUp(commits, 4);            // e ahead of d
        commits.Single(c => c.Sha == Sha("b")).SquashIntoPrevious = true;
        commits.Single(c => c.Sha == Sha("c")).Drop = true;
        commits.Single(c => c.Sha == Sha("d")).NewMessage = "d, reworded";

        var resolution = HistoryPlan.Resolve(commits, original);

        Assert.True(resolution.IsValid);
        Assert.Equal(["pick a", "fixup b", "drop c", "pick e", "pick d"], Steps(resolution.Todo!, commits));
        Assert.Equal(
            "1 commit(s) dropped, 1 commit(s) squashed, 1 commit(s) reworded, order changed",
            resolution.Scope.Summary);
        Assert.Equal(
            [$"{Sha("a")[..8]}  a + b", $"{Sha("e")[..8]}  e", $"{Sha("d")[..8]}  d, reworded"],
            resolution.Preview);
    }

    // ── the combinations no replay can express ─────────────────────────────

    [Fact]
    public void DroppingEveryCommit_IsRefusedWithTheResetAdvice()
    {
        var commits = Range("a", "b");
        commits[0].Drop = true;
        commits[1].Drop = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Null(resolution.Todo);
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

    [Fact]
    public void AReorderThatMovesASquashMarkToTheFront_IsRefusedRatherThanFoldingIntoNothing()
    {
        var commits = Range("a", "b", "c");
        var original = Order(commits);
        commits[2].SquashIntoPrevious = true;
        HistoryPlan.MoveUp(commits, 2);
        HistoryPlan.MoveUp(commits, 1);

        var resolution = HistoryPlan.Resolve(commits, original);

        Assert.False(resolution.IsValid);
        Assert.Contains("nothing before it", resolution.Refusal);
        Assert.Empty(resolution.Preview);
    }

    [Fact]
    public void ASquashWhoseAnchorTheSamePlanDrops_IsRefusedNamingBoth()
    {
        // Retargeting the fold onto whichever commit survives above it would apply a history the
        // reader never planned; losing the marked commit would lose its changes outright.
        var commits = Range("a", "b", "c");
        commits[1].Drop = true;
        commits[2].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Contains($"folds commit {Sha("c")[..8]} into {Sha("b")[..8]}", resolution.Refusal);
        Assert.Contains("a dropped commit cannot be a squash anchor", resolution.Refusal);
        Assert.Empty(resolution.Preview);
    }

    [Fact]
    public void ASquashBelowADroppedOldestCommit_IsRefusedRatherThanStartingItsOwnLine()
    {
        // The mark below a dropped oldest commit has nothing to fold into. Showing it as its own
        // commit would preview a history whose first commit keeps a message the fold discards.
        var commits = Range("alpha", "beta");
        commits[0].Drop = true;
        commits[1].SquashIntoPrevious = true;

        var resolution = HistoryPlan.Resolve(commits, Order(commits));

        Assert.False(resolution.IsValid);
        Assert.Contains("a dropped commit cannot be a squash anchor", resolution.Refusal);
        Assert.Empty(HistoryPlan.Preview(commits));
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
    public void Preview_EveryCommitDropped_IsEmpty()
    {
        var commits = Range("alpha", "beta");
        commits[0].Drop = true;
        commits[1].Drop = true;

        Assert.Empty(HistoryPlan.Preview(commits));
    }
}
