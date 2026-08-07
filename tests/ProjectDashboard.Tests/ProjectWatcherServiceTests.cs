using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Drives the real FileSystemWatcher (the filter pipeline is not reachable
/// otherwise), so every wait allows for the 2-second debounce plus scheduler
/// slack. Negative assertions hold a window longer than the debounce.
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

        public WatchHarness(string root)
        {
            _service.Changed += batch =>
            {
                lock (_gate) _signals.Add(batch);
                _arrived.Release();
            };
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
        Directory.CreateDirectory(Path.Combine(root, "repoA"));
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\notes.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal(["repoA"], batch.Order());
    }

    [Fact]
    public async Task IgnoredChurn_ProducesNoSignal_ButRealEditStillDoes()
    {
        var root = NewRoot();
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
        Assert.Equal(["repoA"], batch.Order());
    }

    [Fact]
    public async Task GitIndexWrite_PassesThroughTheGitFilter()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "repoA", ".git"));
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\.git\index", "binary-ish");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal(["repoA"], batch.Order());
    }

    [Fact]
    public async Task EditsInTwoRepos_SignalBothRepos_NoMoreSignalsThanEdits()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "repoA"));
        Directory.CreateDirectory(Path.Combine(root, "repoB"));
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\a.txt");
        Touch(root, @"repoB\b.txt");

        // Both edits normally coalesce into a single two-repo batch, but a testhost
        // stall longer than the debounce can split them. The invariant is coverage
        // without excess: every touched repo signals, no batch is empty or foreign,
        // and two edits never warrant more than two signals.
        string[] expected = ["repoA", "repoB"];
        do
        {
            await harness.WaitForSignalAsync();
        }
        while (!expected.All(r => harness.Signals.Any(b => b.Contains(r))));

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
        Directory.CreateDirectory(Path.Combine(root, "repoA"));
        using var harness = new WatchHarness(root);

        Touch(root, @"repoA\file.txt");

        var batch = await harness.WaitForSignalAsync();
        Assert.Equal(["repoA"], batch.Order());
    }
}
