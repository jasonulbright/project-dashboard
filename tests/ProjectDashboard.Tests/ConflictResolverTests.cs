using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.Tests;

/// <summary>
/// The sequencer driver against repositories git itself left conflicted: what the index says, what
/// the stage previews render, what a recorded resolution does to the index, and where each
/// continue and abort leaves the repository.
///
/// The editor pin is the load-bearing one. Every `--continue` here runs through the service, which
/// is the only path that carries it; a run without it waits on an editor no window shows and is
/// killed at its timeout, mid-sequence.
/// </summary>
public class ConflictResolverTests
{
    private readonly GitService _git = new();
    private ConflictResolver Resolver => new(_git);

    private static async Task<string> StatusAsync(TempRepo repo) =>
        await repo.GitAsync("status", "--porcelain=v2");

    /// <summary>Only the unmerged records of a status read; a staged deletion is still a record.</summary>
    private static async Task<string> UnmergedLinesAsync(TempRepo repo) =>
        string.Join('\n', (await StatusAsync(repo)).Split('\n').Where(l => l.StartsWith("u ", StringComparison.Ordinal)));

    /// <summary>The index entry one side of a conflict holds, which is what a resolution records.</summary>
    private async Task<ConflictStage?> StageOf(TempRepo repo, string path, ConflictSide side) =>
        (await Resolver.ReadUnmergedAsync(repo.Path)).ByPath[path].Stage(side);

    // ── Reading the unmerged index ──────────────────────────────────────────

    [Fact]
    public async Task ReadUnmerged_NamesTheStagesGitRecordsForEachShape()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        var read = await Resolver.ReadUnmergedAsync(repo.Path);

        Assert.Null(read.Error);
        // Both modified: all three stages.
        var both = read.ByPath["file.txt"];
        Assert.True(both.HasBase && both.HasOurs && both.HasTheirs);
        // Both added: no ancestor to compare against.
        var added = read.ByPath["added.txt"];
        Assert.False(added.HasBase);
        Assert.True(added.HasOurs && added.HasTheirs);
        // They deleted it, we modified it: no stage 3.
        var doomed = read.ByPath["doomed.txt"];
        Assert.True(doomed.HasBase && doomed.HasOurs);
        Assert.False(doomed.HasTheirs);
        Assert.False(both.IsGitlink);
    }

    [Fact]
    public void ParseUnmerged_ReadsAGitlinkFromItsMode()
    {
        var output = "160000 1111111111111111111111111111111111111111 2\tlib/dep\0" +
                     "160000 2222222222222222222222222222222222222222 3\tlib/dep\0";

        var byPath = ConflictResolver.ParseUnmerged(output);

        var entry = Assert.Single(byPath).Value;
        Assert.True(entry.IsGitlink);
        Assert.True(entry.HasOurs && entry.HasTheirs);
        Assert.False(entry.HasBase);
    }

    [Fact]
    public async Task ReadStageDiff_RendersOneStagePairAndFlagsABinaryOne()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        var text = await Resolver.ReadStageDiffAsync(repo.Path, "file.txt", ConflictSide.Base, ConflictSide.Ours);
        Assert.NotNull(text);
        Assert.False(text.IsBinary);
        Assert.Contains(text.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "base");
        Assert.Contains(text.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "ours");

        var binary = await Resolver.ReadStageDiffAsync(repo.Path, "pic.bin", ConflictSide.Ours, ConflictSide.Theirs);
        Assert.NotNull(binary);
        Assert.True(binary.IsBinary);
    }

    [Fact]
    public async Task ReadStageContent_ShowsTheWholeSideWhereTheOtherHasNone()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        var ours = await Resolver.ReadStageContentAsync(repo.Path, "added.txt", ConflictSide.Ours);

        Assert.NotNull(ours);
        Assert.Contains(ours.Lines, l => l.Text == "ours added");
    }

    // ── Recording a resolution ──────────────────────────────────────────────

    [Fact]
    public async Task TakeSide_WithContent_WritesThatSideAndLeavesThePathMerged()
    {
        using var repo = await ConflictFixtures.MergeAsync();

        var result = await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, await StageOf(repo, "file.txt", ConflictSide.Theirs));

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("theirs\n", repo.ReadFile("file.txt").Replace("\r\n", "\n"));
        Assert.DoesNotContain("u ", await StatusAsync(repo));
    }

    [Fact]
    public async Task TakeSide_ThatDeletedTheFile_RecordsTheRemovalAsTheResolution()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        // They deleted doomed.txt; taking their side is the deletion itself.
        var result = await Resolver.TakeSideAsync(repo.Path, "doomed.txt", ConflictSide.Theirs, null);

        Assert.True(result.Success, result.FirstError);
        Assert.False(repo.FileExists("doomed.txt"));
        // The path leaves the unmerged set carrying a staged deletion, which is what the side said.
        Assert.DoesNotContain("doomed.txt", await UnmergedLinesAsync(repo));
        Assert.Contains("D", await repo.GitAsync("diff", "--cached", "--name-status", "--", "doomed.txt"));
    }

    [Fact]
    public async Task StageResolved_TakesWhatTheWorkingTreeHolds()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        repo.WriteFile("file.txt", "merged by hand\n");

        var result = await Resolver.StageResolvedAsync(repo.Path, "file.txt");

        Assert.True(result.Staged);
        Assert.Null(result.Marker);
        Assert.DoesNotContain("u ", await StatusAsync(repo));
    }

    [Fact]
    public async Task StageResolved_RecordsARemovalForAPathTheResolutionDeleted()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        File.Delete(Path.Combine(repo.Path, "file.txt"));

        var result = await Resolver.StageResolvedAsync(repo.Path, "file.txt");

        Assert.True(result.Staged);
        Assert.DoesNotContain("u ", await StatusAsync(repo));
    }

    // ── The marker guard ────────────────────────────────────────────────────

    /// <summary>
    /// Fail-first: the bare `git add` the guard stands in front of DOES put a marker-carrying file
    /// in the index, which is what a continue would then commit into history.
    /// </summary>
    [Fact]
    public async Task WithoutTheGuard_AFileHoldingMarkersStagesAndTheMarkersReachTheIndex()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        Assert.Contains("<<<<<<<", repo.ReadFile("file.txt"));

        await repo.GitAsync("add", "--", "file.txt");

        Assert.DoesNotContain("u ", await StatusAsync(repo));
        Assert.Contains("<<<<<<<", await repo.GitAsync("show", ":0:file.txt"));
    }

    [Fact]
    public async Task TheGuardRefusesTheSameFileAndLeavesThePathUnmerged()
    {
        using var repo = await ConflictFixtures.MergeAsync();

        var result = await Resolver.StageResolvedAsync(repo.Path, "file.txt");

        Assert.False(result.Staged);
        Assert.StartsWith("<<<<<<<", result.Marker);
        Assert.Contains("u ", await StatusAsync(repo));
    }

    /// <summary>
    /// The scan and the stage are two reads of one file. A path whose content moves between them
    /// would otherwise reach the index unchecked, so the staged blob is compared against the one
    /// that was scanned and a mismatch is put back: unmerged again, working tree untouched.
    /// </summary>
    [Fact]
    public async Task AFileThatChangesBetweenTheScanAndTheStageIsPutBackRatherThanTrusted()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        repo.WriteFile("file.txt", "merged by hand\n");
        var git = new SwapFileOnAddGitService(Path.Combine(repo.Path, "file.txt"), "changed underneath\n");

        var result = await new ConflictResolver(git).StageResolvedAsync(repo.Path, "file.txt");

        Assert.False(result.Staged);
        Assert.True(result.ChangedWhileStaging);
        Assert.True(result.ConflictRestored);
        Assert.Contains("u ", await StatusAsync(repo));
        Assert.Equal("changed underneath\n", repo.ReadFile("file.txt").Replace("\r\n", "\n"));
    }

    /// <summary>Writes over the file just before git reads it, which is the race the guard closes.</summary>
    private sealed class SwapFileOnAddGitService(string path, string content) : GitService
    {
        private bool _swapped;

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var vector = args.ToList();
            if (!_swapped && vector.Contains("add"))
            {
                _swapped = true;
                File.WriteAllText(path, content);
            }
            return base.RunAsync(repoPath, vector, environment, ct, timeout);
        }
    }

    /// <summary>
    /// An identity git could not read is not agreement. Treated as one, the whole comparison
    /// stops guarding anything the moment a read fails.
    /// </summary>
    [Fact]
    public async Task AnUnreadableContentIdentityRefusesTheStageRatherThanPassingIt()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        repo.WriteFile("file.txt", "merged by hand\n");
        var git = new FailingReadGitService("hash-object");

        var result = await new ConflictResolver(git).StageResolvedAsync(repo.Path, "file.txt");

        Assert.False(result.Staged);
        Assert.True(result.ContentUnidentified);
        Assert.Contains("u ", await StatusAsync(repo));
    }

    [Fact]
    public async Task AnUnreadableStagedIdentityPutsTheConflictBackRatherThanClaimingItStaged()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        repo.WriteFile("file.txt", "merged by hand\n");
        var git = new FailingReadGitService("ls-files");

        var result = await new ConflictResolver(git).StageResolvedAsync(repo.Path, "file.txt");

        Assert.False(result.Staged);
        Assert.True(result.ChangedWhileStaging);
        Assert.True(result.ConflictRestored);
        Assert.Contains("u ", await StatusAsync(repo));
    }

    /// <summary>Fails one read verb outright, which is what an unreadable identity looks like.</summary>
    private sealed class FailingReadGitService(string verb) : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var vector = args.ToList();
            return vector.Contains(verb)
                ? Task.FromResult(new ProcessResult(1, "", $"{verb} refused by the test", TimedOut: false))
                : base.RunAsync(repoPath, vector, environment, ct, timeout);
        }
    }

    [Fact]
    public async Task TakeSideRecordsTheIndexsOwnBlobEvenWhenTheWorkingTreeMovesUnderIt()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var theirs = await StageOf(repo, "file.txt", ConflictSide.Theirs);
        var git = new SwapFileOnCheckoutGitService(Path.Combine(repo.Path, "file.txt"), "written by somebody else\n");

        var result = await new ConflictResolver(git).TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, theirs);

        Assert.True(result.Success, result.FirstError);
        Assert.DoesNotContain("u ", await StatusAsync(repo));
        // The index holds the side that was chosen, not what the working tree became.
        Assert.Equal("theirs\n", (await repo.GitAsync("show", ":0:file.txt")).Replace("\r\n", "\n"));
    }

    /// <summary>Writes over the file after `checkout` has written it, before the index is recorded.</summary>
    private sealed class SwapFileOnCheckoutGitService(string path, string content) : GitService
    {
        private bool _swapped;

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var vector = args.ToList();
            var result = await base.RunAsync(repoPath, vector, environment, ct, timeout);
            if (!_swapped && vector.Contains("checkout"))
            {
                _swapped = true;
                File.WriteAllText(path, content);
            }
            return result;
        }
    }

    [Fact]
    public async Task TheGuardFindsTheMarkerGitLeftInTheWorkingTree()
    {
        using var repo = await ConflictFixtures.MergeAsync();

        var found = ConflictResolver.FindConflictMarker(repo.Path, "file.txt");

        Assert.NotNull(found);
        Assert.StartsWith("<<<<<<<", found);
    }

    [Fact]
    public async Task TheGuardPassesOnceTheMarkersAreGone()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        repo.WriteFile("file.txt", "merged by hand\n");

        Assert.Null(ConflictResolver.FindConflictMarker(repo.Path, "file.txt"));
    }

    [Fact]
    public void TheGuardReadsEveryMarkerGitWrites_AndNotARuleOfEqualsSigns()
    {
        Assert.True(ConflictResolver.IsConflictMarker("<<<<<<< HEAD"));
        Assert.True(ConflictResolver.IsConflictMarker(">>>>>>> side"));
        Assert.True(ConflictResolver.IsConflictMarker("||||||| base"));
        Assert.True(ConflictResolver.IsConflictMarker("======="));
        Assert.True(ConflictResolver.IsConflictMarker("<<<<<<<"));

        Assert.False(ConflictResolver.IsConflictMarker("========================"));
        Assert.False(ConflictResolver.IsConflictMarker("<<<<<< six only"));
        Assert.False(ConflictResolver.IsConflictMarker("<<<<<<<not a marker"));
        Assert.False(ConflictResolver.IsConflictMarker("plain text"));
    }

    /// <summary>A path resolved by deleting it has no file to scan, and no markers either.</summary>
    [Fact]
    public void TheGuardPassesForAPathTheResolutionRemoved()
    {
        var missingRepo = Path.Combine(TestEnv.Root, "no-such-repo-" + Guid.NewGuid().ToString("N")[..6]);

        Assert.Null(ConflictResolver.FindConflictMarker(missingRepo, "file.txt"));
    }

    // ── Marker length is the repository's, not this app's ───────────────────

    /// <summary>
    /// A repository that raised `conflict-marker-size` gets markers longer than git's default. A
    /// run longer than the length being looked for is still a marker, so the angle-bracket forms
    /// are caught whatever length the scan was told to expect.
    /// </summary>
    [Fact]
    public async Task WideAngleMarkersAreCaughtEvenAtTheDefaultLength()
    {
        using var repo = await ConflictFixtures.WideMarkerMergeAsync();
        Assert.Contains("<<<<<<<<<<", repo.ReadFile("file.txt"));

        Assert.NotNull(ConflictResolver.FindConflictMarker(repo.Path, "file.txt", ConflictResolver.DefaultMarkerSize));
    }

    /// <summary>
    /// Fail-first for the length itself: the separator is matched whole, so a resolution that
    /// deleted the angle markers and left the separator behind is invisible to a scan fixed at
    /// git's default length — and staging it would commit that rule into the file's history.
    /// </summary>
    [Fact]
    public async Task AWideSeparatorLeftBehindIsMissedAtTheDefaultLengthAndCaughtAtTheRepositorysOwn()
    {
        using var repo = await ConflictFixtures.WideMarkerMergeAsync();
        repo.WriteFile("file.txt", $"ours\n{new string('=', 32)}\ntheirs\n");

        Assert.Null(ConflictResolver.FindConflictMarker(repo.Path, "file.txt", ConflictResolver.DefaultMarkerSize));

        var found = await Resolver.FindConflictMarkerAsync(repo.Path, "file.txt");
        Assert.Equal(new string('=', 32), found);

        var result = await Resolver.StageResolvedAsync(repo.Path, "file.txt");
        Assert.False(result.Staged);
        Assert.Contains("u ", await StatusAsync(repo));
    }

    [Fact]
    public async Task TheGuardReadsTheMarkerLengthTheRepositorySetAndRefuses()
    {
        using var repo = await ConflictFixtures.WideMarkerMergeAsync();

        Assert.Equal(32, await Resolver.MarkerSizeAsync(repo.Path, "file.txt"));
        var found = await Resolver.FindConflictMarkerAsync(repo.Path, "file.txt");
        Assert.StartsWith("<<<<<<<<", found);

        var result = await Resolver.StageResolvedAsync(repo.Path, "file.txt");
        Assert.False(result.Staged);
        Assert.Contains("u ", await StatusAsync(repo));
    }

    [Fact]
    public void MarkersAreMatchedAtTheLengthTheAttributeGives()
    {
        Assert.True(ConflictResolver.IsConflictMarker(new string('<', 32) + " HEAD", 32));
        Assert.True(ConflictResolver.IsConflictMarker(new string('=', 32), 32));
        Assert.True(ConflictResolver.IsConflictMarker(new string('>', 32) + " side", 32));
        // A run longer than the size is still a marker; one shorter is not.
        Assert.True(ConflictResolver.IsConflictMarker(new string('<', 40) + " HEAD", 32));
        Assert.False(ConflictResolver.IsConflictMarker(new string('<', 8) + " HEAD", 32));
        // A rule of equals signs under a heading is not the separator of a conflict.
        Assert.False(ConflictResolver.IsConflictMarker(new string('=', 40), 32));
    }

    [Fact]
    public void AnAttributeThatSaysNothingUsableLeavesTheDefaultLength()
    {
        Assert.Equal(32, ConflictResolver.ParseMarkerSize("file.txt: conflict-marker-size: 32\n"));
        Assert.Equal(7, ConflictResolver.ParseMarkerSize("file.txt: conflict-marker-size: unspecified\n"));
        Assert.Equal(7, ConflictResolver.ParseMarkerSize("file.txt: conflict-marker-size: 3\n"));
        Assert.Equal(7, ConflictResolver.ParseMarkerSize(""));
    }

    // ── Prepared messages ───────────────────────────────────────────────────

    [Fact]
    public async Task ThePreparedMessageComesBackWithoutTheLinesGitWroteForItsEditor()
    {
        using var repo = await ConflictFixtures.MergeAsync();

        var message = await Resolver.ReadPreparedMessageAsync(repo.Path, RepoActivity.Merging);

        Assert.Contains("Merge branch 'side'", message);
        Assert.DoesNotContain("#", message);
    }

    [Fact]
    public async Task ARebaseStopCarriesTheMessageOfTheCommitBeingReplayed()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();

        var message = await Resolver.ReadPreparedMessageAsync(repo.Path, RepoActivity.Rebasing);

        Assert.Contains("topic change", message);
    }

    [Fact]
    public void CommentLinesAreStrippedAndTheRestIsLeftAlone()
    {
        Assert.Equal("subject\n\nbody", ConflictResolver.StripCommentLines("subject\n\nbody\n# Conflicts:\n#\tf.txt\n"));
    }

    // ── Continue ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ContinuingAMergeWritesTheCommitGitPreparedAndEndsTheMerge()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Ours, await StageOf(repo, "file.txt", ConflictSide.Ours));

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.Merging, null, SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Contains("Merge branch 'side'", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    [Fact]
    public async Task ContinuingAMergeWithAnEditedMessageCommitsThatMessage()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Ours, await StageOf(repo, "file.txt", ConflictSide.Ours));

        var result = await Resolver.ContinueAsync(
            repo.Path, RepoActivity.Merging, "a message the reader wrote", SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("a message the reader wrote", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    [Fact]
    public async Task ContinuingARebaseLandsWhereTheTerminalSequenceLands()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, await StageOf(repo, "file.txt", ConflictSide.Theirs));

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.Rebasing, null, SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
        var log = await repo.GitAsync("log", "--format=%s", "-3");
        Assert.Equal(["topic change", "main change", "base"], Subjects(log));
    }

    [Fact]
    public async Task ContinuingACherryPickWritesThePickedCommit()
    {
        using var repo = await ConflictFixtures.CherryPickStopAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, await StageOf(repo, "file.txt", ConflictSide.Theirs));

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.CherryPicking, null, SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("side change", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    [Fact]
    public async Task ContinuingARevertWritesTheRevertCommit()
    {
        using var repo = await ConflictFixtures.RevertStopAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, await StageOf(repo, "file.txt", ConflictSide.Theirs));

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.Reverting, null, SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.StartsWith("Revert", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    /// <summary>
    /// A continue over an index that still holds an unmerged path is git's refusal, not a commit:
    /// the panel gates it first, and this is the proof the service does not paper over it.
    /// </summary>
    [Fact]
    public async Task ContinuingWithAConflictStillUnmergedFailsAndWritesNoCommit()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var before = await repo.HeadShaAsync();

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.Merging, null, SigningChoice.NotChosen);

        Assert.False(result.Success);
        Assert.Equal(before, await repo.HeadShaAsync());
        Assert.Equal(RepoActivity.Merging, await ActivityAsync(repo));
    }

    /// <summary>
    /// A stopped rebase holds the replayed commit's author in git's own state; nothing here may
    /// write it as somebody else's work. The edited message goes into the file the sequencer
    /// commits from, so the continue is still git's own commit.
    /// </summary>
    [Fact]
    public async Task AnEditedMessageOnARebaseKeepsTheCommitsOriginalAuthor()
    {
        using var repo = await ConflictFixtures.RebaseStopWithAuthorAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs,
            await StageOf(repo, "file.txt", ConflictSide.Theirs));

        var result = await Resolver.ContinueAsync(
            repo.Path, RepoActivity.Rebasing, "a message the reader wrote", SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("a message the reader wrote", await repo.HeadSubjectAsync());
        Assert.Equal("Original Author <original@elsewhere.invalid>",
            (await repo.GitAsync("log", "-1", "--format=%an <%ae>")).Trim());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    /// <summary>
    /// Fail-first for the same defect: a hand-made commit during the stop takes the committer as
    /// author, because a stopped rebase leaves no CHERRY_PICK_HEAD for git to read one from.
    /// </summary>
    [Fact]
    public async Task AHandMadeCommitDuringAStoppedRebaseWouldTakeTheCommitterAsAuthor()
    {
        using var repo = await ConflictFixtures.RebaseStopWithAuthorAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs,
            await StageOf(repo, "file.txt", ConflictSide.Theirs));

        await _git.RunAsync(repo.Path, ["commit", "-m", "written by hand"],
            new Dictionary<string, string> { ["GIT_EDITOR"] = "true" });

        Assert.DoesNotContain("Original Author", await repo.GitAsync("log", "-1", "--format=%an <%ae>"));
        await repo.GitAsync("rebase", "--abort");
    }

    /// <summary>
    /// A cherry-pick of several commits stopped on the first: an edited message must not end the
    /// sequence. Committing it by hand clears CHERRY_PICK_HEAD, which reads as nothing in progress
    /// while the queued picks are still there — the sequence stranded with no surface driving it.
    /// </summary>
    [Fact]
    public async Task AnEditedMessageOnAMultiPickStillReplaysTheRestOfTheSequence()
    {
        using var repo = await ConflictFixtures.MultiPickStopAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs,
            await StageOf(repo, "file.txt", ConflictSide.Theirs));

        var result = await Resolver.ContinueAsync(
            repo.Path, RepoActivity.CherryPicking, "the first pick, reworded", SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
        var subjects = Subjects(await repo.GitAsync("log", "--format=%s", "-3"));
        Assert.Equal(["second pick", "the first pick, reworded", "main change"], subjects);
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".git", "sequencer")));
        // The picked commit is still its author's work.
        Assert.Contains("Original Author",
            await repo.GitAsync("log", "-1", "--skip=1", "--format=%an"));
    }

    /// <summary>
    /// git strips comment lines from the message the sequencer commits, and writes its own advice
    /// into that message as comments. A subject that opens with an issue reference must survive
    /// that stripping, and git's advice must not survive it.
    /// </summary>
    [Fact]
    public async Task AnEditedMessageKeepsALineOpeningWithAHashAndDropsGitsOwnAdvice()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Ours,
            await StageOf(repo, "file.txt", ConflictSide.Ours));

        var result = await Resolver.ContinueAsync(
            repo.Path, RepoActivity.Merging, "#42 close the issue\n\nthe body", SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("#42 close the issue", await repo.HeadSubjectAsync());
        var message = await repo.GitAsync("log", "-1", "--format=%B");
        Assert.Contains("the body", message);
        Assert.DoesNotContain("It looks like you may be committing a merge", message);
    }

    [Fact]
    public void TheCommentCharacterIsOneNoLineOfTheMessageOpensWith()
    {
        Assert.Equal('#', ConflictResolver.CommentCharFor("an ordinary subject\n\nand a body"));
        Assert.Equal(';', ConflictResolver.CommentCharFor("#42 the subject"));
        Assert.Equal('@', ConflictResolver.CommentCharFor("#42 the subject\n; and a line like this"));
        // Leading whitespace does not hide the character a line starts with.
        Assert.Equal(';', ConflictResolver.CommentCharFor("subject\n\n   # an indented hash line"));
    }

    [Fact]
    public async Task AResolutionThatEmptiesTheReplayedCommitIsKnownBeforeTheContinueRuns()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();
        // Taking the side already on the branch leaves the replayed commit with nothing of its own.
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Ours,
            await StageOf(repo, "file.txt", ConflictSide.Ours));

        Assert.True(await Resolver.ContinueWouldRecordNothingAsync(repo.Path));
    }

    [Fact]
    public async Task AResolutionThatRecordsSomethingIsNotReportedAsEmpty()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs,
            await StageOf(repo, "file.txt", ConflictSide.Theirs));

        Assert.False(await Resolver.ContinueWouldRecordNothingAsync(repo.Path));
    }

    [Fact]
    public async Task ContinuingWhatIsNotRunningIsRefusedWithoutTouchingTheRepository()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("conflict-idle");

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.None, null, SigningChoice.NotChosen);

        Assert.False(result.Success);
        Assert.Contains("no operation to continue", result.FirstError);
    }

    // ── Abort ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbortingAMergePutsTheRepositoryBackWhereItStarted()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        var before = await repo.HeadShaAsync();

        var result = await Resolver.AbortAsync(repo.Path, RepoActivity.Merging);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal(before, await repo.HeadShaAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
        Assert.Equal("ours\n", repo.ReadFile("file.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task AbortingARebaseRestoresTheBranchItWasReplaying()
    {
        using var repo = await ConflictFixtures.RebaseStopAsync();

        var result = await Resolver.AbortAsync(repo.Path, RepoActivity.Rebasing);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
        Assert.Equal("topic change", await repo.HeadSubjectAsync());
    }

    private async Task<RepoActivity> ActivityAsync(TempRepo repo) =>
        (await _git.GetWorkingStateAsync(repo.Path))?.Activity ?? RepoActivity.None;

    private static string[] Subjects(string log) =>
        [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())];
}
