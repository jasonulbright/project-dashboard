using System.Collections.Concurrent;
using System.IO;

namespace ProjectDashboard.Services;

/// <summary>
/// Watches the configured roots for working-tree changes and coalesces them into a
/// debounced "these repos changed" signal, so cards reflect edits without waiting
/// for the periodic timer. Heavily filtered (git internals and build-output churn
/// ignored) and debounced; a buffer overflow falls back to a full-refresh signal
/// rather than losing events silently.
///
/// The signal names repository PATHS, normalized. A bare directory name is ambiguous
/// once more than one root is configured — two roots can each hold a "tabkit" — and is
/// wrong for anything below the first level, where the name of the root's immediate
/// child is not a repository at all.
///
/// One recursive watcher per configured root; all of them coalesce into one debounce buffer.
/// </summary>
public sealed class ProjectWatcherService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RootWatch> _watches = [];
    private System.Threading.Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// The repositories the last scan found, consulted before the disk when a changed path is
    /// resolved. A repository whose folder has just been deleted no longer holds a
    /// <c>.git</c> to walk up to, and its card is exactly the one that has to hear about it.
    /// </summary>
    private volatile HashSet<string> _knownRepos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Normalized paths of the repos that changed. Empty set = do a full refresh (overflow / repo add-remove).</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    /// <summary>The roots currently being watched, in the order they were started; empty while stopped.</summary>
    public IReadOnlyList<string> WatchedRoots
    {
        get { lock (_gate) return [.. _watches.Select(w => w.Root)]; }
    }

    /// <summary>
    /// Points repository resolution at the discovered set. Drops the resolution cache: a
    /// directory that has since become its own repository, or stopped being one, resolved to
    /// a different repository before this scan ran.
    /// </summary>
    public void SetKnownRepos(IEnumerable<string> repoPaths)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in repoPaths)
        {
            var normalized = RepoPaths.Normalize(path);
            if (normalized.Length > 0) known.Add(normalized);
        }
        _knownRepos = known;
        lock (_gate)
            foreach (var watch in _watches) watch.ForgetResolutions();
    }

    /// <summary>
    /// Points the service at every root that should be followed. One watcher per root — a single
    /// recursive watcher cannot cover disjoint trees. A root that cannot be watched is logged
    /// and the rest still start; the periodic reconcile is what still covers it.
    /// </summary>
    public void Start(IEnumerable<string> rootPaths)
    {
        Stop();

        var started = new List<RootWatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPath in rootPaths)
        {
            var root = RepoPaths.Normalize(rootPath);
            if (root.Length == 0 || !seen.Add(root)) continue;
            if (!Directory.Exists(root)) continue;
            started.Add(new RootWatch(root, Queue, OnError));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                foreach (var watch in started) watch.Dispose();
                return;
            }
            _watches.AddRange(started);
        }
    }

    public void Stop()
    {
        List<RootWatch> watches;
        lock (_gate)
        {
            watches = [.. _watches];
            _watches.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pending.Clear();
        }
        foreach (var watch in watches) watch.Dispose();
    }

    private void Queue(RootWatch watch, string fullPath)
    {
        if (!Covers(watch.Root, fullPath)) return;

        // Test the path RELATIVE to the root: an ignored word (bin, packages, .vs, …)
        // in an ANCESTOR of the root must not silently drop every event.
        var relative = fullPath[watch.Root.Length..];

        // "\segment\" test needs delimiters on both sides; pad so a leading .git catches too.
        var padded = "\\" + relative.TrimStart('\\', '/') + "\\";
        foreach (var seg in ScanSkips.Segments)
            if (padded.Contains(seg, StringComparison.OrdinalIgnoreCase))
            {
                // .git/HEAD and .git/index DO matter (branch switch, stage/commit) —
                // let those through even though the rest of .git is ignored.
                if (seg == @"\.git\" &&
                    (fullPath.EndsWith(@"\.git\HEAD", StringComparison.OrdinalIgnoreCase) ||
                     fullPath.EndsWith(@"\.git\index", StringComparison.OrdinalIgnoreCase) ||
                     fullPath.EndsWith(@"\.git\ORIG_HEAD", StringComparison.OrdinalIgnoreCase) ||
                     fullPath.EndsWith(@"\.git\MERGE_HEAD", StringComparison.OrdinalIgnoreCase)))
                    break;
                return;
            }

        var repo = watch.ResolveRepo(fullPath, _knownRepos);
        if (repo is null) return;

        lock (_gate)
        {
            if (_disposed) return;
            _pending.Add(repo);
            _debounceTimer ??= new System.Threading.Timer(OnDebounce, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debounceTimer.Change(Debounce, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private static bool Covers(string root, string fullPath) =>
        fullPath.Length > root.Length && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);

    private void OnDebounce(object? _)
    {
        string[] repos;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            repos = [.. _pending];
            _pending.Clear();
        }
        try { Changed?.Invoke(repos); } catch (Exception ex) { Log.Warn("watcher refresh handler failed", ex); }
    }

    private void OnError(Exception error)
    {
        lock (_gate) { if (_disposed) return; }
        // Buffer overflow: we lost events. Signal a full refresh (empty set).
        Log.Warn("file watcher buffer overflow — requesting full refresh", error);
        try { Changed?.Invoke([]); } catch { }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        Stop();
    }

    /// <summary>
    /// One root's recursive watcher, and the walk that turns a changed path into the
    /// repository that owns it.
    /// </summary>
    private sealed class RootWatch : IDisposable
    {
        /// <summary>
        /// Ceiling on remembered directories. The walk runs on every raw filesystem event
        /// before the debounce, so it has to be cached; a build tree churning under an
        /// unignored name would otherwise grow the cache without bound.
        /// </summary>
        private const int MaxCachedDirectories = 8192;

        private readonly FileSystemWatcher? _fsw;
        private readonly ConcurrentDictionary<string, string?> _repoOfDirectory = new(StringComparer.OrdinalIgnoreCase);

        public string Root { get; }

        public RootWatch(string root, Action<RootWatch, string> queue, Action<Exception> onError)
        {
            Root = root;
            try
            {
                _fsw = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024 // headroom against bursty saves before overflow
                };
                _fsw.Changed += (_, e) => queue(this, e.FullPath);
                _fsw.Created += (_, e) => queue(this, e.FullPath);
                _fsw.Deleted += (_, e) => queue(this, e.FullPath);
                _fsw.Renamed += (_, e) => { queue(this, e.FullPath); queue(this, e.OldFullPath); };
                _fsw.Error += (_, e) => onError(e.GetException());
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Warn($"file watcher could not start for {root}", ex);
                _fsw = null;
            }
        }

        public void ForgetResolutions() => _repoOfDirectory.Clear();

        /// <summary>
        /// The repository that owns a changed path, or null when no repository under this root
        /// does. Cached per containing directory rather than per path: a build writing a
        /// thousand files under one directory otherwise pays the walk a thousand times.
        /// </summary>
        public string? ResolveRepo(string fullPath, HashSet<string> known)
        {
            // The changed path can be the repository directory itself — a rename of the folder
            // reports the folder, not anything inside it.
            var self = RepoPaths.Normalize(fullPath);
            if (known.Contains(self)) return self;

            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null) return null;
            directory = RepoPaths.Normalize(directory);

            if (_repoOfDirectory.TryGetValue(directory, out var cached)) return cached;

            var resolved = WalkUp(directory, known);
            if (_repoOfDirectory.Count >= MaxCachedDirectories) _repoOfDirectory.Clear();
            _repoOfDirectory[directory] = resolved;
            return resolved;
        }

        /// <summary>
        /// The nearest ancestor the scan reported as a repository, bounded by the root. A
        /// repository the scan has not reported is used only when no reported one contains the
        /// path: a repository nested inside another is a leaf the scan deliberately does not
        /// descend into, and its edits belong to the card that does cover it.
        /// </summary>
        private string? WalkUp(string directory, HashSet<string> known)
        {
            string? onDisk = null;
            var current = directory;
            // Both spellings are normalized, and every reported path lies under the root the
            // watcher was constructed with, so length alone bounds the walk at that root.
            while (current.Length > Root.Length)
            {
                if (known.Contains(current)) return current;
                if (onDisk is null && GitService.IsGitRepo(current)) onDisk = current;

                var parent = Path.GetDirectoryName(current);
                if (parent is null) break;
                current = RepoPaths.Normalize(parent);
            }
            return onDisk;
        }

        public void Dispose() => _fsw?.Dispose();
    }
}
