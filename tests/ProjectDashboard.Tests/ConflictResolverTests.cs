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

        var result = await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, sideHasContent: true);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("theirs\n", repo.ReadFile("file.txt").Replace("\r\n", "\n"));
        Assert.DoesNotContain("u ", await StatusAsync(repo));
    }

    [Fact]
    public async Task TakeSide_ThatDeletedTheFile_RecordsTheRemovalAsTheResolution()
    {
        using var repo = await ConflictFixtures.RichMergeAsync();

        // They deleted doomed.txt; taking their side is the deletion itself.
        var result = await Resolver.TakeSideAsync(repo.Path, "doomed.txt", ConflictSide.Theirs, sideHasContent: false);

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
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Ours, sideHasContent: true);

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.Merging, null, SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Contains("Merge branch 'side'", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    [Fact]
    public async Task ContinuingAMergeWithAnEditedMessageCommitsThatMessage()
    {
        using var repo = await ConflictFixtures.MergeAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Ours, sideHasContent: true);

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
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, sideHasContent: true);

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
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, sideHasContent: true);

        var result = await Resolver.ContinueAsync(repo.Path, RepoActivity.CherryPicking, null, SigningChoice.NotChosen);

        Assert.True(result.Success, result.FirstError);
        Assert.Equal("side change", await repo.HeadSubjectAsync());
        Assert.Equal(RepoActivity.None, await ActivityAsync(repo));
    }

    [Fact]
    public async Task ContinuingARevertWritesTheRevertCommit()
    {
        using var repo = await ConflictFixtures.RevertStopAsync();
        await Resolver.TakeSideAsync(repo.Path, "file.txt", ConflictSide.Theirs, sideHasContent: true);

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
