using System.IO;

namespace ProjectDashboard.Services;

/// <summary>
/// Watches the projects root for working-tree changes and coalesces them into a
/// debounced "these repos changed" signal, so cards reflect edits without waiting
/// for the periodic timer. One recursive watcher, heavily filtered (git internals
/// and build-output churn ignored) and debounced; a buffer overflow falls back to
/// a full-refresh signal rather than losing events silently.
/// </summary>
public sealed class ProjectWatcherService : IDisposable
{
    // Path segments whose churn never changes what a card shows.
    private static readonly string[] IgnoredSegments =
        [@"\.git\", @"\node_modules\", @"\bin\", @"\obj\", @"\.vs\", @"\packages\", @"\publish\"];

    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private string _root = "";
    private bool _disposed;

    /// <summary>Repo directory names that changed. Empty set = do a full refresh (overflow / repo add-remove).</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    public void Start(string rootPath)
    {
        Stop();
        if (!Directory.Exists(rootPath)) return;

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024 // headroom against bursty saves before overflow
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"file watcher could not start for {_root}", ex);
            _watcher = null;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pending.Clear();
        }
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        Queue(e.FullPath);
        Queue(e.OldFullPath);
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Queue(string fullPath)
    {
        // "\segment\" test needs delimiters on both sides; pad so a leading .git catches too.
        var padded = "\\" + fullPath + "\\";
        foreach (var seg in IgnoredSegments)
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

        var repo = TopLevelRepo(fullPath);
        if (repo is null) return;

        lock (_gate)
        {
            if (_disposed) return;
            _pending.Add(repo);
            _debounceTimer ??= new System.Threading.Timer(OnDebounce, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debounceTimer.Change(Debounce, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The immediate child of the root that contains this path (the repo directory name).</summary>
    private string? TopLevelRepo(string fullPath)
    {
        if (!fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = fullPath[_root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rest.Length == 0) return null;
        var slash = rest.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return slash < 0 ? rest : rest[..slash];
    }

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

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow: we lost events. Signal a full refresh (empty set).
        Log.Warn("file watcher buffer overflow — requesting full refresh", e.GetException());
        try { Changed?.Invoke([]); } catch { }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        Stop();
    }
}
