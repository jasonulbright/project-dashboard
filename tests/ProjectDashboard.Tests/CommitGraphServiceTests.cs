using System.Text;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Topological paging, decorations, and lane assignment for the graph view (L-11).</summary>
public class CommitGraphServiceTests
{
    private readonly GitService _git = new();
    private readonly CommitGraphService _graph;

    public CommitGraphServiceTests() => _graph = new CommitGraphService(_git);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static async Task<TempRepo> NewRepoAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        return repo;
    }

    private static async Task<string> CommitAsync(TempRepo repo, string subject)
    {
        repo.WriteFile(subject + ".txt", subject + "\n");
        await repo.CommitAllAsync(subject);
        return await repo.HeadShaAsync();
    }

    private static GraphCommit Find(CommitGraphPage page, string subject) =>
        page.Commits.Single(c => c.Subject == subject);

    private static int LaneOf(CommitGraphPage page, string subject) => Find(page, subject).Lane;

    // ── Shapes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Linear_KeepsEveryCommitOnLaneZero()
    {
        using var repo = await NewRepoAsync("g-linear");
        await CommitAsync(repo, "A");
        await CommitAsync(repo, "B");
        await CommitAsync(repo, "C");

        var page = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(["C", "B", "A"], page.Commits.Select(c => c.Subject));
        Assert.All(page.Commits, c => Assert.Equal(0, c.Lane));
        Assert.Equal(1, page.LaneCount);
        Assert.False(page.HasMore);
        Assert.True(Find(page, "A").IsRoot);
    }

    [Fact]
    public async Task SingleMerge_PutsTheSecondParentOnItsOwnLane()
    {
        using var repo = await NewRepoAsync("g-merge");
        await CommitAsync(repo, "A");
        await repo.GitAsync("switch", "-c", "side");
        await CommitAsync(repo, "C");
        await repo.GitAsync("switch", "main");
        await CommitAsync(repo, "B");
        await repo.GitAsync("merge", "--no-ff", "-m", "M", "side");

        var page = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(4, page.Commits.Count);

        var merge = Find(page, "M");
        Assert.True(merge.IsMerge);
        Assert.Equal(0, merge.Lane);
        // The first parent stays in the merge's lane; the second opens the next one.
        Assert.Equal(0, LaneOf(page, "B"));
        Assert.Equal(1, LaneOf(page, "C"));
        Assert.Equal(Find(page, "B").Sha, merge.Parents[0]);
        Assert.Equal(Find(page, "C").Sha, merge.Parents[1]);
        Assert.Equal(0, LaneOf(page, "A"));
        Assert.Equal(2, page.LaneCount);
    }

    [Fact]
    public async Task Octopus_GivesEachAdditionalParentItsOwnLane()
    {
        using var repo = await NewRepoAsync("g-octopus");
        await CommitAsync(repo, "A");
        await repo.GitAsync("switch", "-c", "b1");
        await CommitAsync(repo, "B");
        await repo.GitAsync("switch", "main");
        await repo.GitAsync("switch", "-c", "b2");
        await CommitAsync(repo, "C");
        await repo.GitAsync("switch", "main");
        await repo.GitAsync("switch", "-c", "b3");
        await CommitAsync(repo, "D");
        await repo.GitAsync("switch", "main");
        await repo.GitAsync("merge", "--no-ff", "-m", "M", "b1", "b2", "b3");

        var page = await _graph.GetGraphAsync(repo.Path);
        var merge = Find(page, "M");
        Assert.Equal(4, merge.Parents.Count);
        Assert.Equal(0, merge.Lane);

        // Parents[0] is main's previous tip (A) and keeps lane 0; the branch tips fan out.
        Assert.Equal(Find(page, "A").Sha, merge.Parents[0]);
        Assert.Equal(0, LaneOf(page, "A"));
        Assert.Equal(1, LaneOf(page, "B"));
        Assert.Equal(2, LaneOf(page, "C"));
        Assert.Equal(3, LaneOf(page, "D"));
        Assert.Equal(4, page.LaneCount);
    }

    [Fact]
    public async Task CrissCross_OpensAThirdLaneForTheSecondMergeTip()
    {
        using var repo = await NewRepoAsync("g-criss");
        await CommitAsync(repo, "A");
        await repo.GitAsync("switch", "-c", "x");
        var b = await CommitAsync(repo, "B");
        await repo.GitAsync("switch", "main");
        await repo.GitAsync("switch", "-c", "y");
        var c = await CommitAsync(repo, "C");
        await repo.GitAsync("switch", "x");
        await repo.GitAsync("merge", "--no-ff", "-m", "M1", c);
        await repo.GitAsync("switch", "y");
        await repo.GitAsync("merge", "--no-ff", "-m", "M2", b);

        var page = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(5, page.Commits.Count);

        // Both merge tips precede B and C in topo order; which tip git emits first is its
        // choice, and the assignment is stated relative to that.
        var merges = page.Commits.Where(x => x.IsMerge).ToList();
        Assert.Equal(2, merges.Count);
        var first = merges[0];
        var second = merges[1];

        Assert.Equal(0, first.Lane);
        Assert.Equal(2, second.Lane);
        Assert.Equal(0, page.Commits.Single(x => x.Sha == first.Parents[0]).Lane);
        Assert.Equal(1, page.Commits.Single(x => x.Sha == second.Parents[0]).Lane);
        Assert.Equal(0, LaneOf(page, "A"));
        Assert.Equal(3, page.LaneCount);
    }

    [Fact]
    public async Task OrphanRootBranch_ReusesLaneZeroOnceTheFirstLineEnds()
    {
        using var repo = await NewRepoAsync("g-orphan");
        await CommitAsync(repo, "A");
        await CommitAsync(repo, "B");
        await repo.GitAsync("switch", "--orphan", "solo");
        await CommitAsync(repo, "S");

        var page = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(3, page.Commits.Count);
        Assert.True(Find(page, "S").IsRoot);
        Assert.True(Find(page, "A").IsRoot);
        // --topo-order never intermixes independent lines, so the second root always lands
        // after the first line has released lane 0.
        Assert.All(page.Commits, x => Assert.Equal(0, x.Lane));
        Assert.Equal(1, page.LaneCount);
    }

    [Fact]
    public async Task Decorations_ClassifyHeadLocalBranchAndTag()
    {
        using var repo = await NewRepoAsync("g-decor");
        await CommitAsync(repo, "A");
        await repo.GitAsync("tag", "-a", "v1", "-m", "release one");
        await CommitAsync(repo, "B");
        await repo.GitAsync("branch", "feature");

        var page = await _graph.GetGraphAsync(repo.Path);
        var tip = Find(page, "B");
        Assert.Contains(new GraphRef(GraphRefKind.Head, "HEAD"), tip.Refs);
        Assert.Contains(new GraphRef(GraphRefKind.LocalBranch, "main"), tip.Refs);
        Assert.Contains(new GraphRef(GraphRefKind.LocalBranch, "feature"), tip.Refs);

        var tagged = Find(page, "A");
        Assert.Contains(new GraphRef(GraphRefKind.Tag, "v1"), tagged.Refs);
        Assert.DoesNotContain(tagged.Refs, r => r.Kind == GraphRefKind.LocalBranch);
    }

    [Fact]
    public async Task DetachedHead_IsIncludedInTheDefaultRefSetAndDecorated()
    {
        using var repo = await NewRepoAsync("g-detach");
        await CommitAsync(repo, "A");
        await CommitAsync(repo, "B");
        await repo.GitAsync("checkout", "--detach", "HEAD~1");
        await CommitAsync(repo, "D");

        var page = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(3, page.Commits.Count);

        var detached = Find(page, "D");
        Assert.Contains(new GraphRef(GraphRefKind.Head, "HEAD"), detached.Refs);

        // Two tips share one root: the tip git emits first takes lane 0, the other lane 1,
        // and the shared root collapses back to lane 0.
        var tips = page.Commits.Where(x => x.Subject is "B" or "D").ToList();
        Assert.Equal(0, tips[0].Lane);
        Assert.Equal(1, tips[1].Lane);
        Assert.Equal(0, LaneOf(page, "A"));
        Assert.Equal(2, page.LaneCount);
    }

    [Fact]
    public async Task SingleBranchMode_ExcludesCommitsOffThatBranch()
    {
        using var repo = await NewRepoAsync("g-single");
        await CommitAsync(repo, "A");
        await repo.GitAsync("switch", "-c", "side");
        await CommitAsync(repo, "C");
        await repo.GitAsync("switch", "main");
        await CommitAsync(repo, "B");

        var mainOnly = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Branch = "main" });
        Assert.Equal(["B", "A"], mainOnly.Commits.Select(x => x.Subject));
        Assert.Equal(1, mainOnly.LaneCount);

        var everything = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(3, everything.Commits.Count);
    }

    [Fact]
    public async Task ExplicitRefSet_LimitsTheWalkToThoseRevisions()
    {
        using var repo = await NewRepoAsync("g-refset");
        await CommitAsync(repo, "A");
        await repo.GitAsync("switch", "-c", "side");
        await CommitAsync(repo, "C");
        await repo.GitAsync("switch", "main");
        await CommitAsync(repo, "B");

        var page = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Refs = ["side"] });
        Assert.Equal(["C", "A"], page.Commits.Select(x => x.Subject));
    }

    [Fact]
    public async Task RepoWithoutCommits_ReturnsAnEmptyPage()
    {
        using var repo = await NewRepoAsync("g-empty");

        var page = await _graph.GetGraphAsync(repo.Path);
        Assert.Empty(page.Commits);
        Assert.False(page.HasMore);
        Assert.Equal(0, page.LaneCount);
    }

    // ── Paging ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Paging_CoversTheTopoOrderWithoutGapsOrOverlap()
    {
        using var repo = await NewRepoAsync("g-page");
        for (var i = 1; i <= 10; i++) await CommitAsync(repo, $"c{i:00}");

        var whole = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Take = 50 });
        Assert.Equal(10, whole.Commits.Count);

        var seen = new List<string>();
        for (var skip = 0; skip < 12; skip += 4)
        {
            var page = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Skip = skip, Take = 4 });
            Assert.Equal(skip, page.Skip);
            seen.AddRange(page.Commits.Select(c => c.Sha));
        }
        Assert.Equal(whole.Commits.Select(c => c.Sha), seen);
    }

    [Fact]
    public async Task Paging_ReportsHasMoreUntilTheLastPage()
    {
        using var repo = await NewRepoAsync("g-hasmore");
        for (var i = 1; i <= 6; i++) await CommitAsync(repo, $"c{i}");

        Assert.True((await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Skip = 0, Take = 4 })).HasMore);

        var last = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Skip = 4, Take = 4 });
        Assert.Equal(2, last.Commits.Count);
        Assert.False(last.HasMore);

        var past = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Skip = 20, Take = 4 });
        Assert.Empty(past.Commits);
        Assert.False(past.HasMore);
    }

    [Fact]
    public async Task Paging_KeepsEveryCommitInTheSameLaneAcrossPages()
    {
        using var repo = await NewRepoAsync("g-page-lanes");
        await CommitAsync(repo, "A");
        await CommitAsync(repo, "B");
        await repo.GitAsync("switch", "-c", "side", "HEAD~1");
        await CommitAsync(repo, "C");
        await CommitAsync(repo, "D");
        await repo.GitAsync("switch", "main");
        await repo.GitAsync("merge", "--no-ff", "-m", "M", "side");
        await CommitAsync(repo, "E");

        var whole = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Take = 50 });
        Assert.Equal(6, whole.Commits.Count);
        var expected = whole.Commits.ToDictionary(c => c.Sha, c => c.Lane);
        Assert.Contains(expected.Values, lane => lane == 1);

        for (var skip = 0; skip < 6; skip += 2)
        {
            var page = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Skip = skip, Take = 2 });
            foreach (var commit in page.Commits)
                Assert.Equal(expected[commit.Sha], commit.Lane);
        }
    }

    // ── Bounded default ──────────────────────────────────────────────────────

    [Fact]
    public void Request_NormalizesTakeToTheDefaultAndTheCeiling()
    {
        Assert.Equal(CommitGraphRequest.DefaultTake, new CommitGraphRequest().NormalizedTake);
        Assert.Equal(CommitGraphRequest.DefaultTake, new CommitGraphRequest { Take = 0 }.NormalizedTake);
        Assert.Equal(CommitGraphRequest.DefaultTake, new CommitGraphRequest { Take = -5 }.NormalizedTake);
        Assert.Equal(CommitGraphRequest.MaxTake, new CommitGraphRequest { Take = CommitGraphRequest.MaxTake + 1 }.NormalizedTake);
        Assert.Equal(0, new CommitGraphRequest { Skip = -3 }.NormalizedSkip);
    }

    [Fact]
    public async Task DefaultRequest_StopsAtTheDefaultTakeAndReportsMore()
    {
        const int total = CommitGraphRequest.DefaultTake + 25;
        using var repo = await NewRepoAsync("g-cap");
        await ImportLinearHistoryAsync(repo, total);

        var capped = await _graph.GetGraphAsync(repo.Path);
        Assert.Equal(CommitGraphRequest.DefaultTake, capped.Commits.Count);
        Assert.True(capped.HasMore);
        Assert.Equal("commit 1", capped.Commits[0].Subject);

        var opted = await _graph.GetGraphAsync(repo.Path, new CommitGraphRequest { Take = total });
        Assert.Equal(total, opted.Commits.Count);
        Assert.False(opted.HasMore);
    }

    /// <summary>Builds <paramref name="count"/> linear commits on main in one fast-import stream.</summary>
    private static async Task ImportLinearHistoryAsync(TempRepo repo, int count)
    {
        var stream = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            var blobMark = i * 2 - 1;
            var commitMark = i * 2;
            var content = $"revision {i}\n";
            stream.Append($"blob\nmark :{blobMark}\ndata {content.Length}\n{content}");
            // Newest first in topo order: commit N is the tip, so subjects count down.
            var message = $"commit {count - i + 1}\n";
            stream.Append($"commit refs/heads/main\nmark :{commitMark}\n");
            stream.Append($"author Fixture <fixture@example.invalid> {1700000000 + i} +0000\n");
            stream.Append($"committer Fixture <fixture@example.invalid> {1700000000 + i} +0000\n");
            stream.Append($"data {message.Length}\n{message}");
            if (i > 1) stream.Append($"from :{(i - 1) * 2}\n");
            stream.Append($"M 100644 :{blobMark} file.txt\n\n");
        }
        await Git.RunWithStdinAsync(repo.Path, stream.ToString(), "fast-import", "--quiet");
    }

    // ── Parsing and lane assignment ──────────────────────────────────────────

    [Fact]
    public void ParseDecoration_SplitsHeadArrowBranchesRemotesAndTags()
    {
        var refs = CommitGraphService.ParseDecoration(
            "HEAD -> refs/heads/main, refs/heads/feature, refs/remotes/origin/main, tag: refs/tags/v1.2.0, refs/stash");

        Assert.Equal(
        [
            new GraphRef(GraphRefKind.Head, "HEAD"),
            new GraphRef(GraphRefKind.LocalBranch, "main"),
            new GraphRef(GraphRefKind.LocalBranch, "feature"),
            new GraphRef(GraphRefKind.RemoteBranch, "origin/main"),
            new GraphRef(GraphRefKind.Tag, "v1.2.0"),
            new GraphRef(GraphRefKind.Other, "refs/stash")
        ], refs);

        Assert.Empty(CommitGraphService.ParseDecoration(""));
        Assert.Equal([new GraphRef(GraphRefKind.Head, "HEAD")], CommitGraphService.ParseDecoration("HEAD"));
    }

    [Fact]
    public void ParseLog_KeepsASubjectContainingTheFieldSeparator()
    {
        const string sha = "1111111111111111111111111111111111111111";
        var line = string.Join('\u001f',
            sha, "1111111", "2222222222222222222222222222222222222222 3333333333333333333333333333333333333333",
            "Author Name", "2026-01-02T03:04:05+00:00", "HEAD -> refs/heads/main", "subject\u001fwith separator");

        var commit = Assert.Single(CommitGraphService.ParseLog(line + "\n"));
        Assert.Equal(sha, commit.Sha);
        Assert.Equal(2, commit.Parents.Count);
        Assert.True(commit.IsMerge);
        Assert.Equal("subject\u001fwith separator", commit.Subject);
    }

    [Fact]
    public void AssignLanes_IgnoresEverythingAfterTheRowItAssigns()
    {
        // Diamond with a trailing extra branch, in a fixed topo order.
        List<GraphCommit> Build() =>
        [
            new() { Sha = "m", Parents = ["b", "c"] },
            new() { Sha = "c", Parents = ["a"] },
            new() { Sha = "b", Parents = ["a"] },
            new() { Sha = "a", Parents = [] },
            new() { Sha = "z", Parents = [] }
        ];

        var full = Build();
        CommitGraphService.AssignLanes(full);
        Assert.Equal([0, 1, 0, 0, 0], full.Select(c => c.Lane));
        Assert.Equal([0, 1], full[0].OpenLanes);
        Assert.Empty(full[^1].OpenLanes);

        // The same prefix walked alone lands on the same lanes: no row depends on a later one.
        var prefix = Build().Take(3).ToList();
        CommitGraphService.AssignLanes(prefix);
        Assert.Equal(full.Take(3).Select(c => c.Lane), prefix.Select(c => c.Lane));
    }
}
