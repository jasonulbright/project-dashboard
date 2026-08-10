using System.Diagnostics;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The bounded walk. Depth decides how far down a repository is still found; the guards decide
/// that the walk finishes at all. A walk that stops early has to say so — a floor presented as a
/// total hides repositories with nothing on screen to suggest any are missing.
/// </summary>
public class RepositoryWalkTests
{
    private static readonly WalkLimits Generous = new(10_000, TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(1, new[] { "top" })]
    [InlineData(2, new[] { "top", "site" })]
    [InlineData(3, new[] { "top", "site", "deep" })]
    public void Depth_DecidesHowFarDownARepositoryIsStillFound(int depth, string[] expected)
    {
        var root = TestEnv.NewDir("walk-depth");
        MakeRepo(root, "top");
        MakeRepo(root, @"group\site");
        MakeRepo(root, @"group\clients\deep");

        var result = Walk(root, depth);

        Assert.Equal(RootAvailability.Available, result.Availability);
        Assert.False(result.Truncated);
        Assert.Equal([.. expected.Order()], Names(result));
    }

    /// <summary>Depth 1 is what every build before recursion did, and is what an upgrade gets.</summary>
    [Fact]
    public void TheDefaultDepth_FindsExactlyTheRootsOwnChildren()
    {
        var root = TestEnv.NewDir("walk-default");
        MakeRepo(root, "top");
        MakeRepo(root, @"group\site");

        var result = RepositoryWalk.Run(new ProjectRoot { Path = root }, CancellationToken.None, Generous);

        Assert.Equal(["top"], Names(result));
    }

    /// <summary>
    /// A repository is a leaf. Descending into one would walk its vendored trees and submodule
    /// checkouts, and the card that already covers those edits is the outer repository's.
    /// </summary>
    [Fact]
    public void ARepositoryInsideARepository_IsNotDescendedInto()
    {
        var root = TestEnv.NewDir("walk-nested-repo");
        MakeRepo(root, "outer");
        MakeRepo(root, @"outer\inner");

        var result = Walk(root, ProjectRootSettings.MaxDepth);

        Assert.Equal(["outer"], Names(result));
    }

    [Fact]
    public void ARepositoryUnderASkippedDirectory_IsNeverDiscovered()
    {
        var root = TestEnv.NewDir("walk-skips");
        MakeRepo(root, @"node_modules\some-package");
        MakeRepo(root, @"group\obj\leftover");
        MakeRepo(root, @"group\keeper");

        var result = Walk(root, ProjectRootSettings.MaxDepth);

        Assert.Equal(["keeper"], Names(result));
    }

    /// <summary>A linked worktree carries <c>.git</c> as a file; it is a checkout like any other.</summary>
    [Fact]
    public void AWorktreeCheckout_IsDiscovered()
    {
        var root = TestEnv.NewDir("walk-worktree");
        var checkout = Path.Combine(root, "group", "feature-wt");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, ".git"), "gitdir: ../../main/.git/worktrees/feature-wt\n");

        var result = Walk(root, 2);

        Assert.Equal(["feature-wt"], Names(result));
    }

    // ── Loop protection ─────────────────────────────────────────────────────────

    /// <summary>
    /// A junction pointing back at an ancestor makes the tree a graph, and a walk that follows
    /// it never ends. The walk finishes, and it finishes because it refused to walk through the
    /// link — not because a ceiling caught it after tens of thousands of directories.
    /// </summary>
    [Fact]
    public void AJunctionBackToAnAncestor_TerminatesWithoutBeingWalkedThrough()
    {
        var root = TestEnv.NewDir("walk-junction-loop");
        MakeRepo(root, @"group\keeper");
        MakeJunction(Path.Combine(root, "group", "loop"), root);

        var clock = Stopwatch.StartNew();
        var result = RepositoryWalk.Run(
            new ProjectRoot { Path = root, MaxDepth = ProjectRootSettings.MaxDepth },
            CancellationToken.None,
            new WalkLimits(2_000, TimeSpan.FromSeconds(20)));
        clock.Stop();

        Assert.False(result.Truncated, "the walk hit a bound instead of refusing the link");
        Assert.Equal(["keeper"], Names(result));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the walk took {clock.Elapsed}");
    }

    /// <summary>Refusing to walk THROUGH a link is not refusing to record a repository AT one.</summary>
    [Fact]
    public void ARepositoryAtAJunction_IsStillRecorded()
    {
        var root = TestEnv.NewDir("walk-junction-repo");
        var real = TestEnv.NewDir("walk-junction-target");
        MakeRepo(real, "actual");
        MakeJunction(Path.Combine(root, "linked"), Path.Combine(real, "actual"));

        var result = Walk(root, 2);

        Assert.Equal(["linked"], Names(result));
    }

    // ── Bounds ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDirectoryCeiling_TruncatesAndSaysSo()
    {
        var root = TestEnv.NewDir("walk-ceiling");
        for (var i = 0; i < 40; i++) Directory.CreateDirectory(Path.Combine(root, $"plain-{i:00}"));

        var result = RepositoryWalk.Run(
            new ProjectRoot { Path = root, MaxDepth = 2 },
            CancellationToken.None,
            new WalkLimits(10, TimeSpan.FromSeconds(30)));

        Assert.True(result.Truncated);
        Assert.Contains("10 directories", result.Detail);
    }

    [Fact]
    public void TheWallClockBudget_TruncatesAndSaysSo()
    {
        var root = TestEnv.NewDir("walk-budget");
        for (var i = 0; i < 40; i++) Directory.CreateDirectory(Path.Combine(root, $"plain-{i:00}"));

        var result = RepositoryWalk.Run(
            new ProjectRoot { Path = root, MaxDepth = 2 },
            CancellationToken.None,
            new WalkLimits(10_000, TimeSpan.Zero));

        Assert.True(result.Truncated);
        Assert.Contains("stopped after", result.Detail);
    }

    /// <summary>
    /// A truncated walk is a floor. Reported as a total it would present a scan that stopped
    /// early as one that found everything there is.
    /// </summary>
    [Fact]
    public void ATruncatedWalk_IsSurfacedToTheReader()
    {
        var status = RootStatusFor(truncated: true);
        Assert.NotNull(DashboardEmptyState.DescribeTruncatedRoots([status]));
    }

    /// <summary>
    /// A denied folder and a truncated walk both make the count a floor, and both are reported —
    /// but they are separate facts with separate remedies, so neither is folded into the other.
    /// </summary>
    [Fact]
    public void ADeniedFolder_IsCountedSeparatelyFromHittingABound()
    {
        var complete = RootStatusFor(truncated: false);
        Assert.False(complete.IsPartial);

        var refused = complete with { UnreadableFolders = 2 };
        Assert.True(refused.IsPartial);
        Assert.False(refused.Truncated);
    }

    [Fact]
    public void Cancellation_IsHonouredInsideARootRatherThanOnlyBetweenThem()
    {
        var root = TestEnv.NewDir("walk-cancel");
        for (var i = 0; i < 20; i++) Directory.CreateDirectory(Path.Combine(root, $"plain-{i:00}"));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RepositoryWalk.Run(new ProjectRoot { Path = root, MaxDepth = 3 }, cancelled.Token, Generous));
    }

    // ── Exclusions ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A bare name is a name at any depth — which is what the flat list every build before roots
    /// carried has always meant. A path is exactly one place under the root.
    /// </summary>
    [Fact]
    public void ABareExclusionName_HidesThatNameAtEveryDepth()
    {
        var root = TestEnv.NewDir("walk-exclude-name");
        MakeRepo(root, "docs");
        MakeRepo(root, @"group\docs");
        MakeRepo(root, @"group\keeper");

        var result = Walk(root, 3, "docs");

        Assert.Equal(["keeper"], Names(result));
    }

    [Fact]
    public void ARelativePathExclusion_HidesExactlyOnePlace()
    {
        var root = TestEnv.NewDir("walk-exclude-path");
        MakeRepo(root, "docs");
        MakeRepo(root, @"group\docs");

        var result = Walk(root, 3, @"group\docs");

        Assert.Equal(["docs"], Names(result));
        Assert.Equal(RepoPaths.Normalize(Path.Combine(root, "docs")), result.Repositories.Single());
    }

    [Fact]
    public void APathExclusionOnAGroupFolder_HidesEverythingBelowIt()
    {
        var root = TestEnv.NewDir("walk-exclude-subtree");
        MakeRepo(root, @"group\alpha");
        MakeRepo(root, @"group\bravo");
        MakeRepo(root, @"other\charlie");

        var result = Walk(root, 3, "group");

        Assert.Equal(["charlie"], Names(result));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static RootWalkResult Walk(string root, int depth, params string[] excluded) =>
        RepositoryWalk.Run(
            new ProjectRoot { Path = root, MaxDepth = depth, ExcludedDirectories = excluded },
            CancellationToken.None,
            Generous);

    private static string[] Names(RootWalkResult result) =>
        [.. result.Repositories.Select(Path.GetFileName).OfType<string>().Order()];

    /// <summary>A repository the walk recognizes, with no git process spawned.</summary>
    private static void MakeRepo(string root, string relativePath) =>
        Directory.CreateDirectory(Path.Combine(root, relativePath, ".git"));

    private static RootStatus RootStatusFor(bool truncated) =>
        new(@"C:\root", "", RootAvailability.Available, 0, truncated, 0, "");

    /// <summary>
    /// Junctions need no privilege, unlike symlinks. A machine that refuses fails the test
    /// rather than skipping it: a walk never shown a link proves nothing about following one.
    /// </summary>
    private static void MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        process.WaitForExit(20_000);
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), $"could not create a junction at {link}");
    }
}
