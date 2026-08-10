using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Drives the real FileSystemWatcher (the filter pipeline is not reachable
/// otherwise), so every wait allows for the 2-second debounce plus scheduler
/// slack. Negative assertions hold a window longer than the debounce.
///
/// The signal names repository paths, so every fixture directory that is meant to be
/// signalled is a repository: a directory that holds no repository names none.
/// </summary>
public class ProjectWatcherServiceTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(5);

    private sealed class WatchHarness : IDisposable
    {
        private readonly ProjectWatcherService _service = new();
        private readonly object _gate = new();
        private readonly List<IReadOnlyCollection<string>> _signals = [];
        private readonly SemaphoreSlim _arrived = new(0);

        public WatchHarness(string root, params string[] knownRepos)
        {
            _service.Changed += batch =>
            {
                lock (_gate) _signals.Add(batch);
                _arrived.Release();
            };
            if (knownRepos.Length > 0) _service.SetKnownRepos(knownRepos);
            _service.Start(root);
            // FileSystemWatcher arms asynchronously relative to file writes issued
            // immediately after Start; a short pause avoids missing the first event.
            Thread.Sleep(250);
        }

        public async Task<IReadOnlyCollection<string>> WaitForSignalAsync()
        {
            if (!await _arrived.WaitAsync(SignalTimeout))
                throw new TimeoutException($"no watcher signal within {SignalTimeout}");
            lock (_gate) return _signals[^1];
        }

        public Task<bool> SignalArrivedWithinAsync(TimeSpan window) => _arrived.WaitAsync(window);

        /// <summary>Snapshot of every batch received so far, in arrival order.</summary>
        public IReadOnlyList<IReadOnlyCollection<string>> Signals
        {
            get { lock (_gate) return _signals.ToList(); }
        }

        public void Dispose() => _service.Dispose();
    }

    private static string NewRoot() => TestEnv.NewDir("watch");

    /// <summary>A directory the walk-up recognizes as a repository, with no git process spawned.</summary>
    private static string NewRepo(string root, string relativePath)
    {
        var repo = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        return RepoPaths.Normalize(repo);
    }

    private static void Touch(string root, string relativePath, string content = "x")
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task WorkingTreeEdit_SignalsItsRepo()
    {
        var root = NewRoot();
        var repoA = NewRepo(root, "repoA");
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\notes.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([repoA], batch.Order());
    }

    [Fact]
    public async Task IgnoredChurn_ProducesNoSignal_ButRealEditStillDoes()
    {
        var root = NewRoot();
        var repoA = NewRepo(root, "repoA");
        Directory.CreateDirectory(Path.Combine(root, "repoA", "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(root, "repoA", ".git", "objects", "ab"));
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\node_modules\pkg\lib.js");
        Touch(root, @"repoA\.git\objects\ab\cdef0123456789");

        Assert.False(await harness.SignalArrivedWithinAsync(QuietWindow),
            "ignored paths must not signal");

        // Positive control: the watcher is alive, only the filter suppressed the churn.
        Touch(root, @"repoA\tracked.txt");
        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([repoA], batch.Order());
    }

    [Fact]
    public async Task GitIndexWrite_PassesThroughTheGitFilter()
    {
        var root = NewRoot();
        var repoA = NewRepo(root, "repoA");
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\.git\index", "binary-ish");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([repoA], batch.Order());
    }

    [Fact]
    public async Task EditsInTwoRepos_SignalBothRepos_NoMoreSignalsThanEdits()
    {
        var root = NewRoot();
        var repoA = NewRepo(root, "repoA");
        var repoB = NewRepo(root, "repoB");
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\a.txt");
        Touch(root, @"repoB\b.txt");

        // Both edits normally coalesce into a single two-repo batch, but a testhost
        // stall longer than the debounce can split them. The invariant is coverage
        // without excess: every touched repo signals, no batch is empty or foreign,
        // and two edits never warrant more than two signals.
        string[] expected = [.. new[] { repoA, repoB }.Order()];
        do
        {
            await harness.WaitForSignalAsync();
        }
        while (!expected.All(r => harness.Signals.Any(b => b.Contains(r))));

        // Hold until a full quiet window passes with no further signal: a rogue
        // duplicate arriving after coverage would otherwise land after the
        // snapshot and escape the count ceiling below. The window exceeds the
        // debounce, so any signal already in flight arrives inside it. The
        // drain is capped: a watcher stuck signaling continuously must fail
        // loudly here, not hang the run (xunit has no per-test timeout).
        var drains = 0;
        while (await harness.SignalArrivedWithinAsync(QuietWindow))
        {
            if (++drains >= 10)
                Assert.Fail($"signals still arriving after {drains} quiet-window drains: " +
                    string.Join(" | ", harness.Signals.Select(b => string.Join(",", b))));
        }

        var batches = harness.Signals;
        Assert.All(batches, b => Assert.NotEmpty(b));
        Assert.Equal(expected, batches.SelectMany(b => b).Distinct().Order());
        Assert.True(batches.Count <= expected.Length,
            $"{expected.Length} edits produced {batches.Count} signals: " +
            string.Join(" | ", batches.Select(b => string.Join(",", b))));
    }

    [Fact]
    public async Task IgnoredWordInRootAncestor_DoesNotSuppressEvents()
    {
        // The ignore filter applies to the path RELATIVE to the watch root; an
        // ignored segment (bin) in the root's own ancestry must not drop events.
        var root = Path.Combine(TestEnv.Root, "bin", "watch-" + Guid.NewGuid().ToString("N")[..8]);
        var repoA = NewRepo(root, "repoA");
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\file.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([repoA], batch.Order());
    }

    /// <summary>
    /// The reason the payload is a path. A bare top-level name for an edit two levels down
    /// names the grouping folder, which is no repository at all, and the card that owns the
    /// edit never hears about it.
    /// </summary>
    [Fact]
    public async Task AnEditInsideANestedRepository_NamesThatRepositoryAndNotItsGroupFolder()
    {
        var root = NewRoot();
        var nested = NewRepo(root, @"group\site");
        using var harness = new WatchHarness(root);

        Touch(root, @"group\site\src\index.html");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([nested], batch.Order());
        Assert.DoesNotContain(RepoPaths.Normalize(Path.Combine(root, "group")), batch);
    }

    /// <summary>
    /// A repository nested inside another is a leaf the scan does not descend into, so the
    /// card that covers the edit is the outer one — and only the discovered set can say so.
    /// </summary>
    [Fact]
    public async Task AnEditInsideARepositoryNestedInAnother_NamesTheDiscoveredOuterRepository()
    {
        var root = NewRoot();
        var outer = NewRepo(root, "outer");
        NewRepo(root, @"outer\vendored");
        using var harness = new WatchHarness(root, outer);

        Touch(root, @"outer\vendored\file.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([outer], batch.Order());
    }

    /// <summary>
    /// A deleted repository has no <c>.git</c> left to walk up to, and its card is the one
    /// that has to stop showing a working tree that is gone.
    /// </summary>
    [Fact]
    public async Task AnEditUnderADeletedRepository_StillNamesItFromTheDiscoveredSet()
    {
        var root = NewRoot();
        var repoA = NewRepo(root, "repoA");
        using var harness = new WatchHarness(root, repoA);

        Directory.Delete(Path.Combine(root, "repoA", ".git"), recursive: true);
        Touch(root, @"repoA\left-behind.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([repoA], batch.Order());
    }

    /// <summary>A directory that holds no repository names none rather than naming itself.</summary>
    [Fact]
    public async Task AnEditOutsideEveryRepository_SignalsNothing()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        using var harness = new WatchHarness(root);

        Touch(root, @"notes\scratch.txt");

        Assert.False(await harness.SignalArrivedWithinAsync(QuietWindow),
            "a path under no repository must not signal");
    }

    /// <summary>
    /// A linked worktree carries <c>.git</c> as a file rather than a directory; resolving only
    /// directories would leave every worktree checkout unwatched.
    /// </summary>
    [Fact]
    public async Task AnEditInAWorktreeCheckout_NamesTheCheckout()
    {
        var root = NewRoot();
        var checkout = Path.Combine(root, "feature-wt");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, ".git"), "gitdir: ../main/.git/worktrees/feature-wt\n");
        using var harness = new WatchHarness(root);

        Touch(root, @"feature-wt\file.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal([RepoPaths.Normalize(checkout)], batch.Order());
    }
}
