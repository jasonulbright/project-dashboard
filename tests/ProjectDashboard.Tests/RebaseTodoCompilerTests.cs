using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.Tests;

/// <summary>
/// The combined plan compiler: what one replay can express, and the full matrix of what it
/// cannot. Every refusal here is reached with no repository and no git process, which is the
/// property that makes an impossible combination cost nothing.
///
/// The commands and the previewed result come from one walk, so each test asserts both: a
/// preview that agreed with a different command list would be the drift this compiler exists
/// to remove.
/// </summary>
public class RebaseTodoCompilerTests
{
    private static string Sha(string name) => name.PadRight(40, '0');

    private static List<RebaseCommit> Range(params string[] names) =>
        names.Select(n => new RebaseCommit(Sha(n), n)).ToList();

    private static RebaseTodo Plan(params RebaseStep[] steps) => new() { Steps = steps };

    private static RebaseStep Pick(string name, string? message = null) =>
        new(Sha(name), RebaseStepAction.Pick, message);

    private static RebaseStep Drop(string name) => new(Sha(name), RebaseStepAction.Drop);

    private static RebaseStep Fixup(string name) => new(Sha(name), RebaseStepAction.Fixup);

    private static List<string> Rendered(RebaseTodoCompilation compiled) =>
        compiled.Commands.Select(c => c.Kind switch
        {
            RebaseCommandKind.Pick => $"pick {c.Subject}",
            RebaseCommandKind.Fixup => $"fixup {c.Subject}",
            _ => $"amend {c.Message}"
        }).ToList();

    // ── what one replay expresses ─────────────────────────────────────────

    [Fact]
    public void APlanMixingEveryKind_CompilesToOneCommandListAndOneResult()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a"), Fixup("b"), Drop("c"), Pick("e"), Pick("d", "d, reworded")),
            Range("a", "b", "c", "d", "e"));

        Assert.True(compiled.IsValid, compiled.Refusal);
        Assert.Equal(["pick a", "fixup b", "pick e", "pick d", "amend d, reworded"], Rendered(compiled));
        Assert.Equal(
            [$"{Sha("a")[..8]}  a + b", $"{Sha("e")[..8]}  e", $"{Sha("d")[..8]}  d, reworded"],
            compiled.Result.Select(r => r.Line));
    }

    [Fact]
    public void ARewordedAnchorsAmend_FollowsTheLastCommitFoldedIntoIt()
    {
        // An amend written between the pick and its folds would install a message the folds after
        // it then rewrite, so the commit would keep the anchor's original message instead.
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a", "the combined message"), Fixup("b"), Fixup("c"), Pick("d")),
            Range("a", "b", "c", "d"));

        Assert.Equal(
            ["pick a", "fixup b", "fixup c", "amend the combined message", "pick d"], Rendered(compiled));
        Assert.Equal($"{Sha("a")[..8]}  the combined message + b + c", compiled.Result[0].Line);
    }

    [Fact]
    public void ARewordedCommitWithNoFolds_IsAmendedImmediately()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a", "new a"), Drop("b"), Pick("c")), Range("a", "b", "c"));

        Assert.Equal(["pick a", "amend new a", "pick c"], Rendered(compiled));
    }

    [Fact]
    public void OnlyTheSubjectLineOfARewordReachesTheResult()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a", "the subject\n\nthe body")), Range("a"));

        Assert.Equal($"{Sha("a")[..8]}  the subject", compiled.Result[0].Line);
        // The whole message still reaches git — only the display line is the subject.
        Assert.Equal("the subject\n\nthe body", compiled.Commands[^1].Message);
    }

    [Fact]
    public void AnAbbreviatedShaResolvesAgainstTheRange()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(new RebaseStep(Sha("a")[..7], RebaseStepAction.Pick), Drop(Sha("b")[..7])),
            Range("a", "b"));

        Assert.True(compiled.IsValid, compiled.Refusal);
        Assert.Equal(["pick a"], Rendered(compiled));
    }

    // ── the impossible combinations ───────────────────────────────────────

    [Fact]
    public void AnEmptyPlan_IsRefused()
    {
        var compiled = RebaseTodoCompiler.Compile(Plan(), Range("a"));

        Assert.False(compiled.IsValid);
        Assert.Contains("the plan is empty", compiled.Refusal);
        Assert.Empty(compiled.Commands);
        Assert.Empty(compiled.Result);
    }

    [Fact]
    public void AShaOutsideTheRange_KeepsTheRangeWording()
    {
        var compiled = RebaseTodoCompiler.Compile(Plan(Pick("a"), Pick("z")), Range("a", "b"));

        Assert.Contains($"commit {Sha("z")[..8]} is not in the editable range", compiled.Refusal);
    }

    [Fact]
    public void AShaPrefixTwoCommitsShare_KeepsTheAmbiguityWording()
    {
        var range = new List<RebaseCommit>
        {
            new("abcd" + new string('1', 36), "one"),
            new("abcd" + new string('2', 36), "two")
        };

        var compiled = RebaseTodoCompiler.Compile(
            Plan(new RebaseStep("abcd", RebaseStepAction.Drop), new RebaseStep(range[1].Sha, RebaseStepAction.Pick)),
            range);

        Assert.Contains("matches more than one commit in the range — use a longer sha", compiled.Refusal);
        Assert.DoesNotContain("not in the editable range", compiled.Refusal);
    }

    [Fact]
    public void ACommitDroppedAndSquashedInTheSamePlan_IsRefusedNamingBothMarks()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a"), Drop("b"), Fixup("b")), Range("a", "b"));

        Assert.False(compiled.IsValid);
        Assert.Contains($"lists commit {Sha("b")[..8]} twice — as a drop and as a squash", compiled.Refusal);
    }

    [Fact]
    public void ACommitListedTwiceAsAPick_IsRefused()
    {
        var compiled = RebaseTodoCompiler.Compile(Plan(Pick("a"), Pick("a")), Range("a", "b"));

        Assert.Contains($"lists commit {Sha("a")[..8]} twice — as a pick and as a pick", compiled.Refusal);
    }

    [Fact]
    public void APlanThatDoesNotCoverItsRange_IsRefused()
    {
        // A commit the plan never named would leave the branch as silently as a drop.
        var compiled = RebaseTodoCompiler.Compile(Plan(Pick("a"), Pick("b")), Range("a", "b", "c"));

        Assert.Contains("lists 2 of the 3 commit(s) in the range", compiled.Refusal);
        Assert.Contains("dropped ones included", compiled.Refusal);
    }

    [Fact]
    public void DroppingEveryCommit_KeepsTheResetWording()
    {
        var compiled = RebaseTodoCompiler.Compile(Plan(Drop("a"), Drop("b")), Range("a", "b"));

        Assert.Contains("empty the branch — use a reset instead", compiled.Refusal);
    }

    [Fact]
    public void AFixupFirstInTheOrder_IsRefusedBecauseNothingPrecedesIt()
    {
        var compiled = RebaseTodoCompiler.Compile(Plan(Fixup("b"), Pick("a")), Range("a", "b"));

        Assert.Contains($"folds commit {Sha("b")[..8]} into the commit before it, but puts it first", compiled.Refusal);
    }

    [Fact]
    public void AFixupOntoADroppedAnchor_IsRefusedNamingTheAnchor()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a"), Drop("b"), Fixup("c")), Range("a", "b", "c"));

        Assert.Contains(
            $"folds commit {Sha("c")[..8]} into {Sha("b")[..8]}, which the same plan drops", compiled.Refusal);
        Assert.Contains("a dropped commit cannot be a squash anchor", compiled.Refusal);
    }

    [Fact]
    public void AFixupOntoAFixup_IsAccepted_BecauseTheRunEndsAtAPick()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a"), Fixup("b"), Fixup("c")), Range("a", "b", "c"));

        Assert.True(compiled.IsValid, compiled.Refusal);
        Assert.Equal($"{Sha("a")[..8]}  a + b + c", Assert.Single(compiled.Result).Line);
    }

    [Fact]
    public void ARewordOnADroppedCommit_IsRefused()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a"), new RebaseStep(Sha("b"), RebaseStepAction.Drop, "a message nothing writes")),
            Range("a", "b"));

        Assert.Contains($"rewords commit {Sha("b")[..8]} and drops it", compiled.Refusal);
        Assert.Contains("a dropped commit has no message to set", compiled.Refusal);
    }

    [Fact]
    public void ARewordOnAFoldedCommit_IsRefusedWithTheAnchorAdvice()
    {
        var compiled = RebaseTodoCompiler.Compile(
            Plan(Pick("a"), new RebaseStep(Sha("b"), RebaseStepAction.Fixup, "a message the fold discards")),
            Range("a", "b"));

        Assert.Contains($"rewords commit {Sha("b")[..8]} and folds it into the commit before it", compiled.Refusal);
        Assert.Contains("set the message on the commit it folds into", compiled.Refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    public void AnEmptyReword_KeepsTheEmptyMessageWording(string message)
    {
        var compiled = RebaseTodoCompiler.Compile(Plan(Pick("a", message)), Range("a"));

        Assert.Equal("a commit message cannot be empty", compiled.Refusal);
    }
}
